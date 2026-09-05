# Smart Calibration Frames

Automated flat, flat dark, bias, and dark frame capture for [NINA](https://nighttime-imaging.eu/),
with per-filter exposure search and an Advanced Sequencer batch instruction.

## AI-assisted development

Portions of this plugin's code were developed with AI-assisted tools. The author has reviewed,
tested, and can explain, debug, and maintain all included code, and remains the sole accountable
maintainer of this plugin per NINA's plugin manifest guidelines.

## Features

- **Four dockable tabs:** Run Flats, Flat Darks, Bias Frames, and Dark Frames.
- **Run All:** a one-click batch strip that runs every enabled frame type in turn, with per-item
  resilience so one failed step doesn't abort the rest.
- **Per-filter defaults:** each filter holds one remembered (brightness, exposure) pair for flats,
  edited on the Options page or updated automatically by a converged run.
- **Exposure search:** brightness stays fixed per filter while exposure is searched to hit your
  target mean ADU within a configurable tolerance.
- **Advanced Sequencer instruction:** four capture-type checkboxes (flats, flat darks, bias,
  darks) for unattended multi-filter batches, with an optional "match dark exposures to light
  frames" mode that pulls exposure times from the current imaging history, and support for a
  motorized flat-panel cover guard.
- **Stacking sufficiency:** keeps shooting until the running stack settles below a stability
  threshold (or a configured max is reached), rejecting outlier frames along the way.
- **Explicit failure reporting:** a filter that can't reach target ADU within the configured
  exposure bounds fails loudly and leaves its saved defaults untouched, rather than silently
  accepting an out-of-range result.

## Requirements

- NINA 3.2.0.9001 or later
- An ASCOM CoverCalibrator-compatible flat panel — motorized or manual

## Installation

The easiest way: in NINA, go to **Options → Plugins → Available**, search for "Smart Calibration
Frames," and click Install.

You can also download a release zip directly from this repository's
[Releases](https://github.com/gmilleok0612/SmartCalFrames/releases) page and extract it into your
NINA plugins folder.

## License

MIT — see [LICENSE](LICENSE).
