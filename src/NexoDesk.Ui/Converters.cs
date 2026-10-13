using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NexoDesk.Ui;

/// <summary>true → Visible, false → Collapsed (ou o contrário com Invert).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool flag = value is true;
        return flag ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility v ? (v == Visibility.Visible) ^ Invert : DependencyProperty.UnsetValue;
}
