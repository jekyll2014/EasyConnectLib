namespace EasyTcpLibrary
{
    public class BinaryDataReceivedEventArgs
    {
        public byte[] Data;

        public BinaryDataReceivedEventArgs(byte[] data)
        {
            Data = data;
        }
    }
}