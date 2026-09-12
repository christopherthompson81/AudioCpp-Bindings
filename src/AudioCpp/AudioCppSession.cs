using System.Runtime.InteropServices;
using AudioCpp.Native;

namespace AudioCpp;

/// <summary>
/// A task session. Keeping one alive across requests — reusing graphs, caches
/// and warm allocations — is the reason to embed rather than shell out.
/// </summary>
/// <remarks>
/// Holds its model alive, which in turn holds the registry, so the three may be
/// disposed in any order. That matters in .NET specifically: finalizer order is
/// not deterministic, so an API that required an order would be unusable
/// without careful manual disposal everywhere.
/// </remarks>
public sealed class AudioCppSession : SafeHandle
{
    private readonly AudioCppModel _model;

    private AudioCppSession(IntPtr handle, AudioCppModel model) : base(IntPtr.Zero, ownsHandle: true)
    {
        _model = model;
        SetHandle(handle);
    }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    internal static unsafe AudioCppSession Create(
        AudioCppModel model, string task, string mode, BackendConfig backend, AudioCppOptions? options)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(mode);

        // default(BackendConfig) bypasses the record's parameter defaults, so a
        // caller who omits it would otherwise send a null backend and 0 threads,
        // which the ABI rejects.
        var backendName = string.IsNullOrEmpty(backend.Backend) ? "cpu" : backend.Backend;
        var threads = backend.Threads > 0 ? backend.Threads : 1;

        var nativeBackend = new NativeBackendConfig
        {
            Backend = Utf8.Allocate(backendName),
            Device = backend.Device,
            Threads = threads,
        };
        try
        {
            AudioCppException.ThrowIfFailed(
                NativeMethods.audiocpp_session_create(
                    model.DangerousHandle, task, mode, &nativeBackend,
                    options?.DangerousHandle ?? IntPtr.Zero, out var created),
                nameof(NativeMethods.audiocpp_session_create));
            return new AudioCppSession(created, model);
        }
        finally
        {
            Utf8.Free(nativeBackend.Backend);
        }
    }

    /// <summary>The family backing this session.</summary>
    public string Family => Utf8.ToString(NativeMethods.audiocpp_session_family(handle));

    /// <summary>
    /// Forces allocation ahead of time. <see cref="Run"/> prepares again with
    /// the request it is about to run, because preparation carries the input
    /// length, so pass something representative of what will follow.
    /// </summary>
    public void Prepare(AudioCppRequest? request = null)
    {
        AudioCppException.ThrowIfFailed(
            NativeMethods.audiocpp_session_prepare(handle, request?.DangerousHandle ?? IntPtr.Zero),
            nameof(NativeMethods.audiocpp_session_prepare));
    }

    /// <summary>Runs one offline request.</summary>
    public AudioCppResult Run(AudioCppRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        AudioCppException.ThrowIfFailed(
            NativeMethods.audiocpp_session_run(handle, request.DangerousHandle, out var created),
            nameof(NativeMethods.audiocpp_session_run));
        return new AudioCppResult(created, ownsHandle: true);
    }

    // ---- streaming -------------------------------------------------------

    /// <summary>What this session wants to be fed, including its preferred chunk size.</summary>
    public StreamPolicy GetStreamPolicy()
    {
        AudioCppException.ThrowIfFailed(
            NativeMethods.audiocpp_stream_policy(
                handle, out var input, out var output, out var chunkSamples, out var chunkSeconds),
            nameof(NativeMethods.audiocpp_stream_policy));
        return new StreamPolicy(input, output, chunkSamples, chunkSeconds);
    }

    /// <summary>Starts a stream, resetting anything already in flight.</summary>
    public void StartStream(AudioCppRequest? request = null)
    {
        AudioCppException.ThrowIfFailed(
            NativeMethods.audiocpp_stream_start(handle, request?.DangerousHandle ?? IntPtr.Zero),
            nameof(NativeMethods.audiocpp_stream_start));
    }

    /// <summary>Feeds one chunk and returns whatever event it produced.</summary>
    public unsafe AudioCppStreamEvent? PushStream(
        ReadOnlySpan<float> samples, int sampleRate, int channels, long startSample)
    {
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        var frames = (nuint)(samples.Length / channels);
        fixed (float* pinned = samples)
        {
            AudioCppException.ThrowIfFailed(
                NativeMethods.audiocpp_stream_push(
                    handle, pinned, frames, sampleRate, channels, startSample, out var created),
                nameof(NativeMethods.audiocpp_stream_push));
            return created == IntPtr.Zero ? null : new AudioCppStreamEvent(created);
        }
    }

    /// <summary>
    /// Drains an event the family queued itself. Null means the queue is empty,
    /// which is a normal outcome rather than an error.
    /// </summary>
    public AudioCppStreamEvent? NextStreamEvent()
    {
        AudioCppException.ThrowIfFailed(
            NativeMethods.audiocpp_stream_next_event(handle, out var created),
            nameof(NativeMethods.audiocpp_stream_next_event));
        return created == IntPtr.Zero ? null : new AudioCppStreamEvent(created);
    }

    /// <summary>Ends the stream and returns the final result.</summary>
    public AudioCppResult FinishStream()
    {
        AudioCppException.ThrowIfFailed(
            NativeMethods.audiocpp_stream_finish(handle, out var created),
            nameof(NativeMethods.audiocpp_stream_finish));
        return new AudioCppResult(created, ownsHandle: true);
    }

    /// <summary>Discards streaming state.</summary>
    public void ResetStream()
    {
        AudioCppException.ThrowIfFailed(
            NativeMethods.audiocpp_stream_reset(handle), nameof(NativeMethods.audiocpp_stream_reset));
    }

    internal AudioCppModel Model => _model;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        NativeMethods.audiocpp_session_free(handle);
        return true;
    }
}
