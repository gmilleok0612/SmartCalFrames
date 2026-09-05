using System.ComponentModel.Composition;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using SmartCalFrames.Options;

namespace SmartCalFrames {

    /// <summary>
    /// Plugin entry point. NINA discovers this via MEF because it exports
    /// IPluginManifest - there must be exactly one of these per plugin
    /// assembly. This is also where the plugin-wide options page (target
    /// ADU%, tolerance, exposure/brightness bounds, drift threshold, etc.)
    /// gets wired up via a "<PluginName>_Options" DataTemplate registered
    /// in Templates/SmartCalDataTemplates.xaml.
    ///
    /// That DataTemplate (and the Dockable/Sequencer ones alongside it) is
    /// found by NINA because SmartCalDataTemplates.xaml.cs exports
    /// ResourceDictionary via MEF - NOT by this constructor manually adding
    /// it to Application.Current.Resources.MergedDictionaries, which is
    /// what used to happen here before that export existed. Removed once
    /// the documented mechanism (confirmed against a real, working,
    /// published NINA plugin's source) was in place - no reason to keep an
    /// unverified manual workaround alongside the proven approach.
    /// </summary>
    [Export(typeof(IPluginManifest))]
    public class SmartCalFramesPlugin : PluginBase {

        /// <summary>
        /// NINA sets THIS instance (the IPluginManifest export itself) as the
        /// DataContext for whatever the "Smart Calibration Frames_Options"
        /// DataTemplate renders (ROUND 64 - key renamed to match this round's
        /// AssemblyTitle change; see the comment on that DataTemplate itself
        /// for why the key has to track AssemblyTitle exactly) - confirmed by
        /// the official plugin
        /// template's own comment, reused verbatim in the SkyFlats reference
        /// source: "An instance of this class will be created and set as
        /// datacontext on the plugin options tab." It is NOT the OptionsVM
        /// property below. SmartCalOptionsView.xaml.cs uses this static
        /// reference to explicitly set its own DataContext to the actual
        /// OptionsVM instead, since there's exactly one plugin instance per
        /// NINA process and no other way for that View's parameterless
        /// constructor to reach it.
        /// </summary>
        public static SmartCalFramesPlugin Instance { get; private set; }

        [ImportingConstructor]
        public SmartCalFramesPlugin(IProfileService profileService) {
            Instance = this;
            var settingsProvider = new SmartCalSettingsProvider(profileService);
            // ROUND 32: OptionsVM now also needs the profile service, to list the active profile's
            // filters for the per-filter defaults table - passed straight through from this
            // constructor's own already-imported parameter, not a new MEF import on this class itself.
            OptionsVM = new SmartCalOptionsVM(settingsProvider, profileService);
        }

        public SmartCalOptionsVM OptionsVM { get; }
    }
}
