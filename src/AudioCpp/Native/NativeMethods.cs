using System.Runtime.InteropServices;

namespace AudioCpp.Native;

/// <summary>
/// One-to-one P/Invoke declarations for <c>audiocpp.h</c>. Nothing here
/// interprets anything: lifetimes, ownership and error translation all belong
/// to the wrapper types.
/// </summary>
/// <remarks>
/// Every <c>const char *</c> the native side returns is BORROWED, so it is
/// declared as <see cref="IntPtr"/> rather than <c>string</c>. Letting the
/// marshaller produce a string for a return value would also let it free the
/// pointer, which here belongs to the owning handle.
/// </remarks>
internal static unsafe partial class NativeMethods
{
    internal const string Library = "audiocpp";

    static NativeMethods() => NativeLibraryResolver.Install();

    // ---- versioning ------------------------------------------------------

    [LibraryImport(Library)]
    internal static partial uint audiocpp_abi_version();

    [LibraryImport(Library)]
    internal static partial IntPtr audiocpp_build_version();

    [LibraryImport(Library)]
    internal static partial IntPtr audiocpp_last_error();

    [LibraryImport(Library)]
    internal static partial IntPtr audiocpp_status_string(AudioCppStatus status);

    // ---- options ---------------------------------------------------------

    [LibraryImport(Library)]
    internal static partial IntPtr audiocpp_options_create();

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial AudioCppStatus audiocpp_options_set(IntPtr options, string key, string value);

    [LibraryImport(Library)]
    internal static partial void audiocpp_options_free(IntPtr options);

    // ---- registry --------------------------------------------------------

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial AudioCppStatus audiocpp_registry_create(string? configPath, out IntPtr outRegistry);

    [LibraryImport(Library)]
    internal static partial void audiocpp_registry_free(IntPtr registry);

    [LibraryImport(Library)]
    internal static partial nuint audiocpp_registry_family_count(IntPtr registry);

    [LibraryImport(Library)]
    internal static partial AudioCppStatus audiocpp_registry_family(IntPtr registry, nuint index, out IntPtr outFamily);

    // ---- task vocabulary -------------------------------------------------

    [LibraryImport(Library)]
    internal static partial nuint audiocpp_task_count();

    [LibraryImport(Library)]
    internal static partial IntPtr audiocpp_task_name(nuint index);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr audiocpp_task_from_spec_name(string specTask);

    // ---- model -----------------------------------------------------------

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial AudioCppStatus audiocpp_model_load(
        IntPtr registry, string modelPath, NativeModelConfig* config, IntPtr options, out IntPtr outModel);

    [LibraryImport(Library)]
    internal static partial void audiocpp_model_free(IntPtr model);

    [LibraryImport(Library)]
    internal static partial IntPtr audiocpp_model_family(IntPtr model);

    [LibraryImport(Library)]
    internal static partial IntPtr audiocpp_model_description(IntPtr model);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int audiocpp_model_supports(IntPtr model, string task, string mode);

    [LibraryImport(Library)]
    internal static partial int audiocpp_model_supports_timestamps(IntPtr model);

    [LibraryImport(Library)]
    internal static partial int audiocpp_model_supports_speaker_reference(IntPtr model);

    [LibraryImport(Library)]
    internal static partial int audiocpp_model_supports_style_condition(IntPtr model);

    [LibraryImport(Library)]
    internal static partial nuint audiocpp_model_language_count(IntPtr model);

    [LibraryImport(Library)]
    internal static partial AudioCppStatus audiocpp_model_language(IntPtr model, nuint index, out IntPtr outLanguage);

    [LibraryImport(Library)]
    internal static partial nuint audiocpp_model_option_count(IntPtr model, AudioCppOptionScope scope);

    [LibraryImport(Library)]
    internal static partial AudioCppStatus audiocpp_model_option(
        IntPtr model, AudioCppOptionScope scope, nuint index,
        out IntPtr outName, out IntPtr outValueName, out IntPtr outDescription,
        out IntPtr outDefaultValue, out IntPtr outMinValue, out IntPtr outMaxValue,
        out int outRequired);

    // ---- session ---------------------------------------------------------

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial AudioCppStatus audiocpp_session_create(
        IntPtr model, string task, string mode, NativeBackendConfig* backendConfig,
        IntPtr options, out IntPtr outSession);

    [LibraryImport(Library)]
    internal static partial void audiocpp_session_free(IntPtr session);

    [LibraryImport(Library)]
    internal static partial IntPtr audiocpp_session_family(IntPtr session);

    [LibraryImport(Library)]
    internal static partial AudioCppStatus audiocpp_session_prepare(IntPtr session, IntPtr request);

    [LibraryImport(Library)]
    internal static partial AudioCppStatus audiocpp_session_run(IntPtr session, IntPtr request, out IntPtr outResult);

    // ---- request ---------------------------------------------------------

    [LibraryImport(Library)]
    internal static partial IntPtr audiocpp_request_create();

    [LibraryImport(Library)]
    internal static partial void audiocpp_request_free(IntPtr request);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial AudioCppStatus audiocpp_request_set_text(IntPtr request, string text, string? language);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial AudioCppStatus audiocpp_request_set_text_language(IntPtr request, string? language);

    [LibraryImport(Library)]
    internal static partial AudioCppStatus audiocpp_request_set_audio(
        IntPtr request, float* samples, nuint frames, int sampleRate, int channels);

    [LibraryImport(Library)]
    internal static partial AudioCppStatus audiocpp_request_set_voice_audio(
        IntPtr request, float* samples, nuint frames, int sampleRate, int channels);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial AudioCppStatus audiocpp_request_set_voice_id(IntPtr request, string cachedVoiceId);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial AudioCppStatus audiocpp_request_set_style_language(IntPtr request, string language);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial AudioCppStatus audiocpp_request_set_emotion(IntPtr request, string emotion);

