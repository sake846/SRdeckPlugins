# SRdeckPlugin.HfFax

Stable plugin ID: `hffax`. Receives HF weather radiofacsimile (Wefax) from IQ,
detects APT start/stop tones, and renders IOC 576/288 grayscale images.

The receiver uses an assigned-frequency-centered 3.0 kHz standard IQ channel at
12 kS/s. It maps the F3C deviation from -400 Hz (black) through +400 Hz (white),
supports 60/90/120 LPM, manual reception, slant/phase correction, USB-oriented monitor audio,
and PNG export. A post-demodulation VBW filter is selectable at off, 600, 1200,
or 1800 Hz; 1200 Hz is the default.

Image data and PNG output retain the full IOC-derived scan-line width (`pi * IOC`)
and are scaled down only for display. Completed timeline images are persisted and
restored on the next application start.
If a manual start misses the scan-line origin, changing horizontal shift circularly
realigns both the lines already received and subsequent lines without stopping reception.
When the phasing signal has been observed, its reference is retained so a later manual
start can automatically wait for the next complete scan line. Manual correction remains
necessary if reception itself begins after the phasing signal.
When per-line synchronization is enabled, the receiver continues tracking line-boundary
pulses throughout the image. It preserves line phase across missing pulses and corrects line
phase and scan period when a valid marker returns. The diagnostics tab reports lock state, pulse
count, bridged lines, and the measured line-period error. The option can be disabled for
stations that do not transmit in-image synchronization markers.

Automatic horizontal alignment independently analyzes the persistent dark page-boundary band
after 64 received lines. It circularly rotates the lines already received and all following lines
to a common origin while preserving the operator's manual horizontal offset. Long images restored
from JSONL history are normalized in memory as well, so captures that began at different scan
phases line up without rewriting the original history records. The option can be disabled for
stations whose image format has no stable page-boundary band.

## Build

Build the matching `SRdeckPlugins.sln` release solution with the compatible
SRdeck platform packages.
