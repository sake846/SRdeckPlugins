# SRdeckPlugin.Cw

Stable plugin ID: `cw`. 10/20/50 kHz multi-signal CW skimmer with adaptive Morse decoding.

## Receive path

- Uses standard IQ channel at selectable 10, 20, or 50 kHz bandwidth (10/20/50 kS/s).
- Channel-wide FFT and peak tracking for simultaneous multi-carrier detection.
- Adaptive Morse timing decoder supporting variable speeds (5–60 WPM).
- Automatic callsign extraction and activity tracking.

## Features

- Multi-signal waterfall annotations and frequency overlays.
- Real-time text decoding with signal diagnostics and SNR calculation.
- Monitor audio generation for selected signals.
- Rolling 10-second IQ pre-trigger capture.
- JSONL history persistence and CSV/JSON export.
- Selected-carrier monitor audio translated to an 800 Hz sidetone.
- Character timing confidence and active-language metadata.

History records are committed at sentence boundaries or when a signal track ends;
retention follows the configured maximum row count. In Wabun mode, kana takes
precedence where a code overlaps an International Morse digit (for example,
SE/1); switch back to Western mode before sending a numeric group.

## Build

Build the matching `SRdeckPlugins.sln` release solution with the compatible
SRdeck platform packages.
