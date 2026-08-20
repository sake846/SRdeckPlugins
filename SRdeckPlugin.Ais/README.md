# SRdeckPlugin.Ais

Stable plugin ID: `ais`. Dual-channel maritime AIS GMSK receiver.

## Receive path

- Monitors AIS 1 at 161.975 MHz and AIS 2 at 162.025 MHz.
- Uses synchronized standard-channel IQ blocks for both channels.
- Performs GMSK demodulation, frame detection, HDLC/FCS validation, and AIS message parsing.

## Features

Maintains vessel targets and reception history, with a WPF workspace, audio
monitoring, frequency overlays, result notifications, and CSV/JSON export.

## Build

Build the matching `SRdeckPlugins.sln` release solution with the compatible
SRdeck platform packages.
