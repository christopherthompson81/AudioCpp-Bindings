using System.Runtime.InteropServices;
using AudioCpp.Native;

namespace AudioCpp;

/// <summary>
/// A loaded model. Holds its registry alive, so the two may be disposed in any
/// order — which matters here, because finalizer order is not deterministic.
/// </summary>
public sealed class AudioCppModel : SafeHandle
{
    private readonly AudioCppRegistry _registry;

    private AudioCppModel(IntPtr handle, AudioCppRegistry registry) : base(IntPtr.Zero, ownsHandle: true)
    {
        _registry = registry;
        SetHandle(handle);
    }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    internal static unsafe AudioCppModel Load(
        AudioCppRegistry registry, string modelPath, ModelConfig config, AudioCppOptions? options)
    {
        var native = new NativeModelConfig
        {
            FamilyHint = Utf8.Allocate(config.FamilyHint),
            ConfigId = Utf8.Allocate(config.ConfigId),
            WeightId = Utf8.Allocate(config.WeightId),
            ModelSpecOverride = Utf8.Allocate(config.ModelSpecOverride),
        };
        try
        {
            AudioCppException.ThrowIfFailed(
                NativeMethods.audiocpp_model_load(
                    registry.DangerousHandle, modelPath, &native,
                    options?.DangerousHandle ?? IntPtr.Zero, out var created),
                nameof(NativeMethods.audiocpp_model_load));
            return new AudioCppModel(created, registry);
        }
        finally
        {
            Utf8.Free(native.FamilyHint);
            Utf8.Free(native.ConfigId);
            Utf8.Free(native.WeightId);
            Utf8.Free(native.ModelSpecOverride);
        }
    }

    /// <summary>The family that actually loaded.</summary>
    public string Family => Utf8.ToString(NativeMethods.audiocpp_model_family(handle));

    /// <summary>The model's own description.</summary>
    public string Description => Utf8.ToString(NativeMethods.audiocpp_model_description(handle));

    /// <summary>Whether the model advertises a task/mode pair, e.g. ("tts", "offline").</summary>
    public bool Supports(string task, string mode) =>
        NativeMethods.audiocpp_model_supports(handle, task, mode) != 0;

    /// <summary>Whether the model reports word-level timings.</summary>
    public bool SupportsTimestamps => NativeMethods.audiocpp_model_supports_timestamps(handle) != 0;

    /// <summary>Whether the model accepts a speaker reference.</summary>
    public bool SupportsSpeakerReference =>
        NativeMethods.audiocpp_model_supports_speaker_reference(handle) != 0;

    /// <summary>Whether the model accepts style conditioning.</summary>
    public bool SupportsStyleCondition =>
        NativeMethods.audiocpp_model_supports_style_condition(handle) != 0;

    /// <summary>Languages the model advertises.</summary>
    public IReadOnlyList<string> Languages
    {
        get
        {
            var count = (int)NativeMethods.audiocpp_model_language_count(handle);
            var languages = new string[count];
            for (var i = 0; i < count; i++)
            {
                AudioCppException.ThrowIfFailed(
                    NativeMethods.audiocpp_model_language(handle, (nuint)i, out var value),
                    nameof(NativeMethods.audiocpp_model_language));
                languages[i] = Utf8.ToString(value);
            }
            return languages;
        }
    }

    /// <summary>
    /// What this family accepts, as it describes itself. Enumerating this is
    /// how a caller stays correct across model families without hardcoding any
    /// of them.
    /// </summary>
    public IReadOnlyList<ModelOption> GetOptions(AudioCppOptionScope scope)
    {
        var count = (int)NativeMethods.audiocpp_model_option_count(handle, scope);
        var options = new ModelOption[count];
        for (var i = 0; i < count; i++)
        {
            AudioCppException.ThrowIfFailed(
                NativeMethods.audiocpp_model_option(
                    handle, scope, (nuint)i, out var name, out var valueName, out var description,
                    out var defaultValue, out var minValue, out var maxValue, out var required),
                nameof(NativeMethods.audiocpp_model_option));
            options[i] = new ModelOption(
                Utf8.ToString(name), Utf8.ToString(valueName), Utf8.ToString(description),
                Utf8.ToString(defaultValue), Utf8.ToString(minValue), Utf8.ToString(maxValue),
                required != 0);
        }
        return options;
    }

    /// <summary>Creates a task session. <paramref name="mode"/> is "offline" or "streaming".</summary>
    public AudioCppSession CreateSession(
        string task,
        string mode = "offline",
        BackendConfig backend = default,
        IEnumerable<KeyValuePair<string, string>>? sessionOptions = null)
    {
        using var options = AudioCppOptions.From(sessionOptions);
        return AudioCppSession.Create(this, task, mode, backend, options);
    }

    internal IntPtr DangerousHandle => handle;
    internal AudioCppRegistry Registry => _registry;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        NativeMethods.audiocpp_model_free(handle);
        return true;
    }
}
