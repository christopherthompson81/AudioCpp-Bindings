using System.Diagnostics;

namespace AudioCpp.Bindings.Gui;

/// <summary>Packed RGB24 frames, as a video-producing family reports them.</summary>
public sealed record VideoFormat(int Width, int Height, int Fps)
{
    public int FrameBytes => Width * Height * 3;
}

/// <summary>
/// Turn raw frames into a file a player opens.
/// </summary>
/// <remarks>
/// Upstream's web UI does this in the browser with WebCodecs, muxing in the
/// driving audio. There is no encoder in .NET or Avalonia, so this hands the
/// frames to ffmpeg when it is on the PATH -- the same division of labour as
/// the web UI's, with the encoder outside the app. Without ffmpeg the frames
/// are still written, raw, next to where the video was asked for, with the
/// command that would encode them; losing the output of a long GPU run because
/// a tool is missing would be the worse failure.
/// </remarks>
public static class VideoExport
{
    /// <summary>Write <paramref name="frames"/> to <paramref name="path"/>; returns a status line.</summary>
    public static async Task<string> SaveAsync(
        byte[] frames, VideoFormat format, string? audioPath, string path)
    {
        var count = frames.Length / format.FrameBytes;
        if (count == 0 || frames.Length % format.FrameBytes != 0)
        {
            throw new IOException(
                $"video payload is {frames.Length} bytes, not whole {format.Width}x{format.Height} RGB24 frames");
        }

        var withAudio = audioPath is { Length: > 0 } && File.Exists(audioPath);
        var arguments = new List<string>
        {
            "-y", "-loglevel", "error",
            "-f", "rawvideo", "-pix_fmt", "rgb24",
            "-s", $"{format.Width}x{format.Height}", "-r", format.Fps.ToString(),
            "-i", "pipe:0",
        };
        if (withAudio) arguments.AddRange(["-i", audioPath!]);
        // yuv420p is what players expect, and it needs even dimensions.
        arguments.AddRange(["-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-c:v", "libx264", "-pix_fmt", "yuv420p"]);
        // The clip is generated to the recording's length but in whole frames,
        // so the two differ by a fraction of a frame; the picture decides.
        if (withAudio) arguments.AddRange(["-c:a", "aac", "-shortest"]);
        arguments.Add(path);

        var start = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            process = null;
        }
        if (process is null) return await SaveRawAsync(frames, format, path);

        using (process)
        {
            // Drained while writing: ffmpeg blocks on a full stderr pipe, and
            // this would then block on its full stdin.
            var errors = process.StandardError.ReadToEndAsync();
            try
            {
                await process.StandardInput.BaseStream.WriteAsync(frames);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // ffmpeg exited early; its own message below says why.
            }
            await process.WaitForExitAsync();
            var message = (await errors).Trim();
            if (process.ExitCode != 0)
            {
                throw new IOException($"ffmpeg failed ({process.ExitCode}): {message}");
            }
        }

        return $"Wrote {path} — {count} frames at {format.Fps} fps"
               + (withAudio ? ", with the source audio" : ", no audio track");
    }

    private static async Task<string> SaveRawAsync(byte[] frames, VideoFormat format, string path)
    {
        var raw = Path.ChangeExtension(path, ".rgb");
        await File.WriteAllBytesAsync(raw, frames);
        return $"ffmpeg is not on the PATH, so the frames were written raw to {raw}. "
               + $"Encode with: ffmpeg -f rawvideo -pix_fmt rgb24 -s {format.Width}x{format.Height} "
               + $"-r {format.Fps} -i \"{raw}\" -pix_fmt yuv420p \"{path}\"";
    }
}
