using System.Text.Json.Serialization;
using AudioCpp;
using AudioCpp.Native;

namespace AudioCpp.Server;

/// <summary>
/// Everything a task can produce, captured while the result handle is still
/// open.
/// </summary>
/// <remarks>
/// A snapshot rather than a view: the native result is disposed when the run
/// leaves the model's gate, so anything read after that point is read from
/// freed memory. Copying eagerly is what makes the route's JSON safe to build
/// outside the lock, and it is why every list here is materialised.
/// </remarks>
internal sealed record Ran(
    string Text,
    string Language,
    AudioBuffer? Audio,
    IReadOnlyList<NamedAudio> NamedAudio,
    IReadOnlyList<ResultArtifact> Artifacts,
    IReadOnlyList<SpeechSegment> Segments,
    IReadOnlyList<SpeakerTurn> SpeakerTurns,
    IReadOnlyList<WordTimestamp> Words)
{
    public static Ran From(AudioCppResult output) => new(
        output.Text?.Text ?? "",
        output.Text?.Language ?? "",
        output.Audio,
        [.. output.NamedAudio],
        [.. output.Artifacts],
        [.. output.Segments],
        [.. output.SpeakerTurns],
        [.. output.Words]);

    /// <summary>
    /// The generic run's response, carrying only what the model produced.
    /// </summary>
    /// <remarks>
    /// Fields appear conditionally because this route serves every task: a
    /// fixed schema would mean every TTS response carrying empty word arrays
    /// and every transcription carrying a null audio field, and a client cannot
    /// tell an absent capability from an empty result once that happens.
    ///
    /// Audio is base64 WAV rather than raw samples, which is upstream's choice
    /// and the one that survives JSON — a float array of a minute of 48 kHz
    /// stereo is tens of megabytes of decimal text.
    /// </remarks>
    public Dictionary<string, object?> ToJson(double wallMs)
    {
        var payload = new Dictionary<string, object?>();
        if (Text.Length > 0)
        {
            payload["text"] = Text;
            if (Language.Length > 0) payload["language"] = Language;
        }
        if (Audio is { } audio)
        {
            payload["audio"] = Convert.ToBase64String(
                Wav.ToBytes(audio.Samples, audio.SampleRate, audio.Channels));
            payload["sample_rate"] = audio.SampleRate;
            payload["channels"] = audio.Channels;
        }
        if (NamedAudio.Count > 0)
        {
            payload["named_audio_outputs"] = NamedAudio.Select(named => new
            {
                id = named.Id,
                audio = Convert.ToBase64String(
                    Wav.ToBytes(named.Samples, named.SampleRate, named.Channels)),
                sample_rate = named.SampleRate,
                channels = named.Channels,
            }).ToArray();
        }
        if (Artifacts.Count > 0)
        {
            payload["artifacts"] = Artifacts.Select(artifact => new
            {
                id = artifact.Id,
                kind = KindName(artifact.Kind),
                payload = Convert.ToBase64String(artifact.Payload),
                meta = artifact.Metadata,
            }).ToArray();
        }
        if (Segments.Count > 0)
        {
            payload["segments"] = Segments.Select(segment => new
            {
                start_sample = segment.StartSample,
                end_sample = segment.EndSample,
                confidence = segment.Confidence,
                text = segment.Text,
            }).ToArray();
        }
        if (SpeakerTurns.Count > 0)
        {
            payload["speaker_turns"] = SpeakerTurns.Select(turn => new
            {
                start_sample = turn.StartSample,
                end_sample = turn.EndSample,
                speaker_id = turn.SpeakerId,
                confidence = turn.Confidence,
            }).ToArray();
        }
        if (Words.Count > 0)
        {
            payload["words"] = Words.Select(word => new
            {
                word = word.Word,
                start_sample = word.StartSample,
                end_sample = word.EndSample,
                confidence = word.Confidence,
            }).ToArray();
        }

        // The rate the offsets above are counted in, present only when there
        // are offsets to count. On this route it can also come from the output
        // audio, which is the same number for a model that produced both.
        if (Segments.Count > 0 || SpeakerTurns.Count > 0 || Words.Count > 0)
        {
            payload["sample_rate"] = Audio?.SampleRate ?? 0;
        }

        var seconds = Audio?.Duration
                      ?? (NamedAudio.Count == 1
                          ? (double)NamedAudio[0].Samples.Length
                            / Math.Max(NamedAudio[0].Channels, 1)
                            / Math.Max(NamedAudio[0].SampleRate, 1)
                          : 0.0);
        payload["timing"] = seconds > 0
            ? new Dictionary<string, object?>
            {
                ["wall_ms"] = Math.Round(wallMs, 1),
                ["audio_duration_ms"] = Math.Round(seconds * 1000.0, 1),
                ["rtf"] = Math.Round(wallMs / 1000.0 / seconds, 4),
            }
            : new Dictionary<string, object?> { ["wall_ms"] = Math.Round(wallMs, 1) };
        return payload;
    }

    /// <summary>
    /// Artifact kinds by their wire name, which is the spelling upstream emits
    /// (app/workflow/file_sink.cpp).
    /// </summary>
    /// <remarks>
    /// Spelled out rather than derived from the enum name, because the two only
    /// happen to agree: a derived name would silently invent a new wire value
    /// the day an enum member is named something a snake_case rule does not
    /// reproduce, and the client parsing it would see an unknown kind.
    /// </remarks>
    private static string KindName(AudioCppArtifactKind kind) => kind switch
    {
        AudioCppArtifactKind.SpeakerEmbedding => "speaker_embedding",
        AudioCppArtifactKind.StyleEmbedding => "style_embedding",
        AudioCppArtifactKind.PromptEmbedding => "prompt_embedding",
        AudioCppArtifactKind.AcousticTokens => "acoustic_tokens",
        AudioCppArtifactKind.Midi => "midi",
        AudioCppArtifactKind.TranscriptAlignment => "transcript_alignment",
        AudioCppArtifactKind.DiarizationState => "diarization_state",
        AudioCppArtifactKind.VadState => "vad_state",
        _ => "custom",
    };
}
