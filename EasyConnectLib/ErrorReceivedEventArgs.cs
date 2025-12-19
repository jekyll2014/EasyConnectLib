using System;

namespace EasyConnectLib
{
    public class ErrorReceivedEventArgs : EventArgs
    {
        public string Message { get; }

        public ErrorReceivedEventArgs(string message)
        {
            Message = message;
        }
    }
}