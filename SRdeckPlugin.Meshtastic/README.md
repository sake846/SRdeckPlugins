# SRdeckPlugin.Meshtastic

Stable plugin ID: `meshtastic`. Meshtastic LoRa packet receiver and decoder.

This source is published in `SRdeckPlugins` for source-level development. The
Meshtastic DLL is not included in SRdeck executable packages.

## Profiles

The plugin provides LongFast, LongModerate, LongSlow, MediumFast, MediumSlow,
ShortFast, ShortSlow, and automatic spreading-factor presets for 250 kHz,
125 kHz, and mixed operation.

## Features

Performs LoRa channel extraction, chirp demodulation, FEC/CRC validation, and
Meshtastic packet parsing. In specified mode, the first selected slot is also
FM-demodulated to 48 kHz mono PCM for audio monitoring. The FM monitor is not
processed in discovery mode. The WPF workspace provides profile settings and
frequency overlays.

## Build

Build the matching `SRdeckPlugins.sln` release solution with the compatible
SRdeck platform packages.
