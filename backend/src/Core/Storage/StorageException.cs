namespace Backend.Core.Storage;

/// <summary>
/// A storage operation was rejected or did not take effect (e.g. an auth/permission failure, or a
/// write that did not persist). Distinct from "object absent": a backend rejection is a fault that
/// must surface — never be masked as a phantom success — so the media node maps it to a
/// <c>FatalError</c> on <c>RunState</c> (DL-022/023) and the media proxy returns a real error rather
/// than serving the backend's error body as if it were the asset.
/// </summary>
public sealed class StorageException : Exception
{
    public StorageException(string message)
        : base(message)
    {
    }

    public StorageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
