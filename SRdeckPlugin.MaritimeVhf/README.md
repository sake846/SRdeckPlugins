# SRdeckPlugin.MaritimeVhf

Built-in receive-only International Maritime VHF plugin (`maritime-vhf`). It
provides narrow-FM voice reception, simultaneous DSC Channel 70 decoding, and
automatic receive-channel following from validated DSC calls.

## Features

- International 25 kHz maritime voice-channel plan with ship/coast receive-side
  selection
- 48 kS/s narrow-FM voice demodulation, deemphasis, squelch, and monitor audio
- DSC Channel 70 reception at 156.525 MHz using 1300/2100 Hz, 1200 bit/s AFSK
- DSC ten-bit symbol validation, time-diversity recovery, and ECC validation
- Parsing of distress, individual, group, all-ships, and geographic-area calls
- Optional MMSI filtering and automatic following of the announced working
  channel, with manual release and silence-based return
- DSC history, host result publication, alarms, diagnostics, and frequency
  overlays

AIS 1 and AIS 2 are deliberately excluded. Use `SRdeckPlugin.Ais` for AIS.

Automatic following only retunes the receiver. The plugin does not transmit a
DSC acknowledgement or control radio transmitter hardware. When the SDR input
bandwidth cannot contain both the selected voice frequency and Channel 70, voice
reception continues and the UI reports that simultaneous DSC monitoring is
suspended.

See [README.ja.md](README.ja.md) for the Japanese operating guide.

