using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Gomp.App.Converters;

/// <summary>
/// Turns a <c>#RRGGBB</c> string (the deterministic per-member chat colour, the
/// trust-badge colour) into a cached <see cref="IBrush"/>. Keeps the view-models
/// free of Avalonia media types so they stay trivially testable.
/// </summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public static readonly HexToBrushConverter Instance = new();

    private static readonly ConcurrentDictionary<string, IBrush> Cache = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string hex || string.IsNullOrEmpty(hex))
            return Brushes.Transparent;

        return Cache.GetOrAdd(hex, static h =>
        {
            try
            {
                return new SolidColorBrush(Color.Parse(h));
            }
            catch (FormatException)
            {
                return Brushes.White;
            }
        });
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
