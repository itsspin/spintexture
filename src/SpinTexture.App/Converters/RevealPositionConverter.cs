using System.Globalization;
using System.Windows.Data;

namespace SpinTexture.App.Converters;

/// <summary>
/// Converts a comparison surface width and a percentage into the horizontal
/// reveal position used by the live before/after preview.
/// </summary>
public sealed class RevealPositionConverter : IMultiValueConverter
{
    public object Convert(
        object[] values,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        if (values.Length < 2
            || values[0] is not double width
            || values[1] is not double percentage
            || double.IsNaN(width)
            || double.IsInfinity(width))
        {
            return 0d;
        }

        double fraction = Math.Clamp(percentage, 0d, 100d) / 100d;
        if (parameter is string direction
            && direction.Equals("Reverse", StringComparison.OrdinalIgnoreCase))
        {
            fraction = 1d - fraction;
        }

        return Math.Max(0d, width * fraction);
    }

    public object[] ConvertBack(
        object value,
        Type[] targetTypes,
        object? parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}
