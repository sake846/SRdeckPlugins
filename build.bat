@echo off
setlocal EnableExtensions DisableDelayedExpansion
rem Usage: build.bat [configuration] [--no-pause]
set "BUILD_EXIT_CODE=1"
set "BUILD_CONFIGURATION=Release"
if not "%~1"=="" set "BUILD_CONFIGURATION=%~1"
set "PACKAGE_DIRECTORY=%~dp0platform-packages"
set "PLATFORM_VERSION=1.0.8"

pushd "%~dp0"
if errorlevel 1 goto finish

where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: Install the .NET 10 SDK and reopen this script.
    goto end
)

for %%P in (SRdeckCore.SignalProcessing SRdeckPlugin.Contracts SRdeckPlugin.Sdk SRdeckPlugin.Wpf) do (
    if not exist "%PACKAGE_DIRECTORY%\%%P.%PLATFORM_VERSION%.nupkg" (
        echo ERROR: Missing "%PACKAGE_DIRECTORY%\%%P.%PLATFORM_VERSION%.nupkg".
        echo Download all four platform packages from the matching SRdeck release.
        echo See README.ja.md or README.md for instructions.
        goto end
    )
)

call :BuildPlugin "SRdeckPlugins.sln"
if not "%BUILD_EXIT_CODE%"=="0" goto end

rem Optional plugins: review README and PATENT-NOTICE.md before opting in.
rem Remove the leading "rem " only for the plugin(s) you decide to build.
rem call :BuildPlugin "SRdeckPlugin.Meshtastic\SRdeckPlugin.Meshtastic.csproj"
if not "%BUILD_EXIT_CODE%"=="0" goto end
rem call :BuildPlugin "SRdeckPlugin.OneSeg\SRdeckPlugin.OneSeg.csproj"
if not "%BUILD_EXIT_CODE%"=="0" goto end

echo Build succeeded. Plugin DLLs are in each project's bin\x64\%BUILD_CONFIGURATION%\net10.0-windows\win-x64 directory.

:end
if not "%BUILD_EXIT_CODE%"=="0" echo Build failed. Exit code: %BUILD_EXIT_CODE%
popd

:finish
if /i not "%~2"=="--no-pause" pause
exit /b %BUILD_EXIT_CODE%

:BuildPlugin
echo Restoring %~1 with platform version %PLATFORM_VERSION%...
dotnet restore "%~1" ^
    --configfile "NuGet.Config" ^
    -p:Configuration=%BUILD_CONFIGURATION% ^
    -p:Platform=x64 ^
    -p:SRdeckPlatformVersion=%PLATFORM_VERSION%
set "BUILD_EXIT_CODE=%ERRORLEVEL%"
if not "%BUILD_EXIT_CODE%"=="0" exit /b %BUILD_EXIT_CODE%

echo Building %~1 in %BUILD_CONFIGURATION%...
dotnet build "%~1" ^
    --configuration "%BUILD_CONFIGURATION%" ^
    --no-restore ^
    -p:Platform=x64 ^
    -p:SRdeckPlatformVersion=%PLATFORM_VERSION%
set "BUILD_EXIT_CODE=%ERRORLEVEL%"
exit /b %BUILD_EXIT_CODE%
