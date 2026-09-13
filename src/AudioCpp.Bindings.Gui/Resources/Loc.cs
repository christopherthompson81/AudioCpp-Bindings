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

    /// <summary>Cultures with a resource file present, plus the neutral default.</summary>
    public static IReadOnlyList<string> Available { get; } = ["English", "Pseudo (qps-ploc)"];

    public string this[string key] => Strings.Get(key);

    /// <summary>Switch language and refresh everything bound through this.</summary>
    public static void Use(string display)
    {
        Strings.Culture = display.StartsWith("Pseudo", StringComparison.Ordinal)
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
