using AudioCpp.Audio;

namespace AudioCpp.CaptureTest;

/// <summary>
/// Exercises capture against whatever hardware is present. Needs a machine with
/// an audio backend, so it skips rather than fails when there is none.
///
/// Exit codes follow CTest: 0 pass, 1 fail, 77 skip.
/// </summary>
internal static class Program
{
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
            Console.WriteLine("libaudiocapture not found; skipping.");
            Console.WriteLine("Build native/audiocapture, or set AUDIOCAPTURE_NATIVE_DIR.");
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

        if (failures > 0) { Console.Error.WriteLine($"{failures} problem(s)"); return 1; }
        Console.WriteLine("capture OK");
        return 0;
    }
}