    [LibraryImport(Library)]
    internal static partial AudioCppStatus audiocpp_request_set_speaking_rate(IntPtr request, float speakingRate);

    [LibraryImport(Library)]
    internal static partial AudioCppStatus audiocpp_request_set_pitch_shift(IntPtr request, float pitchShift);

    [LibraryImport(Library)]
    internal static partial AudioCppStatus audiocpp_request_set_energy_scale(IntPtr request, float energyScale);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial AudioCppStatus audiocpp_request_set_style_tag(IntPtr request, string key, string value);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial AudioCppStatus audiocpp_request_add_artifact(
        IntPtr request, AudioCppArtifactKind kind, string id, void* payload, nuint payloadBytes, out nuint outIndex);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial AudioCppStatus audiocpp_request_set_artifact_meta(
        IntPtr request, nuint index, string key, string value);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial AudioCppStatus audiocpp_request_set_option(IntPtr request, string key, string value);

    // ---- result ----------------------------------------------------------

    [LibraryImport(Library)]
    internal static partial void audiocpp_result_free(IntPtr result);

    [LibraryImport(Library)]
    internal static partial AudioCppStatus audiocpp_result_audio(
        IntPtr result, out IntPtr outSamples, out nuint outFrames, out int outSampleRate, out int outChannels);

    [LibraryImport(Library)]
    internal static partial AudioCppStatus audiocpp_result_text(IntPtr result, out IntPtr outText, out IntPtr outLanguage);

    [LibraryImport(Library)]
    internal static partial nuint audiocpp_result_segment_count(IntPtr result);

    [LibraryImport(Library)]
    internal static partial AudioCppStatus audiocpp_result_segment(
        IntPtr result, nuint index, out long outStartSample, out long outEndSample,
        out float outConfidence, out IntPtr outText);

    [LibraryImport(Library)]
    internal static partial nuint audiocpp_result_speaker_turn_count(IntPtr result);

    [LibraryImport(Library)]
    internal static partial AudioCppStatus audiocpp_result_speaker_turn(
        IntPtr result, nuint index, out long outStartSample, out long outEndSample,
        out IntPtr outSpeakerId, out float outConfidence, out IntPtr outText);

    [LibraryImport(Library)]
    internal static partial nuint audiocpp_result_word_count(IntPtr result);

    [LibraryImport(Library)]
    internal static partial AudioCppStatus audiocpp_result_word(
        IntPtr result, nuint index, out IntPtr outWord, out long outStartSample,
        out long outEndSample, out float outConfidence);

    [LibraryImport(Library)]
    internal static partial nuint audiocpp_result_named_audio_count(IntPtr result);

    [LibraryImport(Library)]
    internal static partial AudioCppStatus audiocpp_result_named_audio(
        IntPtr result, nuint index, out IntPtr outId, out IntPtr outSamples,
        out nuint outFrames, out int outSampleRate, out int outChannels);

    [LibraryImport(Library)]
    internal static partial nuint audiocpp_result_artifact_count(IntPtr result);

    [LibraryImport(Library)]
    internal static partial AudioCppStatus audiocpp_result_artifact(
        IntPtr result, nuint index, out AudioCppArtifactKind outKind, out IntPtr outId,
        out IntPtr outPayload, out nuint outPayloadBytes);

    [LibraryImport(Library)]
    internal static partial nuint audiocpp_result_artifact_meta_count(IntPtr result, nuint index);

    [LibraryImport(Library)]
    internal static partial AudioCppStatus audiocpp_result_artifact_meta(
        IntPtr result, nuint index, nuint metaIndex, out IntPtr outKey, out IntPtr outValue);

    // ---- streaming -------------------------------------------------------

    [LibraryImport(Library)]
    internal static partial AudioCppStatus audiocpp_stream_policy(
        IntPtr session, out AudioCppStreamInputKind outInput, out AudioCppStreamOutputKind outOutput,
        out long outPreferredChunkSamples, out double outPreferredChunkSeconds);

    [LibraryImport(Library)]
    internal static partial AudioCppStatus audiocpp_stream_start(IntPtr session, IntPtr request);

    [LibraryImport(Library)]
    internal static partial AudioCppStatus audiocpp_stream_push(
        IntPtr session, float* samples, nuint frames, int sampleRate, int channels,
        long startSample, out IntPtr outEvent);

    [LibraryImport(Library)]
    internal static partial AudioCppStatus audiocpp_stream_next_event(IntPtr session, out IntPtr outEvent);

    [LibraryImport(Library)]
    internal static partial AudioCppStatus audiocpp_stream_finish(IntPtr session, out IntPtr outResult);

    [LibraryImport(Library)]
    internal static partial AudioCppStatus audiocpp_stream_reset(IntPtr session);

    [LibraryImport(Library)]
    internal static partial void audiocpp_event_free(IntPtr streamEvent);

    [LibraryImport(Library)]
    internal static partial int audiocpp_event_is_final(IntPtr streamEvent);

    [LibraryImport(Library)]
    internal static partial IntPtr audiocpp_event_as_result(IntPtr streamEvent);

    [LibraryImport(Library)]
    internal static partial nuint audiocpp_event_voice_activity_count(IntPtr streamEvent);

    [LibraryImport(Library)]
    internal static partial AudioCppStatus audiocpp_event_voice_activity(
        IntPtr streamEvent, nuint index, out AudioCppVoiceActivityKind outKind,
        out long outSample, out float outProbability);
}
