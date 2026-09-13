using System.Globalization;
using Avalonia.Data.Converters;

namespace AudioCpp.Bindings.Gui.Resources;

/// <summary>
/// Uppercases an eyebrow label at render time.
/// </summary>
/// <remarks>
/// The web UI uppercases these in CSS, so its catalogue stores some eyebrows
/// already uppercase ("REQUEST") and leaves others to the stylesheet ("Input",
/// "Ввод"). Uppercasing here instead of in the resource file means every
/// imported string stays byte-identical to upstream's, which is what lets the
/// importer detect drift by comparing them.
///
/// Casing follows the text's own culture, not the machine's: Turkish would
/// otherwise turn "i" into "I" instead of "İ".
/// </remarks>
public sealed class Upper : IValueConverter
{
    public static Upper Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text ? text.ToUpper(Strings.Culture) : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
