using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace EasyConnectLib
{
    public class TcpLib : IConnectionPort
    {
        public string Host = "127.0.0.1";
        public int Port = 23;

        public int ReceiveTimeout { get; set; } = 1000;
        public int SendTimeout { get; set; } = 1000;
        public int KeepAliveDelay = 1000;

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

        private readonly ConcurrentQueue<byte[]> _messageQueue = new ConcurrentQueue<byte[]>();
        private readonly List<byte> receiveBuffer = new List<byte>();

        private bool _disposedValue;

        private Task? _sender;

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

            return Connect();
        }

        public bool Connect()
        {
            _messageQueue.Clear();
            try
            {
                _clientSocket = new TcpClient
                {
                    ReceiveTimeout = ReceiveTimeout,
                    SendTimeout = SendTimeout
                };

                _clientSocket.Connect(Host, Port);
                _serverStream = _clientSocket.GetStream();

                if (KeepAliveDelay > 0)
                    _nextKeepAlive = DateTime.Now.AddMilliseconds(KeepAliveDelay);
            }
            catch (Exception ex)
            {
                Disconnect();
                OnErrorEvent(ex.Message);

                return false;
            }

            OnConnectedEvent();

            _cts = new CancellationTokenSource();
            _sender = Task.Factory.StartNew(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    if (IsConnected)
                    {
                        SendDataFromQueue();
                        ReadTelnet();

                        if (KeepAliveDelay > 0 && DateTime.Now >= _nextKeepAlive && !SendKeepAlive().Result)
                            Disconnect();
                    }
                    else
                    {
                        Disconnect();
                    }

                    await Task.Delay(1);
                }
            }, _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            return true;
        }

        public bool Disconnect()
        {
            _cts.Cancel();
            //_sender?.Wait();
            var result = true;

            try
            {
                _serverStream?.Close();
                _serverStream?.Dispose();
                _clientSocket?.Close();
                _clientSocket?.Dispose();
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

            _messageQueue.Enqueue(data);
            return true;
        }

        private async Task<bool> SendData(byte[] data)
        {
            try
            {
                if (_serverStream != null && IsConnected)
                    await _serverStream.WriteAsync(data.ToArray(), 0, data.Count());
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
            return await SendData(new byte[] { 0 });
        }

        private void ReadTelnet()
        {
            if (IsConnected)
                try
                {
                    if (_serverStream?.DataAvailable ?? false)
                    {
                        var l = _clientSocket?.Available ?? 0;
                        if (l > 0)
                        {
                            var data = new byte[l];
                            int n;
                            try
                            {
                                n = _serverStream.Read(data, 0, l);
                            }
                            catch (Exception ex)
                            {
                                OnErrorEvent(ex.Message);

                                return;
                            }

                            if (n > 0)
                            {
                                if (DataReceivedEvent != null)
                                    OnDataReceivedEvent(data[0..n]);
                                else
                                    receiveBuffer.AddRange(data[0..n]);
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

        private async void SendDataFromQueue()
        {
            if (_messageQueue.TryDequeue(out var message))
                await SendData(message);
        }

        public byte[] Read()
        {
            var result = receiveBuffer.ToArray();
            receiveBuffer.Clear();

            return result;
        }

        #endregion

        #region Events

        private void OnConnectedEvent()
        {
            ConnectedEvent?.Invoke(this, EventArgs.Empty);
        }

        private void OnDisconnectedEvent()
        {
            DisconnectedEvent?.Invoke(this, EventArgs.Empty);
        }

        private void OnDataReceivedEvent(byte[] data)
        {
            DataReceivedEvent?.Invoke(this, new BinaryDataReceivedEventArgs(data));
        }

        private void OnErrorEvent(string message)
        {
            ErrorEvent?.Invoke(this, new ErrorReceivedEventArgs(message));
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
                    _serverStream?.Dispose();
                    _clientSocket?.Close();
                    _clientSocket?.Dispose();
                    _messageQueue.Clear();
                }

                // TODO: set large fields to null
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