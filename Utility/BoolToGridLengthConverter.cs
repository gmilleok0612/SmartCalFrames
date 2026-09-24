using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SmartCalFrames.Utility {

    /// <summary>
    /// ROUND 69 - lets a ColumnDefinition's Width react to a bool, so the image-preview column can
    /// actually collapse to zero width when the Options page's "Show captured image" setting is off,
    /// rather than just hiding its content and leaving a dead blank half of the panel. A plain
    /// Visibility binding on the column's CONTENT isn't enough for this - Visibility.Collapsed on an
    /// element doesn't give its Grid column's reserved space back; the ColumnDefinition's own Width has
    /// to change. true -> a "*" (star) column, sized equally against the log's own "*" column (so the
    /// two split 50/50 when shown); false -> a zero-width column (0 pixels, log gets the space back).
    /// </summary>
    public class BoolToGridLengthConverter : IValueConverter {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            (value is bool b && b) ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
