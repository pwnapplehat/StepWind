using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace StepWind.App;

/// <summary>Shows an element only when the bound view name equals the converter parameter.</summary>
public sealed class ViewVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.Equals(value as string, parameter as string, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps an operation kind ("Move", "Delete"…) to its signature brush.</summary>
public sealed class KindBrushConverter : IValueConverter
{
    /// <summary>"Faint" for chip backgrounds, empty for full-strength dots/text.</summary>
    public string Variant { get; set; } = "";

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string kind = value as string ?? "";
        string suffix = Variant.Length > 0 ? "." + Variant : "";
        string key = kind is "Create" or "Modify" or "Move" or "Rename" or "Delete"
            ? $"SW.Kind.{kind}{suffix}"
            : $"SW.Kind.Modify{suffix}";
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
