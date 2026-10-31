param([ValidateSet('Debug', 'Release', 'DebugPublic')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$nativeConfiguration = if ($Configuration -eq 'DebugPublic') { 'Debug' } else { $Configuration }
$outputDirectory = Join-Path $PSScriptRoot "build/$Configuration"
$buildStamp = Join-Path $outputDirectory 'arib_t98_fec_native.buildstamp'
$nativeBuildStamp = Join-Path $PSScriptRoot "build/$nativeConfiguration/arib_t98_fec_native.buildstamp"
foreach ($stamp in @($buildStamp, $nativeBuildStamp)) {
    if (Test-Path -LiteralPath $stamp) { Remove-Item -LiteralPath $stamp -Force }
}

# LINK can leave a fresh, empty DLL when replacing an old import library fails.
# Regenerate the import library instead of attempting to replace the stale file.
$nativeTargets = @('arib_t98_fec_native', 'arib_t98_analysis_native')
foreach ($target in $nativeTargets) {
    $importLibrary = Join-Path $PSScriptRoot "build/$nativeConfiguration/$target.lib"
    if (Test-Path -LiteralPath $importLibrary) { Remove-Item -LiteralPath $importLibrary -Force }
}

$cmakeCommand = Get-Command cmake -ErrorAction SilentlyContinue
if ($cmakeCommand) { $cmakePath = $cmakeCommand.Source }
else {
    $vswherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswherePath)) { throw 'Install CMake and the Visual Studio C++ build tools.' }
    $vsPath = & $vswherePath -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    $cmakePath = Join-Path $vsPath 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
}
& $cmakePath -S $PSScriptRoot -B (Join-Path $PSScriptRoot 'build') -A x64
if ($LASTEXITCODE -ne 0) { throw 'ARIB T98 codec CMake configuration failed.' }
& $cmakePath --build (Join-Path $PSScriptRoot 'build') --config $nativeConfiguration --target $nativeTargets --parallel 2
if ($LASTEXITCODE -ne 0) { throw 'ARIB T98 codec compilation failed.' }
foreach ($target in $nativeTargets) {
    $nativeDll = Get-Item -LiteralPath (Join-Path $PSScriptRoot "build/$nativeConfiguration/$target.dll")
    if ($nativeDll.Length -eq 0) { throw "ARIB T98 build produced an empty DLL: $target" }
    if ($Configuration -ne $nativeConfiguration) {
        New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
        Copy-Item -LiteralPath $nativeDll.FullName -Destination $outputDirectory
    }
    # The script is an MSBuild input; CMake may not relink when only it changes.
    (Get-Item -LiteralPath (Join-Path $outputDirectory "$target.dll")).LastWriteTime = Get-Date
}
Set-Content -LiteralPath $buildStamp -Value 'Build succeeded.' -Encoding Ascii
