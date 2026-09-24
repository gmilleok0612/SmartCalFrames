using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.ViewModel;
using SmartCalFrames.Equipment;
using SmartCalFrames.Options;

namespace SmartCalFrames.Dockable {

    /*
     * Interactive counterpart to the sequencer instruction: lets you watch the current per-filter
     * defaults and run a manual smart-flats pass without building a sequence.
     *
     * ROUND 32 - removed "Calibrate Flat Device" and "Recalibrate this filter" entirely. Both existed
     * to manage the old learned-curve/history model (FlatCalibrationStore), which no longer exists -
     * each filter now holds exactly one remembered (brightness, exposure) pair, edited directly on the
     * Options page or updated automatically by a converged run. There's nothing left to "sweep" or
     * "clear history for" - resetting a filter's values is just editing them on the Options page.
     *
     * Verified against the installed NINA.Equipment/NINA.WPF.Base/NINA.Core 3.2.0.9001 assemblies:
     * IDockableVM lives in NINA.Equipment.Interfaces.ViewModel (not NINA.WPF.Base, despite DockableVM -
     * the base class implementing it - living there); DockableVM's constructor takes just
     * (IProfileService); ImageGeometry is a System.Windows.Media.GeometryGroup; RelayCommand(Action<object>)
     * and AsyncCommand<T>(Func<Task<T>>) match what's used below.
     */
    [Export(typeof(IDockableVM))]
    public class SmartCalFramesVM : DockableVM {

        // ROUND 32v - lets SmartCalSequenceItem (a completely separate MEF-composed object, executed
        // by NINA's Advanced Sequencer engine - not by anything on this panel) reach this SAME panel's
        // on-screen RunLog/StatusText. Without this, an Advanced-Sequencer-driven run never touches this
        // VM at all: the Round 32r RunLog.Clear() fix lives inside RunAsync below, which only runs when
        // the user clicks one of THIS panel's own two buttons - a sequence item's Execute() is a totally
        // separate call path (see its own comment) that previously left whatever the panel happened to
        // be showing from the last manual test sitting there for the whole run, unclear and unchanged.
        // Always overwritten (never conditionally set) so the latest-constructed VM - the one actually
        // bound to whatever the user is looking at - is always the one a sequence item reaches.
        public static SmartCalFramesVM Instance { get; private set; }

        private readonly IProfileService _profileService;
        private readonly ICameraMediator _cameraMediator;
        private readonly IFilterWheelMediator _filterWheelMediator;
        private readonly IFlatDeviceMediator _flatDeviceMediator;
        private readonly IImagingMediator _imagingMediator;
        private readonly IImageSaveMediator _imageSaveMediator;

        private CancellationTokenSource _cts;

        [ImportingConstructor]
        public SmartCalFramesVM(
            IProfileService profileService,
            ICameraMediator cameraMediator,
            IFilterWheelMediator filterWheelMediator,
            IFlatDeviceMediator flatDeviceMediator,
            IImagingMediator imagingMediator,
            IImageSaveMediator imageSaveMediator) : base(profileService) {

            _profileService = profileService;
            _cameraMediator = cameraMediator;
            _filterWheelMediator = filterWheelMediator;
            _flatDeviceMediator = flatDeviceMediator;
            _imagingMediator = imagingMediator;
            _imageSaveMediator = imageSaveMediator;
            Instance = this;

            Title = "Smart Calibration Frames";
            // Plugin-owned stacked-frames glyph - same shape as the SCFStackedFramesSVG resource in
            // Templates/SmartCalDataTemplates.xaml (which SmartCalSequenceItem's
            // ExportMetadata("Icon", ...) references for the Advanced Sequencer tree), but built
            // directly here with Geometry.Parse rather than looked up via
            // Application.Current.Resources["SCFStackedFramesSVG"]. Reason: this constructor runs
            // during MEF composition, and there's no guarantee NINA has finished merging this
            // plugin's own ResourceDictionary into the app's resources by that point - a lookup here
            // can silently return null (leaving ImageGeometry unset -> NINA's generic placeholder
            // icon in the Imaging tab's tool strip) even though the exact same key resolves fine
            // later, when the Advanced Sequencer actually renders the tree item on screen. Building
            // the Geometry inline sidesteps that ordering question entirely.
            //
            // "F1 " prefix = WPF path mini-language for FillRule=Nonzero. Without it, Geometry.Parse
            // defaults to EvenOdd. Nonzero is what the SCFStackedFramesSVG XAML resource's own
            // PathGeometry.FillRule is also set to, so both places render identically.
            //
            // This shape is three stacked, OPAQUELY OCCLUDING frames (like a fanned stack of opaque
            // cards, each with a window cut out) rather than three simple hollow rings unioned
            // together - an earlier version let a "behind" frame's solid body show through a "front"
            // frame's own hole (physically correct for a literal see-through picture frame, but read as
            // merged/see-through instead of three distinct stacked frames). Fixed by z-ordering the
            // three frames front-to-back and subtracting each frame's FULL outer footprint (not just
            // its ring) from every frame behind it, so a frame in front fully occludes what's behind it,
            // including through its own window. The resulting compound shape (4 closed loops) was
            // computed numerically (rasterized mask + boolean occlusion + marching-squares contour +
            // Douglas-Peucker simplification) rather than hand-authored with arcs, which is why the
            // coordinates below are a dense polygon rather than clean A-command arcs - at this icon's
            // rendered size (16-32px) the polygon corners are visually indistinguishable from the true
            // rounded corners they approximate. Note: this is a single-Fill Geometry - NINA applies one
            // theme brush to the whole shape, there's no separate stroke/second-color slot to make the
            // frame border and interior two different fixed colors.
            var stackedFramesIcon = new GeometryGroup();
            // The GROUP's own FillRule is what actually governs the combined render (it doesn't
            // inherit/respect a child Geometry's individually-set FillRule) - setting only the child
            // PathGeometry's fill rule (via the "F1 " prefix below) was an earlier, ineffective fix.
            stackedFramesIcon.FillRule = FillRule.Nonzero;
            stackedFramesIcon.Children.Add(Geometry.Parse(
                "F1 M1.60,0.50 L10.42,0.50 L10.94,0.71 L11.31,1.08 L11.48,1.47 L11.48,6.47 L16.32,6.49 L16.93,6.71 L17.30,7.07 L17.51,7.68 L17.53,12.52 L22.53,12.52 L22.92,12.69 L23.29,13.06 L23.50,13.58 L23.50,22.40 L23.20,23.05 L22.87,23.33 L22.40,23.50 L13.58,23.50 L13.06,23.29 L12.69,22.92 L12.52,22.53 L12.52,17.53 L7.68,17.51 L7.07,17.30 L6.71,16.93 L6.49,16.32 L6.47,11.48 L1.47,11.48 L1.08,11.31 L0.71,10.94 L0.50,10.42 L0.50,1.60 L0.80,0.95 L1.13,0.67 Z " +
                "M1.60,1.06 L1.28,1.26 L1.06,1.60 L1.06,10.42 L1.15,10.59 L1.43,10.87 L1.69,10.96 L10.37,10.91 L10.63,10.83 L10.96,10.33 L10.96,1.69 L10.87,1.43 L10.42,1.06 Z " +
                "M11.50,7.05 L11.48,10.50 L11.22,11.02 L10.76,11.39 L10.50,11.48 L7.05,11.50 L7.05,16.36 L7.16,16.60 L7.64,16.95 L12.50,16.95 L16.36,16.95 L16.58,16.86 L16.91,16.49 L16.95,7.64 L16.84,7.40 L16.36,7.05 Z " +
                "M17.53,13.04 L17.51,16.32 L17.30,16.93 L16.93,17.30 L16.32,17.51 L13.04,17.53 L13.04,22.31 L13.13,22.57 L13.58,22.94 L18.45,22.94 L22.40,22.94 L22.66,22.81 L22.89,22.53 L22.94,13.58 L22.57,13.13 L22.31,13.04 Z"));
            stackedFramesIcon.Freeze();
            ImageGeometry = stackedFramesIcon;

            KnownFilters = new ObservableCollection<string>();
            RunLog = new ObservableCollection<string>();
            StopCommand = new RelayCommand(_ => _cts?.Cancel());

            // ROUND 73 - image-preview zoom controls, per explicit request ("increase or decrease the
            // image size... similar to the Flat Wizard display"). ImageZoom is a plain multiplier bound
            // to a ScaleTransform on the preview Image element in the View (1.0 = 100%/no scaling).
            // Clamped to a sane [0.25, 8.0] range so repeated clicks can't zoom to nothing or to an
            // unusably huge, memory-heavy render size.
            //
            // ROUND 74 FIX - reported: "Even at 25% it still too large to fit inside the window. There
            // should be a way to view the entire frame that is equal to the size of the window." Root
            // cause: a fixed zoom PERCENTAGE has no idea how big the actual captured frame is or how big
            // the docked panel currently is - a full-frame camera's native pixel size can be large enough
            // that even 25% (the bottom of the old clamp range) is still wider than a ~350px docked
            // panel, and a narrower/wider panel would need a different "25%" every time anyway. Any
            // manual zoom-BY-PERCENTAGE control is fundamentally the wrong tool for "show me the whole
            // frame" - what's actually needed is a fit computed from the real available space, which is
            // exactly what View's Viewbox (Stretch="Uniform", StretchDirection="Both") does automatically
            // - see FitToWindow below and the two-mode Border in the View. Manual zoom is kept alongside
            // it (still useful for examining detail once the whole frame is visible), it's just no longer
            // the ONLY way to view a captured frame - clicking any zoom button now explicitly drops out
            // of fit mode, since a manual multiplier and "fill the available space" can't both drive the
            // same Image at once.
            ZoomInCommand = new RelayCommand(_ => { FitToWindow = false; ImageZoom = Math.Min(ImageZoom * 1.25, 8.0); });
            ZoomOutCommand = new RelayCommand(_ => { FitToWindow = false; ImageZoom = Math.Max(ImageZoom / 1.25, 0.25); });
            ResetZoomCommand = new RelayCommand(_ => { FitToWindow = false; ImageZoom = 1.0; });
            FitToWindowCommand = new RelayCommand(_ => FitToWindow = true);

            // ROUND 67 - "Pause for cover swap" manual-gear support (see PauseForCoverSwapAsync below
            // and SmartCalSettings.PauseForCoverSwap). Continue only ever does anything while a pause is
            // actually in progress (_coverSwapContinueSignal non-null) - a stray click otherwise (there's
            // no Continue button visible anyway, IsAwaitingCoverSwap gates that) is just a no-op.
            ContinueAfterCoverSwapCommand = new RelayCommand(_ => _coverSwapContinueSignal?.TrySetResult(true));

            // ROUND 66 - "Run Flats" tab's own "Capture flats" button, RESTORED. Round 47 deleted this
            // tab's bottom button row on the assumption that the "Run All" strip's "Run Selected" button
            // (RunSelectedBatchCommand -> RunAsync(allFilters:true, standalone:false)) made it redundant -
            // but that path ALWAYS runs every filter in the wheel; it can't target just the one filter
            // picked in the dropdown above. Per explicit report ("Run selected runs ALL filters. No way
            // to just run a single filter. It needs a Capture Flats button like the others") this was a
            // real capability gap, not just a discoverability one - restoring a direct single-filter
            // capture button here, same allFilters:false path the original (Round 59/61-deleted)
            // RunSelectedCommand used to call, just under a name that matches this tab's sibling buttons
            // (RunFlatDarksCommand/RunBiasFramesCommand/RunDarkFramesCommand below) instead of the old
            // "RunSelectedCommand" name, which reads confusingly close to RunSelectedBatchCommand now
            // that both exist side by side.
            RunFlatsCommand = new AsyncCommand<bool>(() => RunAsync(allFilters: false));

            // ROUND 36 - "Flat Darks" tab. Reuses this SAME IsRunning/_cts/StopCommand as the flats
            // "Run" tab, deliberately - the two are mutually exclusive from this one panel (you can't
            // run flats and darks at once), and Stop already works for either without any new plumbing.
            PreviewDarkGroupsCommand = new RelayCommand(_ => RefreshDarkGroupsPreview());
            RunFlatDarksCommand = new AsyncCommand<bool>(() => RunFlatDarksAsync());

            // ROUND 1 (Smart Cal Frames) - "Bias Frames" tab. Same shared IsRunning/_cts/StopCommand as
            // the other two tabs, deliberately - only one of the three can run from this panel at once,
            // and Stop already works for any of them without new plumbing.
            RunBiasFramesCommand = new AsyncCommand<bool>(() => RunBiasFramesAsync());

            // ROUND 39 - "Dark Frames" tab (renamed same session from "Light Darks" per explicit user
            // feedback - "I've never heard of light dark frames"). Same shared IsRunning/_cts/StopCommand
            // as the other three tabs, deliberately - only one of the four can run from this panel at once.
            DarkFrameRows = new ObservableCollection<DarkFrameRowVM>();
            RunDarkFramesCommand = new AsyncCommand<bool>(() => RunDarkFramesAsync());

            // ROUND 43 - "Run All" strip: queue and run any combination of the four routines above with
            // one click. Explicit lambdas below (not bare method-group references) even where a method
            // now only takes its new optional `standalone` parameter - relying on C#'s method-group-to-
            // delegate optional-parameter elision isn't worth the risk of getting it wrong when a plain
            // `() => Foo()` lambda is just as short and unambiguous. Selection defaults to unchecked
            // (nothing pre-selected) - same "opting in is always explicit" convention already used for
            // DarkFrameRows' FrameCount (Round 39) - one click shouldn't be able to fire off every
            // routine unless the user actually checked the boxes.
            SelectAllRunItemsCommand = new RelayCommand(_ => {
                RunFlatsSelected = true;
                RunFlatDarksSelected = true;
                RunBiasSelected = true;
                RunDarkFramesSelected = true;
            });
            SelectNoRunItemsCommand = new RelayCommand(_ => {
                RunFlatsSelected = false;
                RunFlatDarksSelected = false;
                RunBiasSelected = false;
                RunDarkFramesSelected = false;
            });
            RunSelectedBatchCommand = new AsyncCommand<bool>(RunSelectedBatchAsync);

            RefreshKnownFilters();
            RefreshDarkFrameRows();
        }

