using System;
using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Threading;
using NINA.Core.Utility;

namespace SmartCalFrames.Dockable {
    public partial class SmartCalFramesView : UserControl {
        public SmartCalFramesView() {
            InitializeComponent();
            // The running log (RunLogListBox, bound to SmartCalFramesVM.RunLog) only auto-scrolls with
            // help - a plain ItemsSource binding doesn't keep the newest line visible on its own, and
            // during a run new lines arrive every second or so. DataContext isn't set yet in the
            // constructor (it's assigned by the docking framework after construction), so the log is
            // wired up lazily the first time it changes.
            DataContextChanged += (s, e) => {
                if (e.NewValue is SmartCalFramesVM vm) {
                    vm.RunLog.CollectionChanged += RunLog_CollectionChanged;
                }
                if (e.OldValue is SmartCalFramesVM oldVm) {
                    oldVm.RunLog.CollectionChanged -= RunLog_CollectionChanged;
                }
            };
        }

        /*
         * ROUND 14 FIX - real hardware crash, not a false alarm: calling ScrollIntoView synchronously
         * inside this CollectionChanged handler (the original Round 13 implementation) crashed the
         * ENTIRE NINA process, not just this plugin's panel. During a real run, ChangeFilter/
         * ToggleLight/SetBrightness all report their own progress through the same IProgress instance
         * this plugin passes them, on top of the plugin's own status lines, so RunLog.Add() can fire
         * several times in a fraction of a second. Forcing a synchronous ScrollIntoView - which itself
         * forces a synchronous layout pass - while the virtualized ListBox's ItemContainerGenerator was
         * still catching up to an earlier Add desynced its internal item count from the real
         * ItemsSource count ("Accumulated count 3 is different from actual count 4" in the real crash
         * log), which WPF surfaces as an unhandled InvalidOperationException on the UI thread - and an
         * unhandled UI-thread exception takes the whole host application down with it, confirmed by the
         * user's NINA log (App.xaml.cs|Current_DispatcherUnhandledException, immediately after "Setting
         * brightness to 3" - the plugin's search algorithm itself was working correctly right up to the
         * moment this unrelated UI bug crashed the app around it).
         *
         * Fix: defer the scroll with Dispatcher.BeginInvoke at Background priority, which is lower
         * priority than WPF's own Render-priority layout pass, so this runs only after the
         * ItemsControl's internal CollectionChanged handling and layout have fully settled from
         * whichever Add just fired - never synchronously inside the event that triggered it. Also
         * wrapped in try/catch: this is a pure display convenience (keeping the newest line visible),
         * so no failure here should ever be allowed to crash the host application again, even if some
         * other WPF virtualization edge case turns up later. A caught exception is logged at Debug
         * level (not surfaced to the user) purely so it isn't invisible if this ever needs revisiting.
         */
        private void RunLog_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e) {
            Dispatcher.BeginInvoke(new Action(() => {
                try {
                    if (RunLogListBox.Items.Count == 0) return;
                    RunLogListBox.ScrollIntoView(RunLogListBox.Items[RunLogListBox.Items.Count - 1]);
                } catch (Exception ex) {
                    Logger.Debug($"Smart Calibration Frames: RunLog auto-scroll failed harmlessly ({ex.GetType().Name}: {ex.Message}) - log content is unaffected.");
                }
            }), DispatcherPriority.Background);
        }
    }
}
