using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Image.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using SmartCalFrames.Model;
using SmartCalFrames.Options;

namespace SmartCalFrames.Equipment {

    /*
     * ---------------------------------------------------------------------
     * Mediator calls in this file, verified directly against the installed
     * NINA.Equipment/NINA.Core/NINA.Image 3.2.0.9001 assemblies (extracted
     * their real metadata rather than guessed):
     *
     *   IFlatDeviceMediator.SetBrightness(int, IProgress<ApplicationStatus>, CancellationToken)
     *   IFlatDeviceMediator.ToggleLight(bool, IProgress<ApplicationStatus>, CancellationToken)
     *   IFilterWheelMediator.ChangeFilter(FilterInfo, CancellationToken, IProgress<ApplicationStatus>)
     *   IImagingMediator.CaptureAndPrepareImage(CaptureSequence, PrepareImageParameters, CancellationToken, IProgress<ApplicationStatus>) -> Task<IRenderedImage>
     *   ICameraMediator.GetInfo() -> CameraInfo   (declared on the shared IDeviceMediator<,,> base, not ICameraMediator itself)
     *   IFlatDeviceMediator.GetInfo() -> FlatDeviceInfo (same shared base as above) - confirmed directly
     *     from NINA's own source (isbeorn/nina, develop branch) to expose live LightOn (bool) and
     *     Brightness (int) state, alongside Connected/MinBrightness/MaxBrightness/CoverState/
     *     SupportsOnOff/SupportsOpenClose. This is NINA's own FlatDeviceVM's belief about the panel, not
     *     a fresh serial poll of the hardware - see the Round 32n KB entry for the research. Round 32o
     *     tried trusting it to skip the bracket and Round 32s tried a progress-report probe instead -
     *     BOTH reverted (32r, 32t) after real field evidence falsified each in turn. RunAsync is back to
     *     Round 32j/32l's plain value tracker (s_lastCommandedBrightness) - no attempt left to cleverly
     *     detect or skip the bracket on a session's first capture. GetInfo() is still used for the
     *     pre-flight Connected check in SmartCalFramesVM.
     *   CaptureSequence lives in NINA.Equipment.Model (not NINA.Core.Model); ImageType is a plain
     *     string, with CaptureSequence.ImageTypes a nested class of const strings (LIGHT/FLAT/DARK/BIAS/SNAPSHOT).
     *   BinningMode(short x, short y) - constructor takes short, not int.
     *   IImageStatistics (from IRenderedImage.RawImageData.Statistics, a Nito.AsyncEx.AsyncLazy<T>)
     *     exposes Mean, StDev, and BitDepth directly - no need to go via ImageProperties for full well.
     *
     *   IImageSaveMediator.Enqueue(IImageData imageData, Task prepareTask, IProgress<ApplicationStatus>
     *     progress, CancellationToken token) - lives in NINA.WPF.Base.Interfaces.Mediator (a different
     *     assembly/namespace than the other mediators above, same split as IDockableVM/DockableVM
     *     elsewhere in this project).
     *
     *   This plugin captures via the combined CaptureAndPrepareImage (see below) rather than
     *   SkyFlats' separate CaptureImage+PrepareImage calls, so by the time a frame's stats are known
     *   the "prepare" work Enqueue would otherwise await is already done. Enqueue is given the
     *   already-rendered image's RawImageData as imageData, and Task.FromResult(rendered) as
     *   prepareTask, since there's nothing left to wait for.
     *
     * If a future NINA version shifts any of these again, nothing outside
     * this file touches the mediator types directly, so the fix stays here.
     * ---------------------------------------------------------------------
     *
     * ROUND 32 - dropped the learned brightness curve and the live brightness search entirely.
     *
     * Real-world trigger: a full NINA log showed L, OIII, and SII all saving genuinely wrong-ADU flats
     * to disk. Root cause traced precisely: SmartExposurePlanner.SuggestStartingBrightness fell back to
     * a blind MaxBrightness guess whenever a filter's learned curve had no data point that predicted a
     * high enough rate (exactly the situation for L, whose curve had a gap above brightness 55 from
     * earlier saturated/skipped sweep steps, and for OIII/SII, which had no calibration sweep in that
     * log at all) - and ConvergeOnTargetAsync then treated "brightness landed on MaxBrightness on this
     * blind first guess" as equivalent to "we searched and hit a real wall," giving up after a single
     * untested attempt and reusing that bad frame as a saved production flat. Separately, the
     * calibration sweep itself had its own data-quality gap: a point pinned at Min/Max Exposure but
     * still far from target got silently accepted into history whenever the clamped exposure equaled
     * the exposure the search had already arrived at - two different ways for the OLD design's
     * multi-point curve to accumulate bad data that then misled the next run's starting guess.
     *
     * Direct comparison with NINA's own built-in Flat Wizard (researched, not guessed) showed the
     * better shape: it NEVER searches brightness and exposure together - either brightness is fixed and
     * only exposure is searched, or the reverse, never both live in one run. This plugin now follows
     * that same discipline, taken one step further per explicit direction: no learned curve at all, just
     * ONE remembered (brightness, exposure) pair per filter (SmartCalSettingsProvider.
     * GetFilterBrightness/SetFilterBrightness, edited directly on the Options page). A run reads that
     * pair, holds brightness fixed for the whole run, and only ever corrects EXPOSURE - using the exact
     * linear ADU/exposure relationship this plugin has relied on since Round 26. If exposure alone can't
     * reach target within Min/Max Exposure, the run FAILS OUTRIGHT: no frame is saved, the log reports
     * "calibration failed," and the stored brightness/exposure are left untouched for the user to check
     * and adjust on the Options page - deliberately not an automatic retry or an automatic reset, per
     * explicit direction ("tell the user to adjust the default settings and try again, not try again
     * automatically"). A run that DOES converge overwrites the filter's stored values with what actually
     * worked, so they track the panel's slow real-world drift (aging, temperature) for free without ever
     * needing a curve.
     *
     * Everything this replaced - FlatCalibrationStore, BrightnessResponseCurve, FlatSample,
     * SmartExposurePlanner, CalibrateBrightnessSweepAsync/CalibrationSweepResult, and the "Calibrate
     * Flat Device"/"Recalibrate this filter" UI - has been deleted outright, not just deprecated. See
     * the Round 32 entry in the project KB for the full before/after reasoning.
     *
     * ROUND 33 - real-world testing showed this design converges reliably when the filter's stored
     * (brightness, exposure) pair is already close, but fails outright whenever either is off by a fair
     * amount - exactly the "FAILS OUTRIGHT" behavior described above, which was too strict in practice.
     * Per explicit user direction, ConvergeOnTargetAsync (below) now treats exposure hitting a bound as
     * an escalation trigger rather than an immediate failure: too dim even at Max Exposure raises
     * brightness a step; too bright (saturated, or the model wanting less than Min Exposure) even at Min
     * Exposure lowers it a step - then the exposure search restarts fresh at the new brightness. This is
     * NOT a return to the old joint-optimization design this file spent Rounds 13-31 abandoning - it's a
     * bounded escape valve that only engages when exposure alone has already proven insufficient, still
     * never predicts a brightness value the way exposure's linear formula does, and still fails outright
     * (no auto-retry forever) once brightness itself reaches its own configured Min/Max Brightness bound.
     * See SmartCalCaptureService.TryEscalateBrightness for the step-sizing logic and reasoning, and the
     * Round 33 entry in the project KB for the field-report that prompted this.
     *
     * ROUND 33b - the step size itself was revised on the same day, before any field test: the initial
     * Round 33 cut used bisection (half the remaining headroom to the bound), which the user pointed out
     * shrinks to a single brightness unit right near a bound - too small to meaningfully help. Replaced
     * with fixed steps: 10 for the first move in a given direction, 5 for a correction once a direction
     * flip shows that move overshot. See TryEscalateBrightness's own comment for the exact rule.
     *
     * ROUND 36 - "Flat Darks" feature: user asked to look up each filter's stored flat exposure and
     * capture matching dark frames from a tab on the imaging page. Matched-exposure darks are the more
     * rigorous calibration approach for THIS plugin's flats specifically (vs. a plain bias frame), since
     * flat exposures here vary a fair amount by filter (well under a second to several seconds) and a
     * bias-only subtraction assumes dark current over that exposure is negligible - not guaranteed across
     * that whole range. Bias frames were deliberately NOT added - they're mainly useful for dark-scaling
     * LIGHT frames, which this plugin has never touched. See RunFlatDarksAsync below for the capture
     * itself, and SmartCalFramesVM.BuildDarkGroups for how filters are grouped by shared exposure.
     */

    /// <summary>
    /// Mutable wrapper around a running frame index, shared by reference across the search phase
    /// (ConvergeOnTargetAsync) and the production loop within one RunAsync call - see the ROUND 15
    /// comment on CaptureOneAsync for why this exists. A plain int can't be threaded through async
    /// methods by reference (C# disallows ref/out parameters on async methods), so this tiny class
    /// stands in for that.
    /// </summary>
    public class FrameSequenceCounter {
        public int Next = 0;
    }

    public class SmartCalCaptureResult {
        public CalibrationBucketKey Bucket { get; set; }
        public int FramesTaken { get; set; }
        public int FramesRejected { get; set; }
        public double FinalBrightness { get; set; }
        public double FinalExposureSeconds { get; set; }
        public double AchievedMeanADU { get; set; }
        public string Summary { get; set; }
    }

    public class SmartCalCaptureService {

        private readonly ICameraMediator _cameraMediator;
        private readonly IFilterWheelMediator _filterWheelMediator;
        private readonly IFlatDeviceMediator _flatDeviceMediator;
        private readonly IImagingMediator _imagingMediator;
        private readonly IImageSaveMediator _imageSaveMediator;
        private readonly SmartCalSettingsProvider _settingsProvider;
        private readonly SmartCalSettings _settings;

