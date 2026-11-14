namespace Loom.Core.Infrastructure;

/// <summary>Domain error with a stable machine-readable code (mapped to CLI exit codes).</summary>
public sealed class LoomException : Exception
{
    public LoomException(string code, string message, Exception? inner = null) : base(message, inner)
    {
        Code = code;
    }

    public string Code { get; }
}

public static class LoomErrorCodes
{
    public const string NotFound = "E_NOT_FOUND";
    public const string Busy = "E_BUSY";
    public const string NeedsManual = "E_NEEDS_MANUAL";
    public const string NoSnapshot = "E_NO_SNAPSHOT";
    public const string CorruptSnapshot = "E_CORRUPT_SNAPSHOT";
    public const string RestoreRejected = "E_RESTORE_REJECTED";
    public const string Failed = "E_FAILED";
    public const string Cancelled = "E_CANCELLED";
    public const string Unsupported = "E_UNSUPPORTED";
    public const string LockTimeout = "E_LOCK_TIMEOUT";
}
