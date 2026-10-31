# APRS receiver

Receive-only Bell 202 AFSK 1200 / AX.25 plugin for SRdeck.
Provides callsigns, paths, uncompressed/compressed and Mic-E positions, APRS messages,
weather values, telemetry channels, object/item tracking, a shared map, bounded
JSONL history and JSON/CSV/TNC2 export. A numeric graph tab selects a station/object
and measurement, plots values against receive time, and shows an accessible value table.
Weather uses °C, m/s, mm and hPa; telemetry analog channels use transmitted counts
(`raw`) and digital channels use bits. Mic-E speed is in knots.
Default frequency: 144.660 MHz; EU, North America and ISS presets are included.

9600 baud, APRS-IS, transmission, raw vendor weather formats, base91 comment telemetry,
and telemetry PARM/UNIT/EQNS/BITS calibration are not supported. Definitions remain
available as raw APRS messages; channel counts are not inferred to be physical quantities.
RF and interactive Windows GUI validation remain required.
See [the Japanese guide](README.ja.md) for setup, scope, tests and protocol references.
