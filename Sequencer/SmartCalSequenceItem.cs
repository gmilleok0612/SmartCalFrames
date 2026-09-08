using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Validations;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using SmartCalFrames.Dockable;
using SmartCalFrames.Equipment;
using SmartCalFrames.Options;

namespace SmartCalFrames.Sequencer {

    /// <summary>
    /// The Advanced Sequencer drop-in for this plugin's four functions. Originally ("Round 17") this was
    /// Flats-only - replacing "Flat Wizard" + "Take Flats" for a filter (or the whole wheel). ROUND 40
    /// extended it in place (same class/type name - see the load-bearing note below) to also run Flat
    /// Darks, Bias Frames, and Dark Frames from one item, each gated behind its own checkbox, per
    /// explicit direction ("four checkboxes for function... select one or any and it will take the
    /// frames"). Everything that makes any of the four different from a stock capture instruction lives
    /// in SmartCalCaptureService/SmartCalRunPlanning - this class is just the NINA-facing shell:
    /// property bag for the sequence editor, Validate() for the red-squiggly checks NINA runs before a
    /// sequence starts, and Execute() that hands off to the capture service for whichever functions are
    /// checked.
    ///
    /// Namespace/type name are load-bearing: NINA persists sequences as
    /// JSON keyed by fully-qualified type name, so renaming this class
    /// after anyone has saved a sequence using it will break deserialization
    /// of their saved sequence file. Adding new [JsonProperty] fields (as this round does) is safe -
    /// MemberSerialization.OptIn means an old saved sequence simply doesn't have them in its JSON, and
    /// they come back as whatever their C# property initializer says (CaptureFlats defaults to true, so
    /// an old flats-only saved sequence keeps behaving exactly as it always did).
    /// </summary>
    [ExportMetadata("Name", "Smart Calibration Frames")]
    [ExportMetadata("Description", "Runs any combination of this plugin's four capture functions - Flat Frames, Flat Darks, Bias Frames, Dark Frames - each using that function's own tab settings from the dockable panel. Flat Frames self-corrects one remembered brightness/exposure per filter instead of trial-and-error; Dark Frames can optionally match each filter's exposure to the light frames already captured earlier in this same sequence run.")]
    [ExportMetadata("Icon", "FlatWizardSVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_Camera")]
    [Export(typeof(NINA.Sequencer.SequenceItem.ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class SmartCalSequenceItem : SequenceItem, IValidatable {

        private readonly ICameraMediator _cameraMediator;
        private readonly IFilterWheelMediator _filterWheelMediator;
        private readonly IFlatDeviceMediator _flatDeviceMediator;
        private readonly IImagingMediator _imagingMediator;
        private readonly IImageSaveMediator _imageSaveMediator;
        private readonly IProfileService _profileService;

        // ROUND 40 - for "Match dark exposures to light frames" (MatchDarkExposuresToLightHistory
        // below). Confirmed directly against the installed NINA.WPF.Base.dll 3.2.0.9001 that this
        // interface exists and exposes what's needed (IImageHistoryVM.ImageHistory ->
        // List<ImageHistoryPoint>, with Filter/Duration/Type/Id per entry) - see ApplyLightHistoryExposures
        // below for the exact members used. CONFIRMED (post-delivery, via dump_sig.py against the
        // installed NINA.Sequencer.dll 3.2.0.9001 itself) that IImageHistoryVM as a plain
        // [ImportingConstructor] parameter is exactly how NINA's OWN built-in sequence items do this -
        // NINA.Sequencer.SequenceItem.Imaging.TakeExposure/TakeManyExposures/TakeSubframeExposure/
        // SmartExposure, NINA.Sequencer.SequenceItem.FlatDevice.AutoBrightnessFlat/AutoExposureFlat/
        // SkyFlat/TrainedDarkFlatExposure/TrainedFlatExposure, RunAutofocus, and several triggers
        // (DitherAfterExposures, the Autofocus-After-* triggers) all take IImageHistoryVM as a
        // constructor parameter the same way this file does. That rules out MEF composition failure
        // as the cause of the "checkboxes don't render" report - if the item still fails to render after
        // a genuine full rebuild + NINA restart, look at the DataTemplate/binding side, not this
        // constructor.
        private readonly IImageHistoryVM _imageHistoryVM;

        // ROUND 68 - for the "Pause for cover swap" dialog (PauseForCoverSwapAsync below). Confirmed as
        // a valid plain [ImportingConstructor] parameter two independent ways: (1) a dnfile signature
        // dump of the installed NINA.Core.dll/NINA.Sequencer.dll shows NINA's OWN built-in "Message Box"
        // sequence item (NINA.Sequencer.SequenceItem.Utility.MessageBox) takes exactly
        // IWindowServiceFactory this same way - "public void .ctor(IWindowServiceFactory
        // windowServiceFactory)"; (2) fetching that class's real source
        // (raw.githubusercontent.com/isbeorn/nina/develop/NINA.Sequencer/SequenceItem/Utility/
        // MessageBox.cs) confirms the exact usage: `windowServiceFactory.Create()` returns an
        // IWindowService, `new MessageBoxResult(text)` is the "content", and
        // `await service.ShowDialog(msgBoxResult, title)` shows it and awaits the user's Continue/Cancel
        // click - with `token.Register(() => service?.Close())` handling cancellation. This is the exact
        // pattern PauseForCoverSwapAsync below replicates.
        private readonly IWindowServiceFactory _windowServiceFactory;

        [ImportingConstructor]
        public SmartCalSequenceItem(
            IProfileService profileService,
            ICameraMediator cameraMediator,
            IFilterWheelMediator filterWheelMediator,
            IFlatDeviceMediator flatDeviceMediator,
            IImagingMediator imagingMediator,
            IImageSaveMediator imageSaveMediator,
            IImageHistoryVM imageHistoryVM,
            IWindowServiceFactory windowServiceFactory) {
            _profileService = profileService;
            _cameraMediator = cameraMediator;
            _filterWheelMediator = filterWheelMediator;
            _flatDeviceMediator = flatDeviceMediator;
            _imagingMediator = imagingMediator;
            _imageSaveMediator = imageSaveMediator;
            _imageHistoryVM = imageHistoryVM;
            _windowServiceFactory = windowServiceFactory;
        }

        private SmartCalSequenceItem(SmartCalSequenceItem cloneMe) : this(
            cloneMe._profileService, cloneMe._cameraMediator, cloneMe._filterWheelMediator,
            cloneMe._flatDeviceMediator, cloneMe._imagingMediator, cloneMe._imageSaveMediator,
            cloneMe._imageHistoryVM, cloneMe._windowServiceFactory) {
            CopyMetaData(cloneMe);
            CaptureFlats = cloneMe.CaptureFlats;
            RunAllFilters = cloneMe.RunAllFilters;
            FilterName = cloneMe.FilterName;
            BinningX = cloneMe.BinningX;
            BinningY = cloneMe.BinningY;
            Gain = cloneMe.Gain;
            Offset = cloneMe.Offset;
            CaptureFlatDarks = cloneMe.CaptureFlatDarks;
            CaptureBias = cloneMe.CaptureBias;
            CaptureDarkFrames = cloneMe.CaptureDarkFrames;
            MatchDarkExposuresToLightHistory = cloneMe.MatchDarkExposuresToLightHistory;
        }

        public override object Clone() => new SmartCalSequenceItem(this);

        // ROUND 41f - live camera info, so the Bin/Gain/Offset row can show the CONNECTED camera's real
        // supported binning modes and current gain/offset defaults, the same way NINA's own "Take
        // Exposure" row (visible directly above this one in the sequencer) does - per explicit request
        // ("Binning should match the camera settings as should gain and offset... like the take exposure
        // sequence instruction").
        //
        // ROUND 41f CORRECTION - the first version of this made SmartCalSequenceItem implement
        // ICameraConsumer and register/unregister via AfterParentChanged()/Detach(), on the assumption
        // (never actually verified before shipping) that Detach() was a virtual hook meant for exactly
        // this. Real build immediately failed: "'SmartCalSequenceItem.Detach()': cannot override
        // inherited member 'SequenceItem.Detach()' because it is not marked virtual, abstract, or
        // override." Checked properly this time with a targeted dnfile read of SequenceItem's MethodDef
        // flags in NINA.Sequencer.dll (not dump_sig.py, which only reads signatures, not virtual/final
        // flags): Detach() IS marked virtual at the IL level, but ALSO marked final - which C# surfaces
        // as non-overridable, and there is no other virtual "OnDetach"/teardown hook on SequenceItem to
        // use instead (the only genuinely overridable-and-non-final lifecycle members are
        // AfterParentChanged, Initialize, SequenceBlockInitialize/Started/Finished/Teardown, and Teardown
        // itself - none of which fire on item removal). SequenceItem also doesn't implement IDisposable,
        // so there's no Dispose hook to lean on either.
        //
        // Checked how NINA's own TakeExposure actually does this (the thing being matched), rather than
        // guessing again: TakeExposure has a plain CameraInfo { get; set; } property but does NOT
        // implement ICameraConsumer at all (confirmed via the same metadata walk - its interface list is
        // IExposureItem/ISequenceItem/ISequenceEntity/ICloneable/IDroppable/ISequenceHasChanged/
        // IValidatable, nothing camera-related), and IExposureItem itself has no CameraInfo/consumer
        // members either. So NINA's own built-in row is a plain snapshot, not a live standing
        // subscription - it does not need to unregister from anything because it never registers in the
        // first place. Matched that exact behavior here instead: no ICameraConsumer, no
        // RegisterConsumer/RemoveConsumer, no Detach override, no Dispose. CameraInfo is just a plain
        // RaisePropertyChanged-backed property, snapshotted from _cameraMediator.GetInfo() each time
        // AfterParentChanged() fires (i.e. each time this item is actually placed into/moved within a
        // sequence - confirmed virtual and non-final, and Clone() above already proves overriding a base
        // SequenceItem method compiles fine in this project). This trades "live-updates while the item
        // just sits in the editor with nothing running" for "safe, simple, and matches the real reference
        // behavior exactly" - a snapshot taken at placement time is what Take Exposure effectively gives
        // you too, and is enough to satisfy the actual request (showing the camera's real values instead
        // of a hardcoded -1).
        private CameraInfo _cameraInfo;
        public CameraInfo CameraInfo { get => _cameraInfo; private set { _cameraInfo = value; RaisePropertyChanged(); } }

        public override void AfterParentChanged() {
            base.AfterParentChanged();
            CameraInfo = _cameraMediator.GetInfo();
        }

        // ---- Editable properties (bound from the sequencer's XAML editor) ----

        // ROUND 41e - these were plain auto-properties ({ get; set; }) until now, which is why checking/
        // unchecking "Dark Frames" never enabled "Match dark exposures to light frames": a checkbox
        // updates ITS OWN bound value fine on click (that's just the click's own TwoWay binding target),
        // but IsEnabled="{Binding CaptureDarkFrames}" lives on a DIFFERENT control and only re-evaluates
        // when it's told the source changed - which a plain auto-property never does. Converted every
        // editable property here to an explicit backing field + RaisePropertyChanged() in the setter,
        // matching the exact pattern DarkFrameRowVM.cs already uses successfully (that class - and this
        // one, via SequenceItem -> SequenceHasChanged -> BaseINPC, confirmed via the installed
        // NINA.Sequencer.dll's own type metadata - both ultimately derive from NINA.Core.Utility.BaseINPC,
        // so RaisePropertyChanged() is the same inherited method in both places). This also fixes the
        // Flats row's IsEnabled="{Binding CaptureFlats}" and the filter picker's
        // Visibility="{Binding RunAllFilters,...}" not updating live, which were the same bug.

        // ROUND 40 - the four function checkboxes. CaptureFlats defaults true (not false, unlike the
        // other three) specifically so an old saved sequence - built before this round, when this item
        // only ever did flats - keeps doing exactly that after an update: its JSON has no CaptureFlats
        // property at all, so deserialization falls through to this default.
        private bool _captureFlats = true;
        [JsonProperty] public bool CaptureFlats { get => _captureFlats; set { _captureFlats = value; RaisePropertyChanged(); } }

        private bool _captureFlatDarks;
        [JsonProperty] public bool CaptureFlatDarks { get => _captureFlatDarks; set { _captureFlatDarks = value; RaisePropertyChanged(); } }

        private bool _captureBias;
        [JsonProperty] public bool CaptureBias { get => _captureBias; set { _captureBias = value; RaisePropertyChanged(); } }

        private bool _captureDarkFrames;
        [JsonProperty] public bool CaptureDarkFrames { get => _captureDarkFrames; set { _captureDarkFrames = value; RaisePropertyChanged(); } }

        /// <summary>Only consulted when CaptureDarkFrames is true. See ApplyLightHistoryExposures.</summary>
        private bool _matchDarkExposuresToLightHistory;
        [JsonProperty] public bool MatchDarkExposuresToLightHistory { get => _matchDarkExposuresToLightHistory; set { _matchDarkExposuresToLightHistory = value; RaisePropertyChanged(); } }

        /// <summary>Ignored when RunAllFilters is true, or when CaptureFlats is false. Must match a name in the active filter wheel's filter list.</summary>
        private bool _runAllFilters;
        [JsonProperty] public bool RunAllFilters { get => _runAllFilters; set { _runAllFilters = value; RaisePropertyChanged(); } }

        private string _filterName;
        [JsonProperty] public string FilterName { get => _filterName; set { _filterName = value; RaisePropertyChanged(); } }

        private int _binningX = 1;
        [JsonProperty] public int BinningX { get => _binningX; set { _binningX = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(BinningMode)); } }

        private int _binningY = 1;
        [JsonProperty] public int BinningY { get => _binningY; set { _binningY = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(BinningMode)); } }

        /// <summary>ROUND 41f - a single-selection view over BinningX/BinningY for the sequencer row's
        /// Binning ComboBox, matching NINA's own "Take Exposure" row (one "2x2"-style dropdown, not two
        /// separate X/Y fields). BinningMode.Equals is confirmed overridden (via dump_sig.py against the
        /// installed NINA.Core.dll), which is what lets WPF's ComboBox match this getter's freshly
        /// constructed instance against an equal-by-value entry already in the ItemsSource - the same
        /// object reference is not required. BinningX/BinningY remain the actual stored/serialized
        /// values; this property is a UI convenience only, never itself persisted.</summary>
        public BinningMode BinningMode {
            get => new BinningMode((short)BinningX, (short)BinningY);
            set {
                if (value == null) return;
                BinningX = value.X;
                BinningY = value.Y;
            }
        }

        /// <summary>-1 = use the camera's current/default gain. Only used by the Flat Frames function - Flat Darks/Bias/Dark Frames all use the camera's current gain/offset directly, same as their dockable-panel buttons.</summary>
        private int _gain = -1;
        [JsonProperty] public int Gain { get => _gain; set { _gain = value; RaisePropertyChanged(); } }

        /// <summary>-1 = use the camera's current/default offset. See Gain above.</summary>
        private int _offset = -1;
        [JsonProperty] public int Offset { get => _offset; set { _offset = value; RaisePropertyChanged(); } }

        public IList<string> Issues { get; private set; } = new List<string>();

        public bool Validate() {
            var issues = new List<string>();

            if (!CaptureFlats && !CaptureFlatDarks && !CaptureBias && !CaptureDarkFrames) {
                issues.Add("No function selected - check at least one of Flat Frames / Flat Darks / Bias / Dark Frames.");
            }

            var camInfo = _cameraMediator.GetInfo();
            if (camInfo == null || !camInfo.Connected) {
                issues.Add("Camera is not connected.");
            }

            var flatDeviceInfo = _flatDeviceMediator.GetInfo();
            if (flatDeviceInfo == null || !flatDeviceInfo.Connected) {
                issues.Add("Flat panel (Cover Calibrator) is not connected.");
            }

            if (CaptureFlats && !RunAllFilters) {
                var wheelFilters = _profileService.ActiveProfile.FilterWheelSettings.FilterWheelFilters;
                if (wheelFilters != null && wheelFilters.Any()
                        && !string.IsNullOrEmpty(FilterName)
                        && !wheelFilters.Any(f => string.Equals(f.Name, FilterName, StringComparison.OrdinalIgnoreCase))) {
                    issues.Add($"Filter \"{FilterName}\" was not found in the active filter wheel's filter list.");
                }
            }

            Issues = issues;
            return issues.Count == 0;
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            // ROUND 32v - this sequence item is a completely separate call path from the dockable
            // panel's own SmartCalFramesVM.RunAsync (see SmartCalFramesVM.Instance's comment) - without
            // this, a run started from the Advanced Sequencer never touched the panel's on-screen RunLog
            // at all, so it kept showing whatever a previous manual test had left there, unclear and
            // unchanged, for the whole run. Clears the panel (if one exists) right away, then wraps the
            // real `progress` NINA gave us so every status this run reports - search attempts, keeper
            // frames, per-filter summaries - reaches BOTH NINA's own sequencer status display and this
            // panel's RunLog, the same way a manually-started run already updates both via its own local
            // `progress` object in SmartCalFramesVM.RunAsync.
            SmartCalFramesVM.Instance?.ClearForExternalRun();
            // ROUND 32v-fix1 - must be typed IProgress<ApplicationStatus>, not `var`/Progress<T>:
            // System.Progress<T> implements IProgress<T>.Report EXPLICITLY, so calling .Report(...) on a
            // variable whose compile-time type is the concrete Progress<T> class fails to build ("does
            // not contain a definition for 'Report'") even though the object genuinely has one - it's
            // only reachable through the interface. Declaring the local as the interface type from the
            // start makes every call below (both the implicit ones passed into the service methods and
            // the explicit .Report(...) calls further down) resolve correctly.
            IProgress<ApplicationStatus> mirroredProgress = new Progress<ApplicationStatus>(s => {
                progress.Report(s);
                SmartCalFramesVM.Instance?.AppendExternalStatus(s?.Status);
            });

            var settingsProvider = new SmartCalSettingsProvider(_profileService);
            var settings = settingsProvider.Load();
            var service = new SmartCalCaptureService(
                _cameraMediator, _filterWheelMediator, _flatDeviceMediator, _imagingMediator,
                _imageSaveMediator, settingsProvider, settings);

            // ROUND 17 - this sequencer instruction is a SEPARATE call path into
            // SmartCalCaptureService from the dockable panel's SmartCalFramesVM.RunAsync, and shares
            // the same NextFrameNumber counter (same SmartCalSettingsProvider, same per-profile store)
            // so a filename sequence started from a sequence item and continued from the dockable panel
            // (or vice versa) still doesn't collide. Seeded before the run, persisted back in finally
            // regardless of how the item ends (success, Stop, or an exception NINA surfaces as a failed
            // sequence item), for the same reason as the VM: a number the camera actually consumed must
            // never be handed out again. ROUND 40 - one counter shared across ALL FOUR functions in this
            // one Execute() call, same reasoning.
            var frameCounter = new FrameSequenceCounter { Next = settingsProvider.LoadNextFrameNumber() };
            try {
                // ROUND 40 - one cover close covering every checked function in this single Execute()
                // call, not one per function - this whole call is one user-initiated action (one item
                // dragged into the sequence), the same "once per action" granularity
                // EnsureFlatPanelCoverClosedAsync already uses for the dockable panel's Run buttons. This
                // item previously had NO cover guard at all (a gap from Round 38, which only wired it
                // into the dockable panel) - closed here as part of this round. ROUND 69 - the cover is
                // deliberately NOT reopened in the finally below anymore; see
                // LeaveFlatPanelCoverClosedAfterRunAsync's own doc comment.
                if (!await SmartCalRunPlanning.EnsureFlatPanelCoverClosedAsync(_flatDeviceMediator, mirroredProgress, token)) {
                    return;
                }

                // ROUND 40 - each function below catches its own Exception (not OperationCanceledException
                // - a Stop is a deliberate user/NINA action and should abort the whole item, not be treated
                // as "this function failed") so one function failing doesn't cost you the rest, mirroring
                // this round's other per-group/per-filter resilience (RunAllFiltersAsync for flats,
                // RunDarkFramesAsync's per-group try/catch).
                if (CaptureFlats) {
                    // ROUND 67/68 - "Pause for cover swap" (see SmartCalSettings.PauseForCoverSwap):
                    // bracket the Flat Frames function with a real modal dialog (PauseForCoverSwapAsync
                    // below) so this works from an unattended sequence run regardless of whether the
                    // dockable panel has ever been opened this NINA session.
                    if (settings.PauseForCoverSwap) {
                        await PauseForCoverSwapAsync("Remove the cover and put the flat panel in place, then click Continue.", mirroredProgress, token);
                    }
                    await RunFlatsFunctionAsync(service, frameCounter, mirroredProgress, token);
                    if (settings.PauseForCoverSwap) {
                        await PauseForCoverSwapAsync("Remove the flat panel and put the cover back on, then click Continue.", mirroredProgress, token);
                    }
                }
                if (CaptureFlatDarks) {
                    await RunFlatDarksFunctionAsync(service, settingsProvider, settings, frameCounter, mirroredProgress, token);
                }
                if (CaptureBias) {
                    await RunBiasFunctionAsync(service, settings, frameCounter, mirroredProgress, token);
                }
                if (CaptureDarkFrames) {
                    await RunDarkFramesFunctionAsync(service, settingsProvider, frameCounter, mirroredProgress, token);
                }
            } finally {
                settingsProvider.SaveNextFrameNumber(frameCounter.Next);
                // ROUND 72 - settings was already loaded above (for PauseForCoverSwap); OpenCoverAfterRun
                // is read from that same snapshot rather than reloading, matching how settings is already
                // reused for every function's own settings needs in this method.
                await SmartCalRunPlanning.LeaveFlatPanelCoverClosedAfterRunAsync(_flatDeviceMediator, mirroredProgress, settings.OpenCoverAfterRun);
            }
        }

        /// <summary>
        /// ROUND 68 - shows a real, modal NINA dialog and awaits the user's Continue/Cancel click,
        /// independent of whether the dockable panel has ever been opened this session. This replaces
        /// Round 67's SmartCalFramesVM.Instance-routed version, which silently skipped the pause entirely
        /// if the panel had never been opened (a real gap for an unattended sequence-only workflow -
        /// "how could someone not open a dockable panel?" is exactly the case this fixes). Confirmed
        /// working end-to-end by the user's own real NINA test.
        ///
        /// Mechanism confirmed two independent ways before writing this, per this project's standing
        /// rule of never guessing NINA SDK behavior: (1) a dnfile metadata dump of the user's own
        /// installed NINA.Core.dll/NINA.Sequencer.dll (3.2.0.9001) shows NINA's built-in "Message Box"
        /// sequence item takes IWindowServiceFactory via [ImportingConstructor] and that
        /// IWindowService.ShowDialog(object content, string title, ...) returns an awaitable
        /// IDispatcherOperationWrapper; (2) that built-in item's real source (isbeorn/nina,
        /// NINA.Sequencer/SequenceItem/Utility/MessageBox.cs) shows the exact call shape used here -
        /// `windowServiceFactory.Create()` for the content, `await service.ShowDialog(result, title)`,
        /// and `token.Register(() => service?.Close())` to force the dialog closed if the sequence is
        /// stopped while it's up.
        ///
        /// ROUND 70 - the content object changed from NINA's own MessageBoxResult to this plugin's own
        /// CoverSwapDialogResult (see that class's own doc comment for why: real testing found the stock
        /// NINA dialog too narrow/tall and the wrong color, none of which NINA's own type/template lets
        /// this plugin control). Behavior is otherwise identical - Cancel still stops the whole sequence
        /// (via ItemUtility.GetRootContainer(...).Interrupt()), matching NINA's own built-in item's
        /// Continue/Cancel semantics.
        /// </summary>
        private async Task PauseForCoverSwapAsync(string message, IProgress<ApplicationStatus> progress, CancellationToken token) {
            progress.Report(new ApplicationStatus { Status = $"Smart Calibration Frames: Paused - {message}" });
            var dialogService = _windowServiceFactory.Create();
            var dialogResult = new CoverSwapDialogResult(message, dialogService);
            using (token.Register(() => dialogService?.Close())) {
                await dialogService.ShowDialog(dialogResult, "Smart Calibration Frames");
            }
            token.ThrowIfCancellationRequested();

            if (!dialogResult.Continue) {
                Logger.Info("Smart Calibration Frames: \"Pause for cover swap\" dialog was cancelled - stopping the sequence.");
                var root = NINA.Sequencer.Utility.ItemUtility.GetRootContainer(this.Parent);
                root?.Interrupt();
            }
        }

        private async Task RunFlatsFunctionAsync(SmartCalCaptureService service, FrameSequenceCounter frameCounter, IProgress<ApplicationStatus> progress, CancellationToken token) {
            try {
                int binX = BinningX < 1 ? 1 : BinningX;
                int binY = BinningY < 1 ? 1 : BinningY;
                var camInfo = _cameraMediator.GetInfo();
                int gain = Gain >= 0 ? Gain : (camInfo?.Gain ?? -1);
                int offset = Offset >= 0 ? Offset : (camInfo?.Offset ?? -1);

                if (RunAllFilters) {
                    var allFilters = _profileService.ActiveProfile.FilterWheelSettings.FilterWheelFilters;
                    var results = await service.RunAllFiltersAsync(allFilters, binX, binY, gain, offset, frameCounter, progress, token);
                    foreach (var r in results) {
                        progress.Report(new ApplicationStatus { Status = r.Summary });
                    }
                } else {
                    var wheelFilters = _profileService.ActiveProfile.FilterWheelSettings.FilterWheelFilters;
                    var filter = wheelFilters?.FirstOrDefault(f => string.Equals(f.Name, FilterName, StringComparison.OrdinalIgnoreCase));
                    var result = await service.RunAsync(filter, binX, binY, gain, offset, frameCounter, progress, token);
                    progress.Report(new ApplicationStatus { Status = result.Summary });
                }
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                string failLine = $"Smart Calibration Frames: Flat Frames function FAILED - {ex.Message}";
                Logger.Error(failLine);
                progress.Report(new ApplicationStatus { Status = failLine });
            }
        }

        /// <summary>Mirrors SmartCalFramesVM.RunFlatDarksAsync's body - same grouping (SmartCalRunPlanning.BuildFlatDarkGroups), same bin1x1/current-camera-gain-offset convention, same DarkFramesPerGroup setting.</summary>
        private async Task RunFlatDarksFunctionAsync(SmartCalCaptureService service, SmartCalSettingsProvider provider, SmartCalSettings settings, FrameSequenceCounter frameCounter, IProgress<ApplicationStatus> progress, CancellationToken token) {
            try {
                var groups = SmartCalRunPlanning.BuildFlatDarkGroups(_profileService, provider, settings);
                if (groups.Count == 0) {
                    progress.Report(new ApplicationStatus { Status = "Smart Calibration Frames: Flat Darks function - no filters found in the active filter wheel to build dark groups from." });
                    return;
                }
                int framesPerGroup = Math.Max(1, settings.DarkFramesPerGroup);
                var camInfo = _cameraMediator.GetInfo();
                int gain = camInfo?.Gain ?? -1;
                int offset = camInfo?.Offset ?? -1;
                var results = await service.RunFlatDarksAsync(groups.Select(g => g.Exposure), 1, 1, gain, offset, framesPerGroup, frameCounter, progress, token);
                progress.Report(new ApplicationStatus { Status = $"Smart Calibration Frames: Flat Darks complete - {results.Count} exposure group(s) captured." });
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                string failLine = $"Smart Calibration Frames: Flat Darks function FAILED - {ex.Message}";
                Logger.Error(failLine);
                progress.Report(new ApplicationStatus { Status = failLine });
            }
        }

        /// <summary>Mirrors SmartCalFramesVM.RunBiasFramesAsync's body - same exposure resolution (SmartCalRunPlanning.ResolveBiasExposureSeconds), same bin1x1/current-camera-gain-offset convention, same BiasFrameCount setting.</summary>
        private async Task RunBiasFunctionAsync(SmartCalCaptureService service, SmartCalSettings settings, FrameSequenceCounter frameCounter, IProgress<ApplicationStatus> progress, CancellationToken token) {
            try {
                var camInfo = _cameraMediator.GetInfo();
                int frameCount = Math.Max(1, settings.BiasFrameCount);
                double exposureSeconds = SmartCalRunPlanning.ResolveBiasExposureSeconds(camInfo, _profileService);
                int gain = camInfo?.Gain ?? -1;
                int offset = camInfo?.Offset ?? -1;
                string summary = await service.RunBiasFramesAsync(frameCount, exposureSeconds, 1, 1, gain, offset, frameCounter, progress, token);
                progress.Report(new ApplicationStatus { Status = summary });
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                string failLine = $"Smart Calibration Frames: Bias Frames function FAILED - {ex.Message}";
                Logger.Error(failLine);
                progress.Report(new ApplicationStatus { Status = failLine });
            }
        }

        /// <summary>Mirrors SmartCalFramesVM.RunDarkFramesAsync's body - same rows/grouping (SmartCalRunPlanning.LoadDarkFrameRows/BuildDarkFrameGroups/FindMisconfiguredDarkFrameFilters), same current-camera-gain-offset convention, same per-group resilient summary. The one thing this path can do that the dockable button can't: optionally source each filter's exposure from this run's own light-frame history instead of the tab's typed value - see ApplyLightHistoryExposures.</summary>
        private async Task RunDarkFramesFunctionAsync(SmartCalCaptureService service, SmartCalSettingsProvider provider, FrameSequenceCounter frameCounter, IProgress<ApplicationStatus> progress, CancellationToken token) {
            try {
                var rows = SmartCalRunPlanning.LoadDarkFrameRows(_profileService, provider);

                if (MatchDarkExposuresToLightHistory) {
                    rows = ApplyLightHistoryExposures(rows, progress);
                }

                var misconfigured = SmartCalRunPlanning.FindMisconfiguredDarkFrameFilters(rows);
                if (misconfigured.Count > 0) {
                    progress.Report(new ApplicationStatus { Status = $"Smart Calibration Frames: Dark Frames function - skipping {string.Join(", ", misconfigured)} (frame count is set but exposure is still 0)." });
                }

                var groups = SmartCalRunPlanning.BuildDarkFrameGroups(rows);
                if (groups.Count == 0) {
                    progress.Report(new ApplicationStatus { Status = "Smart Calibration Frames: Dark Frames function - no filters have both a frame count and an exposure set; nothing to capture." });
                    return;
                }

                var camInfo = _cameraMediator.GetInfo();
                int gain = camInfo?.Gain ?? -1;
                int offset = camInfo?.Offset ?? -1;
                var results = await service.RunDarkFramesAsync(
                    groups.Select(g => (g.Exposure, g.Binning, g.FrameCount, g.Filters)),
                    gain, offset, frameCounter, progress, token);
                foreach (var r in results) {
                    progress.Report(new ApplicationStatus { Status = $"Smart Calibration Frames: Dark Frames - {r}" });
                }
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                string failLine = $"Smart Calibration Frames: Dark Frames function FAILED - {ex.Message}";
                Logger.Error(failLine);
                progress.Report(new ApplicationStatus { Status = failLine });
            }
        }

        /// <summary>
        /// ROUND 40 - "Match dark exposures to light frames": overrides each row's ExposureSeconds with
        /// the MOST RECENT LIGHT-type exposure this run's IImageHistoryVM has recorded for that filter
        /// (per explicit direction - "most recent" wins if a filter was shot at more than one exposure
        /// this run), falling back to the row's own persisted Exposure (the Dark Frames tab's typed
        /// value) if no LIGHT history entry exists yet for that filter (also per explicit direction) -
        /// e.g. this item runs before that filter has ever been shot this session.
        ///
        /// Confirmed directly against the installed NINA.WPF.Base.dll 3.2.0.9001 (dnfile-based signature
        /// dump, not guessed): IImageHistoryVM.ImageHistory -> List&lt;ImageHistoryPoint&gt;;
        /// ImageHistoryPoint.Filter (string), .Duration (double, the exposure length), .Type (string),
        /// .Id (int, assigned via GetNextImageId() - monotonically increasing, used here as "most recent"
        /// instead of dateTime since it's guaranteed populated and ordered). Filtered on
        /// Type == "LIGHT" (case-insensitive) - the exact string NINA's own "Take Exposure"/"Smart
        /// Exposure" instructions tag light frames with in the Image History graph; NOT independently
        /// confirmed against IL (dump_sig.py reads signatures, not string literals inside method bodies),
        /// so if no LIGHT entries are ever found despite lights clearly having been captured, that casing/
        /// value is the first thing to check.
        ///
        /// Binning is deliberately NOT touched here: image history has nowhere to retain per-frame
        /// binning at all - confirmed by reading every property ImageHistoryPoint (and the
        /// ImageSavedEventArgs it's populated from) exposes, and binning is on neither. Binning always
        /// comes from the Dark Frames tab regardless of this option.
        /// </summary>
        private List<SmartCalRunPlanning.DarkFrameRowSnapshot> ApplyLightHistoryExposures(
                List<SmartCalRunPlanning.DarkFrameRowSnapshot> rows, IProgress<ApplicationStatus> progress) {
            Dictionary<string, double> latestLightExposureByFilter;
            try {
                latestLightExposureByFilter = (_imageHistoryVM?.ImageHistory ?? new List<NINA.WPF.Base.Model.ImageHistoryPoint>())
                    .Where(p => string.Equals(p.Type, "LIGHT", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(p.Filter) && p.Duration > 0)
                    .GroupBy(p => p.Filter, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.Id).First().Duration, StringComparer.OrdinalIgnoreCase);
            } catch (Exception ex) {
                // Best-effort - if image history can't be read for any reason, fall back to every row's
                // manual tab exposure rather than failing the whole Dark Frames function over an optional
                // convenience feature.
                progress.Report(new ApplicationStatus { Status = $"Smart Calibration Frames: Dark Frames function - could not read light-frame history ({ex.Message}); using the Dark Frames tab's manual exposures instead." });
                return rows;
            }

            var updated = new List<SmartCalRunPlanning.DarkFrameRowSnapshot>();
            foreach (var row in rows) {
                if (row.FrameCount <= 0) {
                    // Not "on" - leave it exactly as loaded, BuildDarkFrameGroups will skip it regardless.
                    updated.Add(row);
                    continue;
                }
                if (latestLightExposureByFilter.TryGetValue(row.FilterName, out double lightExposure) && lightExposure > 0) {
                    if (Math.Abs(lightExposure - row.ExposureSeconds) > 0.001) {
                        progress.Report(new ApplicationStatus { Status = $"Smart Calibration Frames: Dark Frames function - {row.FilterName} using {lightExposure:F2}s from this run's light frames (Dark Frames tab was set to {row.ExposureSeconds:F2}s)." });
                    }
                    updated.Add(row.WithExposureSeconds(lightExposure));
                } else {
                    if (row.ExposureSeconds > 0) {
                        progress.Report(new ApplicationStatus { Status = $"Smart Calibration Frames: Dark Frames function - no light frame captured yet for {row.FilterName} this run; using the Dark Frames tab's {row.ExposureSeconds:F2}s." });
                    }
                    updated.Add(row);
                }
            }
            return updated;
        }

        public override string ToString() {
            var functions = new List<string>();
            if (CaptureFlats) functions.Add(RunAllFilters ? "Flats (all filters)" : $"Flats ({FilterName})");
            if (CaptureFlatDarks) functions.Add("Flat Darks");
            if (CaptureBias) functions.Add("Bias");
            if (CaptureDarkFrames) functions.Add(MatchDarkExposuresToLightHistory ? "Dark Frames (from lights)" : "Dark Frames");
            string summary = functions.Count > 0 ? string.Join(" + ", functions) : "no function selected";
            return $"{nameof(SmartCalSequenceItem)}, {summary}";
        }
    }
}
