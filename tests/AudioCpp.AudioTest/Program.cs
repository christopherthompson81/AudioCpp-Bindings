using AudioCpp.Audio;

namespace AudioCpp.AudioTest;

/// <summary>
/// Exercises capture against whatever hardware is present. Needs a machine with
/// an audio backend, so it skips rather than fails when there is none.
///
/// Exit codes follow CTest: 0 pass, 1 fail, 77 skip.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Hammer the paths the UI will overlap: enumeration on one thread, open and
    /// read on others, dispose landing mid-read.
    /// </summary>
    /// <remarks>
    /// This is a smoke test, not a proof. Removing the lock inside Read and
    /// running 250 rounds with eight readers still passed, because the window
    /// between the disposed check and the native call is nanoseconds and freed
    /// memory stays mapped. The lock is kept because ma_pcm_rb is documented
    /// single-producer/single-consumer and concurrent readers corrupt it
    /// silently rather than crashing — which is exactly the failure a test like
    /// this cannot see.
    /// </remarks>
    private static int ConcurrencyCheck()
    {
        const int rounds = 60;
        Console.WriteLine($"concurrency: {rounds} rounds of reads racing dispose");
        var failures = 0;

        for (var round = 0; round < rounds; round++)
        {
            CaptureSession session;
            try { session = AudioCapture.Open(); }
            catch (InvalidOperationException) { continue; }   // device busy; not our failure

            session.Start();

            using var stop = new CancellationTokenSource();
            var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                var buffer = new float[1024];
                while (!stop.Token.IsCancellationRequested)
                {
                    try { session.Read(buffer); session.TakePeak(); session.TakeOverruns(); }
                    catch (ObjectDisposedException) { return; }   // expected once disposed
                }
            })).ToArray();

            var enumerator = Task.Run(() =>
            {
                while (!stop.Token.IsCancellationRequested) AudioCapture.Devices();
            });

            Thread.Sleep(Random.Shared.Next(0, 4));
            session.Dispose();          // lands while the readers are in flight
            stop.Cancel();

            try { Task.WaitAll([.. readers, enumerator], TimeSpan.FromSeconds(5)); }
            catch (AggregateException error)
            {
                foreach (var inner in error.InnerExceptions)
                {
                    if (inner is ObjectDisposedException) continue;
                    Console.Error.WriteLine($"  round {round}: {inner.GetType().Name}: {inner.Message}");
                    failures++;
                }
            }
        }

        Console.WriteLine(failures == 0 ? "concurrency OK" : $"concurrency: {failures} failure(s)");
        return failures;
    }

    /// <summary>
    /// Playback, pause and seek against a generated tone. The transport is
    /// checkable headlessly because position lives in native code; only the
    /// playhead *binding* needs a dispatcher, which is what --live-check covers.
    /// </summary>
    private static int PlaybackCheck()
    {
        Console.WriteLine("playback: tone, pause, seek");
        var failures = 0;

        const int rate = 16000;
        var tone = new float[rate];
        for (var i = 0; i < tone.Length; i++)
        {
            tone[i] = 0.25f * MathF.Sin(2f * MathF.PI * 440f * i / rate);
        }

        AudioPlayer player;
        try { player = AudioPlayer.Open(tone, rate); }
        catch (InvalidOperationException error)
        {
            // No output device is legitimate on a headless box.
            Console.WriteLine($"  no output device ({error.Message}); skipping playback");
            return 0;
        }

        using (player)
        {
            if (player.Length != rate)
            {
                Console.Error.WriteLine($"  length {player.Length} != {rate}");
                failures++;
            }

            player.Play();
            Thread.Sleep(300);
            var advanced = player.Position;
            if (advanced <= 0)
            {
                Console.Error.WriteLine("  position did not advance while playing");
                failures++;
            }

            // The guarantee is that nothing moves *after* pause returns. It is
            // not that the position still reads whatever it read a moment ago:
            // the clip was playing in between, so it may legitimately have
            // advanced. Comparing against the earlier reading is what made this
            // check fail once in a dozen runs, and only under load.
            player.Pause();
            var paused = player.Position;
            if (paused < advanced)
            {
                Console.Error.WriteLine($"  pause went backwards: {advanced} -> {paused}");
                failures++;
            }

            Thread.Sleep(200);
            if (player.Position != paused)
            {
                Console.Error.WriteLine($"  paused playback advanced: {paused} -> {player.Position}");
                failures++;
            }

            // Resuming carries on from the pause rather than restarting or
            // rewinding.
            player.Play();
            player.Pause();
            if (player.Position < paused)
            {
                Console.Error.WriteLine($"  resume went backwards: {paused} -> {player.Position}");
                failures++;
            }

            player.Position = rate / 2;
            if (player.Position != rate / 2)
            {
                Console.Error.WriteLine($"  seek landed at {player.Position}, wanted {rate / 2}");
                failures++;
            }

            // Seeking past the end must clamp rather than run off the buffer.
            player.Position = rate * 10;
            if (player.Position != rate)
            {
                Console.Error.WriteLine($"  seek past the end gave {player.Position}, wanted {rate}");
                failures++;
            }

            // Playing from the end restarts rather than sitting silent.
            player.Play();
            Thread.Sleep(150);
            if (player.Position >= rate)
            {
                Console.Error.WriteLine("  play at the end did not restart");
                failures++;
            }

            Console.WriteLine($"  advanced to {advanced}, seek and clamp behaved");
        }

        Console.WriteLine(failures == 0 ? "playback OK" : $"playback: {failures} failure(s)");
        return failures;
    }

    private static int Main(string[] args)
    {
        int backendOk;
        try
        {
            Console.WriteLine($"miniaudio {AudioCapture.Version}  backend: {AudioCapture.Backend}");
            backendOk = AudioCapture.Backend is not ("none" or "unknown") ? 1 : 0;
        }
        catch (DllNotFoundException)
        {
            Console.WriteLine("libaudioio not found; skipping.");
            Console.WriteLine("Build native/audioio, or set AUDIOIO_NATIVE_DIR.");
            return 77;
        }

        if (backendOk == 0)
        {
            Console.WriteLine("no audio backend on this machine; skipping.");
            return 77;
        }

        var devices = AudioCapture.Devices();
        Console.WriteLine($"capture devices: {devices.Count}");
        foreach (var device in devices.Take(8)) Console.WriteLine($"  [{device.Index}] {device}");

        if (devices.Count == 0)
        {
            Console.WriteLine("no capture devices; skipping.");
            return 77;
        }

        var failures = 0;
        var index = args.Length > 0 && int.TryParse(args[0], out var chosen) ? chosen : -1;
        var selected = index >= 0 ? devices.FirstOrDefault(d => d.Index == index) : (CaptureDeviceInfo?)null;

        using var session = AudioCapture.Open(selected);
        Console.WriteLine($"opened at {session.SampleRate} Hz, {session.Channels}ch");
        session.Start();

        // Two seconds at 16 kHz mono is 32000 frames. Polling at 100ms is what a
        // UI would do; anything much coarser and the ring buffer overruns.
        var buffer = new float[session.SampleRate];
        var total = 0;
        double sumSquares = 0;
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(100);
            var frames = session.Read(buffer);
            for (var i = 0; i < frames; i++) sumSquares += buffer[i] * (double)buffer[i];
            total += frames;
        }
        session.Stop();

        var overruns = session.TakeOverruns();
        var rms = total > 0 ? Math.Sqrt(sumSquares / total) : 0;
        Console.WriteLine($"captured {total} frames (expect ~{session.SampleRate * 2}), "
                          + $"rms {rms:F6}, overruns {overruns}");

        // Frame count proves the device and the rate conversion; signal level
        // does not, because a muted or silent input is legitimate.
        var expected = session.SampleRate * 2;
        if (total < expected * 0.8)
        {
            Console.Error.WriteLine($"  too few frames: {total} < {expected * 0.8:F0}");
            failures++;
        }
        if (overruns > 0)
        {
            Console.Error.WriteLine($"  {overruns} frames dropped while polling at 100ms");
            failures++;
        }

        if (args.Contains("--expect-signal") && rms <= 0)
        {
            Console.Error.WriteLine("  expected a signal but captured silence");
            failures++;
        }

        // The UI polls Read on a timer while Stop and Dispose run on the UI
        // thread, so those paths must survive overlapping. Without the gate
        // this hands a freed handle to the native side.
        failures += ConcurrencyCheck();
        failures += PlaybackCheck();

        if (failures > 0) { Console.Error.WriteLine($"{failures} problem(s)"); return 1; }
        Console.WriteLine("capture OK");
        return 0;
    }
}
