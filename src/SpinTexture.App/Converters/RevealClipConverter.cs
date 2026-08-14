using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace SpinTexture.App.Converters;

/// <summary>
/// Clips an already-arranged full-size image from the left edge. Keeping the
/// original and enhanced visuals in the same layout slot is essential: sizing
/// the enhanced image's parent would cause <see cref="System.Windows.Controls.Image"/>
/// to re-center portrait or landscape textures inside a different viewport.
/// </summary>
public sealed class RevealClipConverter : IMultiValueConverter
{
    public object Convert(
        object[] values,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        if (values.Length < 3
            || values[0] is not double width
            || values[1] is not double height
            || values[2] is not double percent
            || !double.IsFinite(width)
            || !double.IsFinite(height)
            || !double.IsFinite(percent)
            || width <= 0
            || height <= 0)
        {
            return Geometry.Empty;
        }

        var revealWidth = width * Math.Clamp(percent, 0d, 100d) / 100d;
        var geometry = new RectangleGeometry(new Rect(0d, 0d, revealWidth, height));
        geometry.Freeze();
        return geometry;
    }

    public object[] ConvertBack(
        object value,
        Type[] targetTypes,
        object? parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}
