namespace SpinTexture.Core.Pipeline;

public sealed class InstallTransactionException : Exception
{
    public InstallTransactionException(string message, Exception transactionFailure, IReadOnlyList<Exception> rollbackFailures)
        : base(message, transactionFailure)
    {
        RollbackFailures = rollbackFailures;
    }

    public IReadOnlyList<Exception> RollbackFailures { get; }
}
