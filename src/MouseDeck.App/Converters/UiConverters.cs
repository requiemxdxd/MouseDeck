using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using MouseDeck.Core.Models;

namespace MouseDeck.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        if (parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase))
            flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility v && v == Visibility.Visible;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool hasValue = value is string s ? !string.IsNullOrWhiteSpace(s) : value is not null;
        if (parameter is string p && p.Equals("Invert", StringComparison.OrdinalIgnoreCase))
            hasValue = !hasValue;
        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class IntEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int current && parameter is string str && int.TryParse(str, out int target))
            return current == target;
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true && parameter is string str && int.TryParse(str, out int target))
            return target;
        return Binding.DoNothing;
    }
}

public sealed class DoubleEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double current && parameter is string str &&
            double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out double target))
        {
            return Math.Abs(current - target) < 0.05;
        }
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true && parameter is string str &&
            double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out double target))
        {
            return target;
        }
        return Binding.DoNothing;
    }
}

public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }
            catch { }
        }
        return Brushes.DeepSkyBlue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class PacketDirectionToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush TxBrush = Freeze(Color.FromRgb(237, 237, 237));
    private static readonly SolidColorBrush RxBrush = Freeze(Color.FromRgb(76, 195, 138));
    private static readonly SolidColorBrush InfoBrush = Freeze(Color.FromRgb(133, 133, 139));
    private static readonly SolidColorBrush ErrBrush = Freeze(Color.FromRgb(229, 72, 77));

    private static SolidColorBrush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is PacketDirection dir
            ? dir switch
            {
                PacketDirection.Tx => TxBrush,
                PacketDirection.Rx => RxBrush,
                PacketDirection.Error => ErrBrush,
                _ => InfoBrush
            }
            : InfoBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
