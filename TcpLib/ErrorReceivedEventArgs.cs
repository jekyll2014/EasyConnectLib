namespace EasyConnectLib
{
    public class ErrorReceivedEventArgs
    {
        public readonly string Message;

        public ErrorReceivedEventArgs(string message)
        {
            Message = message;
        }
    }
}