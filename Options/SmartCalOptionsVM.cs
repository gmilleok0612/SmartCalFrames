using System;
using System.Collections.ObjectModel;
using System.Linq;
using NINA.Core.Utility;
using NINA.Profile.Interfaces;

namespace SmartCalFrames.Options {

    /// <summary>
    /// Backing VM for the plugin's global options page, registered via the
    /// "SmartCalFrames_Options" DataTemplate (see
    /// Templates/SmartCalDataTemplates.xaml) that NINA's Options ->
    /// Plugins tab looks up by plugin name. Every property setter saves
    /// immediately - there's no separate "Apply" step, matching how NINA's
    /// own options pages behave.
    ///
    /// Takes IProfileService (alongside the settings provider) to enumerate
    /// the active profile's filter list for the per-filter defaults table
    /// below.
    /// </summary>
    public class SmartCalOptionsVM : BaseINPC {

        private readonly SmartCalSettingsProvider _provider;
        private SmartCalSettings _settings;

        public SmartCalOptionsVM(SmartCalSettingsProvider provider, IProfileService profileService) {
            _provider = provider;
            _settings = provider.Load();

            FilterDefaults = new ObservableCollection<FilterDefaultRowVM>();
            var filters = profileService?.ActiveProfile?.FilterWheelSettings?.FilterWheelFilters;
            if (filters != null) {
                foreach (var f in filters.OrderBy(f => f.Position)) {
                    int brightness = _provider.GetFilterBrightness(f.Name, _settings.MinBrightness);
                    // Fallback for a filter that has never converged: the user-configured
                    // "starting exposure for a new filter" value, so this grid shows the SAME
                    // starting exposure a run would actually use for that filter.
                    double exposure = _provider.GetFilterExposureSeconds(f.Name, _settings.PreferredExposureSeconds);
                    FilterDefaults.Add(new FilterDefaultRowVM(_provider, f.Name, brightness, exposure));
                }
            }
        }

        private void Persist() {
            _provider.Save(_settings);
            RaiseAllPropertiesChanged();
        }

        /// <summary>
        /// UI-facing WHOLE-PERCENT view of SmartCalSettings.TargetADUPercent. The setting itself is
        /// unchanged - still stored, and still consumed by SmartCalCaptureService's math, as a 0-1
        /// fraction - so this is purely a presentation convenience (percentages are easier to reason
        /// about than decimals) and carries no risk to anyone's already-saved settings file or to the
        /// capture algorithm. Sanity range 1-99: 0% or 100% of full well isn't a real target.
        /// </summary>
        public int TargetADUPercent {
            get => (int)Math.Round(_settings.TargetADUPercent * 100.0);
            set {
                int clamped = Math.Max(1, Math.Min(99, value));
                _settings.TargetADUPercent = clamped / 100.0;
                Persist();
            }
        }

        /// <summary>See TargetADUPercent above for the fraction/percent split. Sanity range 1-100: zero
        /// tolerance could never be satisfied; beyond 100% is already "accept anything."</summary>
        public int ToleranceADUPercent {
            get => (int)Math.Round(_settings.ToleranceADUPercent * 100.0);
            set {
                int clamped = Math.Max(1, Math.Min(100, value));
                _settings.ToleranceADUPercent = clamped / 100.0;
                Persist();
            }
        }

        public double MinExposureSeconds {
            get => _settings.MinExposureSeconds;
            set { _settings.MinExposureSeconds = value; Persist(); }
        }

        public double MaxExposureSeconds {
            get => _settings.MaxExposureSeconds;
            set { _settings.MaxExposureSeconds = value; Persist(); }
        }

        public double PreferredExposureSeconds {
            get => _settings.PreferredExposureSeconds;
            set { _settings.PreferredExposureSeconds = value; Persist(); }
        }

        /// <summary>
        /// Fallback seed value used the very first time a filter is asked for a brightness before it has
        /// ever had one saved (see SmartCalSettingsProvider.GetFilterBrightness) - also the real floor
        /// ConvergeOnTargetAsync's brightness escalation won't go below (see
        /// SmartCalCaptureService.TryEscalateBrightness), not fallback-only. Clamped to [0, 255] (the
        /// ESP32 digital pot's real range) and never allowed above MaxBrightness, so the escalation logic
        /// always has a well-formed [Min, Max] range to work within regardless of what order the two
        /// boxes are edited in.
        /// </summary>
        public int MinBrightness {
            get => _settings.MinBrightness;
            set {
                int clamped = Math.Max(0, Math.Min(255, value));
                if (clamped > _settings.MaxBrightness) clamped = _settings.MaxBrightness;
                _settings.MinBrightness = clamped;
                Persist();
            }
        }

