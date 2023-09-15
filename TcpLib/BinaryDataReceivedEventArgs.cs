namespace EasyConnectLib
{
    public class BinaryDataReceivedEventArgs
    {
        public readonly byte[] Data;

        public BinaryDataReceivedEventArgs(byte[] data)
        {
            Data = data;
        }
    }
}