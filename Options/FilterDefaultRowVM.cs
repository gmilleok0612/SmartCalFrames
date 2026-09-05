using NINA.Core.Utility;

namespace SmartCalFrames.Options {

    /// <summary>
    /// One row of the Options page's per-filter defaults list: this filter's remembered (Brightness,
    /// ExposureSeconds) pair, editable directly here and also the exact pair
    /// SmartCalCaptureService.ConvergeOnTargetAsync reads at the start of a run and overwrites once a
    /// run converges (see SmartCalSettingsProvider's per-filter Get/Set methods - this VM is a thin
    /// bindable wrapper around those, nothing more). Every setter saves immediately, matching every
    /// other control on this Options page.
    /// </summary>
    public class FilterDefaultRowVM : BaseINPC {

        private readonly SmartCalSettingsProvider _provider;

        public string FilterName { get; }

        private int _brightness;
        public int Brightness {
            get => _brightness;
            set { _brightness = value; _provider.SetFilterBrightness(FilterName, value); RaisePropertyChanged(); }
        }

        private double _exposureSeconds;
        public double ExposureSeconds {
            get => _exposureSeconds;
            set { _exposureSeconds = value; _provider.SetFilterExposureSeconds(FilterName, value); RaisePropertyChanged(); }
        }

        public FilterDefaultRowVM(SmartCalSettingsProvider provider, string filterName, int brightness, double exposureSeconds) {
            _provider = provider;
            FilterName = filterName;
            _brightness = brightness;
            _exposureSeconds = exposureSeconds;
        }

        /// <summary>
        /// Refreshes this row's displayed values after a run converges elsewhere in the plugin (see
        /// SmartCalOptionsVM.RefreshFilterDefault for where this gets called from) - SmartCalOptionsVM
        /// builds its FilterDefaults rows exactly once per NINA process, so without this the Options
        /// page would keep showing stale startup values no matter how many runs converged. Deliberately
        /// a SEPARATE method from the public setters above, not a call to them: the value on disk was
        /// already just written by the very code calling this, so re-persisting it here would be a
        /// harmless but pointless duplicate write - this only needs to update the cached fields and tell
        /// the UI to re-read them.
        /// </summary>
        public void RefreshFromStorage(int brightness, double exposureSeconds) {
            _brightness = brightness;
            _exposureSeconds = exposureSeconds;
            RaisePropertyChanged(nameof(Brightness));
            RaisePropertyChanged(nameof(ExposureSeconds));
        }
    }
}
