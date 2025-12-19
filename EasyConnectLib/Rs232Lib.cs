using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EasyConnectLib
{
    public class Rs232Lib : IConnectionPort
    {
        public string Port = string.Empty;
        public int Speed = 115200;

        public int DataBits = 8;
        public Parity Parity = Parity.None;
        public StopBits StopBits = StopBits.One;
        public Handshake Handshake = Handshake.None;

        public int ReceiveTimeout { get; set; } = 1000;
        public int SendTimeout { get; set; } = 1000;

        // Buffer size limits for embedded systems
        public int MaxReceiveBufferSize { get; set; } = 1024 * 1024; // 1MB default
        public int MaxBytesToReadAtOnce { get; set; } = 64 * 1024; // 64KB default

        public bool DtrEnable
        {
            get => _serialPort?.DtrEnable ?? false;
            set
            {
                if (_serialPort != null)
                    _serialPort.DtrEnable = value;
            }
        }

        public bool RtsEnable
        {
            get => _serialPort?.RtsEnable ?? false;
            set
            {
                if (_serialPort != null)
                    _serialPort.RtsEnable = value;
            }
        }

        public bool BreakState
        {
            get => _serialPort?.BreakState ?? false;
            set
            {
                if (_serialPort != null)
                    _serialPort.BreakState = value;
            }
        }

        public bool CDHolding => _serialPort?.CDHolding ?? false;

        public bool CtsHolding => _serialPort?.CtsHolding ?? false;

        public bool DsrHolding => _serialPort?.DsrHolding ?? false;

        public Encoding Encoding
        {
            get => _serialPort?.Encoding ?? Encoding.ASCII;
            set
            {
                if (_serialPort != null)
                    _serialPort.Encoding = value;
            }
        }

        public string NewLine
        {
            get => _serialPort?.NewLine ?? string.Empty;
            set
            {
                if (_serialPort != null)
                    _serialPort.NewLine = value;
            }
        }

        public byte ParityReplace
        {
            get => _serialPort?.ParityReplace ?? 0;
            set
            {
                if (_serialPort != null)
                    _serialPort.ParityReplace = value;
            }
        }

        public bool IsConnected => _serialPort?.IsOpen ?? false;

        public event IConnectionPort.ConnectedEventHandler? ConnectedEvent;

        public event IConnectionPort.DisconnectedEventHandler? DisconnectedEvent;

        public event IConnectionPort.DataReceivedEventHandler? DataReceivedEvent;

        public delegate void PinChangedEventHandler(object sender, PinChangedEventArgs e);

        public event PinChangedEventHandler? PinChangedEvent;

        public event IConnectionPort.ErrorEventHandler? ErrorEvent;

        private SerialPort? _serialPort;
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private Task? _backgroundTask;
        private readonly object _ctsLock = new object();

        private readonly ConcurrentQueue<byte[]> _messageQueue = new ConcurrentQueue<byte[]>();
        private readonly List<byte> _receiveBuffer = new List<byte>();
        private readonly object _receiveBufferLock = new object();

        private bool _disposedValue;

        public Rs232Lib()
        {
        }

        public Rs232Lib(string port, int speed)
        {
            Port = port;
            Speed = speed;
        }

        public bool Connect(string port, int speed)
        {
            Port = port;
            Speed = speed;
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
                _serialPort = new SerialPort
                {
                    PortName = Port,
                    BaudRate = Speed,
                    DataBits = DataBits,
                    Parity = Parity,
                    StopBits = StopBits,
                    Handshake = Handshake,
                    ReadTimeout = ReceiveTimeout,
                    WriteTimeout = SendTimeout
                };

                _serialPort.DataReceived += SerialDataReceivedEventHandler;
                _serialPort.ErrorReceived += SerialErrorReceivedEventHandler;
                _serialPort.PinChanged += SerialPinChangedEventHandler;
                OnPcbLoggerEvent($"Connecting to: {_serialPort.PortName}");
                _serialPort.Open();

                // Create new CancellationTokenSource outside lock to avoid holding lock during Task.Run
                newCts = new CancellationTokenSource();
            }
            catch (Exception ex)
            {
                newCts?.Dispose(); // Dispose the new CTS if we created it
                Disconnect();
                OnErrorEvent(ex.Message);

                return false;
            }

            OnConnectedEvent();

            lock (_ctsLock)
            {
                // Dispose old CTS and replace with new one
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
                                SendDataFromQueue();
                            }
                            else
                            {
                                Disconnect();
                                OnDisconnectedEvent();
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
                    _cts = new CancellationTokenSource(); // Replace with new one for next connection
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
                // Dispose the old CTS outside the lock
                ctsToDispose?.Dispose();
            }

            var result = true;
            OnPcbLoggerEvent($"Disconnecting from: {_serialPort?.PortName}");
            try
            {
                if (_serialPort != null)
                {
                    _serialPort.Close();
                    _serialPort.DataReceived -= SerialDataReceivedEventHandler;
                    _serialPort.ErrorReceived -= SerialErrorReceivedEventHandler;
                    _serialPort.PinChanged -= SerialPinChangedEventHandler;
                    _serialPort.Dispose();
                    _serialPort = null;
                }
            }
            catch (Exception ex)
            {
                OnErrorEvent(ex.Message);
                result = false;
            }

            OnDisconnectedEvent();

            return result;
        }

        public static string[] GetPortList()
        {
            return SerialPort.GetPortNames();
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

        private void SendDataFromQueue()
        {
            if (_messageQueue.TryDequeue(out var message))
                SendData(message);
        }

        private void SendData(byte[] data)
        {
            try
            {
                OnPcbLoggerEvent($"Sending data: [{System.Text.Encoding.UTF8.GetString(data)}]");
                _serialPort?.Write(data, 0, data.Length);
            }
            catch (Exception ex)
            {
                OnErrorEvent(ex.Message);
            }
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

        #region EventHandlers

        private readonly object _lockReceive = new object();

        private void SerialDataReceivedEventHandler(object sender, SerialDataReceivedEventArgs e)
        {
            lock (_lockReceive)
            {
                if (_serialPort != null && !_serialPort.IsOpen)
                {
                    Disconnect();

                    return;
                }

                var l = _serialPort?.BytesToRead ?? 0;

                // Protect against buffer overflow from malformed hardware/driver
                if (l > MaxBytesToReadAtOnce)
                {
                    OnErrorEvent($"BytesToRead ({l}) exceeds maximum ({MaxBytesToReadAtOnce}). Possible hardware/driver issue.");
                    l = MaxBytesToReadAtOnce;
                }

                while (l > 0)
                {
                    var data = new byte[l];
                    var n = 0;
                    try
                    {
                        n = _serialPort?.Read(data, 0, l) ?? 0;
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

                    if (_cts.IsCancellationRequested)
                        break;

                    l = _serialPort?.BytesToRead ?? 0;

                    // Recheck limit in loop
                    if (l > MaxBytesToReadAtOnce)
                    {
                        OnErrorEvent($"BytesToRead ({l}) exceeds maximum ({MaxBytesToReadAtOnce}).");
                        l = MaxBytesToReadAtOnce;
                    }
                }
            }
        }

        private void SerialErrorReceivedEventHandler(object sender, SerialErrorReceivedEventArgs e)
        {
            var error = e.EventType.ToString();
            OnPcbLoggerEvent($"Error: {error}");
            OnErrorEvent(error);
        }

        private void SerialPinChangedEventHandler(object sender, SerialPinChangedEventArgs e)
        {
            OnPcbLoggerEvent($"Pin changed: {e.EventType.ToString()}");
            OnPinChangedEvent(e.EventType);
        }

        #endregion

        #region Events

        private void OnConnectedEvent()
        {
            OnPcbLoggerEvent($"Connected to: {_serialPort?.PortName}");
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
            ErrorEvent?.Invoke(this, new ErrorReceivedEventArgs(message));
        }

        private void OnPinChangedEvent(SerialPinChange pin)
        {
            PinChangedEvent?.Invoke(this, new PinChangedEventArgs(pin));
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
                        _cts = null!; // Prevent further use
                    }
                    ctsToDispose?.Dispose();

                    _serialPort?.Dispose();
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
