using System.Windows.Controls;

namespace SmartCalFrames.Options {
    public partial class SmartCalOptionsView : UserControl {
        public SmartCalOptionsView() {
            InitializeComponent();

            // NINA sets the plugin manifest instance (SmartCalFramesPlugin)
            // as this View's inherited DataContext, not the OptionsVM this
            // View's bindings actually need - confirmed via the official
            // plugin template convention (see the comment on
            // SmartCalFramesPlugin.Instance). Override it explicitly here
            // rather than restructuring the settings properties directly
            // onto the plugin manifest class.
            DataContext = SmartCalFramesPlugin.Instance?.OptionsVM;
        }
    }
}
