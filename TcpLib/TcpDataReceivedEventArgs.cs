namespace EasyTcpLibrary
{
    public class TcpDataReceivedEventArgs
    {
        public byte[] Data;

        public TcpDataReceivedEventArgs(byte[] data)
        {
            Data = data;
        }
    }
}