        /// <summary>The real ceiling brightness escalation won't go above (see MinBrightness above and
        /// SmartCalCaptureService.TryEscalateBrightness). Clamped to [0, 255] and never allowed below
        /// MinBrightness.</summary>
        public int MaxBrightness {
            get => _settings.MaxBrightness;
            set {
                int clamped = Math.Max(0, Math.Min(255, value));
                if (clamped < _settings.MinBrightness) clamped = _settings.MinBrightness;
                _settings.MaxBrightness = clamped;
                Persist();
            }
        }

        public int PanelSettleTimeMs {
            get => _settings.PanelSettleTimeMs;
            set { _settings.PanelSettleTimeMs = value; Persist(); }
        }

        public int MinFlatsPerFilter {
            get => _settings.MinFlatsPerFilter;
            set { _settings.MinFlatsPerFilter = value; Persist(); }
        }

        public int MaxFlatsPerFilter {
            get => _settings.MaxFlatsPerFilter;
            set { _settings.MaxFlatsPerFilter = value; Persist(); }
        }

        /// <summary>See TargetADUPercent above for the fraction/percent split. Sanity range 0-100: 0 is a
        /// valid (if extreme) "never early-stop, always run to Max flats per filter" setting; outside
        /// 0-100 isn't meaningful for a standard-deviation-over-mean percentage.</summary>
        public int StackStabilityThreshold {
            get => (int)Math.Round(_settings.StackStabilityThreshold * 100.0);
            set {
                int clamped = Math.Max(0, Math.Min(100, value));
                _settings.StackStabilityThreshold = clamped / 100.0;
                Persist();
            }
        }

        /// <summary>See TargetADUPercent above for the fraction/percent split. Sanity range 0-100, same
        /// reasoning as Stack stability threshold: 0 is valid (reject anything that isn't an exact match
        /// to the running average) though an extreme setting in practice.</summary>
        public int FrameOutlierTolerancePercent {
            get => (int)Math.Round(_settings.FrameOutlierTolerancePercent * 100.0);
            set {
                int clamped = Math.Max(0, Math.Min(100, value));
                _settings.FrameOutlierTolerancePercent = clamped / 100.0;
                Persist();
            }
        }

        public bool LogToFileEnabled {
            get => _settings.LogToFileEnabled;
            set { _settings.LogToFileEnabled = value; Persist(); }
        }

        /// <summary>
        /// One row per filter in the active profile's filter wheel, each holding that filter's
        /// remembered (Brightness, ExposureSeconds) pair. See FilterDefaultRowVM and
        /// SmartCalSettingsProvider's per-filter Get/Set methods for where these values actually live.
        /// </summary>
        public ObservableCollection<FilterDefaultRowVM> FilterDefaults { get; }

        public string LogFileFolder => SmartCalSettingsProvider.LogFileFolder;

        /// <summary>
        /// Fix for "the values for exposure and brightness are not being updated in the options page
        /// after the flat converges." SmartCalFramesPlugin builds exactly ONE OptionsVM (and its
        /// FilterDefaults rows) for the whole NINA process - reopening the Options page never rebuilds
        /// it. A run's converged brightness/exposure gets written straight to storage by
        /// SmartCalCaptureService, through a totally separate SmartCalSettingsProvider instance of its
        /// own, so the already-built rows here never found out. Called from ConvergeOnTargetAsync right
        /// after it writes a converged filter's new values, via SmartCalFramesPlugin.Instance.OptionsVM
        /// (the same established singleton-access pattern this plugin already uses elsewhere - no new
        /// static reference needed).
        ///
        /// Wrapped in Dispatcher.Invoke like every other cross-VM UI touch in this plugin (see
        /// SmartCalFramesVM.ClearForExternalRun/AppendExternalStatus for the same reasoning): a converged
        /// run can be driven by the Advanced Sequencer, which may not be running on the UI thread, and
        /// FilterDefaults/its rows are WPF-bound.
        /// </summary>
        public void RefreshFilterDefault(string filterName) {
            if (string.IsNullOrEmpty(filterName)) return;
            System.Windows.Application.Current?.Dispatcher.Invoke(() => {
                var row = FilterDefaults.FirstOrDefault(r => string.Equals(r.FilterName, filterName, StringComparison.OrdinalIgnoreCase));
                if (row == null) return;
                int brightness = _provider.GetFilterBrightness(filterName, _settings.MinBrightness);
                // Fallback for consistency with every other GetFilterExposureSeconds call site, though in
                // practice this one is called right after a converged run just stored a real value for
                // this filter, so the fallback rarely matters here.
                double exposure = _provider.GetFilterExposureSeconds(filterName, _settings.PreferredExposureSeconds);
                row.RefreshFromStorage(brightness, exposure);
            });
        }
    }
}
