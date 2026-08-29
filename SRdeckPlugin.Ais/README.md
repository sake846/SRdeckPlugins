# SRdeckPlugin.Ais

Stable plugin ID: `ais`. Dual-channel maritime AIS GMSK receiver.

## Receive path

- Monitors AIS 1 at 161.975 MHz and AIS 2 at 162.025 MHz.
- Uses synchronized standard-channel IQ blocks for both channels.
- Performs GMSK demodulation, frame detection, HDLC/FCS validation, and AIS message parsing.
- Offers an opt-in burst demodulation gate. A 64-sample power detector replays
  up to 8 ms of filtered IQ pre-roll before the full decoder bank starts.

## Features

Maintains vessel targets and reception history, with a WPF workspace, audio
monitoring, frequency overlays, result notifications, and CSV/JSON export.

## Build

Build the matching `SRdeckPlugins.sln` release solution with the compatible
SRdeck platform packages.
