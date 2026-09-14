using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace AudioCpp.BindingCoverage;

/// <summary>
/// Asserts that every entry point audiocpp.h declares has a binding in this
/// assembly, and that nothing is bound which the header does not declare.
///
/// audio.cpp's own tests/capi/export_surface.py checks the library against the
/// header. Nothing checked these bindings against either, so an entry point
/// added upstream could go unbound indefinitely and a typo in a binding would
/// only surface as a DllNotFoundException at the call site.
///
/// Exit codes follow CTest: 0 pass, 1 fail, 77 skip.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string? header;
        try
        {
            header = ResolveHeader(args);
        }
        catch (FileNotFoundException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }

        if (header is null)
        {
            Console.WriteLine("audiocpp.h not found; skipping.");
            Console.WriteLine("Set AUDIOCPP_HEADER, or pass the path, or keep an audio.cpp "
                              + "checkout beside this repo.");
            return 77;
        }

        Console.WriteLine($"header:   {header}");

        var declared = DeclaredEntryPoints(File.ReadAllText(header));
        var bound = BoundEntryPoints();

        Console.WriteLine($"declared: {declared.Count}   bound: {bound.Count}");

        var missing = declared.Except(bound).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var extra = bound.Except(declared).OrderBy(n => n, StringComparer.Ordinal).ToList();

        foreach (var name in missing) Console.Error.WriteLine($"  NOT BOUND: {name}");
        foreach (var name in extra) Console.Error.WriteLine($"  NOT IN HEADER: {name}");

        if (missing.Count > 0 || extra.Count > 0)
        {
            Console.Error.WriteLine(
                $"binding surface does not match the header "
                + $"({missing.Count} unbound, {extra.Count} unknown)");
            return 1;
        }

        Console.WriteLine($"every declared entry point is bound ({declared.Count} symbols)");
        return 0;
    }

    /// <summary>
    /// Entry points the header declares. AUDIOCPP_API marks each one, and the
    /// return type varies, so match on the macro and take the identifier that
    /// precedes the parameter list.
    /// </summary>
    private static HashSet<string> DeclaredEntryPoints(string header)
    {
        // Strip block comments first: the header documents the ABI heavily, and
        // commented-out or illustrative declarations would otherwise count.
        var code = Regex.Replace(header, @"/\*.*?\*/", "", RegexOptions.Singleline);

        var matches = Regex.Matches(
            code,
            @"AUDIOCPP_API\s+[A-Za-z_][A-Za-z0-9_ \*]*?\b(audiocpp_[a-z0-9_]+)\s*\(",
            RegexOptions.Singleline);

        return matches.Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Entry points this assembly actually binds, by reflection rather than by
    /// reading the source: a name in a file proves only that the file mentions
    /// it. LibraryImport generates the P/Invoke onto a partial method, so the
    /// attribute sits on the declaration we can see here.
    /// </summary>
    private static HashSet<string> BoundEntryPoints()
    {
        var assembly = typeof(AudioCppRegistry).Assembly;
        var native = assembly.GetType("AudioCpp.Native.NativeMethods", throwOnError: true)!;

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in native.GetMethods(
                     BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var import = method.GetCustomAttribute<LibraryImportAttribute>();
            if (import is null) continue;
            names.Add(import.EntryPoint ?? method.Name);
        }
        return names;
    }

    /// <summary>
    /// An explicit path wins, then AUDIOCPP_HEADER, then the include directory
    /// beside a native build (AUDIOCPP_NATIVE_DIR points into build/bin), then
    /// the pinned engine submodule, then an audio.cpp checkout next to this repo.
    /// </summary>
    private static string? ResolveHeader(string[] args)
    {
        static string? Exists(string? path) =>
            path is { Length: > 0 } && File.Exists(path) ? Path.GetFullPath(path) : null;

        // An explicit path that does not exist is an error, not a reason to go
        // looking: silently testing a different header than the one asked for
        // would report a pass about the wrong ABI.
        foreach (var (source, value) in new[]
                 {
                     ("argument", args.Length > 0 ? args[0] : null),
                     ("AUDIOCPP_HEADER", Environment.GetEnvironmentVariable("AUDIOCPP_HEADER")),
                 })
        {
            if (value is not { Length: > 0 }) continue;
            if (Exists(value) is { } given) return given;
            throw new FileNotFoundException($"{source} names a header that does not exist: {value}");
        }

        var candidates = new List<string>();

        var native = Environment.GetEnvironmentVariable("AUDIOCPP_NATIVE_DIR");
        for (var dir = native is { Length: > 0 } ? new DirectoryInfo(native) : null;
             dir is not null; dir = dir.Parent)
        {
            candidates.Add(Path.Combine(dir.FullName, "include", "audiocpp.h"));
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory);
             dir is not null; dir = dir.Parent)
        {
            // The pinned engine first: coverage should be measured against the
            // header of the ABI this repository is actually built against, not
            // whichever audio.cpp checkout happens to sit nearby.
            candidates.Add(Path.Combine(
                dir.FullName, "external", "audio.cpp", "include", "audiocpp.h"));
            candidates.Add(Path.Combine(dir.FullName, "audio.cpp", "include", "audiocpp.h"));
            if (dir.Parent is not null)
            {
                candidates.Add(Path.Combine(
                    dir.Parent.FullName, "audio.cpp", "include", "audiocpp.h"));
            }
        }

        return candidates.Select(Exists).FirstOrDefault(found => found is not null);
    }
}
