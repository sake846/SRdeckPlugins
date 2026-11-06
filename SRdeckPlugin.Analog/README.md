# SRdeckPlugin.Analog

Stable plugin ID: `analog`. AM, FM, and SSB audio demodulation.

## Profiles

- AM amplitude demodulation
- FM frequency demodulation
- SSB single-sideband demodulation

## Features

The plugin owns its audio workspace and settings view, including profile
selection, tuning, squelch, AGC, audio filtering, and a rolling three-second
post-channel-filter IQ pre-trigger capture and continuous IQ recording at the internal sample rate, split into ten-minute files or one-minute files for wide FM. Selecting the first split WAV in the settings view automatically plays the remaining sequence. It also supports headless processing.

Continuous IQ recording stores complex IQ after rate conversion and before the channel filter; CIC compensation is applied when decimating. The rolling three-second pre-trigger capture remains post-channel-filter.

## Build

Build the matching `SRdeckPlugins.sln` release solution with the compatible
SRdeck platform packages.
