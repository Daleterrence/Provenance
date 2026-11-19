using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Provenance.Common.Feeds;

namespace Provenance.Wpf.Converters;

public abstract class OneWayConverter : IValueConverter
{
    public abstract object Convert(object? value, Type targetType, object? parameter, CultureInfo culture);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ArticleTimesConverter : OneWayConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s ? ArticleTimes.ToLocal(s) : value ?? string.Empty;
}

public sealed class InvertBoolConverter : OneWayConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}

public sealed class UpperCaseConverter : OneWayConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as string)?.ToUpper(culture) ?? value ?? string.Empty;
}

public sealed class InvertBoolToVisibilityConverter : OneWayConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;
}

public sealed class LocalTimeConverter : OneWayConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        DateTimeOffset? utc = value switch
        {
            DateTimeOffset d => d,
            // feed times are UTC, and normalising the Kind first means the ctor can't throw on a Local time
            DateTime dt => new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified ? dt : dt.ToUniversalTime(), TimeSpan.Zero),
            _ => null,
        };
        if (utc is not { } at) return string.Empty;
        var local = at.ToLocalTime();
        return local.ToString("d MMM yyyy", CultureInfo.CurrentCulture) + ", " + local.ToString("t", CultureInfo.CurrentCulture);
    }
}

public sealed class NullToVisibilityConverter : OneWayConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool has = value is string s ? !string.IsNullOrWhiteSpace(s) : value is not null;
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase)) has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }
}
