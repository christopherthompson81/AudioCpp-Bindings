using System.Runtime.InteropServices;

namespace AudioCpp.Audio;

/// <summary>One capture device the system offers.</summary>
public readonly record struct CaptureDeviceInfo(int Index, string Name, bool IsDefault)
{
    public override string ToString() => IsDefault ? $"{Name} (default)" : Name;
}

/// <summary>Enumeration and capability queries for audio capture.</summary>
public static class AudioCapture
{
    /// <summary>miniaudio's version, packed major.minor.revision.</summary>
    public static Version Version
    {
        get
        {
            var packed = NativeMethods.audiocapture_version();
            return new Version((int)(packed >> 16), (int)((packed >> 8) & 0xFF), (int)(packed & 0xFF));
        }
    }

    /// <summary>
    /// The backend chosen at runtime — PulseAudio, ALSA, WASAPI, CoreAudio.
    /// Worth surfacing: when capture misbehaves on Linux, which of PulseAudio
    /// and ALSA answered is the first thing anyone needs to know.
    /// </summary>
    public static string Backend =>
        Marshal.PtrToStringUTF8(NativeMethods.audiocapture_backend()) ?? "unknown";

    /// <summary>
    /// Re-enumerate and return the capture devices. Indices are only valid
    /// until the next call, so hold the returned records rather than integers.
    /// </summary>
    public static IReadOnlyList<CaptureDeviceInfo> Devices()
    {
        var count = NativeMethods.audiocapture_refresh_devices();
        var devices = new List<CaptureDeviceInfo>(Math.Max(0, count));
        for (var i = 0; i < count; i++)
        {
            devices.Add(new CaptureDeviceInfo(
                i,
                Marshal.PtrToStringUTF8(NativeMethods.audiocapture_device_name(i)) ?? $"device {i}",
                NativeMethods.audiocapture_device_is_default(i) != 0));
        }
        return devices;
    }

    /// <summary>
    /// Open a device. Pass null for the system default. The device converts to
    /// the requested format itself, so asking for 16 kHz mono gets that
    /// regardless of what the hardware runs at.
    /// </summary>
    public static CaptureSession Open(
        CaptureDeviceInfo? device = null, int sampleRate = 16000, int channels = 1)
        => CaptureSession.Open(device?.Index ?? -1, sampleRate, channels);
}

/// <summary>
/// An open capture device. Pull-based: the native side fills a ring buffer from
/// the audio thread and <see cref="Read"/> drains it, so nothing managed runs
/// inside a real-time callback.
/// </summary>
public sealed unsafe class CaptureSession : IDisposable
{
    // Read runs on whatever thread polls -- a UI timer, typically -- while Stop
    // and Dispose run on the UI thread. Without this, a poll in flight can hand
    // a freed handle to the native side. The lock is only ever held for the
    // duration of a memcpy out of the ring buffer, so it does not serialise the
    // audio thread, which never takes it.
    private readonly Lock _gate = new();
    private IntPtr _handle;

    private CaptureSession(IntPtr handle, int sampleRate, int channels)
    {
        _handle = handle;
        SampleRate = sampleRate;
        Channels = channels;
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public bool IsRunning { get; private set; }

    internal static CaptureSession Open(int index, int sampleRate, int channels)
    {
        var error = stackalloc byte[256];
        var handle = NativeMethods.audiocapture_open(index, sampleRate, channels, error, 256);
        if (handle == IntPtr.Zero)
        {
            var message = Marshal.PtrToStringUTF8((IntPtr)error);
            throw new InvalidOperationException(
                message is { Length: > 0 } ? message : "could not open the capture device");
        }
        return new CaptureSession(handle, sampleRate, channels);
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            if (NativeMethods.audiocapture_start(_handle) == 0)
                throw new InvalidOperationException("could not start the capture device");
            IsRunning = true;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero) return;
            NativeMethods.audiocapture_stop(_handle);
            IsRunning = false;
        }
    }

    /// <summary>
    /// Drain up to <paramref name="buffer"/>'s capacity in frames. Returns the
    /// frame count written, which is often zero: this never blocks, so a caller
    /// polls rather than waits.
    /// </summary>
    public int Read(Span<float> buffer)
    {
        if (buffer.IsEmpty) return 0;

        // ma_pcm_rb is single-producer, single-consumer: the callback is the one
        // producer, and this must be the one consumer. Concurrent readers would
        // corrupt the buffer, so they are serialised rather than documented away.
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);

            var frames = buffer.Length / Channels;
            fixed (float* pointer = buffer)
            {
                return (int)NativeMethods.audiocapture_read(_handle, pointer, (nuint)frames);
            }
        }
    }

    /// <summary>
    /// Frames lost since the last call because the caller did not drain fast
    /// enough. Anything but zero means the recording has holes in it, which is
    /// worth showing rather than hiding.
    /// </summary>
    public ulong TakeOverruns()
    {
        lock (_gate)
        {
            return _handle == IntPtr.Zero ? 0 : NativeMethods.audiocapture_overruns(_handle);
        }
    }

    /// <summary>Peak absolute sample since the last call, 0..1, for a meter.</summary>
    public float TakePeak()
    {
        lock (_gate)
        {
            return _handle == IntPtr.Zero ? 0f : NativeMethods.audiocapture_peak(_handle);
        }
    }

    public void Dispose()
    {
        Close();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A forgotten Dispose would otherwise hold the microphone open for the life
    /// of the process, which on most desktops shows a recording indicator the
    /// user cannot explain.
    /// </summary>
    ~CaptureSession() => Close();

    private void Close()
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero) return;
            var handle = _handle;
            _handle = IntPtr.Zero;
            IsRunning = false;
            NativeMethods.audiocapture_close(handle);
        }
    }
}