        // ---- Bindable state ----

        private string _statusText = "Idle";
        public string StatusText { get => _statusText; set { _statusText = value; RaisePropertyChanged(); } }

        private bool _isRunning;
        public bool IsRunning { get => _isRunning; set { _isRunning = value; RaisePropertyChanged(); } }

        /// <summary>
        /// ROUND 69 - mirrors Smart Flat Wizard's separate image-preview window (as an inline split
        /// instead of a separate window, per explicit request). Always reads the Options-page setting
        /// fresh (same "load fresh right before use" convention as DarkFramesPerGroup/FlatFrameCount
        /// above) rather than caching it - RefreshImagePreviewVisibility below is what tells this
        /// already-bound property's WPF binding to re-query it after the Options page changes it.
        /// </summary>
        public bool ShowImagePreview => new SmartCalSettingsProvider(_profileService).Load().ShowCapturedImagePreview;

        /// <summary>
        /// ROUND 69 - companion to SmartCalOptionsVM.ShowCapturedImagePreview's setter: that VM is a
        /// separate object and has no way to make THIS VM's already-bound ShowImagePreview property
        /// binding re-read the setting on its own. Mirrors the existing Round 34 cross-VM refresh pattern
        /// (RefreshFilterDefault) for the same class of problem in the other direction.
        /// </summary>
        public void RefreshImagePreviewVisibility() => RaisePropertyChanged(nameof(ShowImagePreview));

        private System.Windows.Media.Imaging.BitmapSource _lastCapturedImage;
        /// <summary>
        /// ROUND 69 - the most recently captured frame (search-phase attempt or production/keeper frame,
        /// whichever happened last, from whichever of the four capture functions is currently running).
        /// ROUND 71 - stretched (SmartCalCaptureService.EmitPreviewAsync applies the profile's own
        /// auto-stretch settings) rather than the raw linear render, so it actually shows visible detail
        /// instead of a near-featureless gray square. ROUND 72 - now fed by all four capture functions
        /// (Flats/Flat Darks/Bias/Dark Frames), not just Flats. Updated regardless of whether
        /// ShowImagePreview is currently true - the View's own binding decides visibility, not this
        /// property - so there's never a stale frame the moment the Options-page toggle is flipped on.
        /// </summary>
        public System.Windows.Media.Imaging.BitmapSource LastCapturedImage {
            get => _lastCapturedImage;
            private set { _lastCapturedImage = value; RaisePropertyChanged(); }
        }

        private double _imageZoom = 1.0;
        /// <summary>
        /// ROUND 73 - preview-image zoom multiplier (1.0 = 100%), per explicit request ("increase or
        /// decrease the image size"). Bound to a ScaleTransform on the preview Image element; the Image
        /// itself sits inside a ScrollViewer in the View so a zoomed-in frame can be panned/scrolled
        /// rather than just clipped. Adjusted via ZoomInCommand/ZoomOutCommand/ResetZoomCommand above.
        /// Only actually drives the displayed size while FitToWindow is false - see FitToWindow below.
        /// </summary>
        public double ImageZoom {
            get => _imageZoom;
            set { _imageZoom = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(ImageZoomPercentText)); }
        }

