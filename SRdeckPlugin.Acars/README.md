# SRdeckPlugin.Acars

Stable plugin ID: `acars`. Receives VHF ACARS and decodes ARINC 618 messages.

## Receive path

- Requests selected 25 kHz VHF channels through the SRdeck tuning service.
- Consumes synchronized 48 kS/s standard-channel blocks.
- Demodulates 1200/2400 Hz NRZI-coded AM-MSK at 2400 bit/s.
- Validates sync characters, odd parity, and the block check sequence.

## Features

Aircraft registration, labels, block IDs, and message text are available in
the reception history, result notifications, and CSV/JSON exports.

## Build

Build the matching `SRdeckPlugins.sln` release solution with the compatible
SRdeck platform packages.
