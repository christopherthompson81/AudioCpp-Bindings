using System.Text.Json;

namespace AudioCpp.Packages;

/// <summary>
/// Where a package's files come from. Every spec audio.cpp ships uses
/// huggingface_snapshot; four declare "unsupported", which means the family has
/// no automated download and the user brings their own weights.
/// </summary>
public sealed record DownloadSpec(string Kind, string Repo, string Revision, bool Gated)
{
    public bool Supported => Kind == "huggingface_snapshot" && Repo.Length > 0;
}

/// <summary>
/// One downloadable variant of a family: a precision, a format, and the files
/// that make it up.
/// </summary>
/// <param name="Files">Paths within the source repository.</param>
/// <param name="TargetDirectory">Directory under the models root to install into.</param>
/// <param name="StripPrefix">
/// Leading path segment to remove from each file when laying it out locally.
/// The repositories nest files under a directory named for the package, which
/// would otherwise be duplicated inside the target directory.
/// </param>
public sealed record PackageSpec(
    string Id,
    string DisplayName,
    string Format,
    string Precision,
    string TargetDirectory,
    IReadOnlyList<string> Files,
    string? StripPrefix,
    bool IsDefault,
    DownloadSpec Download);

/// <summary>One model family, as model_specs/&lt;family&gt;.json describes it.</summary>
public sealed record FamilySpec(
    string Family,
    string DisplayName,
    string Description,
    string Category,
    string Status,
    IReadOnlyList<string> Tasks,
    IReadOnlyList<string> Languages,
    IReadOnlyList<PackageSpec> Packages);

/// <summary>
/// The set of families audio.cpp knows how to load, read from its model_specs
/// directory.
///
/// These are plain JSON descriptions — which files live in which repository, and
/// where they belong on disk. Nothing about them needs the engine, which is why
/// this can be reimplemented here rather than waiting on an ABI that has no
/// model-management surface at all (see issue #10).
///
/// The risk taken deliberately: upstream owns the schema and may change it. The
/// parser is therefore tolerant of unknown fields and reports families it cannot
/// read rather than throwing, so a schema addition degrades to a missing entry
/// instead of a broken catalog.
/// </summary>
public sealed class Catalog
{
    private Catalog(IReadOnlyList<FamilySpec> families, IReadOnlyList<string> unreadable)
    {
        Families = families;
        Unreadable = unreadable;
    }

    public IReadOnlyList<FamilySpec> Families { get; }

    /// <summary>Spec files that could not be parsed, by name, with the reason.</summary>
    public IReadOnlyList<string> Unreadable { get; }

    public static Catalog Load(string modelSpecsDirectory)
    {
        var families = new List<FamilySpec>();
        var unreadable = new List<string>();

        if (!Directory.Exists(modelSpecsDirectory))
            return new Catalog(families, [$"{modelSpecsDirectory}: not a directory"]);

        foreach (var path in Directory.EnumerateFiles(modelSpecsDirectory, "*.json")
                                      .OrderBy(p => p, StringComparer.Ordinal))
        {
            try
            {
                if (ParseFamily(path) is { } family) families.Add(family);
            }
            catch (Exception exception) when (exception is JsonException or IOException)
            {
                unreadable.Add($"{Path.GetFileName(path)}: {exception.Message}");
            }
        }

        return new Catalog(families, unreadable);
    }

    private static FamilySpec? ParseFamily(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        var family = Str(root, "family");
        if (family.Length == 0) return null;

        var defaults = root.TryGetProperty("package_defaults", out var pd)
                       && pd.TryGetProperty("download", out var dd)
            ? ParseDownload(dd)
            : new DownloadSpec("unsupported", "", "main", false);

        var packages = new List<PackageSpec>();
        if (root.TryGetProperty("packages", out var packageArray)
            && packageArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in packageArray.EnumerateArray())
            {
                var download = entry.TryGetProperty("download", out var od)
                    ? ParseDownload(od)
                    : defaults;

                packages.Add(new PackageSpec(
                    Str(entry, "id"),
                    Str(entry, "display_name"),
                    Str(entry, "format"),
                    Str(entry, "precision"),
                    Str(entry, "target_directory"),
                    List(entry, "files"),
                    entry.TryGetProperty("strip_prefix", out var sp)
                        && sp.ValueKind == JsonValueKind.String ? sp.GetString() : null,
                    entry.TryGetProperty("default", out var df)
                        && df.ValueKind == JsonValueKind.True,
                    download));
            }
        }

        return new FamilySpec(
            family,
            Str(root, "display_name"),
            Str(root, "description"),
            Str(root, "category"),
            Str(root, "status"),
            List(root, "tasks"),
            List(root, "languages"),
            packages);
    }

    private static DownloadSpec ParseDownload(JsonElement element) => new(
        Str(element, "kind"),
        Str(element, "repo"),
        Str(element, "revision") is { Length: > 0 } revision ? revision : "main",
        element.TryGetProperty("gated", out var g) && g.ValueKind == JsonValueKind.True);

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private static IReadOnlyList<string> List(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Array) return [];
        return value.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToList();
    }
}
