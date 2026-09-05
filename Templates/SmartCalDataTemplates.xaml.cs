using System.ComponentModel.Composition;
using System.Windows;

namespace SmartCalFrames.Templates {

    /// <summary>
    /// Code-behind for SmartCalDataTemplates.xaml. The [Export(typeof(ResourceDictionary))]
    /// attribute is what makes NINA's plugin composition actually find and
    /// merge this resource dictionary into the application - without it,
    /// the "SmartCalFrames_Options", "..._Dockable", and "..._Mini"
    /// DataTemplates defined in the XAML are invisible to NINA no matter
    /// how correct their keys are, because nothing ever tells NINA this
    /// ResourceDictionary exists.
    ///
    /// Confirmed against a real, published, working NINA plugin (SkyFlats
    /// by photon1503, uploaded to this project for comparison): every one
    /// of its resource-dictionary XAML files (Options.xaml, its dockable
    /// template file, its sequence-item template file) has a matching
    /// code-behind class with exactly this shape - x:Class in the XAML
    /// paired with [Export(typeof(ResourceDictionary))] + InitializeComponent()
    /// here. This plugin's Templates/SmartCalDataTemplates.xaml previously
    /// had neither, and SmartCalFramesPlugin.cs was instead manually
    /// adding the dictionary to Application.Current.Resources.MergedDictionaries
    /// at construction time - a mechanism that isn't what NINA's own
    /// plugins use and was never confirmed to work inside NINA's isolated
    /// per-plugin AssemblyLoadContext. That manual add has been removed now
    /// that this file does it the documented way.
    /// </summary>
    [Export(typeof(ResourceDictionary))]
    public partial class SmartCalDataTemplates : ResourceDictionary {
        public SmartCalDataTemplates() {
            InitializeComponent();
        }
    }
}
