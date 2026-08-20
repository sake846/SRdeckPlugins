# SRdeckPlugin.Vdl

Stable plugin ID: `vdl`. VHF Data Link Mode 2 (D8PSK/AVLC) receiver.

## Receive path

- Provides six VDL2 channel profiles from 136.725 MHz through 136.975 MHz.
- Consumes 105 kS/s standard-channel IQ.
- Synchronizes the D8PSK preamble, timing, and carrier.
- Validates header FEC, Reed-Solomon parity, and the frame check sequence.

## Features

Decodes AVLC frames and ACARS upper-layer content, with reception history,
audio monitoring, result notifications, frequency overlays, and CSV/JSON export.

## Build

Build the matching `SRdeckPlugins.sln` release solution with the compatible
SRdeck platform packages.
