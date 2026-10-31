# SRdeckPlugin.Sstv

SSTV receiver for SRdeck: USB/FM input, automatic VIS detection, live color image preview and PNG export.

Supported modes: Martin M1/M2, Scottie S1/S2, Robot 36 and PD120.

Build with `dotnet build SRdeckPlugin.Sstv/SRdeckPlugin.Sstv.csproj -c Release` and place the DLL beside SRdeck.exe. A repository host build deploys it automatically.

Enter the USB dial frequency or FM carrier frequency in Hz. Reception must include the VIS header. Manual start, audio monitoring, image history and automatic saving are not included.

See the [Wiki](https://github.com/sake846/SRdeck/wiki/Plugins-Sstv) or [Japanese README](README.ja.md) for operation.
