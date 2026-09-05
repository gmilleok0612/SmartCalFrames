using System;
using NINA.Core.Utility;
using SmartCalFrames.Options;

namespace SmartCalFrames.Dockable {

    /// <summary>
    /// ROUND 39 - one row of the "Dark Frames" tab's per-filter list (tab and class both renamed same
    /// session from "Light Darks"/LightDarkRowVM - "I've never heard of light dark frames," and fair
    /// enough, no other tab in this plugin uses that phrasing either): this filter's Binning, Frame
    /// count, and Exposure (s) for matching dark frames, editable directly on the tab (not the Options
    /// page - a genuinely different placement decision than FilterDefaultRowVM's per-filter
    /// brightness/exposure, per explicit direction this round). Every setter saves immediately via
    /// SmartCalSettingsProvider, same convention as FilterDefaultRowVM and every other per-filter/
    /// per-tab value in this plugin - no separate "Apply" step.
    ///
    /// ROUND 39 REVISION (same session, per explicit user feedback) - the skip toggle moved from
    /// ExposureSeconds to FrameCount: a filter is now skipped when its FrameCount is 0, not when its
    /// exposure is 0 ("I think only if the count is 0 should it be skipped"). FrameCount's floor
    /// dropped from 1 to 0 to make that representable. ExposureSeconds keeps a nonzero floor once it's
    /// actually being used, though - 0 is still allowed (it just means "not entered yet," and
    /// SmartCalFramesVM.BuildDarkFrameGroups/FindMisconfiguredDarkFrameFilters flags a row that has
    /// frames > 0 but exposure still 0 rather than silently shooting a meaningless 0-second dark), but
    /// anything between 0 and 0.1 is clamped up to 0.1 - the same minimum-sane-exposure floor this
    /// plugin already uses elsewhere (see SmartCalSettings.MinExposureSeconds, default 0.1) - per
    /// explicit follow-up ("Or values for exposure less than .1 not be allowed to be entered?").
    ///
    /// BinningFactor is a single symmetric N (bin NxN) rather than separate X/Y - the UI is a plain
    /// 1x-5x dropdown ("no reason to go farther than that" per explicit direction), and every other
    /// binning value in this plugin (flats, flat-darks, bias) has always been symmetric too.
    /// </summary>
    public class DarkFrameRowVM : BaseINPC {

        private readonly SmartCalSettingsProvider _provider;

        public string FilterName { get; }

        private int _binningFactor;
        public int BinningFactor {
            get => _binningFactor;
            set {
                int clamped = Math.Max(1, Math.Min(5, value));
                _binningFactor = clamped;
                _provider.SetDarkFrameBinning(FilterName, clamped);
                RaisePropertyChanged();
            }
        }

        private int _frameCount;
        public int FrameCount {
            get => _frameCount;
            set {
                // ROUND 39 REVISION - floor dropped from 1 to 0: 0 is now the explicit "skip this
                // filter" value (see BuildDarkFrameGroups), not ExposureSeconds == 0 any more.
                int clamped = Math.Max(0, value);
                _frameCount = clamped;
                _provider.SetDarkFrameCount(FilterName, clamped);
                RaisePropertyChanged();
            }
        }

        private double _exposureSeconds;
        public double ExposureSeconds {
            get => _exposureSeconds;
            set {
                // 0 is still a real, meaningful value here ("not entered yet") - not clamped up to the
                // floor the way a genuinely-in-use exposure is. Anything below 0.1 but above 0 gets
                // pulled up to 0.1 rather than accepted as-is (ROUND 39 REVISION, per explicit
                // direction) - matches this plugin's existing MinExposureSeconds=0.1 convention
                // elsewhere, and stops a stray "0.02" from ever reaching a capture command.
                double clamped;
                if (value <= 0) {
                    clamped = 0;
                } else if (value < 0.1) {
                    clamped = 0.1;
                } else {
                    clamped = value;
                }
                _exposureSeconds = clamped;
                _provider.SetDarkFrameExposureSeconds(FilterName, clamped);
                RaisePropertyChanged();
            }
        }

        public DarkFrameRowVM(SmartCalSettingsProvider provider, string filterName, int binningFactor, int frameCount, double exposureSeconds) {
            _provider = provider;
            FilterName = filterName;
            _binningFactor = binningFactor;
            _frameCount = frameCount;
            _exposureSeconds = exposureSeconds;
        }
    }
}
