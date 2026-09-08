namespace SmartCalFrames.Options {

    /// <summary>Plain data holder for everything the options page exposes. Kept separate from the ViewModel so the planner/capture service don't need to reference WPF.</summary>
    public class SmartCalSettings {

        // ---- Target ----
        /// <summary>Target mean ADU as a fraction (0-1) of the camera's reported full well/max ADU. The
        /// Options page shows/edits this as a whole percent (1-99) - see
        /// SmartCalOptionsVM.TargetADUPercent for the display-only conversion; this stored fraction is
        /// unchanged and is still what SmartCalCaptureService's math consumes directly.</summary>
        public double TargetADUPercent { get; set; } = 0.5;
        /// <summary>
        /// Acceptable deviation from target, as a fraction (0-1), before the brightness search (see
        /// SmartCalCaptureService.ConvergeOnTargetAsync) accepts a measured result and stops searching.
        /// The Options page shows/edits this as a whole percent (1-100) - see
        /// SmartCalOptionsVM.ToleranceADUPercent.
        /// </summary>
        public double ToleranceADUPercent { get; set; } = 0.10;

        // ---- Search bounds ----
        /// <summary>
        /// Bounds for the per-filter EXPOSURE correction in ConvergeOnTargetAsync - the only thing a
        /// real run searches live. Brightness is not searched; each filter holds one fixed, remembered
        /// brightness (see SmartCalSettingsProvider.GetFilterBrightness), and a run only ever adjusts
        /// exposure, within these two bounds, to hit Target ADU % at that fixed brightness. If exposure
        /// alone can't reach target within this range, the run fails outright (see PlanKind.Failed)
        /// rather than accepting a best-effort result.
        /// </summary>
        public double MinExposureSeconds { get; set; } = 0.1;
        /// <summary>Ceiling for the exposure correction above - not a normal operating value, just the outer bound before a run gives up and reports failure.</summary>
        public double MaxExposureSeconds { get; set; } = 30.0;
        /// <summary>
        /// Fallback value used the very first time a filter has never had a default brightness saved
        /// (see SmartCalSettingsProvider.GetFilterBrightness/SetFilterBrightness), AND the real floor a
        /// filter's brightness can be lowered to when the exposure search hits Min Exposure (saturated,
        /// or needing less than Min Exposure) and still can't reach target - see
        /// SmartCalCaptureService.TryEscalateBrightness.
        /// </summary>
        public int MinBrightness { get; set; } = 0;
        /// <summary>
        /// The real ceiling a filter's brightness can be raised to when the exposure search hits Max
        /// Exposure and the panel is still too dim - see SmartCalCaptureService.TryEscalateBrightness.
        /// </summary>
        public int MaxBrightness { get; set; } = 255;

        /// <summary>
        /// The exposure used the very first time a filter is run, before it has ever converged on its
        /// own saved value (see SmartCalSettingsProvider.GetFilterExposureSeconds and
        /// SmartCalCaptureService.ConvergeOnTargetAsync). Matters most for cameras that need noticeably
        /// longer exposures than the 0.1s floor - a too-short first test exposure can be too
        /// read-noise-dominated to give the search a reliable starting estimate. Once a filter has
        /// converged for real, its own saved exposure (Per-Filter Defaults on the Options page) is used
        /// instead and this value stops mattering for that filter.
        /// </summary>
        public double PreferredExposureSeconds { get; set; } = 3.0;

        // ---- Panel timing ----
        /// <summary>How long to wait after commanding a brightness/on state before trusting the panel has settled - matters for boards with slow buck-regulator response.</summary>
        public int PanelSettleTimeMs { get; set; } = 1500;

        // ---- Stacking sufficiency ----
        public int MinFlatsPerFilter { get; set; } = 10;
        public int MaxFlatsPerFilter { get; set; } = 30;
        /// <summary>Once the running stddev of per-frame means, as a fraction of the mean, drops below this, stop early instead of always shooting MaxFlatsPerFilter. The Options page shows/edits this as a whole percent (0-100) - see SmartCalOptionsVM.StackStabilityThreshold.</summary>
        public double StackStabilityThreshold { get; set; } = 0.01;
        /// <summary>
        /// A DIFFERENT check from StackStabilityThreshold above, not the same knob under another name.
        /// StackStabilityThreshold judges the whole group of accepted frames together - "has the stack
        /// as a whole settled down enough to stop." This setting judges ONE new frame in isolation as
        /// it comes in - "does this specific frame belong with the ones already accepted, or is it a
        /// fluke (satellite trail, glitch, brief panel hiccup) that should be thrown out and retaken."
        /// Expressed the same way (deviation from the running mean of already-accepted frames, as a
        /// fraction) but answering a different question - a bad frame skews an overall-group check like
        /// StackStabilityThreshold, it doesn't get caught by it.
        /// Assumes a flat panel, where frame-to-frame brightness should be nearly constant under a
        /// fixed brightness/exposure - NOT sky flats, where brightness is expected to drift steadily
        /// across a session (twilight changing) and a flat running-average comparison would misfire,
        /// rejecting good frames just for following that expected trend. This plugin only drives a
        /// physical flat panel (IFlatDeviceMediator) and has no sky-flat mode, so that's out of scope
        /// for now - flagged here in case sky-flat support is ever added later, since this check would
        /// need to compare against a trend-fitted expectation instead of a flat average at that point.
        /// The Options page shows/edits this as a whole percent (0-100) - see
        /// SmartCalOptionsVM.FrameOutlierTolerancePercent.
        /// </summary>
        public double FrameOutlierTolerancePercent { get; set; } = 0.05;

        // ---- Run logging ----
        /// <summary>
        /// When true, every line of the run log (search attempts, production frames, rejections, the
        /// final per-filter summary) is also appended to a timestamped .log file on disk as the run
        /// progresses, in addition to the dockable panel's on-screen log and NINA's own log file. Off by
        /// default since most users never need it - the dockable panel's own running log already covers
        /// live viewing. Written one line at a time (opened, appended, closed per line) rather than held
        /// open for the whole run, so a crash or a hard NINA close can never leave a run's progress
        /// unsaved or a file locked - see SmartCalSettingsProvider.LogFileFolder for where the files land.
        /// </summary>
        public bool LogToFileEnabled { get; set; } = false;

        // ---- Flat darks ----
        /// <summary>
        /// How many dark frames to capture per unique exposure group on the dockable panel's "Flat
        /// Darks" tab. One group per DISTINCT stored flat exposure across all filters (filters sharing
        /// an exposure share a group - see SmartCalRunPlanning.BuildFlatDarkGroups), a plain fixed count
        /// with no stability-checking - simpler, and matches how most people shoot darks.
        /// </summary>
        public int DarkFramesPerGroup { get; set; } = 20;

        // ---- Bias frames ----
        /// <summary>
        /// How many bias frames to capture on the dockable panel's "Bias Frames" tab. Same "simple fixed
        /// count, no stability-checking" convention as DarkFramesPerGroup above - a bias stack is just
        /// read-noise averaging, there's no ADU target to converge on and nothing to stabilize toward.
        /// </summary>
        public int BiasFrameCount { get; set; } = 20;
        // There is no BiasExposureSeconds setting: there's no dedicated "take a bias frame" ASCOM/NINA
        // call (every exposure command needs a duration), but NINA's own CameraInfo already reports the
        // connected camera's real minimum exposure - CameraInfo.ExposureMin. Bias capture reads that
        // live at run time (see SmartCalRunPlanning.ResolveBiasExposureSeconds) instead of asking the
        // user to guess/type a value - no stored setting needed for it at all.

        // ---- Manual cover swap ----
        /// <summary>
        /// Off by default. EnsureFlatPanelCoverClosedAsync/LeaveFlatPanelCoverClosedAfterRunAsync already
        /// handle a MOTORIZED flat-panel cover automatically (close before a run, and - per
        /// OpenCoverAfterRun below, default leaving it closed afterward) - but a plain
        /// panel with no motorized cover at all (e.g. the author's own White Dwarf, which always reports
        /// SupportsOpenClose false - see SmartCalRunPlanning's own note on that) makes that guard a
        /// complete no-op, and the only way to keep a dark/bias/flat-dark frame light-sealed on that
        /// hardware is a separate physical cap the user has to walk over and place by hand. When this is
        /// on, a Flats capture (dockable panel button, Run All batch item, or the sequencer item's Flat
        /// Frames function) pauses and waits for the user to click Continue TWICE per run: once right
        /// before capturing (time to remove the cap and put the panel in place) and once right after
        /// (time to remove the panel and put the cap back on) - see SmartCalFramesVM.PauseForCoverSwapAsync.
        /// Users with a real motorized cover leave this off; the automatic guard already does their job
        /// with no manual step at all.
        /// </summary>
        public bool PauseForCoverSwap { get; set; } = false;

        // ---- Cover open-after-run ----
        /// <summary>
        /// ROUND 72. Off by default, matching Round 69's "never reopen automatically" behavior for anyone
        /// who doesn't touch this setting. When turned ON, LeaveFlatPanelCoverClosedAfterRunAsync commands
        /// a MOTORIZED flat-panel cover back open once a run finishes (single-filter Flats/Flat Darks/
        /// Bias/Dark Frames, the Run All batch, or the sequencer item) - the same "reopen, best-effort,
        /// never throw" behavior this plugin had before Round 69, now opt-in instead of automatic. A
        /// complete no-op either way for a panel with no motorized cover (SupportsOpenClose false), same
        /// as EnsureFlatPanelCoverClosedAsync/LeaveFlatPanelCoverClosedAfterRunAsync's own close-side
        /// check. Unrelated to PauseForCoverSwap above - that's for a MANUAL panel/cap swap; this is only
        /// about what a MOTORIZED cover does once a run ends.
        /// </summary>
        public bool OpenCoverAfterRun { get; set; } = false;
    }
}
