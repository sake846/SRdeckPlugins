# SRdeckPlugins

This repository contains the official SRdeck plugins as a versioned release
snapshot.

The public source snapshot contains the eight plugins selected for executable
packages and the Meshtastic source-only plugin. Meshtastic is not included in
the SRdeck executable packages. See `PATENT-NOTICE.md` before redistributing
plugins or binaries.

Do not commit product changes directly to this repository.  Use the project's
normal development and review process for product changes.

## Release metadata

- Release version: `1.0.1`
- Required SRdeck platform package version: `1.0.1`

## Building

### Prerequisites

- Windows x64
- .NET 10 SDK (`dotnet --info` should show an installed 10.x SDK)
- Git
- The four SRdeck platform NuGet packages with the same version as
  `1.0.1`

The plugin projects consume `SRdeckPlugin.Contracts`, `SRdeckPlugin.Sdk`,
`SRdeckPlugin.Wpf`, and `SRdeckCore.SignalProcessing` as NuGet packages. The
package version must match the SRdeck host version used for testing. Do not
mix packages from different SRdeck releases.

### 1. Clone the matching source snapshot

```powershell
$releaseVersion = "1.0.1"
$platformVersion = "1.0.1"

git clone --branch "v$releaseVersion" --depth 1 https://github.com/sake846/SRdeckPlugins.git
Set-Location .\SRdeckPlugins
```

### 2. Prepare the platform packages

Create a local package directory and download these four files from the
matching [SRdeck release](https://github.com/sake846/SRdeck/releases):

- `SRdeckCore.SignalProcessing.1.0.1.nupkg`
- `SRdeckPlugin.Contracts.1.0.1.nupkg`
- `SRdeckPlugin.Sdk.1.0.1.nupkg`
- `SRdeckPlugin.Wpf.1.0.1.nupkg`

If GitHub CLI is installed, the files can be downloaded automatically:

```powershell
$packageDirectory = Join-Path (Get-Location) "platform-packages"
New-Item -ItemType Directory -Force $packageDirectory | Out-Null
gh release download "v$platformVersion" `
  --repo sake846/SRdeck `
  --pattern "*.nupkg" `
  --dir $packageDirectory
```

Without GitHub CLI, download the same four `.nupkg` files in a browser and
place them directly in `platform-packages`.

### 3. Restore and build every plugin

```powershell
dotnet restore .\SRdeckPlugins.sln `
  --source $packageDirectory `
  --source https://api.nuget.org/v3/index.json `
  -p:SRdeckPlatformVersion=$platformVersion

dotnet build .\SRdeckPlugins.sln `
  -c Release `
  --no-restore `
  -p:SRdeckPlatformVersion=$platformVersion
```

To build only one plugin after restoring the solution, pass its project file
instead. For example:

```powershell
dotnet build .\SRdeckPlugin.Ais\SRdeckPlugin.Ais.csproj `
  -c Release `
  --no-restore `
  -p:SRdeckPlatformVersion=$platformVersion
```

The resulting assembly is written to:

```text
SRdeckPlugin.Ais\bin\x64\Release\net10.0-windows\win-x64\SRdeckPlugin.Ais.dll
```

Replace `Ais` with the plugin project you built. The other plugin-specific
DLLs in that same output directory are its runtime dependencies.

### 4. Test a plugin with SRdeck locally

Use a host built from the same SRdeck release or the matching host source
snapshot. Copy the plugin DLL and its plugin-specific dependency DLLs into the
directory containing `SRdeck.exe`, then restart SRdeck:

```powershell
$pluginOutput = Join-Path (Get-Location) "SRdeckPlugin.Ais\bin\x64\Release\net10.0-windows\win-x64"
$srdeckDirectory = "C:\path\to\SRdeck"
Get-ChildItem $pluginOutput -Filter "*.dll" | Copy-Item -Destination $srdeckDirectory -Force
```

The DLL search directory is the directory containing `SRdeck.exe`; the
`%LOCALAPPDATA%\SRdeck\plugins` directory is for settings and plugin data,
not for plugin assemblies. Restart the host after replacing a DLL.

Meshtastic can be built from this source snapshot for local testing, but its
DLL is intentionally not included in the official SRdeck executable packages.

### Troubleshooting

- `NU1101` for an SRdeck package: check that `platform-packages` contains all
  four `.nupkg` files and that their versions equal `$platformVersion`.
- Missing or incompatible types at runtime: build and test with a host and
  platform packages from the same SRdeck release.
- No plugin appears after copying the DLL: confirm that it is beside
  `SRdeck.exe`, close all SRdeck processes, and start SRdeck again.

The release workflow performs the same restore and Release build against the
matching SRdeck platform package release before this snapshot is published.
