namespace TenderScout.Infrastructure.Documents;

/// <summary>A manifest or one of its rows cannot be imported; the message says which one and why.</summary>
public sealed class DocumentImportException : Exception
{
    public DocumentImportException(string message)
        : base(message)
    {
    }

    public DocumentImportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
