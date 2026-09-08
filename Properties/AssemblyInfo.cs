using System.Reflection;
using System.Runtime.InteropServices;

// ---- Standard assembly identity ----
[assembly: AssemblyTitle("Smart Calibration Frames")]
[assembly: AssemblyDescription("Automated flat, flat dark, bias, and dark frame capture for NINA, with per-filter exposure search and an Advanced Sequencer batch instruction.")]
[assembly: AssemblyCompany("Gerald R. Miller")]
[assembly: AssemblyProduct("SmartCalFrames")]
[assembly: AssemblyCopyright("Copyright © 2026")]
[assembly: ComVisible(false)]

// Generated once for this plugin - do not change after the first release,
// NINA uses it to identify updates vs. a brand-new plugin.
[assembly: Guid("8970f75b-3138-40f1-80d4-4d1898da383e")]

[assembly: AssemblyVersion("1.0.0.1")]
[assembly: AssemblyFileVersion("1.0.0.1")]

// ---- NINA plugin manifest metadata ----
// These AssemblyMetadata attributes are what NINA's plugin manager (and the
// plugin.template packaging step, if you use it) read to build the manifest
// entry. Field names must match exactly - NINA reads them by string key,
// not by any interface, so a typo here silently produces a blank field
// rather than a build error.
[assembly: AssemblyMetadata("License", "MIT")]
[assembly: AssemblyMetadata("LicenseURL", "https://github.com/gmilleok0612/SmartCalFrames/blob/master/LICENSE")]
[assembly: AssemblyMetadata("Repository", "https://github.com/gmilleok0612/SmartCalFrames")]
[assembly: AssemblyMetadata("ShortDescription",
    "Per-filter flat, flat dark, bias, and dark frame automation: each filter remembers one " +
    "(brightness, exposure) pair, exposure is searched to hit your target ADU, and a one-click " +
    "batch or Advanced Sequencer instruction runs all four frame types unattended.")]
[assembly: AssemblyMetadata("LongDescription",
@"Smart Calibration Frames drives a flat panel (via ASCOM CoverCalibrator) --
motorized or manual -- to capture a full set of calibration frames for each
filter, from four dockable tabs or as an Advanced Sequencer instruction.

- Four dockable tabs: Run Flats, Flat Darks, Bias Frames, and Dark Frames.
- Run All: a one-click batch strip that runs every enabled frame type in turn,
  with per-item resilience so one failed step doesn't abort the rest.
- Per-filter defaults: each filter holds one remembered (brightness, exposure)
  pair for flats, edited on the Options page or updated automatically by a
  converged run.
- Exposure search: brightness stays fixed per filter while exposure is
  searched to hit your target mean ADU within a configurable tolerance.
- Advanced Sequencer instruction: four capture-type checkboxes (flats, flat
  darks, bias, darks) for unattended multi-filter batches, with an optional
  ""match dark exposures to light frames"" mode that pulls exposure times from
  the current imaging history, and support for a motorized flat-panel cover
  guard.
- Stacking sufficiency: keeps shooting until the running stack settles below
  a stability threshold (or a configured max is reached), rejecting outlier
  frames along the way.
- Explicit failure reporting: a filter that can't reach target ADU within the
  configured exposure bounds fails loudly and leaves its saved defaults
  untouched, rather than silently accepting an out-of-range result.")]
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.2.0.9001")]
[assembly: AssemblyMetadata("Tags", "flats,flat darks,bias frames,dark frames,calibration,automation,flat panel,sequencer")]
[assembly: AssemblyMetadata("Homepage", "https://github.com/gmilleok0612/SmartCalFrames")]
[assembly: AssemblyMetadata("FeaturedImageURL", "")]
[assembly: AssemblyMetadata("ScreenshotURL", "")]
[assembly: AssemblyMetadata("AltScreenshotURL", "")]
