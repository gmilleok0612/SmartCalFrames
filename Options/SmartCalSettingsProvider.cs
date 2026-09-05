using System;
using System.IO;
using NINA.Core.Utility;
using NINA.Profile;
using NINA.Profile.Interfaces;

namespace SmartCalFrames.Options {

    /// <summary>
    /// Bridges the plain SmartCalSettings POCO to NINA's per-profile plugin
    /// settings store, namespaced by this plugin's GUID so it can never
    /// collide with another plugin's keys.
    ///
    /// Verified directly against the installed NINA.Profile 3.2.0.9001
    /// assembly: the real API is a separate concrete
    /// <see cref="PluginOptionsAccessor"/> object - constructed once with
    /// (IProfileService, Guid) - exposing GetValueX/SetValueX(name,
    /// default) methods with no per-call Guid argument. This is NOT the
    /// same as IProfile.PluginSettings (that interface exists too, but
    /// exposes a different, lower-level API); PluginOptionsAccessor is the
    /// one plugins are meant to use.
    /// </summary>
    public class SmartCalSettingsProvider {

        public static readonly Guid PluginGuid = new Guid("8970f75b-3138-40f1-80d4-4d1898da383e");

        private readonly PluginOptionsAccessor _accessor;

        public SmartCalSettingsProvider(IProfileService profileService) {
            _accessor = new PluginOptionsAccessor(profileService, PluginGuid);
        }

        // Fallback values below are read from one throwaway `new SmartCalSettings()` rather than a
        // second, hand-copied set of literals - that way SmartCalSettings.cs stays the single source of
        // truth for every default in this plugin, and a change to a default only ever needs to happen in
        // one place.
        public SmartCalSettings Load() {
            var a = _accessor;
            var d = new SmartCalSettings();
            return new SmartCalSettings {
                TargetADUPercent = a.GetValueDouble(nameof(SmartCalSettings.TargetADUPercent), d.TargetADUPercent),
                ToleranceADUPercent = a.GetValueDouble(nameof(SmartCalSettings.ToleranceADUPercent), d.ToleranceADUPercent),
                MinExposureSeconds = a.GetValueDouble(nameof(SmartCalSettings.MinExposureSeconds), d.MinExposureSeconds),
                MaxExposureSeconds = a.GetValueDouble(nameof(SmartCalSettings.MaxExposureSeconds), d.MaxExposureSeconds),
                MinBrightness = a.GetValueInt32(nameof(SmartCalSettings.MinBrightness), d.MinBrightness),
                MaxBrightness = a.GetValueInt32(nameof(SmartCalSettings.MaxBrightness), d.MaxBrightness),
                PreferredExposureSeconds = a.GetValueDouble(nameof(SmartCalSettings.PreferredExposureSeconds), d.PreferredExposureSeconds),
                PanelSettleTimeMs = a.GetValueInt32(nameof(SmartCalSettings.PanelSettleTimeMs), d.PanelSettleTimeMs),
                MinFlatsPerFilter = a.GetValueInt32(nameof(SmartCalSettings.MinFlatsPerFilter), d.MinFlatsPerFilter),
                MaxFlatsPerFilter = a.GetValueInt32(nameof(SmartCalSettings.MaxFlatsPerFilter), d.MaxFlatsPerFilter),
                StackStabilityThreshold = a.GetValueDouble(nameof(SmartCalSettings.StackStabilityThreshold), d.StackStabilityThreshold),
                FrameOutlierTolerancePercent = a.GetValueDouble(nameof(SmartCalSettings.FrameOutlierTolerancePercent), d.FrameOutlierTolerancePercent),
                LogToFileEnabled = a.GetValueBoolean(nameof(SmartCalSettings.LogToFileEnabled), d.LogToFileEnabled),
                DarkFramesPerGroup = a.GetValueInt32(nameof(SmartCalSettings.DarkFramesPerGroup), d.DarkFramesPerGroup),
                BiasFrameCount = a.GetValueInt32(nameof(SmartCalSettings.BiasFrameCount), d.BiasFrameCount),
            };
        }