        /// <summary>
        /// ROUND 42 - lets a user's DARK-type Image File Pattern override (Options -> Imaging -> Image
        /// File Pattern) put Flat Darks and regular Dark Frames into genuinely separate folders, without
        /// touching the FITS header at all. Per explicit request ("I don't want the fits header to
        /// change. What's our option") - CaptureSequence.ImageType could have been given a non-standard
        /// value (e.g. "DARKFLAT") to force separate paths, but that also changes what's written into the
        /// FITS IMAGETYP keyword, which risks breaking calibration-frame auto-matching in stacking
        /// software that expects the standard value - ruled out for that reason.
        ///
        /// The mechanism actually used - confirmed against the real installed NINA.WPF.Base.dll/
        /// NINA.Core.dll metadata, not guessed: IImageSaveMediator exposes a
        /// BeforeFinalizeImageSaved event (Func&lt;object, BeforeFinalizeImageSavedEventArgs, Task&gt;),
        /// and BeforeFinalizeImageSavedEventArgs exposes AddImagePattern(ImagePattern) - the same
        /// mechanism NINA itself uses to make its own built-in $$...$$ tokens available for the Image
        /// File Pattern. This only feeds the file-PATH pattern-resolution engine
        /// (NINA.Core.Model.ImagePatterns) - a completely separate code path from ImageMetaData/the FITS
        /// header, confirmed by checking both types' real members: ImagePattern has no relation to
        /// ImageMetaData at all. Registering a pattern here has zero effect on frame content, only on
        /// where NINA decides to save it if - and only if - the user's own DARK pattern in Options
        /// actually references the new token.
        ///
        /// Subscribed only for the duration of each function's own capture loop (subscribe right before,
        /// unsubscribe in that function's existing finally block) rather than for this service's whole
        /// lifetime - BeforeFinalizeImageSaved is a NINA-wide event that fires for every image NINA ever
        /// saves during the session, from any source, so leaving a permanent subscription would add this
        /// token (harmlessly, but pointlessly) to unrelated captures too, and a permanent subscription on
        /// a service that gets recreated per-run risks stacking duplicate handlers. Safe here because
        /// this plugin's own captures are always awaited sequentially, never concurrent with each other.
        ///
        /// ROUND 42 CORRECTION - the key MUST include the $$ delimiters itself; they are not stripped
        /// by NINA's substitution. Verified directly against NINA.Core.dll's real IL this round (not
        /// guessed, and correcting an earlier wrong assumption in this same file's history): the earlier
        /// belief that ImagePatternKeys' fields hold bare names like "Filter" was based only on reading
        /// the Field table's C# identifier NAMES via dnfile (e.g. the field is named "Filter") - not the
        /// actual string VALUE assigned to that field at runtime. Disassembling ImagePatternKeys' static
        /// constructor (.cctor) and resolving its ldstr operands against the #US heap shows the real
        /// value of ImagePatternKeys.Filter is the literal string "$$FILTER$$", dollar signs included -
        /// same for every other built-in key. ImagePatterns.GetImageFileString then does a plain
        /// `result = result.Replace(pattern.Key, pattern.Value)` (confirmed via IL: get_Key, get_Value,
        /// then a single String.Replace call, per pattern, no regex, no delimiter-stripping logic
        /// anywhere in that method) - so whatever text sits in Key is exactly what gets consumed by the
        /// replace, and exactly what's left behind is whatever the user typed that ISN'T Key.
        ///
        /// With the earlier bare key ("SCFFRAMEKIND"), the user typed $$SCFFRAMEKIND$$ into their DARK
        /// pattern (reasonably, since every built-in token is presented/typed that way) - Replace() then
        /// only ever matched and swapped the inner "SCFFRAMEKIND" substring, leaving the two pairs of
        /// dollar signs the user typed on either side completely untouched in the resolved folder name
        /// ("$$FlatDark$$" instead of just "FlatDark"). This is exactly the folder name the user
        /// reported seeing, and exactly what they're now asking to get rid of. Fixed by making the key
        /// itself "$$SCFFRAMEKIND$$" (matching the real ImagePatternKeys convention), so Replace()
        /// consumes the whole token the user types, dollar signs included, leaving just the plain value.
        /// </summary>
        private const string FrameKindPatternKey = "$$SCFFRAMEKIND$$";

        private static Func<object, BeforeFinalizeImageSavedEventArgs, Task> MakeFrameKindPatternHandler(string frameKind) {
            return (sender, e) => {
                e.AddImagePattern(new ImagePattern(
                    FrameKindPatternKey,
                    "Smart Calibration Frames - distinguishes Flat Darks from regular Dark Frames when both use the DARK image type. Value is 'Flat Darks' or 'Dark'.") {
                    Value = frameKind
                });
                return Task.CompletedTask;
            };
        }

        /// <summary>
        /// Run-log-to-file support. Null whenever LogToFileEnabled is off, or once a write attempt has
        /// failed (see WriteToLogFile below) - both cases mean "don't try again this run." Computed once
        /// per SERVICE INSTANCE, not per RunAsync call, so a "Run All Filters" pass - which constructs one
        /// SmartCalCaptureService and calls this instance's own RunAsync once per filter, see
        /// RunAllFiltersAsync below - gets exactly one file covering every filter in that run, matching
        /// the dockable panel's own on-screen RunLog lifecycle rather than starting a new file per filter.
        /// </summary>
        private string _logFilePath;

        /// <summary>
        /// ROUND 32t - REVERTS Round 32s's ProbeProgress approach after real field evidence falsified its
        /// core assumption. The theory was that NINA's SetBrightness only calls progress.Report(...) when
        /// it decides a real change is needed, mirroring its own Logger.Info gating - but a field log
        /// proved otherwise: Lum's SetBrightness(38) produced no "Setting brightness to 38" in NINA's own
        /// log (a real no-op, confirmed - the very next filter, Red, DID get a real "Setting brightness to
        /// 49" logged for comparison), yet the plugin's own SmartCals log shows none of Round 32s's
        /// "did not report anything... forcing a real change" fallback line either - meaning the probe's
        /// Fired flag came back true despite no real command reaching the ESP32. NINA's progress.Report
        /// call is apparently unconditional, fired on every SetBrightness call regardless of whether
        /// anything actually changes, while Logger.Info (and the real hardware call) are separately gated
        /// on the real equality check. Watching Report can't distinguish the two, so this design can never
        /// catch the exact case it was built for.
        ///
        /// Given GetInfo() (Round 32o) and progress-probing (Round 32s) have now BOTH been empirically
        /// falsified by real field evidence in exactly one test each, this goes back to the one design
        /// never once shown to fail across the whole project: Round 32j/32l's plain value tracker. Skip
        /// the bracket ONLY when we have a known, real value that's provably different from what we're
        /// about to request - a genuine change always produces a real SetBrightness on its own (Round
        /// 32h). Unknown (nothing commanded yet this session) or known-and-equal both still get the full
        /// bracket, no attempt to cleverly detect or skip that case any further. Static for the same
        /// reason as always: both SmartCalFramesVM and SmartCalSequenceItem construct a brand-new
        /// SmartCalCaptureService on every invocation, so only a static field survives across runs.
        ///
        /// Same residual risk as every round since 32j, unavoidable and accepted: this only reflects what
        /// Smart Calibration Frames itself last commanded. If NINA's native Flat Wizard (or anything else) sets
        /// the panel's brightness in between Smart Calibration Frames runs, this tracker won't see it - safe as
        /// long as Smart Calibration Frames is the only thing driving the panel during a session.
        /// </summary>
        private static int? s_lastCommandedBrightness = null;

