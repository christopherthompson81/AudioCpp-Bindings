using System.Runtime.InteropServices;
using AudioCpp.Native;

namespace AudioCpp;

/// <summary>
/// A string-to-string option bag, matching the framework's own option maps.
/// The same shape serves load, session and request options.
/// </summary>
public sealed class AudioCppOptions : SafeHandle
{
    /// <summary>Creates an empty option bag.</summary>
    public AudioCppOptions() : base(IntPtr.Zero, ownsHandle: true)
    {
        var created = NativeMethods.audiocpp_options_create();
        if (created == IntPtr.Zero) throw new OutOfMemoryException("audiocpp_options_create returned null");
        SetHandle(created);
    }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <summary>Sets one option, replacing any previous value for the key.</summary>
    public AudioCppOptions Set(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        AudioCppException.ThrowIfFailed(
            NativeMethods.audiocpp_options_set(handle, key, value), nameof(NativeMethods.audiocpp_options_set));
        return this;
    }

    /// <summary>Builds an option bag from pairs, or null when there are none.</summary>
    public static AudioCppOptions? From(IEnumerable<KeyValuePair<string, string>>? values)
    {
        if (values is null) return null;
        AudioCppOptions? options = null;
        foreach (var (key, value) in values)
        {
            options ??= new AudioCppOptions();
            options.Set(key, value);
        }
        return options;
    }

    internal IntPtr DangerousHandle => handle;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        NativeMethods.audiocpp_options_free(handle);
        return true;
    }
}