        public void Save(SmartCalSettings settings) {
            var a = _accessor;
            a.SetValueDouble(nameof(SmartCalSettings.TargetADUPercent), settings.TargetADUPercent);
            a.SetValueDouble(nameof(SmartCalSettings.ToleranceADUPercent), settings.ToleranceADUPercent);
            a.SetValueDouble(nameof(SmartCalSettings.MinExposureSeconds), settings.MinExposureSeconds);
            a.SetValueDouble(nameof(SmartCalSettings.MaxExposureSeconds), settings.MaxExposureSeconds);
            a.SetValueInt32(nameof(SmartCalSettings.MinBrightness), settings.MinBrightness);
            a.SetValueInt32(nameof(SmartCalSettings.MaxBrightness), settings.MaxBrightness);
            a.SetValueDouble(nameof(SmartCalSettings.PreferredExposureSeconds), settings.PreferredExposureSeconds);
            a.SetValueInt32(nameof(SmartCalSettings.PanelSettleTimeMs), settings.PanelSettleTimeMs);
            a.SetValueInt32(nameof(SmartCalSettings.MinFlatsPerFilter), settings.MinFlatsPerFilter);
            a.SetValueInt32(nameof(SmartCalSettings.MaxFlatsPerFilter), settings.MaxFlatsPerFilter);
            a.SetValueDouble(nameof(SmartCalSettings.StackStabilityThreshold), settings.StackStabilityThreshold);
            a.SetValueDouble(nameof(SmartCalSettings.FrameOutlierTolerancePercent), settings.FrameOutlierTolerancePercent);
            a.SetValueBoolean(nameof(SmartCalSettings.LogToFileEnabled), settings.LogToFileEnabled);
            a.SetValueInt32(nameof(SmartCalSettings.DarkFramesPerGroup), settings.DarkFramesPerGroup);
            a.SetValueInt32(nameof(SmartCalSettings.BiasFrameCount), settings.BiasFrameCount);
        }

        // ---- Per-filter defaults ----
        // Each filter gets exactly ONE remembered (brightness, exposure) pair, keyed by filter NAME
        // only - a deliberate simplification rather than a multi-point learned curve per
        // filter+binning+gain+offset bucket. Stored the same way every other setting in this file is -
        // through PluginOptionsAccessor, namespaced per filter name - rather than a separate JSON file,
        // so there's exactly one persistence mechanism in this whole plugin, not two. A run that
        // converges overwrites these values (see SmartCalCaptureService.ConvergeOnTargetAsync); a run
        // that fails leaves them untouched and reports the failure in the log instead - not an automatic
        // revert, since there's no separate "factory default" left once a value has been overwritten by
        // a real run.
        private static string FilterBrightnessKey(string filterName) => $"FilterBrightness_{filterName}";
        private static string FilterExposureKey(string filterName) => $"FilterExposureSeconds_{filterName}";

        // Each Get below probes the same key twice: once with an impossible-in-practice sentinel
        // default (NaN for doubles, int.MinValue for ints), and logs whether that probe came back as
        // the sentinel (= truly nothing persisted under this exact key) or a real number (= something
        // IS persisted). This turns "did this filter ever get a saved value, or is it using the
        // fallback" into something visible in the log rather than ambiguous - useful when tracking down
        // why a run used an unexpected brightness/exposure for a given filter.
        public int GetFilterBrightness(string filterName, int fallback) {
            if (string.IsNullOrEmpty(filterName)) return fallback;
            string key = FilterBrightnessKey(filterName);
            int probe = _accessor.GetValueInt32(key, int.MinValue);
            int result = probe == int.MinValue ? fallback : probe;
            Logger.Info($"Smart Calibration Frames: GetFilterBrightness key='{key}' storedProbe={(probe == int.MinValue ? "NOTHING STORED" : probe.ToString())} -> using {result}");
            return result;
        }

        public double GetFilterExposureSeconds(string filterName, double fallback) {
            if (string.IsNullOrEmpty(filterName)) return fallback;
            string key = FilterExposureKey(filterName);
            double probe = _accessor.GetValueDouble(key, double.NaN);
            double result = double.IsNaN(probe) ? fallback : probe;
            Logger.Info($"Smart Calibration Frames: GetFilterExposureSeconds key='{key}' storedProbe={(double.IsNaN(probe) ? "NOTHING STORED" : probe.ToString("F4"))} -> using {result:F4}");
            return result;
        }

