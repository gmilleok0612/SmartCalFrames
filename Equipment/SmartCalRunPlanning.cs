using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using SmartCalFrames.Options;

namespace SmartCalFrames.Equipment {

    /// <summary>
    /// ROUND 40 - grouping/resolution/cover-guard logic pulled out of SmartCalFramesVM into shared static
    /// helpers, so the new multi-function sequencer item (Sequencer/SmartCalSequenceItem.cs) doesn't have
    /// to reimplement - and risk drifting from - the same "Flat Darks" grouping, "Dark Frames" grouping,
    /// bias-exposure resolution, and motorized-cover guard the dockable panel already uses. Everything
    /// here is static and reads directly from SmartCalSettingsProvider/IProfileService/
    /// IFlatDeviceMediator - nothing depends on a live SmartCalFramesVM instance existing, since a
    /// sequence can run a capture without the dockable panel ever having been opened this session (see
    /// SmartCalSequenceItem's existing `SmartCalFramesVM.Instance?.` null-conditional pattern for the
    /// same reason).
    ///
    /// SmartCalFramesVM's own BuildDarkGroups/BuildDarkFrameGroups/FindMisconfiguredDarkFrameFilters/
    /// ResolveBiasExposureSeconds/EnsureFlatPanelCoverClosedAsync/ReopenFlatPanelCoverBestEffortAsync now
    /// all delegate to the methods here instead of holding their own copies - see the (much shorter)
    /// versions left in SmartCalFramesVM.cs.
    /// </summary>
    public static class SmartCalRunPlanning {

        // ---- "Flat Darks" tab grouping (unchanged logic, moved from SmartCalFramesVM.BuildDarkGroups) ----

        /// <summary>
        /// Groups every filter in the active profile's filter wheel by its stored FLAT exposure, rounded
        /// to 2 decimals. One group per distinct rounded exposure - filters that share an exposure share
        /// one group instead of shooting the same darks twice. Binning/gain/offset are deliberately not
        /// part of this grouping - Flat Darks has always used bin1x1 and the camera's current gain/offset
        /// (this plugin has never stored those per filter for flats).
        /// </summary>
        public static List<(double Exposure, List<string> Filters)> BuildFlatDarkGroups(
                IProfileService profileService, SmartCalSettingsProvider provider, SmartCalSettings settings) {
            var filters = profileService.ActiveProfile?.FilterWheelSettings?.FilterWheelFilters;
            if (filters == null) return new List<(double, List<string>)>();

            // ROUND 44 - fallback changed from MinExposureSeconds to PreferredExposureSeconds, matching
            // every other GetFilterExposureSeconds call site - a never-converged filter's Flat Darks
            // group now matches the SAME starting exposure a Flats run would actually use for it.
            return filters
                .Select(f => new { f.Name, Exposure = Math.Round(provider.GetFilterExposureSeconds(f.Name, settings.PreferredExposureSeconds), 2) })
                .GroupBy(x => x.Exposure)
                .OrderBy(g => g.Key)
                .Select(g => (g.Key, g.Select(x => x.Name).ToList()))
                .ToList();
        }

        // ---- "Dark Frames" tab rows + grouping (moved from SmartCalFramesVM.RefreshDarkFrameRows/
        // BuildDarkFrameGroups/FindMisconfiguredDarkFrameFilters) ----

        /// <summary>Plain snapshot of one filter's persisted Dark Frames row - everything DarkFrameRowVM
        /// holds, minus the live-editing/save-on-change machinery a sequencer item has no use for.</summary>
        public readonly struct DarkFrameRowSnapshot {
            public string FilterName { get; }
            public int Binning { get; }
            public int FrameCount { get; }
            public double ExposureSeconds { get; }

            public DarkFrameRowSnapshot(string filterName, int binning, int frameCount, double exposureSeconds) {
                FilterName = filterName;
                Binning = binning;
                FrameCount = frameCount;
                ExposureSeconds = exposureSeconds;
            }

            public DarkFrameRowSnapshot WithExposureSeconds(double exposureSeconds) =>
                new DarkFrameRowSnapshot(FilterName, Binning, FrameCount, exposureSeconds);
        }

        /// <summary>Reads the Dark Frames tab's persisted per-filter values directly from
        /// SmartCalSettingsProvider - the same values DarkFrameRowVM/RefreshDarkFrameRows read, just
        /// without needing a live SmartCalFramesVM/ObservableCollection to hold them.</summary>
        public static List<DarkFrameRowSnapshot> LoadDarkFrameRows(IProfileService profileService, SmartCalSettingsProvider provider) {
            var rows = new List<DarkFrameRowSnapshot>();
            var filters = profileService.ActiveProfile?.FilterWheelSettings?.FilterWheelFilters;
            if (filters == null) return rows;
            foreach (var f in filters.OrderBy(f => f.Position)) {
                int binning = provider.GetDarkFrameBinning(f.Name, 1);
                int frameCount = provider.GetDarkFrameCount(f.Name, 0);
                double exposure = provider.GetDarkFrameExposureSeconds(f.Name, 0.0);
                rows.Add(new DarkFrameRowSnapshot(f.Name, binning, frameCount, exposure));
            }
            return rows;
        }

