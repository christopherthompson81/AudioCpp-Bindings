namespace AudioCpp.Audio;

/// <summary>
/// Plays a clip held in memory, with pause and seek: a preview player rather
/// than a media pipeline.
/// </summary>
/// <remarks>
/// The clip is copied into native memory on open. The managed array belongs to
/// a garbage collector that may move it, and the audio thread reads it with no
/// knowledge of that; pinning for the life of a preview would be worse than one
/// copy.
///
/// The output device is started once and left running. Starting a device costs
/// tens of milliseconds, which is audible as a clipped syllable at the head of
/// every play, so pausing outputs silence instead.
/// </remarks>
public sealed unsafe class AudioPlayer : IDisposable
{
    private readonly Lock _gate = new();
    private IntPtr _handle;

    private AudioPlayer(IntPtr handle, int sampleRate, int channels)
    {
        _handle = handle;
        SampleRate = sampleRate;
        Channels = channels;
    }

    public int SampleRate { get; }
    public int Channels { get; }

    public static AudioPlayer Open(ReadOnlySpan<float> samples, int sampleRate, int channels = 1)
    {
        if (samples.IsEmpty) throw new ArgumentException("nothing to play", nameof(samples));

        var error = stackalloc byte[256];
        IntPtr handle;
        fixed (float* pointer = samples)
        {
            handle = NativeMethods.audioio_player_open(
                pointer, (nuint)(samples.Length / channels), sampleRate, channels, error, 256);
        }
        if (handle == IntPtr.Zero)
        {
            var message = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)error);
            throw new InvalidOperationException(
                message is { Length: > 0 } ? message : "could not open the output device");
        }
        return new AudioPlayer(handle, sampleRate, channels);
    }

    /// <summary>Total frames; divide by <see cref="SampleRate"/> for seconds.</summary>
    public long Length
    {
        get { lock (_gate) { return _handle == IntPtr.Zero ? 0 : (long)NativeMethods.audioio_player_length(_handle); } }
    }

    /// <summary>Playhead in frames. Setting it seeks; past the end clamps.</summary>
    public long Position
    {
        get { lock (_gate) { return _handle == IntPtr.Zero ? 0 : (long)NativeMethods.audioio_player_position(_handle); } }
        set { lock (_gate) { if (_handle != IntPtr.Zero) NativeMethods.audioio_player_seek(_handle, (nuint)Math.Max(0, value)); } }
    }

    public bool IsPlaying
    {
        get { lock (_gate) { return _handle != IntPtr.Zero && NativeMethods.audioio_player_is_playing(_handle) != 0; } }
    }

    /// <summary>Play from the current position, restarting if it sits at the end.</summary>
    public void Play()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            NativeMethods.audioio_player_play(_handle);
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_handle != IntPtr.Zero) NativeMethods.audioio_player_pause(_handle);
        }
    }

    /// <summary>Seconds, for a caller that thinks in time rather than frames.</summary>
    public double PositionSeconds
    {
        get => SampleRate > 0 ? Position / (double)SampleRate : 0;
        set => Position = (long)(value * SampleRate);
    }

    public double LengthSeconds => SampleRate > 0 ? Length / (double)SampleRate : 0;

    public void Dispose()
    {
        Close();
        GC.SuppressFinalize(this);
    }

    /// <summary>A forgotten Dispose would hold an output device open for the process's life.</summary>
    ~AudioPlayer() => Close();

    private void Close()
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero) return;
            var handle = _handle;
            _handle = IntPtr.Zero;
            NativeMethods.audioio_player_close(handle);
        }
    }
}
