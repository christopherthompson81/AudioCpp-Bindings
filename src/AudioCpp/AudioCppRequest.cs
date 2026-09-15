using System.Runtime.InteropServices;
using AudioCpp.Native;

namespace AudioCpp;

/// <summary>
/// One unit of work. Audio and payloads are copied into the request, so the
/// caller's buffers need not outlive these calls.
/// </summary>
public sealed class AudioCppRequest : SafeHandle
{
    /// <summary>Creates an empty request.</summary>
    public AudioCppRequest() : base(IntPtr.Zero, ownsHandle: true)
    {
        var created = NativeMethods.audiocpp_request_create();
        if (created == IntPtr.Zero) throw new OutOfMemoryException("audiocpp_request_create returned null");
        SetHandle(created);
    }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <summary>
    /// Sets the text to synthesise. A non-empty <paramref name="language"/> is
    /// also written to the request's "language" option, matching what the CLI's
    /// <c>--language</c> does, because some families read only the option.
    /// </summary>
    /// <remarks>
    /// To set the transcript language without the option — which a family that
    /// validates its options strictly will refuse — pass null here and use
    /// <see cref="SetTextLanguage"/>.
    /// </remarks>
    public AudioCppRequest SetText(string text, string? language = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        AudioCppException.ThrowIfFailed(
            NativeMethods.audiocpp_request_set_text(handle, text, language),
            nameof(NativeMethods.audiocpp_request_set_text));
        return this;
    }

    /// <summary>
    /// Sets the transcript language alone, leaving the request's "language"
    /// option untouched.
    /// </summary>
    /// <remarks>
    /// "Does this model declare a language option" and "does this model need a
    /// transcript language" are different questions with different answers:
    /// parakeet_tdt refuses a request carrying a <c>language</c> option it does
    /// not declare, while qwen3_forced_aligner declares no such option and
    /// requires the transcript language anyway. Through <see cref="SetText"/>
    /// alone the two travel together, so neither model could be served
    /// correctly without guessing.
    /// </remarks>
    public AudioCppRequest SetTextLanguage(string? language)
    {
        AudioCppException.ThrowIfFailed(
            NativeMethods.audiocpp_request_set_text_language(handle, language),
            nameof(NativeMethods.audiocpp_request_set_text_language));
        return this;
    }

    /// <summary>Sets interleaved PCM input. <paramref name="samples"/> is frames × channels.</summary>
    public unsafe AudioCppRequest SetAudio(ReadOnlySpan<float> samples, int sampleRate, int channels = 1)
    {
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        var frames = (nuint)(samples.Length / channels);
        fixed (float* pinned = samples)
        {
            AudioCppException.ThrowIfFailed(
                NativeMethods.audiocpp_request_set_audio(handle, pinned, frames, sampleRate, channels),
                nameof(NativeMethods.audiocpp_request_set_audio));
        }
        return this;
    }

    /// <summary>Sets a speaker reference clip, for cloning and conversion families.</summary>
    public unsafe AudioCppRequest SetVoiceAudio(ReadOnlySpan<float> samples, int sampleRate, int channels = 1)
    {
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        var frames = (nuint)(samples.Length / channels);
        fixed (float* pinned = samples)
        {
            AudioCppException.ThrowIfFailed(
                NativeMethods.audiocpp_request_set_voice_audio(handle, pinned, frames, sampleRate, channels),
                nameof(NativeMethods.audiocpp_request_set_voice_audio));
        }
        return this;
    }

    /// <summary>Selects a packaged voice by id, e.g. "af_heart".</summary>
    public AudioCppRequest SetVoiceId(string voiceId)
    {
        ArgumentNullException.ThrowIfNull(voiceId);
        AudioCppException.ThrowIfFailed(
            NativeMethods.audiocpp_request_set_voice_id(handle, voiceId),
            nameof(NativeMethods.audiocpp_request_set_voice_id));
        return this;
    }

    /// <summary>Style language, for families that advertise style conditioning.</summary>
    public AudioCppRequest SetStyleLanguage(string language)
    {
        AudioCppException.ThrowIfFailed(
            NativeMethods.audiocpp_request_set_style_language(handle, language),
            nameof(NativeMethods.audiocpp_request_set_style_language));
        return this;
    }

    /// <summary>Requested emotion.</summary>
    public AudioCppRequest SetEmotion(string emotion)
    {
        AudioCppException.ThrowIfFailed(
            NativeMethods.audiocpp_request_set_emotion(handle, emotion),
            nameof(NativeMethods.audiocpp_request_set_emotion));
        return this;
    }

    /// <summary>Speaking-rate multiplier.</summary>
    public AudioCppRequest SetSpeakingRate(float speakingRate)
    {
        AudioCppException.ThrowIfFailed(
            NativeMethods.audiocpp_request_set_speaking_rate(handle, speakingRate),
            nameof(NativeMethods.audiocpp_request_set_speaking_rate));
        return this;
    }

    /// <summary>Pitch shift.</summary>
    public AudioCppRequest SetPitchShift(float pitchShift)
    {
        AudioCppException.ThrowIfFailed(
            NativeMethods.audiocpp_request_set_pitch_shift(handle, pitchShift),
            nameof(NativeMethods.audiocpp_request_set_pitch_shift));
        return this;
    }

    /// <summary>Energy scaling.</summary>
    public AudioCppRequest SetEnergyScale(float energyScale)
    {
        AudioCppException.ThrowIfFailed(
            NativeMethods.audiocpp_request_set_energy_scale(handle, energyScale),
            nameof(NativeMethods.audiocpp_request_set_energy_scale));
        return this;
    }

    /// <summary>A free-form style tag.</summary>
    public AudioCppRequest SetStyleTag(string key, string value)
    {
        AudioCppException.ThrowIfFailed(
            NativeMethods.audiocpp_request_set_style_tag(handle, key, value),
            nameof(NativeMethods.audiocpp_request_set_style_tag));
        return this;
    }

    /// <summary>
    /// Attaches an artifact, such as a speaker embedding read out of an earlier
    /// result. Returns its index, for <see cref="SetArtifactMetadata"/>.
    /// </summary>
    public unsafe int AddArtifact(AudioCppArtifactKind kind, string id, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(id);
        fixed (byte* pinned = payload)
        {
            AudioCppException.ThrowIfFailed(
                NativeMethods.audiocpp_request_add_artifact(
                    handle, kind, id, pinned, (nuint)payload.Length, out var index),
                nameof(NativeMethods.audiocpp_request_add_artifact));
            return (int)index;
        }
    }

    /// <summary>Adds metadata to an artifact already attached.</summary>
    public AudioCppRequest SetArtifactMetadata(int index, string key, string value)
    {
        AudioCppException.ThrowIfFailed(
            NativeMethods.audiocpp_request_set_artifact_meta(handle, (nuint)index, key, value),
            nameof(NativeMethods.audiocpp_request_set_artifact_meta));
        return this;
    }

    /// <summary>Sets a request option, as <c>--request-option</c> does.</summary>
    public AudioCppRequest SetOption(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        AudioCppException.ThrowIfFailed(
            NativeMethods.audiocpp_request_set_option(handle, key, value),
            nameof(NativeMethods.audiocpp_request_set_option));
        return this;
    }

    internal IntPtr DangerousHandle => handle;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        NativeMethods.audiocpp_request_free(handle);
        return true;
    }
}
