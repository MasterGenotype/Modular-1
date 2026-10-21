using System.Globalization;
using Avalonia.Data.Converters;

namespace Modular.Gui.Converters;

/// <summary>
/// True when the bound string equals the converter parameter (ordinal).
/// Used to mark the active sidebar entry.
/// </summary>
public sealed class StringEqualsConverter : IValueConverter
{
    public static readonly StringEqualsConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value as string, parameter as string, StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
