using System;
using System.Globalization;
using System.Windows.Data;

namespace SmartCalFrames.Utility {

    /// <summary>Small local converter so the dockable view doesn't depend on a specific NINA-provided resource key existing under that exact name.</summary>
    public class InverseBooleanConverter : IValueConverter {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is bool b ? !b : value;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is bool b ? !b : value;
    }
}
