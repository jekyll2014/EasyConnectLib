using System;

namespace EasyConnectLib
{
    public interface IConnectionPort : IDisposable
    {
        public int ReceiveTimeout { get; set; }
        public int SendTimeout { get; set; }

        public bool IsConnected { get; }

        public delegate void ConnectedEventHandler(object sender, EventArgs e);

        public event ConnectedEventHandler? ConnectedEvent;

        public delegate void DisconnectedEventHandler(object sender, EventArgs e);

        public event DisconnectedEventHandler? DisconnectedEvent;

        public delegate void DataReceivedEventHandler(object sender, BinaryDataReceivedEventArgs e);

        public event DataReceivedEventHandler? DataReceivedEvent;

        public delegate void ErrorEventHandler(object sender, ErrorReceivedEventArgs e);

        public event ErrorEventHandler? ErrorEvent;

        public bool Connect();

        public bool Disconnect();

        public bool Send(byte[] data);

        public byte[] Read();
    }
}