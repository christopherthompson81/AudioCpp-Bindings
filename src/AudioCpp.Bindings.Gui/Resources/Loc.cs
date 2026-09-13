using System.ComponentModel;
using System.Globalization;

namespace AudioCpp.Bindings.Gui.Resources;

/// <summary>
/// Localised text for XAML, through an indexer.
/// </summary>
/// <remarks>
/// An indexer rather than ~55 properties, and a binding rather than x:Static:
/// x:Static resolves once when the window loads, so a language change would not
/// reach anything already on screen. Raising PropertyChanged for "Item[]"
/// re-reads every binding at once, which is how the switch takes effect without
/// a restart.
/// </remarks>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Current { get; } = new();

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
        var languages = new List<string> { "English" };
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

    internal const string PseudoLocale = "Pseudo (qps-ploc)";

    public string this[string key] => Strings.Get(key);

    /// <summary>Switch language and refresh everything bound through this.</summary>
    public static void Use(string display)
    {
        Strings.Culture = display == PseudoLocale
            ? new CultureInfo("qps-ploc")
            : new CultureInfo("en");
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
