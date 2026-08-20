# SRdeckPlugin.Hfdl

Stable plugin ID: `hfdl`. HF Data Link receiver and ARINC 635 decoder.

## Receive path

- Requests selected 3 kHz ground-station channels through the tuning service.
- Consumes 14.4 kS/s IQ and processes 1800 symbols/s BPSK, QPSK, and 8PSK.
- Handles prekey, preamble, M1/M2, phase descrambling, deinterleaving,
  K=7 Viterbi decoding, and CRC validation.

## Features

Provides SPDU/LPDU envelopes, decoded results, raw payloads, reception history,
frequency overlays, and CSV/JSON export.

## Build

Build the matching `SRdeckPlugins.sln` release solution with the compatible
SRdeck platform packages.
