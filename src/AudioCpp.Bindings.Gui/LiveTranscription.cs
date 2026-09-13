using AudioCpp;
using AudioCpp.Audio;

namespace AudioCpp.Bindings.Gui;

/// <summary>
/// Microphone in, transcript out: owns a capture device and a streaming session
/// and pumps one into the other on a background thread.
/// </summary>
/// <remarks>
/// Kept apart from the view model because the pump has real-time-ish duties —
/// drain the ring buffer often enough that it does not overrun — and mixing
/// that with UI state invites doing one on the other's thread. Everything here
/// reports through callbacks the caller marshals.
///
/// Parakeet's streaming is buffered rather than cache-aware, so text arrives in
/// windows rather than word by word. The events do carry partial text, which is
/// what makes a live transcript possible at all.
/// </remarks>
public sealed class LiveTranscription : IDisposable
{
    /// <summary>
    /// The rate everything here runs at. Parakeet's streaming contract rejects
    /// anything else, and word offsets in the result are in these frames, so
    /// one constant serves both the device and anyone converting offsets to
    /// time.
    /// </summary>
    public const int SampleRate = 16000;

    private readonly AudioCppSession _session;
    private readonly CaptureSession _capture;
    private readonly CancellationTokenSource _stop = new();
    private Task? _pump;

    private LiveTranscription(AudioCppSession session, CaptureSession capture)
    {
        _session = session;
        _capture = capture;
    }

    /// <summary>Partial or final text as the engine produces it.</summary>
    public Action<string>? OnText { get; set; }

    /// <summary>Peak level 0..1 and frames dropped, for a meter and a warning.</summary>
    public Action<float, ulong>? OnLevel { get; set; }

    public Action<Exception>? OnError { get; set; }

    /// <summary>
    /// Open the device and start the session. The engine dictates the format:
    /// Parakeet's streaming contract is mono 16 kHz and it rejects anything
    /// else, so the device is asked to convert rather than the caller resampling.
    /// </summary>
    public static LiveTranscription Start(
        AudioCppModel model, string backend, int threads, CaptureDeviceInfo? device)
    {
        var session = model.CreateSession("asr", "streaming", new BackendConfig(backend, 0, threads));
        CaptureSession capture;
        try
        {
            capture = AudioCapture.Open(device, sampleRate: SampleRate, channels: 1);
        }
        catch
        {
            session.Dispose();
            throw;
        }

        var live = new LiveTranscription(session, capture);
        try
        {
            // prepare() wants an audio contract -- the format, not the audio. A
            // one-frame buffer declares 16 kHz mono without pretending to send
            // a clip that does not exist yet.
            using var contract = new AudioCppRequest();
            contract.SetAudio(new float[1], SampleRate, 1);
            session.StartStream(contract);

            capture.Start();
            live._pump = Task.Run(live.PumpAsync);
            return live;
        }
        catch
        {
            live.Dispose();
            throw;
        }
    }

    private async Task PumpAsync()
    {
        // The engine names its preferred window; pushing in that size avoids it
        // re-buffering. Falls back to a second when it declines to say.
        var policy = _session.GetStreamPolicy();
        var chunk = policy.PreferredChunkSamples > 0 ? (int)policy.PreferredChunkSamples : SampleRate;

        var pending = new float[chunk];
        var filled = 0;
        var scratch = new float[chunk];
        var sample = 0L;

        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var frames = _capture.Read(scratch.AsSpan(0, chunk - filled));
                if (frames == 0)
                {
                    // Poll rather than spin: a quarter of the window is frequent
                    // enough to keep a one-second ring buffer from overrunning.
                    await Task.Delay(Math.Max(10, chunk / SampleRate * 250 / 4), _stop.Token);
                    OnLevel?.Invoke(_capture.TakePeak(), _capture.TakeOverruns());
                    continue;
                }

                Array.Copy(scratch, 0, pending, filled, frames);
                filled += frames;
                if (filled < chunk) continue;

                var evt = _session.PushStream(pending.AsSpan(0, filled), SampleRate, 1, sample);
                sample += filled;
                filled = 0;

                Emit(evt);
                while (_session.NextStreamEvent() is { } more) Emit(more);

                OnLevel?.Invoke(_capture.TakePeak(), _capture.TakeOverruns());
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping is not an error.
        }
        catch (Exception exception)
        {
            OnError?.Invoke(exception);
        }
    }

    private void Emit(AudioCppStreamEvent? evt)
    {
        if (evt is null) return;
        using (evt)
        {
            if (evt.Result.Text is { } text && text.Text.Length > 0) OnText?.Invoke(text.Text);
        }
    }

    /// <summary>
    /// Stop capture and drain what the engine still holds. Returns the final
    /// transcript, which is the one worth keeping: the partials are windows.
    /// </summary>
    public async Task<string> StopAsync()
    {
        await _stop.CancelAsync();
        if (_pump is not null)
        {
            try { await _pump; } catch (OperationCanceledException) { }
        }

        _capture.Stop();
        try
        {
            using var result = _session.FinishStream();
            return result.Text?.Text ?? "";
        }
        catch (AudioCppException)
        {
            // A stream stopped before it saw audio has nothing to finish.
            return "";
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _pump?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _capture.Dispose();
        _session.Dispose();
        _stop.Dispose();
    }
}
