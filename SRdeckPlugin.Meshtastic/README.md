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

## SNR / RSSI display

SNR is an estimated receive SNR referred to the selected LoRa bandwidth (250/125 kHz).
It separates repeating signal power from noise using cross-correlation of repeated
preamble chirps, not the old FFT peak ratio (Q). It assumes stationary noise and a
stable channel; fading, coherent interference, and sample-clock error can bias it.
It is not guaranteed to match a LoRa chip's reported SNR. Insufficient training and
old history containing only Q show `SNR —`. Internal acquisition thresholds are unchanged.

RSSI estimates mean signal-plus-noise power in the selected bandwidth from the same
preamble. It uses the packet's IQ level, not the host's current tuned-channel meter.
The display converts dBFS to dBm with the host receiver's calibration; accuracy depends
on that calibration and receiver gain. Without host telemetry it explicitly shows dBFS.
Missing RSSI in older history remains `RSSI —`; historical values are not recalibrated.

## Fine symbol decisions

After preamble acquisition, peak positions use the existing FFT's integer-bin
powers plus two local DFT evaluations at the adjacent half bins. This gives the
same interpolation grid as a twice-zero-padded FFT without padding or executing
the larger transform. The aligned reference, synchronization and payload all use
this method, including soft candidates. A reused IQ buffer and a last-candidate
cache avoid per-symbol allocations and repeating the main peak's DFT evaluation.
Search FFT ratios, thresholds and CRC acceptance are unchanged. No capture-specific
constant offset or decision-directed tracking is applied.

## Reception history

JSON Lines history keeps the newest receptions within the configured age limit,
100,000 records and 128 MiB. Older records are removed when a limit is reached.
During reception and after retention-period changes, retention runs in the background.

## Build

Build the matching `SRdeckPlugins.sln` release solution with the compatible
SRdeck platform packages.
