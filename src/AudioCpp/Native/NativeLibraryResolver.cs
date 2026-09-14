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
    /// Where to look, in order: an explicit setting, then the engine build this
    /// repository pins.
    /// </summary>
    /// <remarks>
    /// The engine is a submodule pinned to a known commit and built into a known
    /// place, so the default is one path rather than a guess: scripts/build-engine.sh
    /// puts it in external/audio.cpp/build/bin. AUDIOCPP_NATIVE_DIR still wins,
    /// which is what a consumer building against their own audio.cpp checkout
    /// sets -- the pin is the tested version, not the only supported one.
    /// </remarks>
    private static IEnumerable<string> SearchDirectories()
    {
        var configured = Environment.GetEnvironmentVariable("AUDIOCPP_NATIVE_DIR");
        if (!string.IsNullOrEmpty(configured)) yield return configured;

        // Walking up already visits every ancestor, so checking dir and dir.Parent
        // on each step would probe each one twice. The walk is what lets this work
        // from a test binary nested several directories deep in bin/Debug/net10.0.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var bin = Path.Combine(dir.FullName, "external", "audio.cpp", "build", "bin");
            yield return bin;

            // Multi-config generators -- Visual Studio, Xcode, Ninja Multi-Config --
            // put outputs in a per-configuration subdirectory, so bin/ alone finds
            // nothing on a tree where the library is sitting right there.
            foreach (var config in Configurations) yield return Path.Combine(bin, config);
        }
    }

    private static readonly string[] Configurations =
        ["Release", "RelWithDebInfo", "MinSizeRel", "Debug"];

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
