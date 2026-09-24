using System.Windows.Controls;
using SmartCalFrames;

namespace SmartCalFrames.Options {

    /// <summary>
    /// ROUND 95 FIX - this code-behind had been reduced to bare boilerplate (InitializeComponent only)
    /// after the real file was accidentally deleted and replaced with a stub earlier in this project's
    /// history. That stub was missing the one line that actually matters: NINA sets the DataContext for
    /// whatever the "Smart Calibration Frames_Options" DataTemplate renders to the IPluginManifest
    /// instance itself (SmartCalFramesPlugin, per that class's own doc comment - "An instance of this
    /// class will be created and set as datacontext on the plugin options tab"), NOT to SmartCalOptionsVM.
    /// Without this constructor explicitly overriding that, every {Binding TargetADUPercent}/
    /// {Binding MinExposureSeconds}/etc. in SmartCalOptionsView.xaml silently resolved against the plugin
    /// manifest object - which has none of those properties - and rendered as empty text boxes with no
    /// error anywhere (the classic silent-binding-failure landmine, just from a missing DataContext
    /// rather than a typo'd property path). Restored the explicit reassignment below, using the same
    /// SmartCalFramesPlugin.Instance static reference this class always relied on for this exact purpose.
    /// </summary>
    public partial class SmartCalOptionsView : UserControl {
        public SmartCalOptionsView() {
            InitializeComponent();
            DataContext = SmartCalFramesPlugin.Instance.OptionsVM;
        }
    }
}
