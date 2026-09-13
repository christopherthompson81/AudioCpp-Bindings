using System.ComponentModel;
using System.Runtime.CompilerServices;
using AudioCpp.Packages;

namespace AudioCpp.Bindings.Gui;

/// <summary>
/// One installable package as the model picker shows it: what it is, whether it
/// is on this machine, and how big it is.
///
/// Mutable because state changes underneath it — a package installs, or is
/// deleted — and the picker should reflect that without being rebuilt.
/// </summary>
public sealed class CatalogEntry(FamilySpec family, PackageSpec package)
    : INotifyPropertyChanged
{
    private InstallState _state = InstallState.Missing;
    private long _bytes;
    private string _note = "";

    public FamilySpec Family { get; } = family;
    public PackageSpec Package { get; } = package;

    public string Title => $"{Family.DisplayName} · {Package.Precision}";
    public string Subtitle => $"{Family.Family} · {Package.Format}";

    public InstallState State
    {
        get => _state;
        set { if (Set(ref _state, value)) { Notify(nameof(StateText)); Notify(nameof(IsInstalled)); } }
    }

    /// <summary>Size on disk when installed, or the download size once probed.</summary>
    public long Bytes
    {
        get => _bytes;
        set { if (Set(ref _bytes, value)) Notify(nameof(SizeText)); }
    }

    /// <summary>Why a package cannot be installed, when the repository will not serve it.</summary>
    public string Note
    {
        get => _note;
        set => Set(ref _note, value);
    }

    public bool IsInstalled => _state == InstallState.Installed;

    public string StateText => _state switch
    {
        InstallState.Installed => "INSTALLED",
        InstallState.Partial => "PARTIAL",
        _ => Package.Download.Supported ? "AVAILABLE" : "MANUAL",
    };

    public string SizeText => _bytes > 0
        ? $"{_bytes / 1024.0 / 1024.0:F0} MB"
        : "";

    /// <summary>
    /// Path to hand the loader. A package is a directory of files; the engine
    /// takes either a directory or a single GGUF, so point at the file when the
    /// package is exactly one and at the directory otherwise.
    /// </summary>
    public string ResolvePath(string modelsRoot) =>
        Package.Files.Count == 1
            ? PackageInstaller.LocalPath(modelsRoot, Package, Package.Files[0])
            : PackageInstaller.TargetDirectory(modelsRoot, Package);

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }

    private void Notify(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
