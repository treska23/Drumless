using System.Globalization;
using System.Windows.Data;

namespace DrumPracticeStudio.Infrastructure;

public sealed class SliderValueToAngleConverter : IMultiValueConverter
{
    public object Convert(
        object[] values,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        if (values.Length < 3 ||
            values[0] is not double value ||
            values[1] is not double minimum ||
            values[2] is not double maximum ||
            maximum <= minimum)
        {
            return -135d;
        }

        var ratio = Math.Clamp((value - minimum) / (maximum - minimum), 0d, 1d);
        return -135d + (ratio * 270d);
    }

    public object[] ConvertBack(
        object value,
        Type[] targetTypes,
        object? parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}
