using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Moondrop.Wpf;

public sealed class FrequencyTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int frequency ? frequency.ToString("N0", culture) : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        int.TryParse(value?.ToString(), NumberStyles.Integer | NumberStyles.AllowThousands, culture, out var frequency)
            ? frequency : DependencyProperty.UnsetValue;
}

public sealed class GainTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double gain) return value;
        if (Math.Abs(gain) < 0.05) gain = 0;
        return gain.ToString(parameter?.ToString() == "signed" ? "+0.0;-0.0;0.0" : "0.0", culture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        double.TryParse(value?.ToString(), NumberStyles.Float, culture, out var gain) && double.IsFinite(gain)
            ? gain : DependencyProperty.UnsetValue;
}

public sealed class QTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double q ? q.ToString(q < 0.01 ? "0.0000" : "0.00", culture) : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        double.TryParse(value?.ToString(), NumberStyles.Float, culture, out var q) && double.IsFinite(q)
            ? q : DependencyProperty.UnsetValue;
}
