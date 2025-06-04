using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
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

        private readonly ConcurrentQueue<byte[]> _messageQueue = new ConcurrentQueue<byte[]>();
        private readonly List<byte> _receiveBuffer = new List<byte>();

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
            _receiveBuffer.Clear();

            return Connect();
        }

        public bool Connect()
        {
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
            }
            catch (Exception ex)
            {
                Disconnect();
                OnErrorEvent(ex.Message);

                return false;
            }

            OnConnectedEvent();

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            Task.Factory.StartNew(async () =>
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

                    await Task.Delay(10).ConfigureAwait(false);
                }
            }, _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            return true;
        }

        public bool Disconnect()
        {
            _cts.Cancel();

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
                _serialPort?.Write(data.ToArray(), 0, data.Count());
            }
            catch (Exception ex)
            {
                OnErrorEvent(ex.Message);
            }
        }

        public byte[] Read()
        {
            var result = _receiveBuffer.ToArray();
            _receiveBuffer.Clear();

            return result;
        }

        #endregion

        #region EventHandlers

        private readonly object _lockReceive = new object();

        private void SerialDataReceivedEventHandler(object sender, SerialDataReceivedEventArgs e)
        {
            lock (_lockReceive)
            {
                if (!(_serialPort?.IsOpen ?? false))
                {
                    Disconnect();

                    return;
                }

                var l = _serialPort.BytesToRead;
                while (l > 0)
                {
                    var data = new byte[l];
                    var n = 0;
                    try
                    {
                        n = _serialPort.Read(data, 0, l);
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
                            if (_receiveBuffer.Count > 0)
                            {
                                OnDataReceivedEvent(_receiveBuffer.ToArray());
                                _receiveBuffer.Clear();
                            }

                            OnDataReceivedEvent(data[0..n]);
                        }
                        else
                            _receiveBuffer.AddRange(data[0..n]);
                    }

                    if (_cts.IsCancellationRequested)
                        break;

                    l = _serialPort.BytesToRead;
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
                    _serialPort?.Dispose();
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
