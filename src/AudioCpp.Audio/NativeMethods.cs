using System.Runtime.InteropServices;

namespace AudioCpp.Audio;

/// <summary>
/// P/Invoke surface for the audioio shim. Mirrors native/audioio.h;
/// the coverage test in tests/AudioCpp.BindingCoverage checks audiocpp's header
/// against its bindings, and this one is small enough to read in full.
/// </summary>
internal static unsafe partial class NativeMethods
{
    internal const string Library = "audioio";

    static NativeMethods() => NativeLibraryResolver.Install();

    [LibraryImport(Library)]
    internal static partial uint audioio_version();

    [LibraryImport(Library)]
    internal static partial IntPtr audioio_backend();

    [LibraryImport(Library)]
    internal static partial int audioio_refresh_devices();

    [LibraryImport(Library)]
    internal static partial int audioio_device_count();

    [LibraryImport(Library)]
    internal static partial IntPtr audioio_device_name(int index);

    [LibraryImport(Library)]
    internal static partial int audioio_device_is_default(int index);

    [LibraryImport(Library)]
    internal static partial IntPtr audioio_open(
        int deviceIndex, int sampleRate, int channels, byte* err, nuint errLen);

    [LibraryImport(Library)]
    internal static partial int audioio_start(IntPtr device);

    [LibraryImport(Library)]
    internal static partial int audioio_stop(IntPtr device);

    [LibraryImport(Library)]
    internal static partial void audioio_close(IntPtr device);

    [LibraryImport(Library)]
    internal static partial nuint audioio_read(IntPtr device, float* buffer, nuint frameCount);

    [LibraryImport(Library)]
    internal static partial ulong audioio_overruns(IntPtr device);

    [LibraryImport(Library)]
    internal static partial float audioio_peak(IntPtr device);

    [LibraryImport(Library)]
    internal static partial IntPtr audioio_player_open(
        float* samples, nuint frameCount, int sampleRate, int channels, byte* err, nuint errLen);

    [LibraryImport(Library)]
    internal static partial int audioio_player_play(IntPtr player);

    [LibraryImport(Library)]
    internal static partial int audioio_player_pause(IntPtr player);

    [LibraryImport(Library)]
    internal static partial void audioio_player_close(IntPtr player);

    [LibraryImport(Library)]
    internal static partial nuint audioio_player_position(IntPtr player);

    [LibraryImport(Library)]
    internal static partial void audioio_player_seek(IntPtr player, nuint frame);

    [LibraryImport(Library)]
    internal static partial nuint audioio_player_length(IntPtr player);

    [LibraryImport(Library)]
    internal static partial int audioio_player_is_playing(IntPtr player);
}
