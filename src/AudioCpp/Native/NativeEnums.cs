namespace AudioCpp.Native;

/// <summary>Mirrors <c>audiocpp_status</c>.</summary>
public enum AudioCppStatus
{
    Ok = 0,
    InvalidArgument = 1,
    UnsupportedFamily = 2,
    LoadFailed = 3,
    Runtime = 4,
    OutOfMemory = 5,
    OutOfRange = 6,
    NotAvailable = 7,
}

/// <summary>Which of a model's option maps a declaration belongs to.</summary>
public enum AudioCppOptionScope
{
    Request = 0,
    Session = 1,
    Load = 2,
}

/// <summary>Mirrors <c>audiocpp_artifact_kind</c>.</summary>
public enum AudioCppArtifactKind
{
    SpeakerEmbedding = 0,
    StyleEmbedding = 1,
    PromptEmbedding = 2,
    AcousticTokens = 3,
    Midi = 4,
    TranscriptAlignment = 5,
    DiarizationState = 6,
    VadState = 7,
    Custom = 8,
}

/// <summary>What a streaming session expects to be fed.</summary>
public enum AudioCppStreamInputKind
{
    None = 0,
    AudioChunks = 1,
}

/// <summary>How a streaming session reports results.</summary>
public enum AudioCppStreamOutputKind
{
    FinalResult = 0,
    PullEvents = 1,
}

/// <summary>Mirrors <c>audiocpp_voice_activity_kind</c>.</summary>
public enum AudioCppVoiceActivityKind
{
    SpeechStart = 0,
    SpeechEnd = 1,
    SpeechSegment = 2,
}
