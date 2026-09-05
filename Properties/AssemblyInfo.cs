using System.Reflection;
using System.Runtime.InteropServices;

// ---- Standard assembly identity ----
[assembly: AssemblyTitle("Smart Calibration Frames")]
[assembly: AssemblyDescription("Per-filter automated flat frame capture for NINA.")]
[assembly: AssemblyCompany("Gerald R. Miller")]
[assembly: AssemblyProduct("SmartCalFrames")]
[assembly: AssemblyCopyright("Copyright © 2026")]
[assembly: ComVisible(false)]

// Generated once for this plugin - do not change after the first release,
// NINA uses it to identify updates vs. a brand-new plugin.
[assembly: Guid("8970f75b-3138-40f1-80d4-4d1898da383e")]

[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

// ---- NINA plugin manifest metadata ----
// These AssemblyMetadata attributes are what NINA's plugin manager (and the
// plugin.template packaging step, if you use it) read to build the manifest
// entry. Field names must match exactly - NINA reads them by string key,
// not by any interface, so a typo here silently produces a blank field
// rather than a build error.
[assembly: AssemblyMetadata("License", "MIT")]
[assembly: AssemblyMetadata("LicenseURL", "https://example.com/LICENSE")]
[assembly: AssemblyMetadata("Repository", "https://example.com/SmartCalFrames")]
[assembly: AssemblyMetadata("ShortDescription",
    "Per-filter flat frame automation: each filter remembers one (brightness, " +
    "exposure) pair, a run searches exposure to hit your target ADU, and " +
    "converged results are saved back automatically.")]
[assembly: AssemblyMetadata("LongDescription",
@"Smart Calibration Frames drives a motorized flat panel (via ASCOM CoverCalibrator)
to capture calibrated flat frames for each filter, from the dockable panel or
as an Advanced Sequencer instruction.

- Per-filter defaults: each filter holds one remembered (brightness, exposure)
  pair, edited on the Options page or updated automatically by a converged run.
- Exposure search: brightness stays fixed per filter while exposure is
  searched to hit your target mean ADU within a configurable tolerance.
- Unattended multi-filter batches: one sequence item or one dockable-panel
  button runs every filter in the wheel in turn.
- Stacking sufficiency: keeps shooting until the running stack settles below
  a stability threshold (or a configured max is reached), rejecting outlier
  frames along the way.
- Explicit failure reporting: a filter that can't reach target ADU within the
  configured exposure bounds fails loudly and leaves its saved defaults
  untouched, rather than silently accepting an out-of-range result.")]
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.2.0.9001")]
[assembly: AssemblyMetadata("Tags", "flats,flat wizard,calibration,automation,flat panel")]
[assembly: AssemblyMetadata("Homepage", "https://example.com")]
[assembly: AssemblyMetadata("FeaturedImageURL", "")]
[assembly: AssemblyMetadata("ScreenshotURL", "")]
[assembly: AssemblyMetadata("AltScreenshotURL", "")]
