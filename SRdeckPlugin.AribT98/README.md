# ARIB STD-T98 receiver

SRdeck receiver for digital convenience radio using STD-T98 4FSK and
pi/4-shift QPSK. It has no transmit function.

- 4FSK: control information, channel FEC, standard PN15 privacy analysis,
  voice monitoring through an external AMBE dongle, and IQ capture.
- PSK: demodulation, control information and TCH extraction. PSK voice is
  not supported.

See the [Japanese user guide](README.ja.md) for channel plans, receiver
settings, privacy analysis and IQ recording.

## Audio requires a DVStick

Connect an AMBE-3000-compatible DVStick 30 or ThumbDV and set its Windows
COM port in the plugin settings, for example `COM6`. Use 460800 baud, or
230400 baud if required by the device. One application/plugin can open
the same COM port at a time.

The plugin performs channel FEC and standard privacy removal, then sends
49 parameter bits per voice slot to the dongle. Only the dongle produces
speech PCM. No `mbe-neo.dll` or software speech synthesizer is used.
Without a usable dongle, reception, control display and privacy analysis
continue with audio muted. Connection and communication failures also mute
audio; there is no software audio fallback.

## Build and deployment

Requirements: Windows x64, .NET 10 SDK, Visual Studio C++ x64 build tools,
a Windows SDK and CMake. Native FEC and parameter-analysis modules are
built automatically. Sources are vendored; the native build does not
download them.

The public source snapshot uses the four matching SRdeck platform NuGet
packages. Prepare them as described in the snapshot's root README, then
run `build.bat` from that root. From the development source tree, use:

```powershell
dotnet build SRdeckPlugin.AribT98/SRdeckPlugin.AribT98.csproj -c Release
```

Use the matching SRdeck host and deploy these files beside `SRdeck.exe`:

- `SRdeckPlugin.AribT98.dll`
- `arib_t98_fec_native.dll`
- `arib_t98_analysis_native.dll`
- `System.IO.Ports.dll`
- `SRdeckPlugin.AribT98.THIRD-PARTY-NOTICES.md`
- `AMBE.THIRD-PARTY-NOTICES.md`
- The generated `licenses` directory

The dongle transport and managed FEC are compiled from `shared/Ambe` into
the plugin; there is no dependency on another plugin.
The plugin-inclusive executable package includes this bundle. Do not add
legacy `mbe-neo.dll` or `arib_t98_mbe_native.dll` files.

## Verification and notices

Known on-air bit vectors, synthetic IQ, privacy analysis, audio gating and
host lifecycle have regression coverage. DVStick hardware tests also verify
known tone and privacy frames; they do not establish live-radio reception
quality or interoperability with every radio.

The combined plugin is GPL-3.0-only. FEC and parameter analysis retain
GPL-2.0-or-later mbelib-neo code/tables and the ISC-licensed DSD bitplane
schedule. Notices and the corresponding native source/build scripts are
included in this source snapshot. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)
and the snapshot's [patent notice](../PATENT-NOTICE.md).
ARIB specification PDFs and private recordings are not redistributed.
