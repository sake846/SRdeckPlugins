# SRdeckPlugins

[![Wiki](https://img.shields.io/badge/docs-SRdeck_Wiki-blue.svg)](https://github.com/sake846/SRdeck/wiki)
[![Plugins Overview](https://img.shields.io/badge/plugins-Plugins_Overview-orange.svg)](https://github.com/sake846/SRdeck/wiki/Plugins-Overview)
[![Developer Guide](https://img.shields.io/badge/docs-Developer_Guide-purple.svg)](https://github.com/sake846/SRdeck/wiki/Developer-Overview)

This repository contains the official SRdeck plugins as a versioned release
snapshot.

The public source snapshot contains the plugins selected for executable packages
and the Meshtastic and OneSeg source-only plugins. Meshtastic and OneSeg are
excluded from the default build and the SRdeck executable packages. Their BAT
build commands are commented out until you explicitly opt in after reviewing
the patent and licensing notice below.

Do not commit product changes directly to this repository.  Use the project's
normal development and review process for product changes.

## 📖 Documentation & Wiki

For detailed plugin specifications, reception procedures, and development guides, visit the [SRdeck Official Wiki](https://github.com/sake846/SRdeck/wiki):

- 🔌 **Using Plugins**: [Official Plugins Overview](https://github.com/sake846/SRdeck/wiki/Plugins-Overview) / [Plugin Management](https://github.com/sake846/SRdeck/wiki/Plugin-Management)
- 💻 **Plugin Development**: [Developer Overview](https://github.com/sake846/SRdeck/wiki/Developer-Overview) / [Development Environment](https://github.com/sake846/SRdeck/wiki/Development-Environment) / [Creating First Plugin](https://github.com/sake846/SRdeck/wiki/Creating-First-Plugin) / [Right Pane UI Design](https://github.com/sake846/SRdeck/wiki/Right-Pane-UI-Design)
- ❓ **Support & FAQ**: [FAQ](https://github.com/sake846/SRdeck/wiki/FAQ) / [Troubleshooting](https://github.com/sake846/SRdeck/wiki/Troubleshooting)

## Release metadata

- Release version: `1.0.8`
- Required SRdeck platform package version: `1.0.8`

## Building

### Prerequisites

- Windows x64
- .NET 10 SDK (`dotnet --info` should show an installed 10.x SDK)
- Git (only if cloning the source; not required for the ZIP download)
- The four SRdeck platform NuGet packages with the same version as
  `1.0.8`

The plugin projects consume `SRdeckPlugin.Contracts`, `SRdeckPlugin.Sdk`,
`SRdeckPlugin.Wpf`, and `SRdeckCore.SignalProcessing` as NuGet packages. The
package version must match the SRdeck host version used for testing. Do not
mix packages from different SRdeck releases.

The plugin build in this public snapshot does not invoke CMake. Building the
SRdeck host from source also requires MSVC x64/x86 build tools, a Windows 10 or
11 SDK, and CMake (3.21 or newer for VS 2022; 4.2 or newer for VS 2026).
See [Development Environment](https://github.com/sake846/SRdeck/wiki/Development-Environment)
for installation steps and CMake troubleshooting.

### 1. Get the matching source snapshot

All commands below are for the Windows **Command Prompt (cmd.exe)**. Open
Command Prompt from the Start menu and set the versions first. Keep using the
same window, and check that each command succeeds before continuing.

```cmd
set "RELEASE_VERSION=1.0.8"
set "PLATFORM_VERSION=1.0.8"
```

#### Without Git: download the ZIP

1. Open the [matching SRdeckPlugins release](https://github.com/sake846/SRdeckPlugins/releases/tag/v1.0.8)
   in a browser and download **Source code (zip)** under Assets.
2. Extract the ZIP using File Explorer's **Extract All** command.
3. Place the extracted folder containing `SRdeckPlugins.sln` at
   `C:\SRdeckBuild\SRdeckPlugins`. The solution must be directly inside this
   directory, rather than inside another nested folder from the ZIP.
4. Change to that directory in Command Prompt:

```cmd
cd /d "C:\SRdeckBuild\SRdeckPlugins"
```

#### With Git

To clone the source instead of downloading the ZIP, run:

```cmd
if not exist "C:\SRdeckBuild" mkdir "C:\SRdeckBuild"
cd /d "C:\SRdeckBuild"
git clone --branch "v%RELEASE_VERSION%" --depth 1 https://github.com/sake846/SRdeckPlugins.git
cd SRdeckPlugins
```

### 2. Prepare the platform packages

Download these four files from the matching
[SRdeck release](https://github.com/sake846/SRdeck/releases):

- `SRdeckCore.SignalProcessing.1.0.8.nupkg`
- `SRdeckPlugin.Contracts.1.0.8.nupkg`
- `SRdeckPlugin.Sdk.1.0.8.nupkg`
- `SRdeckPlugin.Wpf.1.0.8.nupkg`

Run these commands from the source directory to create the package directory:

```cmd
if not exist "platform-packages" mkdir "platform-packages"
set "PACKAGE_DIRECTORY=%CD%\platform-packages"
```

Place the four downloaded `.nupkg` files directly in `platform-packages` without
extracting them. This example shows the layout after extracting the source ZIP
and adding the packages (other source files are omitted):

```text
C:\SRdeckBuild\
└─ SRdeckPlugins\
   ├─ SRdeckPlugins.sln
   ├─ build.bat
   ├─ NuGet.Config
   ├─ Directory.Build.props
   ├─ Directory.Build.targets
   ├─ SRdeckPlugin.Ais\
   │  └─ SRdeckPlugin.Ais.csproj
   └─ platform-packages\
      ├─ SRdeckCore.SignalProcessing.1.0.8.nupkg
      ├─ SRdeckPlugin.Contracts.1.0.8.nupkg
      ├─ SRdeckPlugin.Sdk.1.0.8.nupkg
      └─ SRdeckPlugin.Wpf.1.0.8.nupkg
```

If GitHub CLI is installed, this command can download the packages instead of
using a browser. The ZIP and browser download steps require neither Git nor
GitHub CLI.

```cmd
gh release download "v%PLATFORM_VERSION%" --repo sake846/SRdeck --pattern "*.nupkg" --dir "%PACKAGE_DIRECTORY%"
```

### 3. Restore and build the default plugins

After placing the four packages in `platform-packages`, double-click
`build.bat` in the source folder, or run it from Command Prompt:

```cmd
build.bat
```

The BAT file checks for the .NET SDK and the four matching platform packages,
restores dependencies, and builds the solution in Release configuration. It
uses its own folder as the working directory and the package version recorded
in this snapshot. An internet connection is required to restore dependencies.
The window waits for a key press at the end so you can read the result.
Meshtastic and OneSeg are excluded from `SRdeckPlugins.sln`, so neither the
default BAT build nor a direct solution build restores or builds them.

Optional arguments select the configuration and whether to skip the final key
press (for automated builds):

```cmd
build.bat Debug
build.bat Release --no-pause
```

Restore or build failures stop the script and return a nonzero exit code.

#### Patent and licensing notice: opt in to Meshtastic / OneSeg

**Meshtastic (LoRa demodulation) and OneSeg (broadcast and audio/video codecs)
may involve third-party patent rights.** GPL-3.0-only licensing does not by
itself grant all third-party patent rights that may be required; see
[GPLv3 section 11](https://www.gnu.org/licenses/gpl-3.0.html#section11).
Source availability and receive-only use do not establish non-infringement.
This project does not guarantee non-infringement or that every required patent
license has been obtained.

Before enabling either plugin, read [PATENT-NOTICE.md](PATENT-NOTICE.md),
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), and the plugin's notices.
Assess the applicable patent and licensing conditions for your intended use
and country or region, including any permissions you may need. **Only remove
the comments if you decide that you can build and use the selected plugin.**
If you cannot make that determination, leave it disabled.

Open `build.bat` in a text editor and find these lines:

```bat
rem call :BuildPlugin "SRdeckPlugin.Meshtastic\SRdeckPlugin.Meshtastic.csproj"
rem call :BuildPlugin "SRdeckPlugin.OneSeg\SRdeckPlugin.OneSeg.csproj"
```

Remove only the leading `rem ` from the line for each plugin you choose to
enable, save the file, and run `build.bat` again. Each plugin can be enabled
independently. The BAT file restores and builds the enabled projects separately
after the default solution. Their DLLs remain excluded from official executable
packages.

To build only one default plugin after restoring the solution, pass its project file
instead. For example:

```cmd
dotnet build .\SRdeckPlugin.Ais\SRdeckPlugin.Ais.csproj ^
  -c Release ^
  --no-restore ^
  -p:Platform=x64 ^
  -p:SRdeckPlatformVersion=%PLATFORM_VERSION%
```

The `^` character continues a command onto the next line in Command Prompt.
Do not put spaces after it.

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

This example assumes `SRdeck.exe` is in `C:\SRdeck`. Close SRdeck before running
it, and change `SRDECK_DIRECTORY` to match your host installation:

```cmd
set "PLUGIN_OUTPUT=%CD%\SRdeckPlugin.Ais\bin\x64\Release\net10.0-windows\win-x64"
set "SRDECK_DIRECTORY=C:\SRdeck"
copy /Y "%PLUGIN_OUTPUT%\*.dll" "%SRDECK_DIRECTORY%\"
```

The DLL search directory is the directory containing `SRdeck.exe`; the
`%LOCALAPPDATA%\SRdeck\plugins` directory is for settings and plugin data,
not for plugin assemblies. Restart the host after replacing a DLL.

Meshtastic and OneSeg require the explicit opt-in described above before the BAT
file builds them for local testing. Their DLLs are intentionally not included
in the official SRdeck executable packages.

### Troubleshooting

- CMake errors: this solution does not invoke CMake. Check the command and the
  `-S` path in the error. If building the host or the development monorepo,
  install the native build prerequisites described above.
- `NU1101` for an SRdeck package: check that `platform-packages` contains all
  four `.nupkg` files and that their versions equal `%PLATFORM_VERSION%`.
- Missing or incompatible types at runtime: build and test with a host and
  platform packages from the same SRdeck release.
- No plugin appears after copying the DLL: confirm that it is beside
  `SRdeck.exe`, close all SRdeck processes, and start SRdeck again.

The release workflow performs the same restore and Release build against the
matching SRdeck platform package release before this snapshot is published.
