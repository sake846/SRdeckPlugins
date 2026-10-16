# SRdeckPlugin.OneSeg

Stable plugin ID: `oneseg`. ISDB-T 1-segment television, audio, caption, and EPG receiver.

This source is published in `SRdeckPlugins` for source-level development. The
OneSeg DLL is not included in SRdeck executable packages. See `PATENT-NOTICE.md`
and `THIRD-PARTY-NOTICES.md` before redistributing plugins or binaries.

## Profiles

Supports channel selection across Japanese UHF broadcast channels 13–52 (470–710 MHz)
with prefecture-specific representative preset lists and channel scanning.

## Features

- OFDM demodulation with Mode 3 (8k FFT), 1/8 guard interval, and DQPSK/QPSK/16QAM deinterleaving.
- Viterbi convolutional decoding and Reed-Solomon error correction.
- MPEG-2 TS demultiplexing, PSI/SI table parsing, EPG/event aggregation, and ARIB STD-B24 caption decoding.
- Video and AAC audio playback powered by LibVLCSharp / LibVLC.
- BML data broadcast viewing with Microsoft WebView2.
- TS recording segmentation and IQ pre-trigger capture.

## Build

OneSeg is excluded from the default `SRdeckPlugins.sln` and BAT build.
Before opting in, read the release snapshot's README, `PATENT-NOTICE.md`, and
`THIRD-PARTY-NOTICES.md` and assess the patent and licensing conditions for your
use and country or region. Source availability does not grant every third-party
patent right or establish non-infringement. Only if you decide to enable it,
remove the leading `rem ` from the OneSeg `call :BuildPlugin` line in
`build.bat`, then run the BAT with matching SRdeck platform packages.
