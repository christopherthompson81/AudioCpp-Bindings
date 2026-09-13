using System.Runtime.InteropServices;

namespace AudioCpp.Audio;

/// <summary>
/// Finds libaudioio, which CMake builds rather than NuGet shipping.
/// </summary>
/// <remarks>
/// Same shape as the audiocpp resolver, and for the same reason: requiring an
/// environment variable for every run is a papercut. AUDIOIO_NATIVE_DIR
/// wins when set, then the usual build directories relative to this assembly.
/// </remarks>
internal static class NativeLibraryResolver
{
    internal static void Install() =>
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);

    private static IntPtr Resolve(string name, System.Reflection.Assembly assembly, DllImportSearchPath? path)
    {
        if (name != NativeMethods.Library) return IntPtr.Zero;

        foreach (var directory in SearchDirectories())
        {
            foreach (var candidate in FileNames())
            {
                var full = Path.Combine(directory, candidate);
                if (File.Exists(full) && NativeLibrary.TryLoad(full, out var loaded)) return loaded;
            }
        }
        return NativeLibrary.TryLoad(name, assembly, path, out var found) ? found : IntPtr.Zero;
    }

    private static IEnumerable<string> SearchDirectories()
    {
        var configured = Environment.GetEnvironmentVariable("AUDIOIO_NATIVE_DIR");
        if (!string.IsNullOrEmpty(configured)) yield return configured;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            yield return dir.FullName;
            yield return Path.Combine(dir.FullName, "native", "audioio", "build");
            yield return Path.Combine(dir.FullName, "build", "native");
        }
    }

    private static IEnumerable<string> FileNames()
    {
        if (OperatingSystem.IsWindows()) { yield return "audioio.dll"; yield break; }
        if (OperatingSystem.IsMacOS()) { yield return "libaudioio.dylib"; yield break; }
        yield return "libaudioio.so";
    }
}
