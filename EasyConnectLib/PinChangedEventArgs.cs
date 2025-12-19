using System;
using System.IO.Ports;

namespace EasyConnectLib
{
    public class PinChangedEventArgs : EventArgs
    {
        public SerialPinChange Pin { get; }

        public PinChangedEventArgs(SerialPinChange pin)
        {
            Pin = pin;
        }
    }
}