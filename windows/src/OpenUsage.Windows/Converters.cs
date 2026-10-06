using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace OpenUsage.Windows;

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null or "" ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public static readonly InverseBoolConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// "#F59E0B" → brush; null → the inherited foreground.
public sealed class HexBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string hex ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)) : DependencyProperty.UnsetValue;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// Splits a usage bar's width into filled / remaining star columns. ConverterParameter is "filled" or "rest".
public sealed class FractionStarConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var fraction = Math.Clamp(value as double? ?? 0, 0, 1);
        return new GridLength(parameter as string == "filled" ? fraction : 1 - fraction, GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// Bar fill colour: blue, amber past 75%, red past 90%.
public sealed class FractionBrushConverter : IValueConverter
{
    private static readonly Brush Normal = Frozen(0x0A, 0x84, 0xFF);
    private static readonly Brush High = Frozen(0xF5, 0x9E, 0x0B);
    private static readonly Brush Critical = Frozen(0xFF, 0x45, 0x3A);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => (value as double? ?? 0) switch
    {
        >= 0.9 => Critical,
        >= 0.75 => High,
        _ => Normal,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