        private bool _fitToWindow = true;
        /// <summary>
        /// ROUND 74 - per explicit report that manual zoom percentages ("even at 25%") couldn't reliably
        /// show a whole captured frame inside the docked panel's actual (variable) available space. When
        /// true, the View displays the preview image in a Viewbox (Stretch="Uniform", StretchDirection=
        /// "Both") instead of the manual-zoom ScrollViewer+ScaleTransform, so the ENTIRE frame is always
        /// scaled - up or down, whichever is needed - to exactly fill the space actually available right
        /// now, at whatever size the docked panel happens to be. Defaults to true so a freshly captured
        /// frame is fully visible immediately with no zoom fiddling required; any zoom button click drops
        /// this back to false (see ZoomInCommand/ZoomOutCommand/ResetZoomCommand above) since manual zoom
        /// and "fill the available space" are mutually exclusive ways of sizing the same Image. The "Fit"
        /// button (FitToWindowCommand) returns to this mode from a manual zoom level.
        /// </summary>
        public bool FitToWindow {
            get => _fitToWindow;
            set { _fitToWindow = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(ImageZoomPercentText)); }
        }

        /// <summary>ROUND 73 - live "125%"-style readout next to the zoom buttons, mirroring the
        /// percentage shown in NINA's own image-view toolbar (see the screenshot in this round's
        /// discussion). ROUND 74 - reads "Fit" instead of a percentage while FitToWindow is active, since
        /// ImageZoom itself isn't what's actually sizing the image in that mode.</summary>
        public string ImageZoomPercentText => FitToWindow ? "Fit" : $"{ImageZoom:P0}";

        /// <summary>
        /// ROUND 69 companion to AppendExternalStatus - mirrors the most recently captured frame from a
        /// sequence-triggered run (SmartCalSequenceItem, running on NINA's own sequencer thread, not this
        /// VM's UI thread) onto this panel's image preview. ROUND 73-78 briefly extended this to also
        /// carry a per-frame histogram (data points + bit depth); ROUND 93 removed the histogram feature
        /// entirely per explicit request ("rip out all dead code" following "I don't think it's really
        /// that useful anyway"), so this is back to carrying just the image.
        /// </summary>
        public void AppendExternalPreview(System.Windows.Media.Imaging.BitmapSource image) {
            if (image == null) return;
            System.Windows.Application.Current?.Dispatcher.Invoke(() => {
                LastCapturedImage = image;
            });
        }

        public ObservableCollection<string> KnownFilters { get; }

        /// <summary>
        /// Running per-frame exposure/ADU log for this run, newest entry last (view scrolls to end).
        /// Populated from the same IProgress reports the sequencer's status bar uses, so it shows both
        /// the search-phase attempts (ConvergeOnTargetAsync in SmartCalCaptureService) and the
        /// production keeper frames - not just the single latest line StatusText holds. Capped so a
        /// long "Run all filters" pass doesn't grow this without bound.
        /// </summary>
        public ObservableCollection<string> RunLog { get; }
        private const int MaxRunLogLines = 400;

        /// <summary>
        /// ROUND 32v - entry point for SmartCalSequenceItem (running on NINA's own sequencer engine,
        /// not on this VM's UI thread) to clear this panel's on-screen log at the start of a
        /// sequence-triggered run, mirroring what RunAsync below already does for a manually-started
        /// one. Dispatcher.Invoke (not BeginInvoke) so the clear is guaranteed to have actually happened
        /// before the caller's first capture begins - and, just as important, so touching RunLog (a WPF-
        /// bound ObservableCollection) from whatever thread NINA runs sequence items on doesn't throw a
        /// cross-thread-access exception. Safe to call even if nothing is listening (Application.Current
        /// null, e.g. outside a running NINA instance) - it just becomes a no-op.
        /// </summary>
        public void ClearForExternalRun() {
            System.Windows.Application.Current?.Dispatcher.Invoke(() => RunLog.Clear());
        }

        /// <summary>
        /// ROUND 32v - companion to ClearForExternalRun: mirrors one status line from a sequence-
        /// triggered run onto this panel's StatusText/RunLog, the same way the local `progress` object
        /// inside RunAsync already does for a manually-started run. See ClearForExternalRun for why this
        /// has to go through the Dispatcher rather than touching RunLog directly.
        /// </summary>
        public void AppendExternalStatus(string status) {
            if (string.IsNullOrWhiteSpace(status)) return;
            System.Windows.Application.Current?.Dispatcher.Invoke(() => {
                StatusText = status;
                RunLog.Add($"{DateTime.Now:HH:mm:ss}  {status}");
                while (RunLog.Count > MaxRunLogLines) RunLog.RemoveAt(0);
            });
        }

        private string _selectedFilter;
        public string SelectedFilter { get => _selectedFilter; set { _selectedFilter = value; RaisePropertyChanged(); RefreshFilterDefaultsSummary(); } }

        private string _filterDefaultsSummary = "No filter selected.";
        public string FilterDefaultsSummary { get => _filterDefaultsSummary; set { _filterDefaultsSummary = value; RaisePropertyChanged(); } }

        public ICommand StopCommand { get; }
        public ICommand ZoomInCommand { get; }
        public ICommand ZoomOutCommand { get; }
        public ICommand ResetZoomCommand { get; }
        public ICommand FitToWindowCommand { get; }

        /// <summary>ROUND 66 - Run Flats tab's own "Capture flats" button. Runs RunAsync(allFilters:
        /// false), i.e. just the filter currently selected in this tab's own dropdown (SelectedFilter) -
        /// see the constructor comment for why this was restored.</summary>
        public IAsyncCommand RunFlatsCommand { get; }

        // ---- ROUND 67 - "Pause for cover swap" manual-gear support ----

        /// <summary>Mirrors SmartCalSettings.PauseForCoverSwap onto the Run All strip, same "always
        /// read/write through a fresh SmartCalSettingsProvider, no cached field" convention as
        /// FlatFrameCount/DarkFramesPerGroup/BiasFrameCount above - editing this here or on the Options
        /// page always reflects the same one stored value.</summary>
        public bool PauseForCoverSwap {
            get => new SmartCalSettingsProvider(_profileService).Load().PauseForCoverSwap;
            set {
                var provider = new SmartCalSettingsProvider(_profileService);
                var s = provider.Load();
                s.PauseForCoverSwap = value;
                provider.Save(s);
                RaisePropertyChanged();
            }
        }

        /// <summary>ROUND 72 - mirrors SmartCalSettings.OpenCoverAfterRun onto the Run All strip, same
        /// "always read/write through a fresh SmartCalSettingsProvider, no cached field" convention as
        /// PauseForCoverSwap immediately above.</summary>
        public bool OpenCoverAfterRun {
            get => new SmartCalSettingsProvider(_profileService).Load().OpenCoverAfterRun;
            set {
                var provider = new SmartCalSettingsProvider(_profileService);
                var s = provider.Load();
                s.OpenCoverAfterRun = value;
                provider.Save(s);
                RaisePropertyChanged();
            }
        }

        private bool _isAwaitingCoverSwap;
        /// <summary>True while a PauseForCoverSwapAsync call is waiting for Continue - the panel's
        /// Continue button binds its Visibility to this.</summary>
        public bool IsAwaitingCoverSwap { get => _isAwaitingCoverSwap; private set { _isAwaitingCoverSwap = value; RaisePropertyChanged(); } }

        private string _coverSwapMessage = string.Empty;
        /// <summary>What to tell the user while paused - also mirrored into StatusText/RunLog, but kept
        /// separately so the panel can show it prominently (e.g. next to the Continue button) regardless
        /// of whatever else has since been appended to RunLog.</summary>
        public string CoverSwapMessage { get => _coverSwapMessage; private set { _coverSwapMessage = value; RaisePropertyChanged(); } }

        public ICommand ContinueAfterCoverSwapCommand { get; }

        private TaskCompletionSource<bool> _coverSwapContinueSignal;

        // ---- ROUND 44 - Run Flats tab: "Frames per filter" simple control ----
        //
        // Per explicit user report ("how do I select the number of flat frames? ... even I can't do
        // so"): the real controls (MinFlatsPerFilter/MaxFlatsPerFilter on the Options page) are an
        // adaptive floor/ceiling, not a plain count, and the Run Flats tab itself showed nothing about
        // frame count at all - there was no hint the number even lived on a different page. These four
        // properties don't add any new persisted setting - they're friendlier read/write paths onto the
        // SAME MinFlatsPerFilter/MaxFlatsPerFilter values the Options page already edits, so changing
        // one from either page is instantly reflected on the other (both always Load() fresh, same
        // convention as DarkFramesPerGroup/BiasFrameCount above).
        //
        // FlatAdaptiveMode derives its value from the settings themselves (Min != Max means adaptive is
        // effectively already in play) rather than needing a new persisted flag - consistent with the
        // "set Min and Max to the same number" fixed-count convention already documented on the Options
        // page for this exact pair. Flipping it off collapses Max down to Min (a real fixed count from
        // then on); flipping it on gives Max some room above Min if they were equal (a bare "adaptive"
        // checkbox with a zero-width range would do nothing useful) - the exact spread mirrors this
        // plugin's own shipped defaults (Min 10 / Max 30, a spread of 20).
        public int FlatFrameCount {
            get => new SmartCalSettingsProvider(_profileService).Load().MinFlatsPerFilter;
            set {
                var provider = new SmartCalSettingsProvider(_profileService);
                var s = provider.Load();
                int v = Math.Max(1, value);
                s.MinFlatsPerFilter = v;
                s.MaxFlatsPerFilter = v;
                provider.Save(s);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(FlatMinFramesPerFilter));
                RaisePropertyChanged(nameof(FlatMaxFramesPerFilter));
                // ROUND 51 FIX - reported: "the adaptive checkbox is backwards. It's off when checked and
                // on when unchecked." Root cause: FlatAdaptiveMode is DERIVED (Min != Max), not its own
                // stored field - this setter (and FlatMinFramesPerFilter/FlatMaxFramesPerFilter below)
                // changes the very values FlatAdaptiveMode's getter reads, but never told WPF that
                // property might have changed too. The Adaptive checkbox's IsChecked binding went stale -
                // still showing whatever it last evaluated to - until something else happened to touch
                // it, making it look randomly inverted relative to the actual Min/Max state. Every setter
                // that can change whether Min==Max now raises this too.
                RaisePropertyChanged(nameof(FlatAdaptiveMode));
            }
        }

        private const int AdaptiveDefaultSpread = 20;

        public bool FlatAdaptiveMode {
            get {
                var s = new SmartCalSettingsProvider(_profileService).Load();
                return s.MinFlatsPerFilter != s.MaxFlatsPerFilter;
            }
            set {
                var provider = new SmartCalSettingsProvider(_profileService);
                var s = provider.Load();
                if (value) {
                    if (s.MinFlatsPerFilter == s.MaxFlatsPerFilter) {
                        s.MaxFlatsPerFilter = s.MinFlatsPerFilter + AdaptiveDefaultSpread;
                    }
                } else {
                    s.MaxFlatsPerFilter = s.MinFlatsPerFilter;
                }
                provider.Save(s);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(FlatFrameCount));
                RaisePropertyChanged(nameof(FlatMinFramesPerFilter));
                RaisePropertyChanged(nameof(FlatMaxFramesPerFilter));
            }
        }

        public int FlatMinFramesPerFilter {
            get => new SmartCalSettingsProvider(_profileService).Load().MinFlatsPerFilter;
            set {
                // ROUND 51 - see FlatFrameCount above: this changes Min, which FlatAdaptiveMode's getter
                // reads (Min != Max) - must notify that property too, or its bound CheckBox goes stale.
                var provider = new SmartCalSettingsProvider(_profileService);
                var s = provider.Load();
                s.MinFlatsPerFilter = Math.Max(1, value);
                provider.Save(s);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(FlatFrameCount));
                RaisePropertyChanged(nameof(FlatAdaptiveMode));
            }
        }

        public int FlatMaxFramesPerFilter {
            get => new SmartCalSettingsProvider(_profileService).Load().MaxFlatsPerFilter;
            set {
                // ROUND 51 - see FlatFrameCount above: this changes Max, which FlatAdaptiveMode's getter
                // reads (Min != Max) - must notify that property too, or its bound CheckBox goes stale.
                var provider = new SmartCalSettingsProvider(_profileService);
                var s = provider.Load();
                s.MaxFlatsPerFilter = Math.Max(1, value);
                provider.Save(s);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(FlatFrameCount));
                RaisePropertyChanged(nameof(FlatAdaptiveMode));
            }
        }

        // ---- ROUND 36 - "Flat Darks" tab bindable state ----

        /// <summary>
        /// How many dark frames to capture per exposure group. Reads/writes through a fresh
        /// SmartCalSettingsProvider on every access rather than a cached field, matching the same
        /// "always Load() fresh right before use" style already used by RefreshFilterDefaultsSummaryFor
        /// above - keeps this correct even if the Options page is open and being edited at the same time.
        /// </summary>
        public int DarkFramesPerGroup {
            get => new SmartCalSettingsProvider(_profileService).Load().DarkFramesPerGroup;
            set {
                var provider = new SmartCalSettingsProvider(_profileService);
                var s = provider.Load();
                s.DarkFramesPerGroup = Math.Max(1, value);
                provider.Save(s);
                RaisePropertyChanged();
            }
        }

        private string _darkGroupsPreview = "Click \"Preview groups\" to see the dark frame groups this will capture.";
        public string DarkGroupsPreview { get => _darkGroupsPreview; set { _darkGroupsPreview = value; RaisePropertyChanged(); } }

        // ROUND 56 - reported: "the Preview Groups should toggle the groups on or off. Once toggled it
        // stays on." Before this, the button only ever showed/refreshed the preview text - there was no
        // way to hide it again short of switching tabs. Now a real on/off toggle: showing =
        // refresh-and-show, showing again = hide. Bound on the TextBlock's Visibility in the XAML.
        private bool _darkGroupsPreviewVisible;
        public bool DarkGroupsPreviewVisible { get => _darkGroupsPreviewVisible; set { _darkGroupsPreviewVisible = value; RaisePropertyChanged(); } }

        public ICommand PreviewDarkGroupsCommand { get; }
        public IAsyncCommand RunFlatDarksCommand { get; }

        // ---- ROUND 39 - "Dark Frames" tab bindable state (renamed same session from "Light Darks" per
        // explicit user feedback - "I've never heard of light dark frames") ----

        /// <summary>
        /// One row per filter in the active filter wheel - Binning/Frame count/Exposure (s), edited
        /// directly on the tab (see DarkFrameRowVM). Built once in the constructor (RefreshDarkFrameRows)
        /// - unlike KnownFilters/SelectedFilter above, this doesn't need to react to a filter wheel swap
        /// mid-session any more than the Options page's own FilterDefaults grid does (same convention,
        /// same limitation, not new to this round).
        /// </summary>
        public ObservableCollection<DarkFrameRowVM> DarkFrameRows { get; }

        public IAsyncCommand RunDarkFramesCommand { get; }

        // ROUND 80 added SelectedTabIndex/IsDarkFramesTabSelected here, TwoWay-bound to the View's
        // TabControl.SelectedIndex, so the shared image/histogram row could tell which tab was active and
        // hide itself specifically while the Dark Frames tab's own (then-separate) embedded histogram was
        // showing instead. ROUND 83 removed the Dark Frames tab's embedded histogram - the histogram is
        // now the same single shared element on every tab, so there's nothing left that needs to know
        // which tab is selected. Both properties are gone; the TabControl in the View is a plain,
        // unbound-SelectedIndex control again.

        // ---- ROUND 43 - "Run All" strip bindable state ----

        private bool _runFlatsSelected;
        public bool RunFlatsSelected { get => _runFlatsSelected; set { _runFlatsSelected = value; RaisePropertyChanged(); } }

        private bool _runFlatDarksSelected;
        public bool RunFlatDarksSelected { get => _runFlatDarksSelected; set { _runFlatDarksSelected = value; RaisePropertyChanged(); } }

        private bool _runBiasSelected;
        public bool RunBiasSelected { get => _runBiasSelected; set { _runBiasSelected = value; RaisePropertyChanged(); } }

        private bool _runDarkFramesSelected;
        public bool RunDarkFramesSelected { get => _runDarkFramesSelected; set { _runDarkFramesSelected = value; RaisePropertyChanged(); } }

        public ICommand SelectAllRunItemsCommand { get; }
        public ICommand SelectNoRunItemsCommand { get; }
        public IAsyncCommand RunSelectedBatchCommand { get; }

        /// <summary>
        /// ROUND 39 REVISION - a fresh row's FrameCount now defaults to 0 (skip), not
        /// settings.DarkFramesPerGroup: since FrameCount is now the skip toggle (see BuildDarkFrameGroups
        /// below), defaulting it to a nonzero count would have every filter silently "on" the first time
        /// this tab is opened, most of them with exposure still at 0 - exactly the misconfigured state
        /// FindMisconfiguredDarkFrameFilters warns about. Opting a filter in is now something the user
        /// always does explicitly, by typing both a frame count and an exposure.
        /// </summary>
        private void RefreshDarkFrameRows() {
            DarkFrameRows.Clear();
            var provider = new SmartCalSettingsProvider(_profileService);
            // ROUND 40 - reads through the same SmartCalRunPlanning.LoadDarkFrameRows the new multi-
            // function sequencer item uses, so "what a fresh row starts at" can't drift between the two
            // call paths; each snapshot is then wrapped in a DarkFrameRowVM for this tab's live editing.
            foreach (var row in SmartCalRunPlanning.LoadDarkFrameRows(_profileService, provider)) {
                DarkFrameRows.Add(new DarkFrameRowVM(provider, row.FilterName, row.Binning, row.FrameCount, row.ExposureSeconds));
            }
        }

        /// <summary>
        /// ROUND 39, REVISED same session per explicit user feedback ("I think only if the count is 0
        /// should it be skipped") - groups DarkFrameRows down to capture groups: only rows with BOTH a
        /// real frame count (> 0 - a row left at 0 frames means "skip this filter") AND a real exposure
        /// (> 0) are included. A row with frames > 0 but exposure still 0 is deliberately left OUT of the
        /// groups here - shooting a 0-second "dark" would be meaningless - and is instead surfaced by
        /// FindMisconfiguredDarkFrameFilters below so the run warns about it instead of silently ignoring
        /// it or silently capturing garbage.
        ///
        /// Rows that agree on BOTH exposure (rounded to 2 decimals, same precision convention as
        /// BuildDarkGroups) AND binning share one group - a 300s/1x row and a 300s/2x row are genuinely
        /// different dark frames (binned pixels sum dark current differently), so they only merge when
        /// both match. A merged group's frame count is the LARGER of its members' requested counts, per
        /// explicit direction - a filter that asked for fewer frames just gets some harmless surplus
        /// rather than being under-served, and nothing needs to be captured twice for two filters that
        /// share both values.
        /// </summary>
        private List<(double Exposure, int Binning, int FrameCount, List<string> Filters)> BuildDarkFrameGroups() {
            // ROUND 40 - the actual grouping rule now lives in SmartCalRunPlanning.BuildDarkFrameGroups
            // (shared with the new multi-function sequencer item); this just adapts DarkFrameRows'
            // bindable DarkFrameRowVM objects into the plain snapshots that shared method takes.
            return SmartCalRunPlanning.BuildDarkFrameGroups(
                DarkFrameRows.Select(r => new SmartCalRunPlanning.DarkFrameRowSnapshot(r.FilterName, r.BinningFactor, r.FrameCount, r.ExposureSeconds)));
        }

        /// <summary>
        /// ROUND 39 REVISION - companion to BuildDarkFrameGroups: filters that are "on" (frame count > 0)
        /// but haven't had a real exposure entered yet (still 0). These are excluded from the actual
        /// capture groups rather than silently shooting a 0-second dark - RunDarkFramesAsync below logs
        /// them as a heads-up before the run starts, so a filter the user meant to include doesn't just
        /// silently get skipped without explanation.
        /// </summary>
        private List<string> FindMisconfiguredDarkFrameFilters() {
            return SmartCalRunPlanning.FindMisconfiguredDarkFrameFilters(
                DarkFrameRows.Select(r => new SmartCalRunPlanning.DarkFrameRowSnapshot(r.FilterName, r.BinningFactor, r.FrameCount, r.ExposureSeconds)));
        }

        // ---- ROUND 1 (Smart Cal Frames) - "Bias Frames" tab bindable state ----

        /// <summary>How many bias frames to capture. Same "always Load()/Save() fresh, no cached field" style as DarkFramesPerGroup above.</summary>
        public int BiasFrameCount {
            get => new SmartCalSettingsProvider(_profileService).Load().BiasFrameCount;
            set {
                var provider = new SmartCalSettingsProvider(_profileService);
                var s = provider.Load();
                s.BiasFrameCount = Math.Max(1, value);
                provider.Save(s);
                RaisePropertyChanged();
            }
        }

        public IAsyncCommand RunBiasFramesCommand { get; }

        /// <summary>
        /// ROUND 37d - the exposure a bias frame uses is not something the user should have to type in.
        /// There's no dedicated "take a bias frame" ASCOM/NINA call - every exposure command needs a
        /// duration - but the connected camera's driver already reports its own real shortest exposure
        /// via CameraInfo.ExposureMin (confirmed directly against the installed NINA.Equipment.dll
        /// 3.2.0.9001, not assumed). Falls back to the flats' own MinExposureSeconds safety-floor
        /// setting only if the camera doesn't report a usable (> 0) ExposureMin - some older/simpler
        /// ASCOM drivers leave it at its type default (0) rather than populating it for real.
        /// </summary>
        private double ResolveBiasExposureSeconds(NINA.Equipment.Equipment.MyCamera.CameraInfo camInfo) {
            // ROUND 40 - moved to SmartCalRunPlanning.ResolveBiasExposureSeconds so the new multi-function
            // sequencer item's Bias function resolves exposure exactly the same way, without duplicating
            // this logic a second time.
            return SmartCalRunPlanning.ResolveBiasExposureSeconds(camInfo, _profileService);
        }

        /// <summary>
        /// ROUND 38 - user's own White Dwarf panel has no motorized cover at all (CoverCalibrator.cs
        /// always reports CoverState.NotPresent and SupportsOpenClose false, and throws
        /// MethodNotImplementedException if OpenCover/CloseCover are ever called on it - confirmed
        /// directly from that driver's own source, not guessed) - so for that hardware this whole check
        /// is a complete no-op, exactly as before it existed: GetInfo().SupportsOpenClose reads false and
        /// this returns true immediately. It matters for a DIFFERENT class of device this plugin may run
        /// against someday - a motorized flip-flat-style panel (Alnitak Flip-Flat and similar), where the
        /// flap genuinely has to be closed over the aperture before ANY calibration frame - flat, dark, OR
        /// bias - can be trusted, since closing it is what actually blocks light on that hardware, the
        /// same job the White Dwarf's fixed panel body does today just by physically sitting in front of
        /// the scope. Confirmed directly against the real NINA.Equipment.dll 3.2.0.9001 before writing
        /// this (this project's standing discipline - never guess a NINA SDK signature):
        /// IFlatDeviceMediator.CloseCover(IProgress&lt;ApplicationStatus&gt;, CancellationToken) -&gt; Task,
        /// FlatDeviceInfo.SupportsOpenClose (bool), FlatDeviceInfo.CoverState (enum: Unknown,
        /// NeitherOpenNorClosed, Closed, Open, Error, NotPresent).
        ///
        /// Called once per user-initiated run (Run this filter / Run all filters / Flat Darks / Bias
        /// Frames / Dark Frames) - the SAME granularity as the existing camera/flat-panel Connected
        /// pre-flight checks above each run method - deliberately NOT inside SmartCalCaptureService's own
        /// per-filter RunAsync, since "Run all filters" calls that once per filter and re-checking/
        /// re-closing the cover on every single filter switch would be pure waste once it's already
        /// closed (the CoverState == Closed check makes every call after the first a cheap no-op
        /// regardless, but there's no reason to even try more than once per user action).
        ///
        /// ROUND 40 - the actual logic now lives in SmartCalRunPlanning.EnsureFlatPanelCoverClosedAsync
        /// (shared with the new multi-function sequencer item, which previously had NO cover guard at
        /// all - a gap this round also closed); this just wires a local IProgress that mirrors onto
        /// StatusText/RunLog the same way it always has.
        /// </summary>
        private async Task<bool> EnsureFlatPanelCoverClosedAsync(CancellationToken token) {
            var progress = new Progress<ApplicationStatus>(s => {
                if (string.IsNullOrWhiteSpace(s?.Status)) return;
                StatusText = s.Status;
                RunLog.Add($"{DateTime.Now:HH:mm:ss}  {s.Status}");
            });
            return await SmartCalRunPlanning.EnsureFlatPanelCoverClosedAsync(_flatDeviceMediator, progress, token);
        }

        /// <summary>
        /// Called from every run's finally block, once the run has finished (successfully, cancelled, or
        /// failed) - the counterpart to EnsureFlatPanelCoverClosedAsync above. ROUND 69 - per explicit
        /// direction ("make sure the cover never opens - after a run - it should remain shut"), this
        /// stopped reopening a motorized cover automatically; whatever was closed for the run stayed
        /// closed. ROUND 72 - that's now the OFF (default) state of a new opt-in setting,
        /// SmartCalSettings.OpenCoverAfterRun ("the user can have it any way they want") - read fresh here
        /// so the Run All strip's own checkbox and the Options page always agree. Logged for visibility,
        /// never fatal, never allowed to mask whatever the run's own real result was, same philosophy as
        /// SmartCalCaptureService's own ToggleLight(false) cleanup (a project convention since Round 16).
        /// A no-op (SupportsOpenClose false) for the White Dwarf and every other calibrator-only panel -
        /// no cost for that hardware, same as the check above.
        ///
        /// ROUND 40 - delegates to SmartCalRunPlanning.LeaveFlatPanelCoverClosedAfterRunAsync (renamed in
        /// ROUND 69 from ReopenFlatPanelCoverBestEffortAsync, same reason as EnsureFlatPanelCoverClosedAsync
        /// above).
        /// </summary>
        private async Task LeaveFlatPanelCoverClosedAfterRunAsync() {
            var progress = new Progress<ApplicationStatus>(s => {
                if (string.IsNullOrWhiteSpace(s?.Status)) return;
                RunLog.Add($"{DateTime.Now:HH:mm:ss}  {s.Status}");
            });
            var openCoverAfterRun = new SmartCalSettingsProvider(_profileService).Load().OpenCoverAfterRun;
            await SmartCalRunPlanning.LeaveFlatPanelCoverClosedAfterRunAsync(_flatDeviceMediator, progress, openCoverAfterRun);
        }

        /// <summary>
        /// ROUND 67 - manual-gear counterpart to EnsureFlatPanelCoverClosedAsync/
        /// LeaveFlatPanelCoverClosedAfterRunAsync above: those two handle a MOTORIZED flat-panel cover
        /// automatically and are a complete no-op for hardware that has none (see
        /// SmartCalRunPlanning.EnsureFlatPanelCoverClosedAsync's own note - the author's own White Dwarf
        /// panel always reports SupportsOpenClose false). For that hardware the only way to keep a dark/
        /// bias/flat-dark frame light-sealed is a separate physical cap the user places by hand, and the
        /// only way to get the flat panel itself in front of the scope is the user putting IT there by
        /// hand too - there's nothing to command over ASCOM either way. This halts the run at the given
        /// point, shows `message` (RunLog and the dedicated CoverSwapMessage the panel's Continue button
        /// sits next to), and waits for either a Continue click or the run being cancelled - same `token`
        /// (this VM's own _cts.Token in every call site below) every other await in this class already
        /// respects, so Stop during a pause behaves exactly like Stop during a real exposure: an
        /// OperationCanceledException that the caller's existing try/catch already handles.
        ///
        /// ROUND 93 - reported: "at the top under the red banner, a duplicate text appears... can we
        /// eliminate the extra one underneath the red banner since it is duplicative?" This used to also
        /// set StatusText to the same message - harmless when StatusText and the pause banner lived far
        /// apart on screen, but Round 91 moved the StatusText row to sit immediately below the pause
        /// banner row, so the identical sentence started rendering twice in a row. Removed the StatusText
        /// write here; RunLog (the scrolling history) and CoverSwapMessage (the banner itself) still show
        /// it, and StatusText now simply keeps whatever it already said (e.g. "Running Filter X...") until
        /// the run's next real status update overwrites it, rather than being redundantly overwritten with
        /// the pause text.
        ///
        /// Safe to call from a background thread - RunLog/IsAwaitingCoverSwap/CoverSwapMessage are all
        /// WPF-bound, so every write goes through Dispatcher.Invoke, same convention as
        /// ClearForExternalRun/AppendExternalStatus below (Dispatcher.Invoke from the UI thread itself -
        /// the case for every call site inside this VM - just runs synchronously in place, so this is
        /// safe either way).
        /// </summary>
        private async Task PauseForCoverSwapAsync(string message, CancellationToken token) {
            var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _coverSwapContinueSignal = signal;
            System.Windows.Application.Current?.Dispatcher.Invoke(() => {
                RunLog.Add($"{DateTime.Now:HH:mm:ss}  {message}");
                CoverSwapMessage = message;
                IsAwaitingCoverSwap = true;
            });
            using (token.Register(() => signal.TrySetCanceled(token))) {
                try {
                    await signal.Task;
                } finally {
                    _coverSwapContinueSignal = null;
                    System.Windows.Application.Current?.Dispatcher.Invoke(() => IsAwaitingCoverSwap = false);
                }
            }
        }

        private void RefreshKnownFilters() {
            KnownFilters.Clear();
            var filters = _profileService.ActiveProfile.FilterWheelSettings.FilterWheelFilters;
            if (filters == null) return;
            foreach (var f in filters) KnownFilters.Add(f.Name);
            SelectedFilter = KnownFilters.FirstOrDefault();
        }

        private void RefreshFilterDefaultsSummary() {
            RefreshFilterDefaultsSummaryFor(SelectedFilter);
        }

        /// <summary>
        /// ROUND 32 - shows this filter's current stored (brightness, exposure) default, the exact pair
        /// the next run will start from and a converged run will overwrite. Keyed by filter name only
        /// now (see SmartCalSettingsProvider), so unlike the old bucket-keyed history summary this
        /// doesn't need gain/offset at all.
        /// </summary>
        private void RefreshFilterDefaultsSummaryFor(string filterName) {
            if (string.IsNullOrEmpty(filterName)) {
                FilterDefaultsSummary = "No filter selected.";
                return;
            }
            var provider = new SmartCalSettingsProvider(_profileService);
            var settings = provider.Load();
            int brightness = provider.GetFilterBrightness(filterName, settings.MinBrightness);
            // ROUND 44 - fallback changed from MinExposureSeconds to PreferredExposureSeconds, matching
            // the same change in SmartCalCaptureService, so this summary shows the SAME starting exposure
            // a run will actually use for a never-converged filter, instead of a stale MinExposureSeconds
            // value the real search no longer starts from.
            double exposure = provider.GetFilterExposureSeconds(filterName, settings.PreferredExposureSeconds);
            FilterDefaultsSummary = $"Default brightness {brightness}, exposure {exposure:F2}s (edit on the Options page - a converged run updates these automatically).";
        }

        /// <summary>
        /// ROUND 43 - `standalone` (default true, unchanged behavior for the existing per-tab buttons):
        /// when false, this run is one leg of a "Run All" batch (see RunSelectedBatchAsync), which
        /// already owns the RunLog.Clear()/_cts creation/IsRunning toggle/cover open-close for the whole
        /// batch's duration - so this method skips its own copies of those and just does the capture,
        /// letting its own StatusText/RunLog lines land in the batch's single continuous log. The
        /// pre-flight connectivity checks below stay unconditional either way - equipment can disconnect
        /// between batch items just as easily as before a single manual run, so re-checking per item is
        /// cheap and worth keeping rather than trusting the batch's own one-time check at the start.
        /// </summary>
        private async Task<bool> RunAsync(bool allFilters, bool standalone = true) {
            // ROUND 32r - clear the on-screen running log at the very start of every run (single-filter
            // AND "run all filters" both go through this same method, so one Clear() here covers both) -
            // per explicit user request, so a fresh run's log can never be misread against leftover lines
            // still sitting on screen from whatever the previous run left behind. Deliberately BEFORE the
            // pre-flight checks below, not after - even a run that fails pre-flight should start from a
            // clean, empty log rather than appending onto stale content from last time.
            if (standalone) RunLog.Clear();

            // ROUND 18 - pre-flight connectivity check, so a run started from this panel fails the same
            // friendly way up front (before anything is commanded) instead of deep inside the service.
            var preflightCamInfo = _cameraMediator.GetInfo();
            if (preflightCamInfo == null || !preflightCamInfo.Connected) {
                StatusText = "Camera is not connected. Connect it in the Equipment tab, then try again.";
                RunLog.Add($"{DateTime.Now:HH:mm:ss}  {StatusText}");
                return false;
            }
            var preflightFlatInfo = _flatDeviceMediator.GetInfo();
            if (preflightFlatInfo == null || !preflightFlatInfo.Connected) {
                StatusText = "Flat panel (Cover Calibrator) is not connected. Connect it in the Equipment tab, then try again.";
                RunLog.Add($"{DateTime.Now:HH:mm:ss}  {StatusText}");
                return false;
            }

            if (standalone) {
                _cts = new CancellationTokenSource();
                IsRunning = true;
            }
            StatusText = allFilters ? "Running all filters..." : $"Running {SelectedFilter}...";

            var provider = new SmartCalSettingsProvider(_profileService);

            // ROUND 17 - seeded from persisted storage rather than starting at 0 every call, so filenames
            // never collide with an earlier run's numbers. Saved back in the finally regardless of how
            // this run ends, so a number that was actually consumed is never handed out again.
            var frameCounter = new SmartCalFrames.Equipment.FrameSequenceCounter {
                Next = provider.LoadNextFrameNumber()
            };
            try {
                if (standalone && !await EnsureFlatPanelCoverClosedAsync(_cts.Token)) return false;

                var settings = provider.Load();

                // ROUND 67 - "Pause for cover swap", before: gives the user time to remove the cap and
                // put the flat panel in place before this capture actually starts. See
                // SmartCalSettings.PauseForCoverSwap / PauseForCoverSwapAsync for the full explanation.
                if (settings.PauseForCoverSwap) {
                    await PauseForCoverSwapAsync("Paused - remove the cover and put the flat panel in place, then click Continue.", _cts.Token);
                }

                var service = new SmartCalCaptureService(
                    _cameraMediator, _filterWheelMediator, _flatDeviceMediator, _imagingMediator,
                    _imageSaveMediator, provider, settings, _profileService,
                    // ROUND 69 - mirrors Smart Flat Wizard's separate image-preview window. Fired after
                    // EVERY capture (search-phase attempts and production frames alike) - always updates
                    // LastCapturedImage regardless of whether the Options page toggle is currently on;
                    // the View's own column-width binding (ShowImagePreview) is what actually decides
                    // whether this is visible, so there's no stale image the moment the toggle is
                    // flipped on. Called directly (no Dispatcher wrapping) since this run was started
                    // from this VM's own UI-thread command handler - same reasoning as the `progress`
                    // callback just below, which already touches RunLog/StatusText directly.
                    onFrameCaptured: bmp => { LastCapturedImage = bmp; });

                var camInfo = _cameraMediator.GetInfo();
                int gain = camInfo?.Gain ?? -1;
                int offset = camInfo?.Offset ?? -1;
                var progress = new Progress<ApplicationStatus>(s => {
                    StatusText = s.Status;
                    if (string.IsNullOrWhiteSpace(s.Status)) return;
                    RunLog.Add($"{DateTime.Now:HH:mm:ss}  {s.Status}");
                    while (RunLog.Count > MaxRunLogLines) RunLog.RemoveAt(0);
                });

                if (allFilters) {
                    var filters = _profileService.ActiveProfile.FilterWheelSettings.FilterWheelFilters;
                    var results = await service.RunAllFiltersAsync(filters, 1, 1, gain, offset, frameCounter, progress, _cts.Token);
                    // ROUND 32m - was joined with " | ", which packed every filter's summary onto one
                    // long, hard-to-read line in the StatusText TextBlock (Wrap-enabled, but a single
                    // paragraph still runs everything together with no visual separation between
                    // filters). A real line break per filter reads the same as this run's Summary lines
                    // already look in the SmartCals log file - see the comment below on where those
                    // already live.
                    StatusText = string.Join(Environment.NewLine, results.Select(r => r.Summary));
                } else {
                    var filters = _profileService.ActiveProfile.FilterWheelSettings.FilterWheelFilters;
                    var filter = filters?.FirstOrDefault(f => f.Name == SelectedFilter);
                    var result = await service.RunAsync(filter, 1, 1, gain, offset, frameCounter, progress, _cts.Token);
                    StatusText = result.Summary;
                }

                // ROUND 67 - "Pause for cover swap", after: gives the user time to remove the flat panel
                // and put the cap back on before whatever runs next (Flat Darks/Bias/Dark Frames) needs
                // the scope light-sealed.
                if (settings.PauseForCoverSwap) {
                    await PauseForCoverSwapAsync("Paused - remove the flat panel and put the cover back on, then click Continue.", _cts.Token);
                }

                RefreshFilterDefaultsSummaryFor(SelectedFilter);
                return true;
            } catch (OperationCanceledException) {
                StatusText = "Stopped.";
                return false;
            } catch (Exception ex) {
                StatusText = $"Error: {ex.Message}";
                return false;
            } finally {
                // Persist however far the counter actually advanced, even on Stop/error.
                provider.SaveNextFrameNumber(frameCounter.Next);
                if (standalone) {
                    IsRunning = false;
                    await LeaveFlatPanelCoverClosedAfterRunAsync();
                }
            }
        }

        /// <summary>
        /// ROUND 36, moved to SmartCalRunPlanning.BuildFlatDarkGroups (ROUND 40) so the new multi-function
        /// sequencer item can build the same groups without needing a live VM instance. See that method
        /// for the grouping rule (one group per distinct rounded flat exposure, bin1x1/current-camera
        /// gain-offset, same as RunAsync above uses for flats themselves).
        /// </summary>
        private List<(double Exposure, List<string> Filters)> BuildDarkGroups(SmartCalSettingsProvider provider, SmartCalSettings settings) {
            return SmartCalRunPlanning.BuildFlatDarkGroups(_profileService, provider, settings);
        }

        private void RefreshDarkGroupsPreview() {
            // ROUND 56 - "Preview groups" is now a toggle: if the preview is already showing, hide it
            // and stop - a second click turns it off instead of just re-showing the same (or a
            // refreshed) preview with no way to dismiss it. Only refresh-and-show when it was hidden.
            if (DarkGroupsPreviewVisible) {
                DarkGroupsPreviewVisible = false;
                return;
            }

            var provider = new SmartCalSettingsProvider(_profileService);
            var settings = provider.Load();
            var groups = BuildDarkGroups(provider, settings);
            if (groups.Count == 0) {
                DarkGroupsPreview = "No filters found in the active filter wheel.";
            } else {
                DarkGroupsPreview = string.Join(Environment.NewLine,
                    groups.Select(g => $"{g.Exposure:F2}s  ({DarkFramesPerGroup} frames)  <-  {string.Join(", ", g.Filters)}"));
            }
            DarkGroupsPreviewVisible = true;
        }

        /// <summary>
        /// ROUND 36 - "Flat Darks" tab: captures dark frames matched to each filter's stored flat
        /// exposure, one group per unique exposure (see BuildDarkGroups above). Structured to mirror
        /// RunAsync above as closely as possible - same pre-flight connectivity checks, same
        /// RunLog.Clear()-at-the-very-start behavior, same frame-counter persistence, same
        /// try/catch(OperationCanceledException)/catch(Exception)/finally shape - so anyone reading one
        /// method already understands the other.
        /// </summary>
        private async Task<bool> RunFlatDarksAsync(bool standalone = true) {
            if (standalone) RunLog.Clear();

            var preflightCamInfo = _cameraMediator.GetInfo();
            if (preflightCamInfo == null || !preflightCamInfo.Connected) {
                StatusText = "Camera is not connected. Connect it in the Equipment tab, then try again.";
                RunLog.Add($"{DateTime.Now:HH:mm:ss}  {StatusText}");
                return false;
            }
            // ROUND 76 FIX - reported: "SmartCalibrationFrames checks for connection to the flat panel
            // always on any run - it should only do so when a flat run is attempted. It need not be
            // connected for flat darks, bias, or dark frames." Removed the unconditional flat-panel-
            // Connected preflight check that used to sit here (still present, deliberately, in RunAsync
            // above - Flats genuinely needs the panel connected to turn its light on and command a
            // brightness). Flat Darks doesn't need the panel connected at all: it only ever calls
            // ToggleLight(false) as a best-effort "make sure the light isn't on" precaution
            // (SmartCalCaptureService.RunFlatDarksAsync already wraps every ToggleLight(false) call in its
            // own try/catch that logs a warning and continues rather than aborting - confirmed by reading
            // that method directly, not assumed), and EnsureFlatPanelCoverClosedAsync below already no-ops
            // safely when the panel is absent/disconnected (SmartCalRunPlanning.
            // EnsureFlatPanelCoverClosedAsync: `if (info == null || !info.SupportsOpenClose) return true;`).
            // So a disconnected (or entirely absent) flat panel was never actually a real obstacle to this
            // capture - only this now-removed check was.
            var provider = new SmartCalSettingsProvider(_profileService);
            var settings = provider.Load();
            var groups = BuildDarkGroups(provider, settings);
            if (groups.Count == 0) {
                StatusText = "No filters found in the active filter wheel to build dark groups from.";
                RunLog.Add($"{DateTime.Now:HH:mm:ss}  {StatusText}");
                return false;
            }

            int framesPerGroup = Math.Max(1, DarkFramesPerGroup);

            if (standalone) {
                _cts = new CancellationTokenSource();
                IsRunning = true;
            }
            StatusText = $"Capturing flat-darks: {groups.Count} exposure group(s), {framesPerGroup} frame(s) each...";
            RunLog.Add($"{DateTime.Now:HH:mm:ss}  {StatusText}");
            foreach (var g in groups) {
                RunLog.Add($"{DateTime.Now:HH:mm:ss}    {g.Exposure:F2}s  <-  {string.Join(", ", g.Filters)}");
            }

            var frameCounter = new SmartCalFrames.Equipment.FrameSequenceCounter { Next = provider.LoadNextFrameNumber() };
            try {
                if (standalone && !await EnsureFlatPanelCoverClosedAsync(_cts.Token)) return false;

                var service = new SmartCalCaptureService(
                    _cameraMediator, _filterWheelMediator, _flatDeviceMediator, _imagingMediator,
                    _imageSaveMediator, provider, settings, _profileService,
                    // ROUND 69 - mirrors Smart Flat Wizard's separate image-preview window. Fired after
                    // EVERY capture (search-phase attempts and production frames alike) - always updates
                    // LastCapturedImage regardless of whether the Options page toggle is currently on;
                    // the View's own column-width binding (ShowImagePreview) is what actually decides
                    // whether this is visible, so there's no stale image the moment the toggle is
                    // flipped on. Called directly (no Dispatcher wrapping) since this run was started
                    // from this VM's own UI-thread command handler - same reasoning as the `progress`
                    // callback just below, which already touches RunLog/StatusText directly.
                    onFrameCaptured: bmp => { LastCapturedImage = bmp; });

                var camInfo = _cameraMediator.GetInfo();
                int gain = camInfo?.Gain ?? -1;
                int offset = camInfo?.Offset ?? -1;
                var progress = new Progress<ApplicationStatus>(s => {
                    StatusText = s.Status;
                    if (string.IsNullOrWhiteSpace(s.Status)) return;
                    RunLog.Add($"{DateTime.Now:HH:mm:ss}  {s.Status}");
                    while (RunLog.Count > MaxRunLogLines) RunLog.RemoveAt(0);
                });

                var results = await service.RunFlatDarksAsync(
                    groups.Select(g => g.Exposure), 1, 1, gain, offset, framesPerGroup, frameCounter, progress, _cts.Token);

                StatusText = $"Flat-darks complete: {results.Count} exposure group(s) captured.";
                return true;
            } catch (OperationCanceledException) {
                StatusText = "Stopped.";
                return false;
            } catch (Exception ex) {
                StatusText = $"Error: {ex.Message}";
                return false;
            } finally {
                provider.SaveNextFrameNumber(frameCounter.Next);
                if (standalone) {
                    IsRunning = false;
                    await LeaveFlatPanelCoverClosedAfterRunAsync();
                }
            }
        }

        /// <summary>
        /// ROUND 1 (Smart Cal Frames), exposure logic revised ROUND 37d: captures BiasFrameCount frames
        /// at whatever ResolveBiasExposureSeconds resolves for the connected camera (its own reported
        /// ExposureMin, or the flats' MinExposureSeconds floor as a fallback - see that method), current
        /// camera gain/offset, bin1x1. Structured to mirror RunFlatDarksAsync/RunAsync above as closely
        /// as possible - same pre-flight connectivity checks, same RunLog.Clear()-at-the-very-start
        /// behavior, same frame-counter persistence, same
        /// try/catch(OperationCanceledException)/catch(Exception)/finally shape.
        /// </summary>
        private async Task<bool> RunBiasFramesAsync(bool standalone = true) {
            if (standalone) RunLog.Clear();

            var preflightCamInfo = _cameraMediator.GetInfo();
            if (preflightCamInfo == null || !preflightCamInfo.Connected) {
                StatusText = "Camera is not connected. Connect it in the Equipment tab, then try again.";
                RunLog.Add($"{DateTime.Now:HH:mm:ss}  {StatusText}");
                return false;
            }
            // ROUND 76 FIX - see the identical note in RunFlatDarksAsync above: Bias Frames doesn't need
            // the flat panel connected either (same best-effort ToggleLight(false)/cover-guard reasoning),
            // so the unconditional flat-panel-Connected preflight check that used to sit here was removed.
            var provider = new SmartCalSettingsProvider(_profileService);
            var settings = provider.Load();
            int frameCount = Math.Max(1, settings.BiasFrameCount);
            double exposureSeconds = ResolveBiasExposureSeconds(preflightCamInfo);

            if (standalone) {
                _cts = new CancellationTokenSource();
                IsRunning = true;
            }
            StatusText = $"Capturing {frameCount} bias frame(s) at {exposureSeconds:F4}s...";
            RunLog.Add($"{DateTime.Now:HH:mm:ss}  {StatusText}");

            var frameCounter = new SmartCalFrames.Equipment.FrameSequenceCounter { Next = provider.LoadNextFrameNumber() };
            try {
                if (standalone && !await EnsureFlatPanelCoverClosedAsync(_cts.Token)) return false;

                var service = new SmartCalCaptureService(
                    _cameraMediator, _filterWheelMediator, _flatDeviceMediator, _imagingMediator,
                    _imageSaveMediator, provider, settings, _profileService,
                    // ROUND 69 - mirrors Smart Flat Wizard's separate image-preview window. Fired after
                    // EVERY capture (search-phase attempts and production frames alike) - always updates
                    // LastCapturedImage regardless of whether the Options page toggle is currently on;
                    // the View's own column-width binding (ShowImagePreview) is what actually decides
                    // whether this is visible, so there's no stale image the moment the toggle is
                    // flipped on. Called directly (no Dispatcher wrapping) since this run was started
                    // from this VM's own UI-thread command handler - same reasoning as the `progress`
                    // callback just below, which already touches RunLog/StatusText directly.
                    onFrameCaptured: bmp => { LastCapturedImage = bmp; });

                var camInfo = _cameraMediator.GetInfo();
                int gain = camInfo?.Gain ?? -1;
                int offset = camInfo?.Offset ?? -1;
                var progress = new Progress<ApplicationStatus>(s => {
                    StatusText = s.Status;
                    if (string.IsNullOrWhiteSpace(s.Status)) return;
                    RunLog.Add($"{DateTime.Now:HH:mm:ss}  {s.Status}");
                    while (RunLog.Count > MaxRunLogLines) RunLog.RemoveAt(0);
                });

                StatusText = await service.RunBiasFramesAsync(
                    frameCount, exposureSeconds, 1, 1, gain, offset, frameCounter, progress, _cts.Token);
                return true;
            } catch (OperationCanceledException) {
                StatusText = "Stopped.";
                return false;
            } catch (Exception ex) {
                StatusText = $"Error: {ex.Message}";
                return false;
            } finally {
                provider.SaveNextFrameNumber(frameCounter.Next);
                if (standalone) {
                    IsRunning = false;
                    await LeaveFlatPanelCoverClosedAfterRunAsync();
                }
            }
        }

        /// <summary>
        /// ROUND 39, renamed same session from "Light Darks"/RunLightDarksAsync per explicit user
        /// feedback ("I've never heard of light dark frames") - "Dark Frames" tab: captures dark frames
        /// matched to each filter's own (Binning, Frame count, Exposure) - see
        /// DarkFrameRows/BuildDarkFrameGroups above for where those come from and how they're grouped.
        /// Structured to mirror RunFlatDarksAsync/RunBiasFramesAsync above as closely as possible - same
        /// pre-flight connectivity checks, same RunLog.Clear()-at-the-very-start behavior, same
        /// frame-counter persistence, same EnsureFlatPanelCoverClosedAsync/
        /// LeaveFlatPanelCoverClosedAfterRunAsync cover guard, same
        /// try/catch(OperationCanceledException)/catch(Exception)/finally shape.
        ///
        /// ROUND 39 REVISION - end-of-run StatusText is now a per-group summary joined the same way
        /// RunAsync(allFilters: true) above already joins flats' per-filter Summary lines, per explicit
        /// user request ("a summary of what frames were taken at the top of the log... to be sure none
        /// failed"). service.RunDarkFramesAsync now tolerates one group failing without aborting the
        /// rest (mirrors RunAllFiltersAsync's per-filter resilience for flats), so this summary can show
        /// every group's real outcome - "N/M dark frame(s) captured" or "FAILED after N/M..." - even when
        /// something partway through didn't finish, rather than the run aborting outright with no
        /// per-group detail at all.
        /// </summary>
        private async Task<bool> RunDarkFramesAsync(bool standalone = true) {
            if (standalone) RunLog.Clear();

            var preflightCamInfo = _cameraMediator.GetInfo();
            if (preflightCamInfo == null || !preflightCamInfo.Connected) {
                StatusText = "Camera is not connected. Connect it in the Equipment tab, then try again.";
                RunLog.Add($"{DateTime.Now:HH:mm:ss}  {StatusText}");
                return false;
            }
            // ROUND 76 FIX - see the identical note in RunFlatDarksAsync above: Dark Frames doesn't need
            // the flat panel connected either (same best-effort ToggleLight(false)/cover-guard reasoning),
            // so the unconditional flat-panel-Connected preflight check that used to sit here was removed.
            var groups = BuildDarkFrameGroups();
            var misconfigured = FindMisconfiguredDarkFrameFilters();
            if (groups.Count == 0) {
                StatusText = misconfigured.Count > 0
                    ? $"{string.Join(", ", misconfigured)} - frame count is set but exposure is still 0. Enter an exposure (at least 0.1s), then try again."
                    : "No filters have a frame count set on the Dark Frames tab - set at least one filter's Frames above 0 (and its exposure), then try again.";
                RunLog.Add($"{DateTime.Now:HH:mm:ss}  {StatusText}");
                return false;
            }

            var provider = new SmartCalSettingsProvider(_profileService);

            if (standalone) {
                _cts = new CancellationTokenSource();
                IsRunning = true;
            }
            StatusText = $"Capturing dark frames: {groups.Count} group(s)...";
            RunLog.Add($"{DateTime.Now:HH:mm:ss}  {StatusText}");
            foreach (var g in groups) {
                RunLog.Add($"{DateTime.Now:HH:mm:ss}    {g.Exposure:F2}s bin{g.Binning}x{g.Binning} ({g.FrameCount} frames)  <-  {string.Join(", ", g.Filters)}");
            }
            if (misconfigured.Count > 0) {
                RunLog.Add($"{DateTime.Now:HH:mm:ss}  Skipping {string.Join(", ", misconfigured)} - frame count is set but exposure is still 0.");
            }

            var frameCounter = new SmartCalFrames.Equipment.FrameSequenceCounter { Next = provider.LoadNextFrameNumber() };
            try {
                if (standalone && !await EnsureFlatPanelCoverClosedAsync(_cts.Token)) return false;

                var settings = provider.Load();
                var service = new SmartCalCaptureService(
                    _cameraMediator, _filterWheelMediator, _flatDeviceMediator, _imagingMediator,
                    _imageSaveMediator, provider, settings, _profileService,
                    // ROUND 69 - mirrors Smart Flat Wizard's separate image-preview window. Fired after
                    // EVERY capture (search-phase attempts and production frames alike) - always updates
                    // LastCapturedImage regardless of whether the Options page toggle is currently on;
                    // the View's own column-width binding (ShowImagePreview) is what actually decides
                    // whether this is visible, so there's no stale image the moment the toggle is
                    // flipped on. Called directly (no Dispatcher wrapping) since this run was started
                    // from this VM's own UI-thread command handler - same reasoning as the `progress`
                    // callback just below, which already touches RunLog/StatusText directly.
                    onFrameCaptured: bmp => { LastCapturedImage = bmp; });

                var camInfo = _cameraMediator.GetInfo();
                int gain = camInfo?.Gain ?? -1;
                int offset = camInfo?.Offset ?? -1;
                var progress = new Progress<ApplicationStatus>(s => {
                    StatusText = s.Status;
                    if (string.IsNullOrWhiteSpace(s.Status)) return;
                    RunLog.Add($"{DateTime.Now:HH:mm:ss}  {s.Status}");
                    while (RunLog.Count > MaxRunLogLines) RunLog.RemoveAt(0);
                });

                var results = await service.RunDarkFramesAsync(
                    groups.Select(g => (g.Exposure, g.Binning, g.FrameCount, g.Filters)),
                    gain, offset, frameCounter, progress, _cts.Token);

                // Same convention as RunAsync(allFilters: true)'s per-filter summary join above - one
                // line per group, so a failed group ("FAILED after...") is easy to spot at a glance
                // without scrolling back through every individual frame line.
                StatusText = string.Join(Environment.NewLine, results);
                return true;
            } catch (OperationCanceledException) {
                StatusText = "Stopped.";
                return false;
            } catch (Exception ex) {
                StatusText = $"Error: {ex.Message}";
                return false;
            } finally {
                provider.SaveNextFrameNumber(frameCounter.Next);
                if (standalone) {
                    IsRunning = false;
                    await LeaveFlatPanelCoverClosedAfterRunAsync();
                }
            }
        }

        /// <summary>
        /// ROUND 43 - "Run All" strip: runs any combination of the four routines above with one click,
        /// each using whatever is already configured on its own tab (Flats reuses "Run all filters",
        /// not "Run this filter" - checking the Flats box means "run every filter", matching the design
        /// discussed with the user). Order is fixed (Flats, Flat Darks, Bias, Dark Frames) regardless of
        /// checkbox order - matches the existing tab order, and reads naturally even though Flat Darks
        /// pulls from each filter's stored exposure rather than a flat this same batch just took.
        ///
        /// This method owns the RunLog.Clear()/_cts creation/IsRunning toggle/cover open-close for the
        /// WHOLE batch's duration - each item is called with standalone:false so it skips its own copies
        /// of those (see RunAsync's doc comment) and just appends its own StatusText/RunLog lines onto
        /// this one continuous log, with a "--- Name ---" separator line between items.
        ///
        /// Failure handling: a failed or errored item does NOT abort the remaining queue - same
        /// philosophy as RunDarkFramesAsync's own per-group resilience (Round 39). Each of the four
        /// methods already catches its own exceptions internally and returns false rather than throwing,
        /// so this loop just reads that result and moves on. The one thing that DOES stop the whole
        /// queue is the user clicking Stop: every item shares this VM's single _cts field (the same one
        /// Stop already cancels for any of the four individual buttons), so once cancellation has been
        /// requested the loop breaks rather than "running" further items against an already-cancelled
        /// token.
        /// </summary>
        private async Task<bool> RunSelectedBatchAsync() {
            var items = new List<(string Name, Func<Task<bool>> Run)>();
            if (RunFlatsSelected) items.Add(("Flats", () => RunAsync(allFilters: true, standalone: false)));
            if (RunFlatDarksSelected) items.Add(("Flat Darks", () => RunFlatDarksAsync(standalone: false)));
            if (RunBiasSelected) items.Add(("Bias Frames", () => RunBiasFramesAsync(standalone: false)));
            if (RunDarkFramesSelected) items.Add(("Dark Frames", () => RunDarkFramesAsync(standalone: false)));

            if (items.Count == 0) {
                StatusText = "Nothing selected - check at least one of Flats/Flat Darks/Bias Frames/Dark Frames above, then try again.";
                return false;
            }

            // Same pre-flight checks every individual run method already does on its own - fail the same
            // friendly way up front, before touching the log or starting anything, rather than the first
            // queued item being the one to discover it.
            var preflightCamInfo = _cameraMediator.GetInfo();
            if (preflightCamInfo == null || !preflightCamInfo.Connected) {
                StatusText = "Camera is not connected. Connect it in the Equipment tab, then try again.";
                return false;
            }
            // ROUND 76 FIX - reported: the flat panel connection was being required for every batch
            // regardless of what was actually checked. Only Flats itself needs the panel connected (to
            // turn its light on / command a brightness) - Flat Darks/Bias/Dark Frames don't (see the
            // identical note in RunFlatDarksAsync above), so this check now only runs when RunFlatsSelected
            // is actually checked, matching RunAsync's own (unconditional, and unchanged) check for a
            // standalone Flats run.
            if (RunFlatsSelected) {
                var preflightFlatInfo = _flatDeviceMediator.GetInfo();
                if (preflightFlatInfo == null || !preflightFlatInfo.Connected) {
                    StatusText = "Flat panel (Cover Calibrator) is not connected. Connect it in the Equipment tab, then try again.";
                    return false;
                }
            }

            RunLog.Clear();
            _cts = new CancellationTokenSource();
            IsRunning = true;
            StatusText = $"Running {items.Count} selected routine(s): {string.Join(", ", items.Select(i => i.Name))}...";
            RunLog.Add($"{DateTime.Now:HH:mm:ss}  {StatusText}");

            var outcomes = new List<string>();
            try {
                if (!await EnsureFlatPanelCoverClosedAsync(_cts.Token)) return false;

                foreach (var item in items) {
                    if (_cts.Token.IsCancellationRequested) break;
                    RunLog.Add($"{DateTime.Now:HH:mm:ss}  --- {item.Name} ---");
                    bool ok = await item.Run();
                    outcomes.Add($"{item.Name}: {(ok ? "OK" : (_cts.Token.IsCancellationRequested ? "stopped" : "FAILED"))}");
                    if (_cts.Token.IsCancellationRequested) break;
                }

                StatusText = string.Join(Environment.NewLine, outcomes);
                return true;
            } catch (OperationCanceledException) {
                StatusText = "Stopped.";
                return false;
            } catch (Exception ex) {
                StatusText = $"Error: {ex.Message}";
                return false;
            } finally {
                IsRunning = false;
                await LeaveFlatPanelCoverClosedAfterRunAsync();
            }
        }
    }
}
