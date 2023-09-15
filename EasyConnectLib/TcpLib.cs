﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace EasyConnectLib
{
    public class TcpLib : IDisposable, IConnectionPort
    {
        public string? Host => _hostName;
        public int? Port => _port;

        public int ReceiveTimeout = 1000;
        public int SendTimeout = 1000;
        public int KeepAliveDelay = 1000;

        public bool IsConnected => _clientSocket?.Client?.Connected ?? false;

        int IConnectionPort.ReceiveTimeout { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        int IConnectionPort.SendTimeout { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

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
        private string? _hostName;
        private int _port;
        private DateTime _nextKeepAlive = DateTime.Now;
        private CancellationTokenSource _cts = new CancellationTokenSource();

        private readonly ConcurrentQueue<byte[]> _messageQueue = new ConcurrentQueue<byte[]>();
        private readonly List<byte> receiveBuffer = new List<byte>();

        private bool _disposedValue;

        public TcpLib() { }

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
                _clientSocket = new TcpClient
                {
                    ReceiveTimeout = ReceiveTimeout,
                    SendTimeout = SendTimeout
                };

                _clientSocket.Connect(_hostName, _port);
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
            Task.Run(() =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    if (IsConnected)
                    {
                        ReadTelnet();
                        SendDataFromQueue();

                        if (KeepAliveDelay > 0 && DateTime.Now >= _nextKeepAlive && !SendKeepAlive())
                            Disconnect();
                    }
                    else
                    {
                        Disconnect();
                    }
                }
            }, _cts.Token);

            return true;
        }

        public bool Disconnect()
        {
            _cts.Cancel();
            var result = true;
            try
            {
                _serverStream?.Close();
                _serverStream?.Dispose();
                _clientSocket?.Close();
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

        private bool SendData(byte[] data)
        {
            try
            {
                _serverStream?.Write(data.ToArray(), 0, data.Count());
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

        private bool SendKeepAlive()
        {
            return SendData(new byte[] { 0 });
        }

        private readonly object _lockReceive = new object();
        private void ReadTelnet()
        {
            lock (_lockReceive)
            {
                if (IsConnected)
                {
                    if (_serverStream?.DataAvailable ?? false)
                    {
                        var l = _clientSocket?.Available ?? 0;
                        if (l > 0)
                        {
                            var data = new byte[l];
                            var n = 0;
                            try
                            {
                                n = _serverStream.Read(data, 0, l);
                            }
                            catch (Exception ex)
                            {
                                OnErrorEvent(ex.Message);

                                return;
                            }

                            if (DataReceivedEvent != null)
                                OnDataReceivedEvent(data[0..(n - 1)]);
                            else
                                receiveBuffer.AddRange(data[0..(n - 1)]);
                        }
                    }
                }
                else
                {
                    Disconnect();
                }
            }
        }

        private void SendDataFromQueue()
        {
            if (_messageQueue.TryDequeue(out var message))
                SendData(message);
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

            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}