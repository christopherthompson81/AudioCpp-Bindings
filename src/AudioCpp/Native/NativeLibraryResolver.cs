using System.Runtime.InteropServices;

namespace AudioCpp.Native;

/// <summary>
/// Teaches the runtime where libaudiocpp lives.
/// </summary>
/// <remarks>
/// The native library is built by CMake, not packaged by NuGet, so it will not
/// be sitting next to the managed assembly in a normal build. Rather than
/// making every consumer set LD_LIBRARY_PATH, an explicit resolver checks
/// AUDIOCPP_NATIVE_DIR first and then falls back to the default probing the
/// runtime would have done anyway.
/// </remarks>
internal static class NativeLibraryResolver
{
    /// <summary>
    /// Called from <see cref="NativeMethods"/>'s static constructor, so it runs
    /// on first interop use rather than at assembly load. A module initializer
    /// would run earlier and less predictably for consumers (CA2255).
    /// </summary>
    internal static void Install() =>
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);

    private static IntPtr Resolve(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != NativeMethods.Library) return IntPtr.Zero;

        foreach (var directory in SearchDirectories())
        {
            foreach (var candidate in FileNames())
            {
                var path = Path.Combine(directory, candidate);
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out var loaded)) return loaded;
            }
        }

        // Fall back to the platform's own search, so a properly installed or
        // co-located library still works with no configuration.
        return NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var found) ? found : IntPtr.Zero;
    }

    /// <summary>
    /// Where to look, in order: an explicit setting, then the usual places an
    /// audio.cpp build sits relative to a checkout of this repository.
    /// </summary>
    /// <remarks>
    /// Requiring AUDIOCPP_NATIVE_DIR for every run is a papercut: the library is
    /// almost always in a build directory of a sibling audio.cpp checkout, and
    /// looking there costs a few File.Exists calls on first interop use. The
    /// environment variable still wins, so an explicit choice is never
    /// second-guessed.
    /// </remarks>
    private static IEnumerable<string> SearchDirectories()
    {
        var configured = Environment.GetEnvironmentVariable("AUDIOCPP_NATIVE_DIR");
        if (!string.IsNullOrEmpty(configured)) yield return configured;

        // Common CMake build directory names, in the order a developer is
        // likeliest to have built most recently.
        string[] builds = ["build", "build-cuda", "build-release", "cmake-build-release"];

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            foreach (var root in new[] { dir, dir.Parent })
            {
                if (root is null) continue;
                foreach (var build in builds)
                {
                    yield return Path.Combine(root.FullName, "audio.cpp", build, "bin");
                }
            }
        }
    }

    private static IEnumerable<string> FileNames()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return "audiocpp.dll";
            // MinGW keeps the lib prefix on Windows; MSVC does not.
            yield return "libaudiocpp.dll";
            yield break;
        }
        if (OperatingSystem.IsMacOS())
        {
            yield return "libaudiocpp.dylib";
            yield break;
        }
        yield return "libaudiocpp.so";
        yield return "libaudiocpp.so.0";
    }
}
