using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace EasyConnectLib
{
    public class TcpLib : IConnectionPort
    {
        public string Host { get; private set; } = "127.0.0.1";
        public int Port { get; private set; } = 23;

        public int ReceiveTimeout { get; set; } = 1000;
        public int SendTimeout { get; set; } = 1000;
        public int KeepAliveDelay = 1000;

        // Buffer size limits for embedded systems
        public int MaxReceiveBufferSize { get; set; } = 1024 * 1024; // 1MB default
        public int MaxBytesToReadAtOnce { get; set; } = 64 * 1024; // 64KB default

        public bool IsConnected => _clientSocket?.Client?.Connected ?? false;

        public delegate void ConnectedEventHandler(object sender, EventArgs e);

        public event IConnectionPort.ConnectedEventHandler? ConnectedEvent;

        public delegate void DisconnectedEventHandler(object sender, EventArgs e);

        public event IConnectionPort.DisconnectedEventHandler? DisconnectedEvent;

        public delegate void DataReceivedEventHandler(object sender, BinaryDataReceivedEventArgs e);

        public event IConnectionPort.DataReceivedEventHandler? DataReceivedEvent;

        public delegate void ErrorEventHandler(object sender, ErrorReceivedEventArgs e);

        public event IConnectionPort.ErrorEventHandler? ErrorEvent;

        private TcpClient? _clientSocket;
        private NetworkStream? _serverStream;
        private DateTime _nextKeepAlive = DateTime.Now;
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private Task? _backgroundTask;
        private readonly object _ctsLock = new object();

        private readonly ConcurrentQueue<byte[]> _messageQueue = new ConcurrentQueue<byte[]>();
        private readonly List<byte> _receiveBuffer = new List<byte>();
        private readonly object _receiveBufferLock = new object();

        private bool _disposedValue;

        public TcpLib()
        {
        }

        public TcpLib(string host, int port)
        {
            Host = host;
            Port = port;
        }

        public bool Connect(string host, int port)
        {
            Host = host;
            Port = port;
            _messageQueue.Clear();
            lock (_receiveBufferLock)
            {
                _receiveBuffer.Clear();
            }

            return Connect();
        }

        public bool Connect()
        {
            // Prevent multiple concurrent connections
            if (IsConnected)
            {
                OnPcbLoggerEvent("Already connected");
                return true;
            }

            // Ensure clean state before connecting
            Disconnect();

            CancellationTokenSource? newCts = null;
            try
            {
                _clientSocket = new TcpClient
                {
                    ReceiveTimeout = ReceiveTimeout,
                    SendTimeout = SendTimeout
                };

                OnPcbLoggerEvent($"Connecting to: {Host}:{Port}");
                _clientSocket.Connect(Host, Port);
                _serverStream = _clientSocket.GetStream();

                if (KeepAliveDelay > 0)
                    _nextKeepAlive = DateTime.Now.AddMilliseconds(KeepAliveDelay);

                // Create new CancellationTokenSource outside lock
                newCts = new CancellationTokenSource();
            }
            catch (Exception ex)
            {
                newCts?.Dispose();
                Disconnect();
                OnErrorEvent(ex.Message);

                return false;
            }

            OnConnectedEvent();

            lock (_ctsLock)
            {
                // Atomic swap
                var oldCts = _cts;
                _cts = newCts;
                oldCts?.Dispose();

                _backgroundTask = Task.Run(async () =>
                {
                    try
                    {
                        while (!_cts.IsCancellationRequested)
                        {
                            if (IsConnected)
                            {
                                await SendDataFromQueue();
                                await ReadTelnet();

                                if (KeepAliveDelay > 0 && DateTime.Now >= _nextKeepAlive && !(await SendKeepAlive()))
                                    Disconnect();
                            }
                            else
                            {
                                Disconnect();
                            }

                            await Task.Delay(10, _cts.Token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when cancellation is requested
                    }
                    catch (Exception ex)
                    {
                        OnErrorEvent($"Background task error: {ex.Message}");
                    }
                }, _cts.Token);
            }

            return true;
        }

        public bool Disconnect()
        {
            CancellationTokenSource? ctsToDispose = null;

            lock (_ctsLock)
            {
                if (_cts != null)
                {
                    _cts.Cancel();
                    ctsToDispose = _cts;
                    _cts = new CancellationTokenSource();
                }
            }

            // Wait for background task to complete (with timeout)
            try
            {
                _backgroundTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // Task was cancelled, this is expected
            }
            finally
            {
                ctsToDispose?.Dispose();
            }

            var result = true;

            OnPcbLoggerEvent($"Disconnecting from: {_clientSocket?.Client?.RemoteEndPoint}");
            try
            {
                _serverStream?.Close();
                _clientSocket?.Close();
                _serverStream?.Dispose();
                _clientSocket?.Dispose();
                _serverStream = null;
                _clientSocket = null;
            }
            catch (Exception ex)
            {
                OnErrorEvent(ex.Message);
                result = false;
            }

            OnDisconnectedEvent();

            return result;
        }

        #region Data acquisition

        public bool Send(byte[] data)
        {
            if (!IsConnected)
                return false;

            OnPcbLoggerEvent($"Queueing to send: [{System.Text.Encoding.UTF8.GetString(data)}]");
            _messageQueue.Enqueue(data);

            return true;
        }

        private async Task<bool> SendData(byte[] data)
        {
            try
            {
                if (_serverStream != null && IsConnected)
                {
                    OnPcbLoggerEvent($"Sending data: [{System.Text.Encoding.UTF8.GetString(data)}]");
                    await _serverStream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
                }
                else
                    Disconnect();
            }
            catch (Exception ex)
            {
                OnErrorEvent(ex.Message);

                return false;
            }

            if (KeepAliveDelay > 0)
                _nextKeepAlive = DateTime.Now.AddMilliseconds(KeepAliveDelay);

            return true;
        }

        private async Task<bool> SendKeepAlive()
        {
            return await SendData(new byte[] { 0 }).ConfigureAwait(false);
        }

        private async Task ReadTelnet()
        {
            if (IsConnected)
                try
                {
                    if (_serverStream?.DataAvailable ?? false)
                    {
                        var l = _clientSocket?.Available ?? 0;

                        // Protect against buffer overflow
                        if (l > MaxBytesToReadAtOnce)
                        {
                            OnErrorEvent($"Available bytes ({l}) exceeds maximum ({MaxBytesToReadAtOnce}).");
                            l = MaxBytesToReadAtOnce;
                        }

                        if (l > 0)
                        {
                            var data = new byte[l];
                            int n;
                            try
                            {
                                n = await _serverStream.ReadAsync(data, 0, l);
                            }
                            catch (Exception ex)
                            {
                                OnErrorEvent(ex.Message);

                                return;
                            }

                            if (n > 0)
                            {
                                OnPcbLoggerEvent($"Receiving data: [{System.Text.Encoding.UTF8.GetString(data[0..n])}]");
                                if (DataReceivedEvent != null)
                                {
                                    lock (_receiveBufferLock)
                                    {
                                        if (_receiveBuffer.Count > 0)
                                        {
                                            OnDataReceivedEvent(_receiveBuffer.ToArray());
                                            _receiveBuffer.Clear();
                                        }
                                    }

                                    OnDataReceivedEvent(data[0..n]);
                                }
                                else
                                {
                                    lock (_receiveBufferLock)
                                    {
                                        // Check buffer size limit before adding
                                        if (_receiveBuffer.Count + n > MaxReceiveBufferSize)
                                        {
                                            OnErrorEvent($"Receive buffer overflow. Clearing buffer. Size: {_receiveBuffer.Count + n}");
                                            _receiveBuffer.Clear();
                                        }

                                        _receiveBuffer.AddRange(data[0..n]);
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    OnErrorEvent(ex.Message);
                    Disconnect();
                }
            else
                Disconnect();
        }

        private async Task SendDataFromQueue()
        {
            if (_messageQueue.TryDequeue(out var message))
                await SendData(message).ConfigureAwait(false);
        }

        public byte[] Read()
        {
            lock (_receiveBufferLock)
            {
                var result = _receiveBuffer.ToArray();
                _receiveBuffer.Clear();

                return result;
            }
        }

        #endregion

        #region Events

        private void OnConnectedEvent()
        {
            OnPcbLoggerEvent($"Connected to: {_clientSocket?.Client.RemoteEndPoint}");
            ConnectedEvent?.Invoke(this, EventArgs.Empty);
        }

        private void OnDisconnectedEvent()
        {
            OnPcbLoggerEvent($"Disconnected");
            DisconnectedEvent?.Invoke(this, EventArgs.Empty);
        }

        private void OnDataReceivedEvent(byte[] data)
        {
            OnPcbLoggerEvent($"Data received");
            DataReceivedEvent?.Invoke(this, new BinaryDataReceivedEventArgs(data));
        }

        private void OnErrorEvent(string message)
        {
            OnPcbLoggerEvent($"Error: {message}");
            ErrorEvent?.Invoke(this, new ErrorReceivedEventArgs(message));
        }

        #endregion

        #region Logging

        public event IConnectionPort.PcbLoggerEventHandler? PcbLoggerEvent;

        public void OnPcbLoggerEvent(string message)
        {
            PcbLoggerEvent?.Invoke(this, message);
        }

        #endregion

        #region Dispose

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    Disconnect();

                    // Dispose the final CancellationTokenSource
                    CancellationTokenSource? ctsToDispose = null;
                    lock (_ctsLock)
                    {
                        ctsToDispose = _cts;
                        _cts = null!;
                    }
                    ctsToDispose?.Dispose();

                    _serverStream?.Dispose();
                    _clientSocket?.Close();
                    _clientSocket?.Dispose();
                    _messageQueue.Clear();

                    lock (_receiveBufferLock)
                    {
                        _receiveBuffer.Clear();
                    }
                }

                _disposedValue = true;
            }
        }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method

            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion
    }
}