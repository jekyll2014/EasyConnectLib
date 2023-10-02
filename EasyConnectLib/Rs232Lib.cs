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
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private readonly ConcurrentQueue<byte[]> _messageQueue = new ConcurrentQueue<byte[]>();
        private readonly List<byte> receiveBuffer = new List<byte>();
        private bool _disposedValue;

        public Rs232Lib() { }

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
            receiveBuffer.Clear();

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
                    WriteTimeout = SendTimeout,
                    RtsEnable = true
                };
                _serialPort.Open();

                _serialPort.DataReceived += SerialDataReceivedEventHandler;
                _serialPort.ErrorReceived += SerialErrorReceivedEventHandler;
                _serialPort.PinChanged += SerialPinChangedEventHandler;
            }
            catch (Exception ex)
            {
                Disconnect();
                OnErrorEvent(ex.Message);

                return false;
            }

            OnConnectedEvent();

            Task.Run(() =>
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
                }
            });

            return true;
        }

        /*public bool Reconnect()
        {
            Disconnect();
            return Connect();
        }*/

        public bool Disconnect()
        {
            try
            {
                _serialPort?.Dispose();
            }
            catch (Exception ex)
            {
                OnErrorEvent(ex.Message);

                return false;
            }

            _cts.Cancel();

            OnDisconnectedEvent();

            return true;
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
                _serialPort?.Write(data.ToArray(), 0, data.Count());
            }
            catch (Exception ex)
            {
                OnErrorEvent(ex.Message);
            }
        }

        public byte[] Read()
        {
            var result = receiveBuffer.ToArray();
            receiveBuffer.Clear();

            return result;
        }
        #endregion

        #region EventHandlers
        private readonly object _lockReceive = new object();
        private void SerialDataReceivedEventHandler(object sender, SerialDataReceivedEventArgs e)
        {
            if (_serialPort?.IsOpen ?? false)
            {
                lock (_lockReceive)
                {
                    var l = _serialPort.BytesToRead;
                    if (l > 0)
                    {
                        var n = 0;
                        var data = new byte[l];
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
                            if (DataReceivedEvent != null)
                                OnDataReceivedEvent(data[0..n]);
                            else
                                receiveBuffer.AddRange(data[0..n]);
                        }
                    }
                }
            }
            else
            {
                Disconnect();
            }
        }

        private void SerialErrorReceivedEventHandler(object sender, SerialErrorReceivedEventArgs e)
        {
            OnErrorEvent(e.EventType.ToString());
        }

        private void SerialPinChangedEventHandler(object sender, SerialPinChangedEventArgs e)
        {
            OnPinChangedEvent(e.EventType);
        }
        #endregion

        #region Events
        private void OnConnectedEvent()
        {
            Task.Run(() => ConnectedEvent?.Invoke(this, EventArgs.Empty));
        }

        private void OnDisconnectedEvent()
        {
            Task.Run(() => DisconnectedEvent?.Invoke(this, EventArgs.Empty));
        }

        private void OnDataReceivedEvent(byte[] data)
        {
            Task.Run(() => DataReceivedEvent?.Invoke(this, new BinaryDataReceivedEventArgs(data)));
        }

        private void OnErrorEvent(string message)
        {
            Task.Run(() => ErrorEvent?.Invoke(this, new ErrorReceivedEventArgs(message)));
        }

        private void OnPinChangedEvent(SerialPinChange pin)
        {
            PinChangedEvent?.Invoke(this, new PinChangedEventArgs(pin));
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

            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
