using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ZeyhDramTweaker.ViewModels;

/// <summary>null/empty → Collapsed (or false when ConverterParameter="bool").</summary>
public sealed class NullToCollapsed : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        bool has = value is string s ? s.Length > 0 : value != null;
        return p as string == "bool" ? has : has ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class InverseBoolToVisibility : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class InverseBool : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is not true;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => v is not true;
}
