using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Magpie.App.Converters;

/// <summary>bool/number/string/collection truthiness → Visibility.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = Truthy(value);
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility v && v == Visibility.Visible ? !Invert : Invert;

    internal static bool Truthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        int i => i != 0,
        long l => l != 0,
        double d => d != 0,
        string s => s.Length > 0,
        System.Collections.ICollection c => c.Count > 0,
        _ => true,
    };
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => !BoolToVisibilityConverter.Truthy(value);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => !(value is bool b && b);
}

/// <summary>value.ToString() == parameter → bool or Visibility; ConvertBack returns the enum value for radio buttons.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public bool ToVisibility { get; set; }
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool eq = string.Equals(value?.ToString() ?? "", parameter?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
        if (Invert) eq = !eq;
        if (ToVisibility) return eq ? Visibility.Visible : Visibility.Collapsed;
        return eq;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b && parameter != null && targetType.IsEnum) return Enum.Parse(targetType, parameter.ToString()!, true);
        return Binding.DoNothing;
    }
}

/// <summary>Compensates for the invisible resize border WPF adds when a WindowChrome window is maximized.</summary>
public sealed class MaximizedMarginConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is WindowState.Maximized ? new Thickness(7) : new Thickness(0);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            if (value is string s && s.Length > 0)
            {
                var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(s));
                b.Freeze();
                return b;
            }
        }
        catch { }
        return Brushes.Gray;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolToFontWeightConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BoolToVisibilityConverter.Truthy(value) ? FontWeights.SemiBold : FontWeights.Normal;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Truthiness → one of two theme brushes: ConverterParameter "Brush.Success|Brush.Danger".</summary>
public sealed class BoolToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var parts = (parameter as string ?? "Brush.Success|Brush.Danger").Split('|');
        var key = BoolToVisibilityConverter.Truthy(value) ? parts[0] : (parts.Length > 1 ? parts[1] : parts[0]);
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
