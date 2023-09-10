using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace EasyTcpLibrary
{
    public class TcpLib : IDisposable
    {

        public string? Host { get => _hostName; }
        public int? Port { get => _port; }

        public int ReceiveTimeout = 1000;
        public int SendTimeout = 1000;
        public int KeepAliveDelay = 1000;

        public bool IsConnected
        {
            get
            {
                return _clientSocket?.Client?.Connected ?? false;
            }
        }

        public delegate void ConnectedEventHandler(object sender, EventArgs e);
        public event ConnectedEventHandler? ConnectedEvent;

        public delegate void DisconnectedEventHandler(object sender, EventArgs e);
        public event DisconnectedEventHandler? DisconnectedEvent;

        public delegate void DataReceivedEventHandler(object sender, TcpDataReceivedEventArgs e);
        public event DataReceivedEventHandler? DataReceivedEvent;

        private TcpClient? _clientSocket;
        private NetworkStream? _serverStream;
        private string? _hostName;
        private int _port;
        private DateTime _nextKeepAlive = DateTime.Now;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private readonly ConcurrentQueue<byte[]> _messageQueue = new ConcurrentQueue<byte[]>();

        private bool _disposedValue;

        public TcpLib()
        {
        }

        public TcpLib(string host, int port)
        {
            Connect(host, port);
        }

        public bool Connect(string host, int ipPort)
        {
            _hostName = host;
            _port = ipPort;
            _messageQueue.Clear();
            try
            {
                _clientSocket = new TcpClient();
                _clientSocket.ReceiveTimeout = ReceiveTimeout;
                _clientSocket.SendTimeout = SendTimeout;
                _clientSocket.Connect(_hostName, _port);
                _serverStream = _clientSocket.GetStream();
                if (KeepAliveDelay > 0)
                    _nextKeepAlive = DateTime.Now.AddMilliseconds(KeepAliveDelay);
            }
            catch (Exception ex)
            {
                Disconnect();
                _clientSocket = new TcpClient();

                return false;
            }

            OnConnectedEvent();

            Task.Run(() =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    if (IsConnected)
                    {
                        ReadTelnet();
                        SendTelnet();
                        if (DateTime.Now >= _nextKeepAlive)
                        {
                            if (!SendKeepAlive())
                                OnDisconnectedEvent();
                        }
                    }
                    else
                    {
                        OnDisconnectedEvent();
                    }
                }
            });

            return true;
        }

        public bool Disconnect()
        {
            _cts.Cancel();
            try
            {
                _serverStream?.Close();
                _serverStream?.Dispose();
                _clientSocket?.Close();
            }
            catch (Exception ex)
            {
                return false;
            }

            _hostName = null;
            _port = -1;

            return true;
        }

        #region Data acquisition

        public bool Send(byte[] data)
        {
            if (!IsConnected)
                return false;

            _messageQueue.Enqueue(data);
            return true;
        }

        private bool SendData(byte[] data)
        {
            try
            {
                _serverStream?.Write(data.ToArray(), 0, data.Count());

                if (KeepAliveDelay > 0)
                    _nextKeepAlive = DateTime.Now.AddMilliseconds(KeepAliveDelay);
            }
            catch
            {
                return false;
            }

            return true;
        }

        private bool SendKeepAlive()
        {
            var result = false;
            if (IsConnected)
            {
                //_serverStream.WriteTimeout = 1000;
                result = SendData(new byte[] { 0 });
                //_serverStream.WriteTimeout = SendTimeout;
            }

            return result;
        }

        private void ReadTelnet()
        {
            if (IsConnected && (_serverStream?.DataAvailable ?? false))
            {
                try
                {
                    var data = new List<byte>();
                    while (_clientSocket?.Available > 0)
                    {
                        var buffer = new byte[_clientSocket.Available];
                        _serverStream.ReadAsync(buffer, 0, buffer.Length).Wait();
                        data.AddRange(buffer);
                    }

                    OnDataReceivedEvent(data.ToArray());
                }
                catch (Exception ex)
                {
                    Debug.Write(ex.Message);
                }
            }
        }

        private void SendTelnet()
        {
            if (_messageQueue.TryDequeue(out var message))
                SendData(message);
        }
        #endregion

        #region Events
        private void OnConnectedEvent()
        {
            ConnectedEvent?.Invoke(this, EventArgs.Empty);
        }

        private void OnDisconnectedEvent()
        {
            Disconnect();
            DisconnectedEvent?.Invoke(this, EventArgs.Empty);
        }

        private void OnDataReceivedEvent(byte[] data)
        {
            DataReceivedEvent?.Invoke(this, new TcpDataReceivedEventArgs(data));
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

            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}