        /// <summary>
        /// ROUND 32: takes the settings provider directly now (needed so a converged run can write this
        /// filter's new brightness/exposure straight back to Options-page storage), instead of a
        /// FlatCalibrationStore instance - see the header comment above for the full redesign.
        /// </summary>
        public SmartCalCaptureService(
            ICameraMediator cameraMediator,
            IFilterWheelMediator filterWheelMediator,
            IFlatDeviceMediator flatDeviceMediator,
            IImagingMediator imagingMediator,
            IImageSaveMediator imageSaveMediator,
            SmartCalSettingsProvider settingsProvider,
            SmartCalSettings settings) {
            _cameraMediator = cameraMediator;
            _filterWheelMediator = filterWheelMediator;
            _flatDeviceMediator = flatDeviceMediator;
            _imagingMediator = imagingMediator;
            _imageSaveMediator = imageSaveMediator;
            _settingsProvider = settingsProvider;
            _settings = settings;

            if (_settings.LogToFileEnabled) {
                try {
                    _logFilePath = Path.Combine(SmartCalSettingsProvider.LogFileFolder, $"SmartCals_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                    File.AppendAllText(_logFilePath, $"{DateTime.Now:HH:mm:ss}  === Smart Calibration Frames run started ==={Environment.NewLine}");
                } catch (Exception ex) {
                    // Best-effort only, same philosophy as the ToggleLight(false) cleanup below - a
                    // logging convenience must never stop the actual flat capture run from working, and
                    // NINA's own log still gets every line regardless (Logger.Info calls throughout this
                    // file are unaffected by this failing).
                    Logger.Warning($"Smart Calibration Frames: could not create run log file - {ex.Message}. Continuing without file logging for this run.");
                    _logFilePath = null;
                }
            }
        }

        /// <summary>
        /// Appends one line to this run's log file, if LogToFileEnabled is on and file logging hasn't
        /// already failed once this run (see _logFilePath above). Opens, writes, and closes the file on
        /// every call rather than holding a writer open for the whole run - slightly more I/O, but it
        /// means every line is safely on disk the moment it's written and there's no handle to leak or
        /// forget to close if a run ends via an exception, Stop, or (per this project's Round 14 history)
        /// NINA itself going down mid-run.
        /// </summary>
        private void WriteToLogFile(string line) {
            if (_logFilePath == null) return;
            try {
                File.AppendAllText(_logFilePath, $"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
            } catch (Exception ex) {
                Logger.Warning($"Smart Calibration Frames: run log file write failed - {ex.Message}. Continuing without file logging for the rest of this run.");
                _logFilePath = null;
            }
        }

        /// <summary>
        /// ROUND 17 - SECOND HALF of the FRAMENR fix. The caller (SmartCalFramesVM or
        /// SmartCalSequenceItem) owns one FrameSequenceCounter per invocation, seeded from a value
        /// persisted across runs/filters/NINA restarts, and passes it in here rather than this method
        /// creating its own - see the caller for where it's loaded and saved back.
        /// </summary>
        public async Task<SmartCalCaptureResult> RunAsync(
            FilterInfo filter,
            int binningX, int binningY, int gain, int offset,
            FrameSequenceCounter frameCounter,
            IProgress<ApplicationStatus> progress,
            CancellationToken token) {

            var key = new CalibrationBucketKey(filter?.Name, binningX, binningY, gain, offset);
            progress.Report(new ApplicationStatus { Status = $"Smart Calibration Frames: {key}" });
            WriteToLogFile($"Smart Calibration Frames: {key}");

            if (filter != null) {
                await _filterWheelMediator.ChangeFilter(filter, token, progress);
            }

            // ROUND 32l - REMOVED the ToggleLight(true) call that used to sit here. Two independent
            // pieces of evidence made this an easy call: (1) the ASCOM standard itself gives this driver
            // exactly one way to turn the panel on - CalibratorOn(int Brightness) - which is the SAME
            // call SetBrightness already goes through, and which the ESP32 firmware's "SET n" always
            // turns the relay on for, any n, no exceptions - so a real SetBrightness call already
            // guarantees the light is on, with nothing extra required. (2) Real hardware evidence from
            // Round 32k's testing showed that on the rare occasions ToggleLight(true) DOES actually fire
            // for real (rather than its usual silent no-op), it drives the panel to a highly visible full
            // brightness (255) as some internal default, before the very next SetBrightness call brings
            // it back down - a real, user-visible flash for zero benefit, since it was never doing
            // anything SetBrightness wasn't already doing on its own. Removing it fixes that flash outright
            // and simplifies the whole on/off story down to one real mechanism (SetBrightness) instead of
            // two overlapping, unpredictable ones.
            double targetADU = _settings.TargetADUPercent * GetFullWellADU();

            // ROUND 15 - reused across every exposure in this run (search AND production frames), rather
            // than each CaptureOneAsync call building its own CaptureSequence from scratch - see the
            // header comment on CaptureOneAsync below for why (NINA's $$FRAMENR$$ file pattern token).
            var capture = new CaptureSequence();

            var means = new List<double>();
            int rejected = 0;
            PlannedExposure plan;
            CapturedFlatFrame convergedFrame = null;

            // ROUND 16 - SAFETY FIX. Wrapping the search + capture loop in try/finally guarantees a
            // best-effort panel-off attempt runs no matter how this exits - success, exception,
            // cancellation, or (ROUND 32) an early return for a failed calibration.
            //
            // The finally's own ToggleLight call deliberately passes CancellationToken.None, not the
            // run's own token: if the reason we're in finally is that the token got cancelled (a normal
            // user-initiated Stop), reusing that same cancelled token here would make THIS call throw
            // immediately too and skip turning the panel off.
            try {
                // ROUND 32d - this line was a stale leftover from the pre-Round-32 design (when exposure
                // really was pinned to Min Exposure throughout). It never got updated for Round 32's
                // per-filter fixed-brightness/searched-exposure redesign, so it kept printing
                // _settings.MinExposureSeconds (0.1s) here regardless of what the search was actually
                // about to try - misleadingly implying every run "starts at 0.1s" even when the real
                // per-filter starting exposure (read inside ConvergeOnTargetAsync) was correct all along.
                // Now logs the SAME per-filter values ConvergeOnTargetAsync is about to read, so this
                // first line of the run's log matches reality instead of a fossil from an earlier design.
                int startingBrightness = _settingsProvider.GetFilterBrightness(key.FilterName, _settings.MinBrightness);
                // ROUND 44 - fallback changed from MinExposureSeconds to PreferredExposureSeconds: a
                // never-converged filter now starts its search from a user-chosen realistic exposure
                // (matters most for CCDs, which need much more than the old 0.1s floor to get a
                // reliable first ADU reading) instead of always starting at Min Exposure. Once a filter
                // HAS converged, GetFilterExposureSeconds returns its own real stored value and this
                // fallback never comes into play for it again - see SmartCalOptionsView.xaml's own
                // updated description for the full reasoning.
                double startingExposure = _settingsProvider.GetFilterExposureSeconds(key.FilterName, _settings.PreferredExposureSeconds);

                // ROUND 32i - the Round 32h dummy-bracket fix was applied to EVERY capture (every search
                // attempt AND every production frame), but real-hardware log evidence only ever showed a
                // failure on the FIRST capture of a run - every frame after that, at the same fixed
                // brightness, worked fine with no repeated SetBrightness at all, because the panel just
                // holds whatever it was last really told (per direction: no need to touch it again unless
                // it needs to change, and brightness never changes mid-run by Round 32's own design - only
                // exposure gets corrected if the ADU drifts). So the dummy-bracket now runs exactly ONCE,
                // here, before this run's first capture - not once per frame. CaptureOneAsync itself is
                // back to a plain SetBrightness (Round 32f's version) since by the time it runs, the panel
                // is already guaranteed to be at the right value.
                //
                // ROUND 32t - REVERTS Round 32s (see s_lastCommandedBrightness's own comment above for the
                // field evidence that falsified it). Back to Round 32j/32l's plain value tracker: skip the
                // bracket ONLY when we have a known, real value that's provably different from what we're
                // about to request. Unknown (nothing commanded yet this session) or known-and-equal both
                // still get the full bracket - no cleverness left in this decision.
                bool knownAndDifferent = s_lastCommandedBrightness.HasValue && s_lastCommandedBrightness.Value != startingBrightness;

                if (knownAndDifferent) {
                    await _flatDeviceMediator.SetBrightness(startingBrightness, progress, token);
                } else {
                    int dummyBrightness = startingBrightness <= 1 ? startingBrightness + 1 : startingBrightness - 1;
                    // ROUND 32q - the dummy step is a pure internal trick to stop NINA's own SetBrightness
                    // from silently no-op'ing on a value it already believes is current (see Round 32h) -
                    // it was never meant to be user-visible. Passing a real, do-nothing IProgress here
                    // (instead of the shared `progress`) keeps NINA's own report of this step from ever
                    // reaching SmartCalFramesVM's on-screen StatusText/RunLog, without touching the actual
                    // command sent to the panel, WriteToLogFile, or NINA's own log file (none of which are
                    // wired through this specific IProgress instance anyway). Deliberately NOT `null` -
                    // NINA's own SetBrightness may call progress.Report(...) without a null-check, and this
                    // exact bracket mechanism has already broken hardware three separate times (Rounds 32f,
                    // 32h, 32k), so a real no-op object is the safe way to silence it. The very next line,
                    // the real SetBrightness, still uses the shared `progress` and displays normally.
                    var silentProgress = new Progress<ApplicationStatus>(_ => { });
                    await _flatDeviceMediator.SetBrightness(dummyBrightness, silentProgress, token);
                    await _flatDeviceMediator.SetBrightness(startingBrightness, progress, token);
                }
                s_lastCommandedBrightness = startingBrightness;

                // ROUND 33 - dropped "(fixed)" here: brightness now only STARTS at this filter's stored
                // default, and may be nudged up/down by ConvergeOnTargetAsync's escalation if exposure
                // alone can't reach target (see TryEscalateBrightness) - it's no longer guaranteed fixed
                // for the whole run the way it was under the pure Round 32 design.
                string searchStartLine = $"Smart Calibration Frames: {key} searching for {targetADU:F0} ADU starting at brightness {startingBrightness}, {startingExposure:F2}s exposure";
                progress.Report(new ApplicationStatus { Status = searchStartLine });
                WriteToLogFile(searchStartLine);
                (plan, convergedFrame) = await ConvergeOnTargetAsync(key, targetADU, filter, binningX, binningY, gain, offset, capture, frameCounter, progress, token);

                // ROUND 32 - calibration failed: exposure alone couldn't reach target within Min/Max
                // Exposure at this filter's fixed brightness. Per explicit direction, do NOT save
                // anything and do NOT touch this filter's stored brightness/exposure - just report the
                // failure plainly and let the user adjust the Options-page values themselves before the
                // next attempt.
                if (plan.Kind == PlanKind.Failed) {
                    string failLine = $"Smart Calibration Frames: {key} - CALIBRATION FAILED: {plan.Reason} No flat frames were saved for this filter - " +
                                       "adjust its default brightness/exposure on the Options page and try again.";
                    progress.Report(new ApplicationStatus { Status = failLine });
                    Logger.Error(failLine);
                    WriteToLogFile(failLine);
                    return new SmartCalCaptureResult {
                        Bucket = key,
                        FramesTaken = 0,
                        FramesRejected = 0,
                        FinalBrightness = plan.Brightness,
                        FinalExposureSeconds = plan.ExposureSeconds,
                        AchievedMeanADU = plan.PredictedADU,
                        Summary = failLine
                    };
                }

                // Production frames: keep shooting until we have enough ACCEPTED frames for a clean
                // stack AND the accepted group's running mean has stabilized, or we hit the configured
                // cap on total attempts (accepted + rejected combined). Each captured frame is first
                // checked in isolation against the frames already accepted (Frame outlier tolerance)
                // before it's allowed to join the group; only frames that pass get saved to disk and
                // counted toward Min/Max.
                int attempts = 0;

                // ROUND 19 - SPEED FIX. The frame that just converged the search above is a real,
                // already-captured flat at exactly plan.Brightness/plan.ExposureSeconds - the same
                // settings the production loop below is about to shoot at anyway. Reusing it here saves
                // exactly one production-frame cycle on every run, for free.
                if (convergedFrame != null) {
                    attempts++;
                    means.Add(convergedFrame.Stats.Mean);
                    await _imageSaveMediator.Enqueue(convergedFrame.Rendered.RawImageData, Task.FromResult(convergedFrame.Rendered), progress, token);
                    string reusedLine = $"Smart Calibration Frames: {key} frame {means.Count} (reused the converged search frame - no extra capture needed) - " +
                                         $"brightness {plan.Brightness}, {plan.ExposureSeconds:F2}s - mean {convergedFrame.Stats.Mean:F0} ADU (target {targetADU:F0})";
                    progress.Report(new ApplicationStatus { Status = reusedLine });
                    Logger.Info(reusedLine);
                    WriteToLogFile(reusedLine);
                }
                while (attempts < _settings.MaxFlatsPerFilter) {
                    token.ThrowIfCancellationRequested();
                    attempts++;
                    var frame = await CaptureOneAsync(capture, filter, plan.Brightness, plan.ExposureSeconds, binningX, binningY, gain, offset, frameCounter, progress, token);

                    if (means.Count > 0 && IsOutlier(frame.Stats.Mean, means, _settings.FrameOutlierTolerancePercent)) {
                        rejected++;
                        string rejectLine = $"Smart Calibration Frames: {key} frame rejected - mean {frame.Stats.Mean:F0} ADU is an outlier vs. accepted frames so far, retrying";
                        progress.Report(new ApplicationStatus { Status = rejectLine });
                        WriteToLogFile(rejectLine);
                        continue;
                    }

                    means.Add(frame.Stats.Mean);

                    // Preparation is already complete by the time we have frame.Rendered (this plugin
                    // captures via the combined CaptureAndPrepareImage, not separate Capture+Prepare
                    // calls), so there's nothing left for Enqueue to await.
                    await _imageSaveMediator.Enqueue(frame.Rendered.RawImageData, Task.FromResult(frame.Rendered), progress, token);

                    string frameLine = $"Smart Calibration Frames: {key} frame {means.Count} - brightness {plan.Brightness}, {plan.ExposureSeconds:F2}s - mean {frame.Stats.Mean:F0} ADU (target {targetADU:F0})";
                    progress.Report(new ApplicationStatus { Status = frameLine });
                    Logger.Info(frameLine);
                    WriteToLogFile(frameLine);

                    bool enoughFrames = means.Count >= _settings.MinFlatsPerFilter;
                    bool stable = IsStable(means, _settings.StackStabilityThreshold);
                    if (enoughFrames && stable) break;
                }
            } finally {
                try {
                    // ROUND 32l - back to a bare ToggleLight(false), Round 32j's version (and every round
                    // before 32k). Round 32k added a SetBrightness(0) call right before this one, hoping
                    // to leave the panel at a known sentinel for the next run - but real hardware evidence
                    // showed NINA's FlatDeviceVM treats "brightness == 0" as equivalent to "already off"
                    // internally, which made THIS call silently no-op every single time right after that
                    // SetBrightness(0) ran (confirmed: 0 real "Toggling light to False" executions across
                    // a full session, versus 100% reliability in every log before Round 32k). Since
                    // SetBrightness(0) still turns the relay ON regardless (per the firmware's "SET n"
                    // behavior), that combination left the panel lit - dim, but never actually off - at
                    // the end of every run. Dropping the SetBrightness(0) call restores this to the one
                    // thing that's been reliable across the whole project: a plain ToggleLight(false),
                    // nothing else touching brightness around it.
                    await _flatDeviceMediator.ToggleLight(false, progress, CancellationToken.None);
                } catch (Exception offEx) {
                    string offFailLine = $"Smart Calibration Frames: {key} - failed to turn off the flat panel during cleanup: " +
                                          $"{offEx.Message}. Manually verify the panel is off.";
                    Logger.Error(offFailLine);
                    WriteToLogFile(offFailLine);
                }
            }

            double achievedMean = means.Count > 0 ? means.Average() : 0;

            bool stoppedShortOnRejections = means.Count < _settings.MinFlatsPerFilter;
            string rejectionNote = rejected > 0
                ? $" ({rejected} frame{(rejected == 1 ? "" : "s")} rejected as outliers{(stoppedShortOnRejections ? " - stopped at Max attempts before reaching Min accepted" : "")}.)"
                : "";

            string summary = $"{key}: {means.Count} flats at brightness {plan.Brightness}, {plan.ExposureSeconds:F2}s - mean {achievedMean:F0} ADU ({plan.Reason}){rejectionNote}";
            WriteToLogFile(summary);

            return new SmartCalCaptureResult {
                Bucket = key,
                FramesTaken = means.Count,
                FramesRejected = rejected,
                FinalBrightness = plan.Brightness,
                FinalExposureSeconds = plan.ExposureSeconds,
                AchievedMeanADU = achievedMean,
                Summary = summary
            };
        }

        /// <summary>Runs every filter in the wheel unattended, in filter-wheel position order. One filter failing calibration (see RunAsync) doesn't stop the rest.</summary>
        public async Task<List<SmartCalCaptureResult>> RunAllFiltersAsync(
            IEnumerable<FilterInfo> allFilters,
            int binningX, int binningY, int gain, int offset,
            FrameSequenceCounter frameCounter,
            IProgress<ApplicationStatus> progress,
            CancellationToken token) {

            var ordered = allFilters.OrderBy(f => f.Position).ToList();

            var results = new List<SmartCalCaptureResult>();
            foreach (var filter in ordered) {
                token.ThrowIfCancellationRequested();
                // Same frameCounter instance passed to every filter, deliberately - it must keep
                // advancing across filters within one "Run All", not just within one filter's own
                // RunAsync call, or two different filters would collide with each other's numbers.
                results.Add(await RunAsync(filter, binningX, binningY, gain, offset, frameCounter, progress, token));
            }
            return results;
        }

        /// <summary>
        /// ROUND 36 - "Flat Darks": captures dark frames matched to each filter's stored flat exposure,
        /// one dark GROUP per UNIQUE exposure the caller passes in (filters sharing an exposure share a
        /// group - built by SmartCalFramesVM.BuildDarkGroups, not here, since grouping needs the active
        /// profile's filter list, which this service doesn't hold a reference to).
        ///
        /// Darks are captured at whatever binning/gain/offset the CALLER passes in - this plugin has
        /// never stored gain/offset/binning per filter (only brightness/exposure, keyed by filter name -
        /// see SmartCalSettingsProvider), so there's nothing per-filter to look up for those three; the
        /// caller passes the SAME convention flats themselves already use (current camera gain/offset,
        /// bin1x1 - see RunAsync's own callers). No filter wheel move happens here - a dark frame doesn't
        /// depend on filter position, so the wheel is simply left wherever it already is.
        ///
        /// The flat panel's light is turned OFF (best-effort, logged-not-fatal if it fails) before the
        /// first group and again in a finally block, mirroring RunAsync's own cleanup - since this
        /// plugin's flat panel physically covers the aperture, leaving the cover in place with the light
        /// off gives a true "flat dark" through the exact same optical path, with no need to separately
        /// cap the scope. ToggleLight(false) is the one call in this whole project confirmed 100%
        /// reliable across every field log collected (see s_lastCommandedBrightness's own comment for
        /// that history) - safe to lean on here too.
        /// </summary>
        public async Task<List<string>> RunFlatDarksAsync(
            IEnumerable<double> uniqueExposures,
            int binningX, int binningY, int gain, int offset, int framesPerGroup,
            FrameSequenceCounter frameCounter,
            IProgress<ApplicationStatus> progress, CancellationToken token) {

            var capture = new CaptureSequence();
            var summaries = new List<string>();

            // ROUND 42 - see FrameKindPatternHandler's own doc comment for the full mechanism/reasoning.
            var frameKindHandler = MakeFrameKindPatternHandler("Flat Darks");
            _imageSaveMediator.BeforeFinalizeImageSaved += frameKindHandler;

            try {
                try {
                    await _flatDeviceMediator.ToggleLight(false, progress, token);
                } catch (Exception onEx) {
                    string onFailLine = $"Smart Calibration Frames: flat-darks - could not confirm the flat panel's light is off before starting: {onEx.Message}. Continuing - verify manually if frames look wrong.";
                    Logger.Warning(onFailLine);
                    WriteToLogFile(onFailLine);
                }

                foreach (var exposure in uniqueExposures) {
                    token.ThrowIfCancellationRequested();

                    string startLine = $"Smart Calibration Frames: starting {framesPerGroup} flat-dark frame(s) at {exposure:F2}s (bin{binningX}x{binningY}, gain {gain}, offset {offset}).";
                    progress.Report(new ApplicationStatus { Status = startLine });
                    Logger.Info(startLine);
                    WriteToLogFile(startLine);

                    for (int i = 1; i <= framesPerGroup; i++) {
                        token.ThrowIfCancellationRequested();

                        capture.ExposureTime = exposure;
                        capture.ImageType = CaptureSequence.ImageTypes.DARK;
                        capture.Binning = new BinningMode((short)binningX, (short)binningY);
                        capture.Gain = gain;
                        capture.Offset = offset;
                        capture.ProgressExposureCount = frameCounter.Next++;

                        var prepareParams = new PrepareImageParameters(autoStretch: false, detectStars: false);
                        var rendered = await _imagingMediator.CaptureAndPrepareImage(capture, prepareParams, token, progress);
                        await _imageSaveMediator.Enqueue(rendered.RawImageData, Task.FromResult(rendered), progress, token);

                        string frameLine = $"Smart Calibration Frames: dark {i}/{framesPerGroup} at {exposure:F2}s saved.";
                        progress.Report(new ApplicationStatus { Status = frameLine });
                        WriteToLogFile(frameLine);
                    }

                    string doneLine = $"Smart Calibration Frames: finished {framesPerGroup} flat-dark frame(s) at {exposure:F2}s.";
                    progress.Report(new ApplicationStatus { Status = doneLine });
                    Logger.Info(doneLine);
                    WriteToLogFile(doneLine);
                    summaries.Add(doneLine);
                }
            } finally {
                _imageSaveMediator.BeforeFinalizeImageSaved -= frameKindHandler;
                try {
                    await _flatDeviceMediator.ToggleLight(false, progress, CancellationToken.None);
                } catch (Exception offEx) {
                    string offFailLine = $"Smart Calibration Frames: flat-darks - failed to confirm the flat panel is off during cleanup: {offEx.Message}. Manually verify the panel is off.";
                    Logger.Error(offFailLine);
                    WriteToLogFile(offFailLine);
                }
            }

            return summaries;
        }

        /// <summary>
        /// ROUND 39, renamed same session from "Light Darks"/RunLightDarksAsync per explicit user
        /// feedback ("I've never heard of light dark frames") - "Dark Frames" tab: captures dark frames
        /// matched to each filter's own light-frame (Exposure, Binning, Frame count) - unlike
        /// RunFlatDarksAsync above, binning is NOT fixed for the whole run here, since the whole point of
        /// this tab (per explicit direction) is letting a filter that's actually shot binned (2x2
        /// narrowband, say) get a dark that matches, rather than this plugin's usual hardcoded bin1x1.
        /// Gain/offset ARE still the caller's current-camera-settings convention (this plugin has never
        /// stored those per filter), same as every other tab.
        ///
        /// Groups are built by SmartCalRunPlanning.BuildDarkFrameGroups (ROUND 40 - moved out of
        /// SmartCalFramesVM so the multi-function sequencer item can build the same groups), not here
        /// (same division of responsibility as RunFlatDarksAsync/BuildDarkGroups - grouping needs the
        /// active profile's filter list, which this service doesn't hold a reference to): filters sharing BOTH the same
        /// exposure AND the same binning share one group, using the LARGER of the two requested frame
        /// counts (per explicit direction - a filter that asked for fewer frames just gets some harmless
        /// surplus rather than being under-served). FilterNames is carried through purely for the
        /// log/status lines below ("<- filters"), it doesn't change what's captured - a dark frame
        /// doesn't depend on filter position, so (same as RunFlatDarksAsync) the filter wheel is never
        /// touched here.
        ///
        /// ROUND 39 REVISION - one group failing mid-capture no longer aborts the whole run: each group
        /// now has its own try/catch, mirroring RunAllFiltersAsync's "one filter failing doesn't stop the
        /// rest" philosophy for flats. A Stop (OperationCanceledException) still aborts everything
        /// immediately - that's a deliberate user action, not a per-group failure, so it's re-thrown
        /// rather than folded into a group's summary. Every group's outcome (frames actually captured vs.
        /// requested, and why if it didn't finish) is returned in the summary list so SmartCalFramesVM
        /// can show one end-of-run summary the same way flats' "Run all filters" already does - per
        /// explicit user request, so a failed group is impossible to miss without scrolling back through
        /// every individual frame line.
        ///
        /// Reuses the same flat-panel-as-cover trick as RunFlatDarksAsync/RunBiasFramesAsync: the
        /// panel's light is turned OFF (best-effort, logged-not-fatal) before the first group and again
        /// in a finally block, so the cover stays in place and every frame is taken through the same
        /// optical path as every other calibration frame this plugin produces.
        /// </summary>
        public async Task<List<string>> RunDarkFramesAsync(
            IEnumerable<(double ExposureSeconds, int Binning, int FrameCount, List<string> FilterNames)> groups,
            int gain, int offset,
            FrameSequenceCounter frameCounter,
            IProgress<ApplicationStatus> progress, CancellationToken token) {

            var capture = new CaptureSequence();
            var summaries = new List<string>();

            // ROUND 42 - see FrameKindPatternHandler's own doc comment (above RunFlatDarksAsync) for the
            // full mechanism/reasoning. "Dark" here (vs "Flat Darks" there) is what gives the two functions
            // different values for the same $$SCFFRAMEKIND$$ token, so a single DARK-type pattern
            // override that includes it separates both into their own folders.
            var frameKindHandler = MakeFrameKindPatternHandler("Dark");
            _imageSaveMediator.BeforeFinalizeImageSaved += frameKindHandler;

            try {
                try {
                    await _flatDeviceMediator.ToggleLight(false, progress, token);
                } catch (Exception onEx) {
                    string onFailLine = $"Smart Calibration Frames: dark frames - could not confirm the flat panel's light is off before starting: {onEx.Message}. Continuing - verify manually if frames look wrong.";
                    Logger.Warning(onFailLine);
                    WriteToLogFile(onFailLine);
                }

                foreach (var group in groups) {
                    token.ThrowIfCancellationRequested();

                    string filterList = string.Join(", ", group.FilterNames);
                    string groupKey = $"{group.ExposureSeconds:F2}s bin{group.Binning}x{group.Binning}";
                    string startLine = $"Smart Calibration Frames: starting {group.FrameCount} dark frame(s) at {groupKey} (gain {gain}, offset {offset}) <- {filterList}.";
                    progress.Report(new ApplicationStatus { Status = startLine });
                    Logger.Info(startLine);
                    WriteToLogFile(startLine);

                    int captured = 0;
                    try {
                        for (int i = 1; i <= group.FrameCount; i++) {
                            token.ThrowIfCancellationRequested();

                            capture.ExposureTime = group.ExposureSeconds;
                            capture.ImageType = CaptureSequence.ImageTypes.DARK;
                            capture.Binning = new BinningMode((short)group.Binning, (short)group.Binning);
                            capture.Gain = gain;
                            capture.Offset = offset;
                            capture.ProgressExposureCount = frameCounter.Next++;

                            var prepareParams = new PrepareImageParameters(autoStretch: false, detectStars: false);
                            var rendered = await _imagingMediator.CaptureAndPrepareImage(capture, prepareParams, token, progress);
                            await _imageSaveMediator.Enqueue(rendered.RawImageData, Task.FromResult(rendered), progress, token);
                            captured++;

                            string frameLine = $"Smart Calibration Frames: dark {i}/{group.FrameCount} at {groupKey} saved.";
                            progress.Report(new ApplicationStatus { Status = frameLine });
                            WriteToLogFile(frameLine);
                        }

                        string doneLine = $"{groupKey}: {captured}/{group.FrameCount} dark frame(s) captured <- {filterList}";
                        progress.Report(new ApplicationStatus { Status = $"Smart Calibration Frames: finished {doneLine}" });
                        Logger.Info($"Smart Calibration Frames: finished {doneLine}");
                        WriteToLogFile($"Smart Calibration Frames: finished {doneLine}");
                        summaries.Add(doneLine);
                    } catch (OperationCanceledException) {
                        // A Stop is a deliberate user action, not this group's failure - let it abort the
                        // whole run (caught by the outer try/finally below) rather than being recorded as
                        // a per-group FAILED summary line.
                        throw;
                    } catch (Exception ex) {
                        string failLine = $"{groupKey}: FAILED after {captured}/{group.FrameCount} dark frame(s) - {ex.Message} <- {filterList}";
                        Logger.Error($"Smart Calibration Frames: {failLine}");
                        WriteToLogFile($"Smart Calibration Frames: {failLine}");
                        progress.Report(new ApplicationStatus { Status = $"Smart Calibration Frames: {failLine}" });
                        summaries.Add(failLine);
                    }
                }
            } finally {
                _imageSaveMediator.BeforeFinalizeImageSaved -= frameKindHandler;
                try {
                    await _flatDeviceMediator.ToggleLight(false, progress, CancellationToken.None);
                } catch (Exception offEx) {
                    string offFailLine = $"Smart Calibration Frames: dark frames - failed to confirm the flat panel is off during cleanup: {offEx.Message}. Manually verify the panel is off.";
                    Logger.Error(offFailLine);
                    WriteToLogFile(offFailLine);
                }
            }

            return summaries;
        }

        /// <summary>
        /// ROUND 1 (Smart Cal Frames) - "Bias Frames" tab: captures a fixed count of bias frames at a
        /// fixed exposure, at whatever binning/gain/offset the caller passes in - same convention
        /// RunFlatDarksAsync above already uses (this plugin has never stored gain/offset/binning per
        /// filter, only brightness/exposure, so there's nothing per-filter to look up here either). No
        /// filter wheel move (bias is filter-independent, same reasoning as a dark frame), and no
        /// grouping - unlike flat-darks, every bias frame shares the exact same exposure by definition,
        /// so there's only ever one group.
        ///
        /// ROUND 37d - this method's own signature/logic is unchanged, but where exposureSeconds comes
        /// from changed: the caller no longer reads a stored SmartCalSettings.BiasExposureSeconds
        /// (removed - there is no such setting anymore). Instead it resolves the connected camera's own
        /// real minimum exposure live via SmartCalRunPlanning.ResolveBiasExposureSeconds (ROUND 40 -
        /// moved out of SmartCalFramesVM so the multi-function sequencer item's Bias function resolves it
        /// identically; CameraInfo.ExposureMin, confirmed against the real installed NINA.Equipment.dll,
        /// falling back to the flats' MinExposureSeconds floor only if the camera doesn't report a usable
        /// value) and passes the result in as this parameter.
        ///
        /// Reuses the same flat-panel-as-cover trick as RunFlatDarksAsync: the panel's light is turned
        /// OFF (best-effort, logged-not-fatal) before capture and again in a finally block, so the cover
        /// stays in place and the frame is taken through the same optical path as every other calibration
        /// frame this plugin produces - no need to separately cap the scope.
        /// </summary>
        public async Task<string> RunBiasFramesAsync(
            int frameCount, double exposureSeconds,
            int binningX, int binningY, int gain, int offset,
            FrameSequenceCounter frameCounter,
            IProgress<ApplicationStatus> progress, CancellationToken token) {

            var capture = new CaptureSequence();
            int saved = 0;

            try {
                try {
                    await _flatDeviceMediator.ToggleLight(false, progress, token);
                } catch (Exception onEx) {
                    string onFailLine = $"Smart Calibration Frames: bias frames - could not confirm the flat panel's light is off before starting: {onEx.Message}. Continuing - verify manually if frames look wrong.";
                    Logger.Warning(onFailLine);
                    WriteToLogFile(onFailLine);
                }

                string startLine = $"Smart Calibration Frames: starting {frameCount} bias frame(s) at {exposureSeconds:F4}s (bin{binningX}x{binningY}, gain {gain}, offset {offset}).";
                progress.Report(new ApplicationStatus { Status = startLine });
                Logger.Info(startLine);
                WriteToLogFile(startLine);

                for (int i = 1; i <= frameCount; i++) {
                    token.ThrowIfCancellationRequested();

                    capture.ExposureTime = exposureSeconds;
                    capture.ImageType = CaptureSequence.ImageTypes.BIAS;
                    capture.Binning = new BinningMode((short)binningX, (short)binningY);
                    capture.Gain = gain;
                    capture.Offset = offset;
                    capture.ProgressExposureCount = frameCounter.Next++;

                    var prepareParams = new PrepareImageParameters(autoStretch: false, detectStars: false);
                    var rendered = await _imagingMediator.CaptureAndPrepareImage(capture, prepareParams, token, progress);
                    await _imageSaveMediator.Enqueue(rendered.RawImageData, Task.FromResult(rendered), progress, token);
                    saved++;

                    string frameLine = $"Smart Calibration Frames: bias {i}/{frameCount} at {exposureSeconds:F4}s saved.";
                    progress.Report(new ApplicationStatus { Status = frameLine });
                    WriteToLogFile(frameLine);
                }
            } finally {
                try {
                    await _flatDeviceMediator.ToggleLight(false, progress, CancellationToken.None);
                } catch (Exception offEx) {
                    string offFailLine = $"Smart Calibration Frames: bias frames - failed to confirm the flat panel is off during cleanup: {offEx.Message}. Manually verify the panel is off.";
                    Logger.Error(offFailLine);
                    WriteToLogFile(offFailLine);
                }
            }

            string doneLine = $"Smart Calibration Frames: finished {saved} bias frame(s) at {exposureSeconds:F4}s.";
            progress.Report(new ApplicationStatus { Status = doneLine });
            Logger.Info(doneLine);
            WriteToLogFile(doneLine);
            return doneLine;
        }

        /// <summary>
        /// ROUND 32 REDESIGN, ROUND 33 ESCALATION - brightness starts each run at this filter's stored
        /// default (SmartCalSettingsProvider.GetFilterBrightness) and, for as long as EXPOSURE ALONE can
        /// reach target, stays fixed there for the whole search - the exact linear ADU/exposure
        /// relationship this plugin has relied on since Round 26 (given one measured frame at
        /// (brightness, exposure) -> achieved ADU, the exposure that would hit targetADU at that SAME
        /// brightness is exposure * (targetADU / achieved), no guessing, verified with a real frame next
        /// attempt) still does all the real work, every attempt, at whatever brightness is currently in
        /// play.
        ///
        /// ROUND 33 - per explicit user direction, exposure hitting a bound is no longer an automatic
        /// failure by itself: if exposure would need to go OVER Max Exposure and the panel is still too
        /// dim, brightness is raised a step and the exposure search restarts fresh at the new brightness;
        /// if exposure is already at (or the model wants to go UNDER) Min Exposure and the panel is still
        /// too bright - including the saturated-at-Min-Exposure case - brightness is lowered a step
        /// instead. Only once brightness ITSELF is already at the Options page's Min/Max Brightness bound
        /// and the same problem persists does this method give up for real. See TryEscalateBrightness
        /// below for exactly how a "step" is sized and why this deliberately does NOT try to predict a
        /// precise target brightness the way exposure's formula does.
        ///
        /// Three outcomes now:
        ///   CONVERGED - a real frame landed within Tolerance, at whatever brightness the search ended up
        ///     on (the stored default, or a Round-33-escalated value). This filter's stored
        ///     brightness/exposure are overwritten with what actually worked (self-updating, no separate
        ///     "recalibrate" step needed), and the frame is returned for the caller to reuse as
        ///     production frame 1.
        ///   FAILED (brightness already at its allowed bound) - exposure hit a wall AND brightness has no
        ///     more room left toward the bound that would help. No frame is saved, the filter's stored
        ///     brightness/exposure are left UNTOUCHED, and the failure message says which bound and which
        ///     direction, per the original Round 32 direction to report plainly rather than auto-retry
        ///     forever.
        ///   FAILED (ran out of attempts/escalations) - the rare safety-net case: maxAttempts exhausted at
        ///     some brightness without converging or hitting a bound, or maxBrightnessEscalations
        ///     exhausted without converging at all. Same "no frame saved, nothing touched" handling.
        ///
        /// SATURATION caveat, same as every earlier round: a clipped reading (at/near the sensor's full
        /// well) doesn't tell you the true light level, only that it was AT LEAST that bright, so the
        /// linear ratio would be computed from a wrong number. Detected before the exposure math runs and
        /// backed off directly instead (halve exposure toward Min) rather than trusting an interpolation
        /// built on a clipped pixel value; still saturated at Min Exposure now escalates brightness down
        /// (Round 33) instead of failing immediately - failing outright only once Min Brightness itself is
        /// reached.
        /// </summary>
        private async Task<(PlannedExposure Plan, CapturedFlatFrame Frame)> ConvergeOnTargetAsync(
            CalibrationBucketKey key, double targetADU,
            FilterInfo filter, int binningX, int binningY, int gain, int offset,
            CaptureSequence capture, FrameSequenceCounter frameCounter,
            IProgress<ApplicationStatus> progress, CancellationToken token) {

            const int maxAttempts = 8;
            // ROUND 33b - per explicit user direction, escalation steps are fixed sizes, not the
            // bisection Round 33's first pass used: 10 brightness units for the first move in a new
            // direction (big enough to actually matter, not the 1-unit steps bisection could produce
            // right near a bound), 5 units for a correction once a direction FLIP shows the first move
            // overshot (see fineMode below). Raised from 4 to 10 escalations now that steps are a fixed
            // size instead of shrinking geometrically - worst case this is (maxBrightnessEscalations + 1)
            // * maxAttempts = 11 * 8 = 88 search frames in one run, same safety-first "bounded, not
            // unbounded" spirit as every attempt cap already in this file, just a wider bound to give a
            // meaningful step room to work. In practice this only matters for a filter whose stored
            // default is far off - the common "off by a little" case this round was built for converges
            // in 1-2 escalations.
            const int primaryBrightnessStep = 10;
            const int correctionBrightnessStep = 5;
            const int maxBrightnessEscalations = 10;
            const double saturationFraction = 0.98; // >=98% of full well treated as clipped/unreliable

            // ROUND 35 - real field log (Blue filter, brightness fixed at 35, never saturated, never hit
            // an exposure bound) showed the plain exposure formula settling into a genuine 2-point limit
            // cycle: 0.40s->12303, 1.06s->39093, 0.89s->28105, 1.04s->38971, 0.87s->27387, 1.05s->38612,
            // 0.89s->26670, 1.09s->38321 - alternating over/under target every single attempt, with the
            // over/under amplitude staying roughly constant (not shrinking) for all 8 tries, so more
            // attempts of the SAME one-shot correction would not have converged either. This is the
            // classic failure mode of a "deadbeat" (jump straight to the fully-predicted value) proportional
            // correction: the real ADU-vs-exposure relationship at this filter/brightness isn't quite
            // clean proportional-through-zero (could be sensor readout overhead, panel PWM/exposure timing
            // interaction, or plain frame-to-frame noise - the fix below doesn't need to know which), so
            // jumping straight to the "predicted" exposure every time overshoots equally in both
            // directions forever instead of settling. Fix: once the correction direction has flipped
            // twice in a row (up-down-up or down-up-down - real oscillation, not just a normal
            // overshoot-then-settle), stop jumping straight to the predicted exposure and instead take
            // only half the suggested step (oscillationDampingFactor), which should shrink the error
            // geometrically each attempt instead of bouncing at a constant amplitude. Also nudge brightness
            // a SMALL amount (2 steps - deliberately much smaller than primaryBrightnessStep/
            // correctionBrightnessStep above, which are for the "exposure hit a real bound" case, not
            // this) in whichever direction pulls the oscillation's own midpoint toward target, in case the
            // oscillation is tied to something at this specific brightness (PWM timing, etc.) rather than
            // purely a math artifact - cheap to try, bounded (maxOscillationNudges), and skipped
            // gracefully (falls back to damping-only) if brightness is already at the relevant bound.
            const double oscillationDampingFactor = 0.5;
            const int oscillationBrightnessNudgeStep = 2;
            const int maxOscillationNudges = 3;

            int brightness = _settingsProvider.GetFilterBrightness(key.FilterName, _settings.MinBrightness);
            // ROUND 44 - fallback changed from MinExposureSeconds to PreferredExposureSeconds (see the
            // matching comment above this method's caller, RunAsync, for the full reasoning). Still
            // clamped to Min/Max exposure immediately below - Preferred only changes where an unconverged
            // filter's search starts, never what the search is allowed to do.
            double exposure = Math.Clamp(
                _settingsProvider.GetFilterExposureSeconds(key.FilterName, _settings.PreferredExposureSeconds),
                _settings.MinExposureSeconds, _settings.MaxExposureSeconds);

            double lastAchieved = 0;
            double lastExposure = exposure;
            int totalAttempts = 0;

            // ROUND 33b - tracks the direction of the last brightness escalation this run (null = none
            // yet) so a direction FLIP - raised, then found we need to lower, or vice versa - can be
            // detected and treated as "we overshot the first move," switching to the smaller
            // correctionBrightnessStep for this and every later escalation this run rather than
            // continuing to swing by the full primaryBrightnessStep and risking a slow oscillation.
            bool? lastEscalationLowered = null;
            bool fineCorrectionMode = false;

            for (int brightnessPass = 0; brightnessPass <= maxBrightnessEscalations; brightnessPass++) {
                bool escalated = false;

                // ROUND 35 - oscillation-detection state, reset fresh at the start of every brightnessPass
                // (a brightness escalation is itself already a deliberate, larger correction - it
                // shouldn't be second-guessed by oscillation logic left over from the PREVIOUS brightness).
                bool? lastCorrectionIncreased = null;
                int consecutiveFlips = 0;
                int oscillationNudgesUsed = 0;

                for (int attempt = 1; attempt <= maxAttempts; attempt++) {
                    token.ThrowIfCancellationRequested();
                    totalAttempts++;
                    double previousAchieved = lastAchieved; // snapshot BEFORE this attempt's result overwrites it below
                    lastExposure = exposure;

                    var frame = await CaptureOneAsync(capture, filter, brightness, exposure, binningX, binningY, gain, offset, frameCounter, progress, token);
                    double achieved = frame.Stats.Mean;
                    lastAchieved = achieved;

                    double error = targetADU > 0 ? (achieved - targetADU) / targetADU : 0;
                    string line = $"Smart Calibration Frames: {key} search {totalAttempts} - brightness {brightness}, {exposure:F2}s -> {achieved:F0} ADU " +
                                  $"(target {targetADU:F0}, {error * 100:+0.0;-0.0}%)";
                    progress.Report(new ApplicationStatus { Status = line });
                    Logger.Info(line);
                    WriteToLogFile(line);

                    if (Math.Abs(error) <= _settings.ToleranceADUPercent) {
                        // Converged - remember what actually worked for this filter, overwriting whatever
                        // was there before (never appended, never kept as history - see the header comment).
                        _settingsProvider.SetFilterBrightness(key.FilterName, brightness);
                        _settingsProvider.SetFilterExposureSeconds(key.FilterName, exposure);
                        // ROUND 34 - the write above goes through THIS service's own SmartCalSettingsProvider
                        // instance, a completely separate object from the one the Options page's
                        // FilterDefaultRowVM rows were built from at NINA startup (see
                        // SmartCalFramesPlugin's constructor - OptionsVM is built exactly once for the
                        // whole process). Without this, the Options page kept showing stale startup
                        // values no matter how many runs converged. SmartCalFramesPlugin.Instance is the
                        // same established singleton-access pattern already used elsewhere in this
                        // plugin - null-safe in case the Options page was never opened/composed yet.
                        SmartCalFramesPlugin.Instance?.OptionsVM?.RefreshFilterDefault(key.FilterName);
                        string reason = $"Converged after {totalAttempts} search frame{(totalAttempts == 1 ? "" : "s")} at brightness {brightness}, " +
                                         $"{exposure:F2}s, within {_settings.ToleranceADUPercent:P0} of target." +
                                         (brightnessPass > 0 ? $" (brightness was adjusted {brightnessPass} time{(brightnessPass == 1 ? "" : "s")} from this filter's stored default during this run.)" : "");
                        return (new PlannedExposure {
                            Brightness = brightness,
                            ExposureSeconds = exposure,
                            PredictedADU = achieved,
                            Kind = PlanKind.Converged,
                            Reason = reason
                        }, frame);
                    }

                    double saturationCeiling = frame.Stats.FullWellADU * saturationFraction;
                    if (achieved >= saturationCeiling) {
                        if (exposure > _settings.MinExposureSeconds + 0.001) {
                            exposure = Math.Max(_settings.MinExposureSeconds, exposure / 2.0);
                            continue;
                        }
                        // Saturated even at Min Exposure - exposure has no more room. ROUND 33: lower
                        // brightness a step and restart the exposure search, instead of failing here.
                        // ROUND 33b - exposure is deliberately LEFT AS-IS (not reset) here: it's already
                        // sitting at (or near) Min Exposure, exactly where the next pass's search should
                        // start from - the trusted linear formula will raise it again from there only if
                        // the new, dimmer brightness actually needs more.
                        if (lastEscalationLowered == false) fineCorrectionMode = true; // flipped raise -> lower: we overshot
                        int lowerStep = fineCorrectionMode ? correctionBrightnessStep : primaryBrightnessStep;
                        if (TryEscalateBrightness(brightness, lowerNotRaise: true, lowerStep, out int lowered, out string escalateLine)) {
                            lastEscalationLowered = true;
                            brightness = lowered;
                            progress.Report(new ApplicationStatus { Status = escalateLine });
                            Logger.Info(escalateLine);
                            WriteToLogFile(escalateLine);
                            escalated = true;
                            break;
                        }
                        return (FailedPlan(brightness, exposure, achieved, totalAttempts,
                            $"still saturated even at Min Exposure ({achieved:F0} ADU) with brightness already at its Min Brightness floor ({_settings.MinBrightness}) - " +
                            "the panel is too bright for this filter at any allowed brightness."), null);
                    }

                    // Not saturated - solve directly for the exposure THIS (fixed) brightness needs to hit
                    // target, from this one real measurement.
                    double neededExposure = exposure * (targetADU / Math.Max(1, achieved));

                    if (neededExposure > _settings.MaxExposureSeconds + 0.001) {
                        // Too dim even at Max Exposure. ROUND 33: raise brightness a step and restart the
                        // exposure search, instead of failing here. ROUND 33b - exposure left as-is
                        // (already at/near Max Exposure, the right starting point for the new brightness).
                        if (lastEscalationLowered == true) fineCorrectionMode = true; // flipped lower -> raise: we overshot
                        int raiseStep = fineCorrectionMode ? correctionBrightnessStep : primaryBrightnessStep;
                        if (TryEscalateBrightness(brightness, lowerNotRaise: false, raiseStep, out int raised, out string escalateLine)) {
                            lastEscalationLowered = false;
                            brightness = raised;
                            progress.Report(new ApplicationStatus { Status = escalateLine });
                            Logger.Info(escalateLine);
                            WriteToLogFile(escalateLine);
                            escalated = true;
                            break;
                        }
                        return (FailedPlan(brightness, exposure, achieved, totalAttempts,
                            $"could not reach target ADU within Max Exposure with brightness already at its Max Brightness ceiling ({_settings.MaxBrightness}) - " +
                            "the panel is too dim for this filter at any allowed brightness."), null);
                    }
                    if (neededExposure < _settings.MinExposureSeconds - 0.001) {
                        // Too bright even at Min Exposure - not clipped, but the trusted linear model
                        // already wants less than Min. ROUND 33: same escalation, lower brightness a step.
                        // ROUND 33b - exposure left as-is (already at/near Min Exposure).
                        if (lastEscalationLowered == false) fineCorrectionMode = true; // flipped raise -> lower: we overshot
                        int lowerStep2 = fineCorrectionMode ? correctionBrightnessStep : primaryBrightnessStep;
                        if (TryEscalateBrightness(brightness, lowerNotRaise: true, lowerStep2, out int lowered, out string escalateLine)) {
                            lastEscalationLowered = true;
                            brightness = lowered;
                            progress.Report(new ApplicationStatus { Status = escalateLine });
                            Logger.Info(escalateLine);
                            WriteToLogFile(escalateLine);
                            escalated = true;
                            break;
                        }
                        return (FailedPlan(brightness, exposure, achieved, totalAttempts,
                            $"needs less than Min Exposure to reach target with brightness already at its Min Brightness floor ({_settings.MinBrightness}) - " +
                            "the panel is too bright for this filter at any allowed brightness."), null);
                    }

                    double clamped = Math.Clamp(neededExposure, _settings.MinExposureSeconds, _settings.MaxExposureSeconds);
                    if (Math.Abs(clamped - exposure) <= 0.001) {
                        // Already sitting at the exact exposure this brightness predicts for target, and
                        // still outside tolerance for real - the panel isn't perfectly linear right here.
                        // Not a bound-hit case, so Round 33's escalation doesn't apply - unchanged from
                        // Round 32.
                        return (FailedPlan(brightness, exposure, achieved, totalAttempts,
                            $"brightness {brightness} at {exposure:F2}s is already the exact exposure predicted for target, but the real " +
                            "result is still outside Tolerance - loosen Tolerance ADU %, or adjust this filter's default brightness."), null);
                    }
                    // ROUND 35 - oscillation detection. A plain one-shot jump straight to `clamped` every
                    // time is exactly what produced the real 2-point limit cycle in the field log
                    // documented in the const block near the top of this method, so before committing to
                    // that jump, check whether the correction direction has now flipped twice in a row
                    // (up-down-up or down-up-down) - a normal single overshoot-then-settle never produces
                    // two flips back to back, so this is a reliable signal of real oscillation rather than
                    // an ordinary correction.
                    bool thisIncreased = clamped > exposure;
                    if (lastCorrectionIncreased.HasValue && thisIncreased != lastCorrectionIncreased.Value) {
                        consecutiveFlips++;
                    } else {
                        consecutiveFlips = 0;
                    }
                    lastCorrectionIncreased = thisIncreased;

                    if (consecutiveFlips >= 2 && oscillationNudgesUsed < maxOscillationNudges) {
                        // Confirmed oscillation - stop jumping straight to the fully-predicted exposure and
                        // instead take only half the suggested step (oscillationDampingFactor), which
                        // shrinks the error geometrically each attempt instead of bouncing at a roughly
                        // constant amplitude the way the field log showed.
                        double damped = exposure + oscillationDampingFactor * (clamped - exposure);
                        string dampLine = $"Smart Calibration Frames: {key} search {totalAttempts} - exposure is oscillating back and forth " +
                                           $"(direction flipped {consecutiveFlips} times in a row) - damping the correction to {damped:F2}s " +
                                           $"instead of jumping straight to the predicted {clamped:F2}s.";

                        // Also try a small brightness nudge - separate, smaller budget (maxOscillationNudges)
                        // than the primary/correction escalation steps above, in whichever direction pulls
                        // the midpoint of the two most recent achieved ADU readings toward target, in case
                        // the oscillation is tied to something at this specific brightness rather than
                        // purely a math artifact. Falls back to damping-only if brightness is already at
                        // the relevant bound.
                        double oscillationMidpoint = (achieved + previousAchieved) / 2.0;
                        bool nudgeLower = oscillationMidpoint > targetADU;
                        if (TryEscalateBrightness(brightness, lowerNotRaise: nudgeLower, oscillationBrightnessNudgeStep, out int nudgedBrightness, out string _)) {
                            brightness = nudgedBrightness;
                            oscillationNudgesUsed++;
                            dampLine += $" Also nudged brightness to {brightness} ({(nudgeLower ? "-" : "+")}{oscillationBrightnessNudgeStep} step, " +
                                        $"{oscillationNudgesUsed}/{maxOscillationNudges} oscillation nudges used at this brightness) to pull the last " +
                                        "two readings' midpoint toward target.";
                        }

                        progress.Report(new ApplicationStatus { Status = dampLine });
                        Logger.Info(dampLine);
                        WriteToLogFile(dampLine);

                        exposure = damped;
                        consecutiveFlips = 0;
                    } else {
                        exposure = clamped;
                    }
                }

                if (!escalated) {
                    // Exhausted maxAttempts at this brightness without converging AND without hitting a
                    // bound that would trigger an escalation (e.g. oscillating without settling within
                    // Tolerance) - same rare safety-net failure Round 32 always had.
                    return (FailedPlan(brightness, lastExposure, lastAchieved, totalAttempts,
                        $"did not converge within {_settings.ToleranceADUPercent:P0} after {maxAttempts} search frames at brightness {brightness}."), null);
                }
                // else: brightness was escalated - loop continues into the next brightnessPass with the
                // new brightness and a fresh exposure guess.
            }

            return (FailedPlan(brightness, lastExposure, lastAchieved, totalAttempts,
                $"did not converge even after adjusting brightness {maxBrightnessEscalations} time{(maxBrightnessEscalations == 1 ? "" : "s")} " +
                $"within this filter's allowed [{_settings.MinBrightness}, {_settings.MaxBrightness}] brightness range."), null);
        }

        /// <summary>
        /// ROUND 33, revised ROUND 33b per explicit user feedback ("just a single step?... that needs to
        /// be at least 10 steps... if still too bright reduce the brightness by 5 steps") - bounded,
        /// deterministic "escape valve" for when the trusted linear exposure search above hits a genuine
        /// wall: Min or Max Exposure reached (or still saturated at Min Exposure) and target still isn't
        /// in reach at the filter's CURRENT brightness. Per direction: too dim at Max Exposure -> raise
        /// brightness; too bright (saturated, or the model wanting less than Min Exposure) at Min Exposure
        /// -> lower brightness.
        ///
        /// ROUND 33's first pass used a bisection step (half the remaining headroom to the bound) - the
        /// user pointed out this can shrink to a single brightness unit right near a bound, too small to
        /// meaningfully help. ROUND 33b replaces that with the fixed step sizes the user specified: the
        /// CALLER (ConvergeOnTargetAsync) decides which size to pass in as `step` - primaryBrightnessStep
        /// (10) for the first move in a given direction, correctionBrightnessStep (5) once a direction
        /// FLIP shows that move overshot - and this method's only remaining job is to apply that step and
        /// clamp it to the configured bound (Min/MaxBrightness from the Options page - the same two fields
        /// Round 32 left in place only as "fallback"/"legacy" values, now load-bearing again as the real
        /// allowed range).
        ///
        /// Still deliberately NOT a live proportional prediction the way exposure's own
        /// `exposure * (targetADU / achieved)` is: this project spent Rounds 13-31 on a joint
        /// brightness+exposure model and abandoned it (see the ROUND 32 header comment at the top of this
        /// file) specifically because brightness-to-ADU response isn't reliably linear across a panel's
        /// full range - guessing a precise target brightness from one ADU reading would risk repeating
        /// that exact mistake. Fixed steps, not a computed guess, is the point.
        ///
        /// Returns false (no brightness/logLine set beyond echoing the input) once brightness is already
        /// sitting exactly at the relevant bound - the caller treats that as a genuine dead end and fails
        /// outright, per the same "report plainly, let the user adjust settings" philosophy as every other
        /// failure path in ConvergeOnTargetAsync. A step that would overshoot past the bound is silently
        /// clamped TO the bound instead (still counts as a real, if partial, escalation) - only a
        /// currentBrightness already AT the bound returns false.
        /// </summary>
        private bool TryEscalateBrightness(int currentBrightness, bool lowerNotRaise, int step, out int newBrightness, out string logLine) {
            if (lowerNotRaise) {
                int floor = _settings.MinBrightness;
                if (currentBrightness <= floor) {
                    newBrightness = currentBrightness;
                    logLine = null;
                    return false;
                }
                newBrightness = Math.Max(floor, currentBrightness - step);
                logLine = $"Smart Calibration Frames: too bright for this filter at brightness {currentBrightness} - lowering brightness to {newBrightness} (-{currentBrightness - newBrightness} step{(currentBrightness - newBrightness == 1 ? "" : "s")}) and restarting the exposure search.";
                return true;
            } else {
                int ceiling = _settings.MaxBrightness;
                if (currentBrightness >= ceiling) {
                    newBrightness = currentBrightness;
                    logLine = null;
                    return false;
                }
                newBrightness = Math.Min(ceiling, currentBrightness + step);
                logLine = $"Smart Calibration Frames: too dim for this filter at brightness {currentBrightness} - raising brightness to {newBrightness} (+{newBrightness - currentBrightness} step{(newBrightness - currentBrightness == 1 ? "" : "s")}) and restarting the exposure search.";
                return true;
            }
        }

        private static PlannedExposure FailedPlan(int brightness, double exposure, double achieved, int attempts, string reason) {
            return new PlannedExposure {
                Brightness = brightness,
                ExposureSeconds = exposure,
                PredictedADU = achieved,
                Kind = PlanKind.Failed,
                Reason = $"{reason} ({attempts} search frame{(attempts == 1 ? "" : "s")}.)"
            };
        }

        /// <summary>
        /// ROUND 15 - reported: saved flats collided on the same filename ("1_0.10s_0000(1).fits") because
        /// $$FRAMENR$$ in NINA's file pattern always evaluated to the same value. Fix: the caller creates
        /// ONE CaptureSequence for the whole run (search phase AND production frames) and passes it in
        /// here to be mutated in place, plus a FrameSequenceCounter shared the same way.
        /// </summary>
        private async Task<CapturedFlatFrame> CaptureOneAsync(
            CaptureSequence capture, FilterInfo filter, int brightness, double exposureSeconds,
            int binningX, int binningY, int gain, int offset, FrameSequenceCounter frameCounter,
            IProgress<ApplicationStatus> progress, CancellationToken token) {

            // ROUND 32e - reported: the flat panel sometimes did not turn on at all - reliably on the
            // very first capture of a session in some cases, and reproducibly on a second consecutive
            // SmartCals run on the SAME filter/bucket with no filter change in between (confirmed by
            // the user: Lum run #1 -> panel on, correct ADU; Lum run #2 immediately after, no filter
            // switch -> panel never came on, frames came back at bias-level ADU).
            //
            // Log evidence (610-line main NINA session log spanning both a working and a failing run):
            // "Toggling light to True" never once appeared anywhere in the entire log, while "Toggling
            // light to False" logged reliably on every cleanup - and two
            // NINA.Core.Model.SequenceEntityFailedException: "Failed to toggle light. Current light
            // state: True" errors appeared from the user's own native NINA Flat Wizard testing in that
            // same session. Together this points at NINA's own FlatDeviceVM tracking an on/off (and
            // brightness) state that can get out of sync with the real hardware, then silently treating
            // a requested SetBrightness/ToggleLight(true) as a no-op because it believes the light is
            // "already" where it's being asked to go - so the real ASCOM CalibratorOn() call, and the
            // real "SET n" command to the ESP32, never actually happen.
            //
            // ROUND 32f - the first attempt at this fix (ToggleLight(false) then ToggleLight(true) then
            // SetBrightness) made things WORSE on real hardware: "The panel fails to come on at all."
            // Per direction, removed the leading ToggleLight(false) - it's very likely that forcing an
            // OFF call when NINA/the driver already believed the light was off was itself throwing or
            // stalling (CalibratorOff() throws a DriverException if the ESP32's response doesn't start
            // with "OK"), which would abort this method before the ToggleLight(true) below ever ran,
            // leaving the panel off for the whole capture.
            //
            // ROUND 32h - two full real-hardware logs later, the actual mechanism is now clear and it's
            // NOT about on/off state at all. Across a 5-run session: Lum run #1 (brightness 38, the
            // very first capture of the whole session) FAILED dark; Red run #1 (brightness 49, a value
            // never used yet) SUCCEEDED; Red run #2 (brightness 49 again, unchanged) FAILED dark; Lum
            // run #2 (brightness 38, changed from 49) SUCCEEDED; Lum run #3 (brightness 38 again,
            // unchanged) FAILED dark. Every single failure was a request for the SAME brightness value
            // that was already current; every success was a genuinely NEW value. "Setting brightness to
            // N" only logged twice in that whole session (once for each of the two times the value
            // actually changed) - never for a repeat. "Toggling light to True" never logged even once,
            // yet two of the five runs got real light anyway - proving the panel physically stays lit at
            // whatever brightness it was last real-commanded to, and NINA's own ToggleLight(true) being
            // a no-op doesn't matter as long as a real SetBrightness reaches the driver. The one thing
            // that's 100% reliable in every log collected so far (14/14 across two sessions) is
            // ToggleLight(false) - it always executes and logs.
            //
            // This points squarely at NINA's SetBrightness (not ToggleLight) silently skipping the real
            // hardware call whenever the requested brightness equals what NINA already believes is
            // current - including on a run's very first capture, if NINA's cache (populated from the
            // driver at Connect) already happened to match. The fix: force a real brightness CHANGE
            // immediately before the one we actually want, so the value-equality check can never see a
            // match.
            //
            // ROUND 32i - that dummy-bracket now lives ONCE at the top of RunAsync (right before this
            // run's search begins), not here on every single capture - every real failure seen so far was
            // on a run's first capture only; every later frame at the same fixed brightness worked fine
            // with a plain repeat SetBrightness (a harmless, expected no-op once the panel is already
            // correctly on).
            //
            // ROUND 32l - dropped the ToggleLight(true) call that used to sit here too. Real hardware
            // evidence (see RunAsync's own note where its ToggleLight(true) was removed) showed this call
            // is both unnecessary - SetBrightness's
            // underlying "SET n" firmware command already turns the relay on every time, ASCOM gives this
            // driver no other way to turn it on in the first place - and actively harmful the rare times
            // it DID fire for real, driving the panel to a full-brightness (255) flash first. Now just a
            // plain SetBrightness, relying entirely on RunAsync's own bracket-or-skip logic to guarantee
            // the panel is already at the right value by the time this runs.
            //
            // ROUND 33 - s_lastCommandedBrightness is now refreshed here, after EVERY real SetBrightness
            // call, not just the one RunAsync makes before its first capture. Needed because
            // ConvergeOnTargetAsync can now change brightness mid-run (see TryEscalateBrightness) - without
            // this, the static tracker would keep remembering only this run's STARTING brightness even
            // after the search (and every production frame) moved on to a different, escalated value that
            // NINA's own driver was actually last told. The very next run would then see the tracker
            // disagree with NINA's real belief and wrongly decide a plain SetBrightness was safe (or
            // wrongly force an unnecessary bracket) - exactly the class of bug Round 32h-32t fought over,
            // just reintroduced from a different angle. Keeping this always in sync with the real last
            // command, from whichever call site made it, is the fix.
            await _flatDeviceMediator.SetBrightness(brightness, progress, token);
            s_lastCommandedBrightness = brightness;
            await Task.Delay(_settings.PanelSettleTimeMs, token);

            capture.ExposureTime = exposureSeconds;
            capture.ImageType = CaptureSequence.ImageTypes.FLAT;
            capture.FilterType = filter;
            capture.Binning = new BinningMode((short)binningX, (short)binningY);
            capture.Gain = gain;
            capture.Offset = offset;
            capture.ProgressExposureCount = frameCounter.Next++;

            var prepareParams = new PrepareImageParameters(autoStretch: false, detectStars: false);
            var rendered = await _imagingMediator.CaptureAndPrepareImage(capture, prepareParams, token, progress);
            var stats = await rendered.RawImageData.Statistics.Task;

            return new CapturedFlatFrame {
                Rendered = rendered,
                Stats = new ImageStatistics {
                    Mean = stats.Mean,
                    StdDev = stats.StDev,
                    FullWellADU = Math.Pow(2, stats.BitDepth) - 1
                }
            };
        }

        /// <summary>Holds both the lightweight stats used for every accept/reject/stop decision and the
        /// underlying rendered image, kept only long enough to hand accepted frames to IImageSaveMediator.</summary>
        private class CapturedFlatFrame {
            public IRenderedImage Rendered { get; set; }
            public ImageStatistics Stats { get; set; }
        }

        // ROUND 59 CLEANUP - this never actually awaited anything (no equipment call here needs to),
        // so the old async/Task-returning signature only ever produced a compiler warning
        // ("this async method lacks 'await' operators and will run synchronously") for no benefit.
        // Plain synchronous method, unused CancellationToken parameter dropped along with it.
        private double GetFullWellADU() {
            var camInfo = _cameraMediator.GetInfo();
            int bitDepth = camInfo?.BitDepth > 0 ? camInfo.BitDepth : 16;
            return Math.Pow(2, bitDepth) - 1;
        }

        private static bool IsStable(List<double> means, double threshold) {
            if (means.Count < 2) return false;
            double avg = means.Average();
            if (avg <= 0) return false;
            double variance = means.Select(m => (m - avg) * (m - avg)).Sum() / means.Count;
            double stdDev = Math.Sqrt(variance);
            return (stdDev / avg) < threshold;
        }

        /// <summary>
        /// Per-frame check, deliberately separate from IsStable above: judges ONE new frame against
        /// the average of frames already accepted, rather than judging the accepted group as a whole.
        /// Only called once at least one frame has already been accepted (nothing to compare the very
        /// first frame against, so it's always accepted).
        /// </summary>
        private static bool IsOutlier(double candidateMean, List<double> acceptedMeans, double tolerance) {
            double avg = acceptedMeans.Average();
            if (avg <= 0) return false;
            return Math.Abs(candidateMean - avg) / avg > tolerance;
        }
    }
}
