# Unknown Signal Analysis Workbench

Enable the plugin in plugin management, select **未知信号の解析ワークベンチ** (`signal-workbench`) in MODE, tune a signal, select a 6 / 12.5 / 25 / 50 / 100 / 200 kHz analysis bandwidth and start reception.

The plugin uses host-channelized IQ at four times the selected bandwidth. It displays an RMS-normalized, unsynchronized IQ constellation, an instantaneous-frequency histogram, and up to five symbol-rate candidates derived from periodic envelope, phase-change and frequency-change features. The 16,384-sample analysis window is updated about every 250 ms, or once per window for longer windows. Display hold freezes the current measurements while processing continues.

Candidate strength is spectral peak prominence over the median, not a confidence probability. Harmonics and analog modulation can produce candidates; weak signals and short bursts may produce none. No carrier/timing synchronization, automatic modulation classification or bit decoding is performed. Retuning, rate changes, gaps and restarts clear the window.

FSK analysis searches for 2, 4 or 8 separated, approximately equally spaced frequency levels. It displays
each tone's frequency, occupancy and local standard deviation, plus the midpoint offset, adjacent spacing
and outer deviation. A discriminator trace shows the first 1,024 consecutive samples of the window.
When tones are found, equal-weight tone transitions provide an additional clock feature, and each
symbol-rate candidate includes `h = adjacent tone spacing / candidate symbol rate` (binary FSK: `2Δf / Rs`).
These are hypotheses: overlapping tones in strongly shaped GFSK/GMSK, missing levels, short bursts and
mixed signals can prevent detection or give misleading results. The Japanese guide documents the detection thresholds.

## Distribution and build

The public `with-plugins` Windows x64 ZIP includes this plugin; the `host-only` ZIP does not.
Use the matching SRdeck release and .NET 10 Desktop Runtime.

To build the published source on Windows x64, place the four matching platform NuGet packages in
the repository's `platform-packages` directory as described in the [root README](../README.md), then run from the repository root:

```powershell
dotnet build SRdeckPlugin.SignalWorkbench\SRdeckPlugin.SignalWorkbench.csproj -c Release -p:Platform=x64
```

Copy `SRdeckPlugin.SignalWorkbench.dll` from `bin/x64/Release/net10.0-windows/win-x64` under this project
to the directory containing `SRdeck.exe`, restart the host and enable the plugin. There is no plugin-specific native DLL.

See the [Japanese guide](README.ja.md) for operation and measurement definitions, and the
[validation record](TESTING.ja.md) for synthetic-signal coverage and remaining RF validation.

## License

Licensed under [GPL-3.0-only](../LICENSE). Retain the common [third-party notices](../THIRD-PARTY-NOTICES.md)
for CommunityToolkit.Mvvm and the shared platform dependencies when redistributing this plugin.
