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

        var configured = Environment.GetEnvironmentVariable("AUDIOCPP_NATIVE_DIR");
        if (!string.IsNullOrEmpty(configured))
        {
            foreach (var candidate in FileNames())
            {
                var path = Path.Combine(configured, candidate);
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out var loaded)) return loaded;
            }
        }

        // Fall back to the platform's own search, so a properly installed or
        // co-located library still works with no configuration.
        return NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var found) ? found : IntPtr.Zero;
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