        /// <summary>Same grouping rule as SmartCalFramesVM.BuildDarkFrameGroups: a row is included only
        /// with BOTH FrameCount &gt; 0 (the skip toggle) AND ExposureSeconds &gt; 0; rows sharing both
        /// exposure (rounded to 2 decimals) and binning merge into one group, using the LARGER requested
        /// frame count.</summary>
        public static List<(double Exposure, int Binning, int FrameCount, List<string> Filters)> BuildDarkFrameGroups(IEnumerable<DarkFrameRowSnapshot> rows) {
            return rows
                .Where(r => r.FrameCount > 0 && r.ExposureSeconds > 0)
                .Select(r => new { r.FilterName, Exposure = Math.Round(r.ExposureSeconds, 2), r.Binning, r.FrameCount })
                .GroupBy(x => new { x.Exposure, x.Binning })
                .OrderBy(g => g.Key.Exposure).ThenBy(g => g.Key.Binning)
                .Select(g => (g.Key.Exposure, g.Key.Binning, g.Max(x => x.FrameCount), g.Select(x => x.FilterName).ToList()))
                .ToList();
        }

        /// <summary>Rows that are "on" (FrameCount &gt; 0) but have no exposure entered yet (still 0) -
        /// excluded from BuildDarkFrameGroups above, surfaced here so the caller can warn about them
        /// instead of silently dropping them.</summary>
        public static List<string> FindMisconfiguredDarkFrameFilters(IEnumerable<DarkFrameRowSnapshot> rows) {
            return rows.Where(r => r.FrameCount > 0 && r.ExposureSeconds <= 0).Select(r => r.FilterName).ToList();
        }

        // ---- Bias exposure resolution (moved from SmartCalFramesVM.ResolveBiasExposureSeconds) ----

        /// <summary>Same logic as SmartCalFramesVM.ResolveBiasExposureSeconds: prefer the connected
        /// camera's own reported ExposureMin, falling back to the flats tab's MinExposureSeconds only if
        /// the camera doesn't report a usable (&gt; 0) ExposureMin.</summary>
        public static double ResolveBiasExposureSeconds(NINA.Equipment.Equipment.MyCamera.CameraInfo camInfo, IProfileService profileService) {
            if (camInfo != null && camInfo.ExposureMin > 0) return camInfo.ExposureMin;
            var settings = new SmartCalSettingsProvider(profileService).Load();
            return settings.MinExposureSeconds;
        }

        // ---- Motorized-cover guard (moved from SmartCalFramesVM.EnsureFlatPanelCoverClosedAsync/
        // ReopenFlatPanelCoverBestEffortAsync, ROUND 38) ----

        /// <summary>Same logic as SmartCalFramesVM.EnsureFlatPanelCoverClosedAsync, generalized to report
        /// through a plain IProgress instead of touching StatusText/RunLog directly, so both the dockable
        /// panel and the sequencer item (which have different ways of showing a line to the user) can use
        /// it. No-op (returns true immediately) for a panel without a motorized cover - the White Dwarf
        /// and every other calibrator-only panel, exactly as before.</summary>
        public static async Task<bool> EnsureFlatPanelCoverClosedAsync(
                IFlatDeviceMediator flatDeviceMediator, IProgress<ApplicationStatus> progress, CancellationToken token) {
            var info = flatDeviceMediator.GetInfo();
            if (info == null || !info.SupportsOpenClose) {
                return true;
            }
            if (info.CoverState == CoverState.Closed) {
                return true;
            }

            progress.Report(new ApplicationStatus { Status = $"Flat panel cover is {info.CoverState} - closing it before capturing..." });
            try {
                await flatDeviceMediator.CloseCover(progress, token);
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                progress.Report(new ApplicationStatus { Status = $"Failed to close the flat panel cover: {ex.Message}. Aborting - cannot guarantee a light-sealed frame." });
                return false;
            }

            var confirm = flatDeviceMediator.GetInfo();
            if (confirm == null || confirm.CoverState != CoverState.Closed) {
                progress.Report(new ApplicationStatus { Status = $"Flat panel cover did not confirm closed after commanding it (reports {(confirm?.CoverState.ToString() ?? "unknown")}). Aborting - cannot guarantee a light-sealed frame." });
                return false;
            }
            return true;
        }

        /// <summary>Same logic as SmartCalFramesVM.ReopenFlatPanelCoverBestEffortAsync: best-effort,
        /// never throws, no-op for a panel without a motorized cover.</summary>
        public static async Task ReopenFlatPanelCoverBestEffortAsync(IFlatDeviceMediator flatDeviceMediator, IProgress<ApplicationStatus> progress) {
            try {
                var info = flatDeviceMediator.GetInfo();
                if (info == null || !info.SupportsOpenClose) return;
                if (info.CoverState == CoverState.Open) return;
                var doNothingProgress = new Progress<ApplicationStatus>(_ => { });
                await flatDeviceMediator.OpenCover(doNothingProgress, CancellationToken.None);
                progress.Report(new ApplicationStatus { Status = "Reopened the flat panel cover." });
            } catch (Exception ex) {
                Logger.Warning($"Smart Calibration Frames: failed to reopen the flat panel cover during cleanup: {ex.Message}. Manually verify the cover state before pointing at the sky.");
            }
        }
    }
}
