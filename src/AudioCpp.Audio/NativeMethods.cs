using System.Runtime.InteropServices;

namespace AudioCpp.Audio;

/// <summary>
/// P/Invoke surface for the audiocapture shim. Mirrors native/audiocapture.h;
/// the coverage test in tests/AudioCpp.BindingCoverage checks audiocpp's header
/// against its bindings, and this one is small enough to read in full.
/// </summary>
internal static unsafe partial class NativeMethods
{
    internal const string Library = "audiocapture";

    static NativeMethods() => NativeLibraryResolver.Install();

    [LibraryImport(Library)]
    internal static partial uint audiocapture_version();

    [LibraryImport(Library)]
    internal static partial IntPtr audiocapture_backend();

    [LibraryImport(Library)]
    internal static partial int audiocapture_refresh_devices();

    [LibraryImport(Library)]
    internal static partial int audiocapture_device_count();

    [LibraryImport(Library)]
    internal static partial IntPtr audiocapture_device_name(int index);

    [LibraryImport(Library)]
    internal static partial int audiocapture_device_is_default(int index);

    [LibraryImport(Library)]
    internal static partial IntPtr audiocapture_open(
        int deviceIndex, int sampleRate, int channels, byte* err, nuint errLen);

    [LibraryImport(Library)]
    internal static partial int audiocapture_start(IntPtr device);

    [LibraryImport(Library)]
    internal static partial int audiocapture_stop(IntPtr device);

    [LibraryImport(Library)]
    internal static partial void audiocapture_close(IntPtr device);

    [LibraryImport(Library)]
    internal static partial nuint audiocapture_read(IntPtr device, float* buffer, nuint frameCount);

    [LibraryImport(Library)]
    internal static partial ulong audiocapture_overruns(IntPtr device);

    [LibraryImport(Library)]
    internal static partial float audiocapture_peak(IntPtr device);
}
