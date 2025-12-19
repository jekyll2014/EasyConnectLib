using System;

namespace EasyConnectLib
{
    public class BinaryDataReceivedEventArgs : EventArgs
    {
        public byte[] Data { get; }

        public BinaryDataReceivedEventArgs(byte[] data)
        {
            // Defensive copy to prevent external modification
            Data = (byte[])data.Clone();
        }
    }
}