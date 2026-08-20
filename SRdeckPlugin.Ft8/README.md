# SRdeckPlugin.Ft8

Stable plugin ID: `ft8`. Multi-signal FT8, FT4, and JT65 weak-signal receiver.

## Receive path

- Loads band and mode definitions from `Bands.json`, packaged as `SRdeckPlugin.Ft8.bands.json`.
- Aligns FT8, FT4, and JT65A decoding to their UTC time slots.
- Uses the selected standard channel and does not silently fall back to wideband raw IQ.
- Searches candidates, scores synchronization, and performs LDPC/RS decoding.

## Features

Provides band profiles, reception history, audio monitoring, waterfall
annotations, result notifications, and CSV/JSON export. Protocol provenance
and third-party attribution are documented in `docs/protocol-provenance.md`
and `THIRD-PARTY-NOTICES.md`.

## Build

Build the matching `SRdeckPlugins.sln` release solution with the compatible
SRdeck platform packages.
