namespace Sim.Persistence;

public sealed class RecoveryException : Exception
{
    public RecoveryException(string message) : base(message) { }
    public RecoveryException(string message, Exception inner) : base(message, inner) { }
}
