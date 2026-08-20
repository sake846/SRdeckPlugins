# SRdeckPlugin.AdsB

Stable plugin ID: `adsb`. Receives 1090 MHz Mode S extended squitter.

## Receive path

- Prefers a 1090 MHz standard channel with 1.85 MHz bandwidth and 4 MS/s output.
- Requires at least 2 MS/s from the host input and rejects insufficient tuning requests.
- Detects Mode S preambles, demodulates PPM, and validates the CRC.

## Features

Decodes DF17/DF18 traffic, aircraft identity, altitude, speed, track, vertical
rate, and even/odd CPR position. The WPF workspace exposes reception history,
aircraft lists, settings, result notifications, and CSV/JSON export.

## Build

Build the matching `SRdeckPlugins.sln` release solution with the compatible
SRdeck platform packages.
