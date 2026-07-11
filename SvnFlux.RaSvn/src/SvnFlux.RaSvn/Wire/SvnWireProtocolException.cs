namespace SvnFlux.RaSvn.Wire;

public sealed class SvnWireProtocolException : Exception
{
    public SvnWireProtocolException(string message) : base(message)
    {
    }
}
