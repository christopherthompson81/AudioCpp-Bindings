using System.Runtime.InteropServices;
using AudioCpp.Native;

namespace AudioCpp;

/// <summary>The set of model families this build links.</summary>
public sealed class AudioCppRegistry : SafeHandle
{
    private AudioCppRegistry(IntPtr handle) : base(IntPtr.Zero, ownsHandle: true) => SetHandle(handle);

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <summary>The ABI version of the loaded native library, packed major/minor/patch.</summary>
    public static uint AbiVersion => NativeMethods.audiocpp_abi_version();

    /// <summary>The audio.cpp build version string.</summary>
    public static string BuildVersion => Utf8.ToString(NativeMethods.audiocpp_build_version());

    /// <summary>The ABI major version this assembly was written against.</summary>
    public const uint SupportedAbiMajor = 0;

    /// <summary>
    /// Opens the default registry, or one described by <paramref name="configPath"/>.
    /// </summary>
    /// <exception cref="AudioCppException">The native library refused to build a registry.</exception>
    /// <exception cref="NotSupportedException">
    /// The native library's ABI major version differs from the one this
    /// assembly was built against, so the two cannot be used together.
    /// </exception>
    public static AudioCppRegistry Create(string? configPath = null)
    {
        var major = AbiVersion >> 16;
        if (major != SupportedAbiMajor)
        {
            throw new NotSupportedException(
                $"libaudiocpp reports ABI major {major}; this binding targets {SupportedAbiMajor}.");
        }
        AudioCppException.ThrowIfFailed(
            NativeMethods.audiocpp_registry_create(configPath, out var created),
            nameof(NativeMethods.audiocpp_registry_create));
        return new AudioCppRegistry(created);
    }

    /// <summary>Every family this build can load.</summary>
    public IReadOnlyList<string> Families
    {
        get
        {
            var count = (int)NativeMethods.audiocpp_registry_family_count(handle);
            var families = new string[count];
            for (var i = 0; i < count; i++)
            {
                AudioCppException.ThrowIfFailed(
                    NativeMethods.audiocpp_registry_family(handle, (nuint)i, out var name),
                    nameof(NativeMethods.audiocpp_registry_family));
                families[i] = Utf8.ToString(name);
            }
            return families;
        }
    }

    /// <summary>Loads a model. See <see cref="ModelConfig"/> for selection beyond the family.</summary>
    public AudioCppModel Load(
        string modelPath,
        ModelConfig config = default,
        IEnumerable<KeyValuePair<string, string>>? loadOptions = null)
    {
        ArgumentNullException.ThrowIfNull(modelPath);
        using var options = AudioCppOptions.From(loadOptions);
        return AudioCppModel.Load(this, modelPath, config, options);
    }

    /// <summary>Convenience overload for the common case of naming only the family.</summary>
    public AudioCppModel Load(string modelPath, string familyHint) =>
        Load(modelPath, new ModelConfig(familyHint));

    internal IntPtr DangerousHandle => handle;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        NativeMethods.audiocpp_registry_free(handle);
        return true;
    }
}