        public void SetFilterBrightness(string filterName, int value) {
            if (string.IsNullOrEmpty(filterName)) return;
            string key = FilterBrightnessKey(filterName);
            _accessor.SetValueInt32(key, value);
            Logger.Info($"Smart Calibration Frames: SetFilterBrightness key='{key}' value={value}");
        }

        public void SetFilterExposureSeconds(string filterName, double value) {
            if (string.IsNullOrEmpty(filterName)) return;
            string key = FilterExposureKey(filterName);
            _accessor.SetValueDouble(key, value);
            Logger.Info($"Smart Calibration Frames: SetFilterExposureSeconds key='{key}' value={value:F4}");
        }

        // ---- Dark Frames per-filter rows ----
        // Three values per filter - Exposure, Frame count, Binning - edited directly on the dockable
        // panel's "Dark Frames" tab (not the Options page - a per-filter list needs its own storage the
        // same shape as the brightness/exposure per-filter pair above, rather than a single Options-page
        // control). A filter is skipped when its FrameCount is 0 - see DarkFrameRowVM and
        // SmartCalRunPlanning.BuildDarkFrameGroups.
        private static string DarkFrameExposureKey(string filterName) => $"DarkFrameExposureSeconds_{filterName}";
        private static string DarkFrameCountKey(string filterName) => $"DarkFrameCount_{filterName}";
        private static string DarkFrameBinningKey(string filterName) => $"DarkFrameBinning_{filterName}";

        public double GetDarkFrameExposureSeconds(string filterName, double fallback) {
            if (string.IsNullOrEmpty(filterName)) return fallback;
            return _accessor.GetValueDouble(DarkFrameExposureKey(filterName), fallback);
        }

        public void SetDarkFrameExposureSeconds(string filterName, double value) {
            if (string.IsNullOrEmpty(filterName)) return;
            _accessor.SetValueDouble(DarkFrameExposureKey(filterName), value);
        }

        public int GetDarkFrameCount(string filterName, int fallback) {
            if (string.IsNullOrEmpty(filterName)) return fallback;
            return _accessor.GetValueInt32(DarkFrameCountKey(filterName), fallback);
        }

        public void SetDarkFrameCount(string filterName, int value) {
            if (string.IsNullOrEmpty(filterName)) return;
            _accessor.SetValueInt32(DarkFrameCountKey(filterName), value);
        }

        public int GetDarkFrameBinning(string filterName, int fallback) {
            if (string.IsNullOrEmpty(filterName)) return fallback;
            return _accessor.GetValueInt32(DarkFrameBinningKey(filterName), fallback);
        }

        public void SetDarkFrameBinning(string filterName, int value) {
            if (string.IsNullOrEmpty(filterName)) return;
            _accessor.SetValueInt32(DarkFrameBinningKey(filterName), value);
        }

        /// <summary>
        /// Where run-log files land when LogToFileEnabled is on (see SmartCalSettings). Static, not
        /// tied to a specific filter or profile the way the per-filter defaults above are - these are
        /// plain diagnostic text files for a human to read later. SmartCalCaptureService (which has no
        /// IProfileService of its own) calls this directly to build each run's log file path.
        /// </summary>
        public static string LogFileFolder {
            get {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NINA", "SmartCalFrames", "Logs");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        // ---- Frame numbering ----
        // Persists the running frame-sequence counter across runs, filters within one "Run All", and
        // NINA restarts, using the same per-profile plugin settings store as every other option above.
        // Without this, filenames could get a "(1)", "(2)", ... collision suffix even though their
        // numbers were correctly incrementing WITHIN a run - a fresh RunAsync call starting a brand new
        // counter at 0 would keep reusing the same low numbers, which collide with whatever's already
        // sitting in the output folder from an earlier run. Not exposed on the Options page - this is
        // internal bookkeeping, not a user preference - so it's read/written directly here rather than
        // through the SmartCalSettings POCO.
        private const string NextFrameNumberKey = "NextFrameNumber";

        public int LoadNextFrameNumber() => _accessor.GetValueInt32(NextFrameNumberKey, 0);

        public void SaveNextFrameNumber(int value) => _accessor.SetValueInt32(NextFrameNumberKey, value);
    }
}
