using System.ComponentModel;
using System.Globalization;

namespace AudioCpp.Bindings.Gui.Resources;

/// <summary>
/// Localised text for XAML, through an indexer.
/// </summary>
/// <remarks>
/// An indexer rather than a property per key, and a binding rather than
/// x:Static: x:Static resolves once when the window loads, so a language change
/// would not reach anything already on screen. Raising PropertyChanged for the
/// indexer re-reads every binding at once, which is how the switch takes effect
/// without a restart.
/// </remarks>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Current { get; } = new();

    /// <summary>
    /// Display name and culture for each language offered, named in that
    /// language rather than in English — a reader who needs the Russian build
    /// is not helped by the word "Russian".
    /// </summary>
    /// <remarks>
    /// The four translations come from the audio.cpp web UI, imported by
    /// tools/locales/import_webui_locales.py, which prints this list when it
    /// runs. Adding a language upstream means running the importer and adding
    /// a row here.
    /// </remarks>
    private static readonly (string Display, string Culture)[] Translations =
    [
        ("English", "en"),
        ("Italiano", "it"),
        ("Polski", "pl"),
        ("Русский", "ru"),
        ("中文", "zh"),
    ];

    internal const string PseudoLocale = "Pseudo (qps-ploc)";

    /// <summary>
    /// Languages offered in the selector.
    /// </summary>
    /// <remarks>
    /// The pseudo-locale is a development aid, not a language, so it is offered
    /// only in a debug build or when AUDIOCPP_PSEUDO_LOCALE is set. Shipping it
    /// in the selector would invite a user to pick a setting that makes the app
    /// look broken.
    /// </remarks>
    public static IReadOnlyList<string> Available { get; } = BuildAvailable();

    private static IReadOnlyList<string> BuildAvailable()
    {
        var languages = Translations.Select(language => language.Display).ToList();
#if DEBUG
        languages.Add(PseudoLocale);
#else
        if (Environment.GetEnvironmentVariable("AUDIOCPP_PSEUDO_LOCALE") is { Length: > 0 })
        {
            languages.Add(PseudoLocale);
        }
#endif
        return languages;
    }

    public string this[string key] => Strings.Get(key);

    /// <summary>Switch language and refresh everything bound through this.</summary>
    public static void Use(string display)
    {
        var culture = display == PseudoLocale
            ? "qps-ploc"
            : Translations.FirstOrDefault(language => language.Display == display).Culture ?? "en";
        Strings.Culture = new CultureInfo(culture);

        // Both: "Item[]" is the WPF convention for an indexer, and an empty
        // name means "every property", which is what actually reaches Avalonia's
        // indexer bindings. Raising only the first leaves the UI in the old
        // language while the view model has already changed — which is exactly
        // how this was found, with pseudo-locale text in the hero and English
        // everywhere else.
        Current.PropertyChanged?.Invoke(Current, new PropertyChangedEventArgs("Item[]"));
        Current.PropertyChanged?.Invoke(Current, new PropertyChangedEventArgs(string.Empty));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
