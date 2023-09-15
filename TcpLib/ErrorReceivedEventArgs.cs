namespace EasyTcpLibrary
{
    public class ErrorReceivedEventArgs
    {
        public string Message;

        public ErrorReceivedEventArgs(string message)
        {
            Message = message;
        }
    }
}