namespace AudioCpp.Bindings.Gui;

/// <summary>
/// Where this app keeps its own files.
/// </summary>
/// <remarks>
/// Environment.GetFolderPath returns an empty string on Unix when it cannot
/// work out the folder — an unset HOME, or an XDG variable pointing somewhere
/// unusable. Combined with a name that produces a *relative* path, so the app
/// scatters settings, voices and downloaded models into whatever directory it
/// happened to be started from, and finds none of them next time.
///
/// Found while testing the settings round trip against a scratch config
/// directory: the file was written, just not anywhere it would be looked for.
/// </remarks>
internal static class AppData
{
    public const string FolderName = "audiocpp-studio";

    /// <summary>A directory for this app's data, always absolute.</summary>
    public static string Root(Environment.SpecialFolder folder, string name = FolderName)
    {
        var root = Environment.GetFolderPath(folder);

        if (root.Length == 0)
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            root = home is { Length: > 0 }
                ? Path.Combine(home, folder == Environment.SpecialFolder.ApplicationData
                    ? ".config" : ".local/share")
                : AppContext.BaseDirectory;
        }

        return Path.GetFullPath(Path.Combine(root, name));
    }
}
