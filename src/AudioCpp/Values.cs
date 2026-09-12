using AudioCpp.Native;

namespace AudioCpp;

/// <summary>Backend selection for a session. Mirrors <c>--backend/--device/--threads</c>.</summary>
/// <param name="Backend">"cpu", "cuda", "hip"/"rocm", "vulkan", "metal" or "best".</param>
/// <param name="Threads">
/// The library never calls <c>omp_set_num_threads</c> — that is process-global
/// state — so this is the only place thread count is set.
/// </param>
public readonly record struct BackendConfig(string Backend = "cpu", int Device = 0, int Threads = 1);

/// <summary>Model selection. Mirrors <c>--family/--config/--weight/--model-spec-override</c>.</summary>
public readonly record struct ModelConfig(
    string? FamilyHint = null,
    string? ConfigId = null,
    string? WeightId = null,
    string? ModelSpecOverride = null);

/// <summary>One declared option, as the model describes itself at runtime.</summary>
public readonly record struct ModelOption(
    string Name,
    string ValueName,
    string Description,
    string DefaultValue,
    string MinValue,
    string MaxValue,
    bool Required);

/// <summary>A detected span of speech.</summary>
public readonly record struct SpeechSegment(long StartSample, long EndSample, float Confidence, string Text);

/// <summary>A span attributed to one speaker.</summary>
public readonly record struct SpeakerTurn(
    long StartSample, long EndSample, string SpeakerId, float Confidence, string Text);

/// <summary>A word with its span.</summary>
public readonly record struct WordTimestamp(string Word, long StartSample, long EndSample, float Confidence);

/// <summary>One named output stream, for families that emit more than one.</summary>
public readonly record struct NamedAudio(string Id, float[] Samples, int SampleRate, int Channels)
{
    /// <summary>Frames per channel.</summary>
    public int Frames => Channels > 0 ? Samples.Length / Channels : 0;

    /// <summary>Duration in seconds.</summary>
    public double Duration => SampleRate > 0 ? (double)Frames / SampleRate : 0.0;
}

/// <summary>An opaque payload a family produced.</summary>
public readonly record struct ResultArtifact(
    AudioCppArtifactKind Kind,
    string Id,
    byte[] Payload,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>What a streaming session wants to be fed.</summary>
public readonly record struct StreamPolicy(
    AudioCppStreamInputKind Input,
    AudioCppStreamOutputKind Output,
    long PreferredChunkSamples,
    double PreferredChunkSeconds);

/// <summary>A voice-activity transition reported during streaming.</summary>
public readonly record struct VoiceActivityEvent(
    AudioCppVoiceActivityKind Kind, long Sample, float Probability);

/// <summary>Interleaved PCM handed back by a task.</summary>
public readonly record struct AudioBuffer(float[] Samples, int SampleRate, int Channels)
{
    /// <summary>Frames per channel.</summary>
    public int Frames => Channels > 0 ? Samples.Length / Channels : 0;

    /// <summary>Duration in seconds.</summary>
    public double Duration => SampleRate > 0 ? (double)Frames / SampleRate : 0.0;
}
