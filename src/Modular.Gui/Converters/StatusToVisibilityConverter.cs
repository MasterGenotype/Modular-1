using System.Globalization;
using Avalonia.Data.Converters;
using Modular.Gui.Models;

namespace Modular.Gui.Converters;

/// <summary>
/// Converts ModDownloadStatus to boolean for visibility binding.
/// </summary>
public class StatusToVisibilityConverter : IValueConverter
{
    public static readonly StatusToVisibilityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ModDownloadStatus status && parameter is string targetStatus)
        {
            return Enum.TryParse<ModDownloadStatus>(targetStatus, out var target) && status == target;
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
