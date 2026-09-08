using System;
using System.Windows.Input;
using NINA.Core.Utility;
using NINA.Core.Utility.WindowService;

namespace SmartCalFrames.Sequencer {

    /// <summary>
    /// ROUND 70 - plain data class shown as the "content" of the Round 68 pause-for-cover-swap dialog
    /// (see SmartCalSequenceItem.PauseForCoverSwapAsync), replacing NINA's own
    /// NINA.Sequencer.SequenceItem.Utility.MessageBoxResult that Round 68 originally used. Reason: after
    /// real in-NINA testing, the feedback was "the text line almost goes past the sides on both sides,
    /// so make the box wider... it's also too tall so shorten it... same color red as the main panel" -
    /// none of which is controllable through NINA's own built-in MessageBoxResult template (its exact
    /// XAML lives compiled into NINA.Sequencer.dll's resources, not something this plugin can safely
    /// override - two unkeyed DataTemplates for the exact same DataType from two different merged
    /// resource dictionaries is an untested precedence question this project won't gamble a
    /// just-verified-working feature on). Using this plugin's OWN type instead means the DataTemplate in
    /// Templates/SmartCalDataTemplates.xaml is the ONLY template that can ever match it - no ambiguity.
    ///
    /// Shape (Message, Continue defaulting true, ContinueCommand/CancelCommand) still mirrors NINA's own
    /// MessageBoxResult, so SmartCalSequenceItem's Continue/Cancel handling needed no logic changes - only
    /// the `new MessageBoxResult(...)` call site changed to `new CoverSwapDialogResult(...)`.
    ///
    /// One real difference from NINA's class: NINA's own MessageBoxResult has no reference to the window
    /// showing it, yet its dialog demonstrably closes when Continue/Cancel is clicked (confirmed by this
    /// project's own successful real-NINA test of Round 68) - by some mechanism baked into NINA's own
    /// compiled template that this project did not need to (and could not safely) reverse-engineer
    /// further. Rather than depend on an unverified, undocumented close mechanism, THIS class is hooked
    /// up with its own live IWindowService instance (see PauseForCoverSwapAsync's call site) and closes
    /// it explicitly and directly from the button command - self-contained and independent of whatever
    /// NINA's own class relies on.
    /// </summary>
    public class CoverSwapDialogResult {
        public CoverSwapDialogResult(string message, IWindowService dialogService) {
            Message = message;
            Continue = true;
            ContinueCommand = new RelayCommand(_ => { Continue = true; CloseDialog(dialogService); });
            CancelCommand = new RelayCommand(_ => { Continue = false; CloseDialog(dialogService); });
        }

        public string Message { get; }
        public bool Continue { get; private set; }

        public ICommand ContinueCommand { get; }
        public ICommand CancelCommand { get; }

        /// <summary>IWindowService.Close() returns a Task (confirmed via dnfile against the installed
        /// NINA.Core.dll) - discarded on purpose here, same "best-effort, never let cleanup mask the
        /// real result" convention this project already uses for cover-guard cleanup elsewhere
        /// (SmartCalRunPlanning.LeaveFlatPanelCoverClosedAfterRunAsync). A button click that fails to
        /// close its own dialog should log, not throw back into WPF's command-invocation plumbing.</summary>
        private static void CloseDialog(IWindowService dialogService) {
            try {
                _ = dialogService?.Close();
            } catch (Exception ex) {
                Logger.Warning($"Smart Calibration Frames: error closing the pause-for-cover-swap dialog: {ex.Message}");
            }
        }
    }
}
