using AudioCpp.Native;

namespace AudioCpp;

/// <summary>A native call returned a non-OK status.</summary>
public sealed class AudioCppException : Exception
{
    internal AudioCppException(AudioCppStatus status, string detail, string operation)
        : base(detail.Length > 0
            ? $"{operation}: {detail} ({Describe(status)})"
            : $"{operation}: {Describe(status)}")
    {
        Status = status;
        Detail = detail;
        Operation = operation;
    }

    /// <summary>The status code the native call returned.</summary>
    public AudioCppStatus Status { get; }

    /// <summary>The native detail string, empty when the call left none.</summary>
    public string Detail { get; }

    /// <summary>The ABI entry point that failed.</summary>
    public string Operation { get; }

    private static string Describe(AudioCppStatus status) =>
        Utf8.ToString(NativeMethods.audiocpp_status_string(status));

    /// <summary>
    /// Throws unless the status is OK.
    /// </summary>
    /// <remarks>
    /// The detail string is thread-local on the native side and a later call
    /// may clear it, so it is read here — synchronously, on the thread that
    /// made the call — rather than anywhere further out.
    /// </remarks>
    internal static void ThrowIfFailed(AudioCppStatus status, string operation)
    {
        if (status == AudioCppStatus.Ok) return;
        throw new AudioCppException(status, Utf8.ToString(NativeMethods.audiocpp_last_error()), operation);
    }
}
