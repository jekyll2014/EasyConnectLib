using System.IO.Ports;

namespace EasyConnectLib
{
    public class PinChangedEventArgs
    {
        public readonly SerialPinChange Pin;

        public PinChangedEventArgs(SerialPinChange pin)
        {
            this.Pin = pin;
        }
    }
}