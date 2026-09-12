using System.Runtime.InteropServices;
using AudioCpp.Native;

namespace AudioCpp;

/// <summary>
/// What a task produced. Accessors copy on the way out, because everything the
/// ABI hands back is borrowed from this handle.
/// </summary>
public sealed class AudioCppResult : SafeHandle
{
    internal AudioCppResult(IntPtr handle, bool ownsHandle) : base(IntPtr.Zero, ownsHandle) => SetHandle(handle);

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <summary>Generated audio, or null when the task produced none.</summary>
    public AudioBuffer? Audio
    {
        get
        {
            var status = NativeMethods.audiocpp_result_audio(
                handle, out var samples, out var frames, out var sampleRate, out var channels);
            if (status == AudioCppStatus.NotAvailable) return null;
            AudioCppException.ThrowIfFailed(status, nameof(NativeMethods.audiocpp_result_audio));
            return new AudioBuffer(Copy(samples, frames, channels), sampleRate, channels);
        }
    }

    /// <summary>Transcribed text, or null when the task produced none.</summary>
    public (string Text, string Language)? Text
    {
        get
        {
            var status = NativeMethods.audiocpp_result_text(handle, out var text, out var language);
            if (status == AudioCppStatus.NotAvailable) return null;
            AudioCppException.ThrowIfFailed(status, nameof(NativeMethods.audiocpp_result_text));
            return (Utf8.ToString(text), Utf8.ToString(language));
        }
    }

    /// <summary>Detected speech spans.</summary>
    public IReadOnlyList<SpeechSegment> Segments
    {
        get
        {
            var count = (int)NativeMethods.audiocpp_result_segment_count(handle);
            var segments = new SpeechSegment[count];
            for (var i = 0; i < count; i++)
            {
                AudioCppException.ThrowIfFailed(
                    NativeMethods.audiocpp_result_segment(
                        handle, (nuint)i, out var start, out var end, out var confidence, out var text),
                    nameof(NativeMethods.audiocpp_result_segment));
                segments[i] = new SpeechSegment(start, end, confidence, Utf8.ToString(text));
            }
            return segments;
        }
    }

    /// <summary>Spans attributed to speakers.</summary>
    public IReadOnlyList<SpeakerTurn> SpeakerTurns
    {
        get
        {
            var count = (int)NativeMethods.audiocpp_result_speaker_turn_count(handle);
            var turns = new SpeakerTurn[count];
            for (var i = 0; i < count; i++)
            {
                AudioCppException.ThrowIfFailed(
                    NativeMethods.audiocpp_result_speaker_turn(
                        handle, (nuint)i, out var start, out var end, out var speaker,
                        out var confidence, out var text),
                    nameof(NativeMethods.audiocpp_result_speaker_turn));
                turns[i] = new SpeakerTurn(start, end, Utf8.ToString(speaker), confidence, Utf8.ToString(text));
            }
            return turns;
        }
    }

    /// <summary>Word-level timings, for families that report them.</summary>
    public IReadOnlyList<WordTimestamp> Words
    {
        get
        {
            var count = (int)NativeMethods.audiocpp_result_word_count(handle);
            var words = new WordTimestamp[count];
            for (var i = 0; i < count; i++)
            {
                AudioCppException.ThrowIfFailed(
                    NativeMethods.audiocpp_result_word(
                        handle, (nuint)i, out var word, out var start, out var end, out var confidence),
                    nameof(NativeMethods.audiocpp_result_word));
                words[i] = new WordTimestamp(Utf8.ToString(word), start, end, confidence);
            }
            return words;
        }
    }

    /// <summary>Named output streams, e.g. separated stems.</summary>
    public IReadOnlyList<NamedAudio> NamedAudio
    {
        get
        {
            var count = (int)NativeMethods.audiocpp_result_named_audio_count(handle);
            var streams = new NamedAudio[count];
            for (var i = 0; i < count; i++)
            {
                AudioCppException.ThrowIfFailed(
                    NativeMethods.audiocpp_result_named_audio(
                        handle, (nuint)i, out var id, out var samples, out var frames,
                        out var sampleRate, out var channels),
                    nameof(NativeMethods.audiocpp_result_named_audio));
                streams[i] = new NamedAudio(
                    Utf8.ToString(id), Copy(samples, frames, channels), sampleRate, channels);
            }
            return streams;
        }
    }

    /// <summary>Opaque payloads the task produced.</summary>
    public IReadOnlyList<ResultArtifact> Artifacts
    {
        get
        {
            var count = (int)NativeMethods.audiocpp_result_artifact_count(handle);
            var artifacts = new ResultArtifact[count];
            for (var i = 0; i < count; i++)
            {
                AudioCppException.ThrowIfFailed(
                    NativeMethods.audiocpp_result_artifact(
                        handle, (nuint)i, out var kind, out var id, out var payload, out var bytes),
                    nameof(NativeMethods.audiocpp_result_artifact));
                var copied = new byte[(int)bytes];
                if (bytes > 0 && payload != IntPtr.Zero) Marshal.Copy(payload, copied, 0, copied.Length);

                var metaCount = (int)NativeMethods.audiocpp_result_artifact_meta_count(handle, (nuint)i);
                var metadata = new Dictionary<string, string>(metaCount);
                for (var m = 0; m < metaCount; m++)
                {
                    AudioCppException.ThrowIfFailed(
                        NativeMethods.audiocpp_result_artifact_meta(
                            handle, (nuint)i, (nuint)m, out var key, out var value),
                        nameof(NativeMethods.audiocpp_result_artifact_meta));
                    metadata[Utf8.ToString(key)] = Utf8.ToString(value);
                }
                artifacts[i] = new ResultArtifact(kind, Utf8.ToString(id), copied, metadata);
            }
            return artifacts;
        }
    }

    private static float[] Copy(IntPtr samples, nuint frames, int channels)
    {
        var total = checked((int)frames * Math.Max(channels, 1));
        var copy = new float[total];
        if (total > 0 && samples != IntPtr.Zero) Marshal.Copy(samples, copy, 0, total);
        return copy;
    }

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        NativeMethods.audiocpp_result_free(handle);
        return true;
    }
}
