# Run from the repository root: powershell -NoProfile -ExecutionPolicy Bypass -File SRdeckPlugin.AribT98/Native/Test-Build-Native.ps1
# Test the build wrapper with a CMake stub, without compiling or changing real outputs.
$ErrorActionPreference = 'Stop'
$testDirectory = Join-Path $PSScriptRoot "build/build-script-tests-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
$buildScript = Join-Path $testDirectory 'Build-Native.ps1'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Build-Native.ps1') -Destination $buildScript
$cmakeStub = Join-Path $testDirectory 'CMake-Stub.ps1'
Set-Content -LiteralPath $cmakeStub -Encoding Ascii -Value @'
if ($args[0] -eq '-S') {
    $global:LASTEXITCODE = if ($failConfigure) { 1 } else { 0 }
    return
}
$configuration = $args[[Array]::IndexOf($args, '--config') + 1]
$outputDirectory = Join-Path $PSScriptRoot "build/$configuration"
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
if ($failBuild -or $emptyDll) { $bytes = [byte[]]@() } else { $bytes = [byte[]]@(77, 90, 1, 2) }
[IO.File]::WriteAllBytes((Join-Path $outputDirectory 'arib_t98_fec_native.dll'), $bytes)
if ($emptyAnalysisDll) { $analysisBytes = [byte[]]@() } else { $analysisBytes = $bytes }
[IO.File]::WriteAllBytes((Join-Path $outputDirectory 'arib_t98_analysis_native.dll'), $analysisBytes)
$global:LASTEXITCODE = if ($failBuild) { 1 } else { 0 }
'@
# This mock is local to this script and visible to the child build script.
function Get-Command { param($Name, $ErrorAction) [pscustomobject]@{ Source = $cmakeStub } }
function Assert($condition, $message) { if (-not $condition) { throw $message } }
function Expect-Failure($configuration, $message) {
    $failed = $false
    try { & $buildScript -Configuration $configuration }
    catch { $failed = $_.Exception.Message -like "*$message*" }
    Assert $failed "Expected $configuration failure: $message"
}

$failConfigure = $false
$failBuild = $false
$emptyDll = $false
$emptyAnalysisDll = $false
$debugDirectory = Join-Path $testDirectory 'build/Debug'
$debugDll = Join-Path $debugDirectory 'arib_t98_fec_native.dll'
$debugLib = Join-Path $debugDirectory 'arib_t98_fec_native.lib'
$debugStamp = Join-Path $debugDirectory 'arib_t98_fec_native.buildstamp'
$publicDirectory = Join-Path $testDirectory 'build/DebugPublic'
$publicStamp = Join-Path $publicDirectory 'arib_t98_fec_native.buildstamp'
try {
    # Reproduce a failed LINK: a recent zero-byte DLL and a stale import library.
    New-Item -ItemType Directory -Path $debugDirectory -Force | Out-Null
    [IO.File]::WriteAllBytes($debugDll, [byte[]]@())
    Set-Content -LiteralPath $debugLib -Value 'stale'
    (Get-Item -LiteralPath $debugLib).IsReadOnly = $true
    & $buildScript -Configuration Debug
    Assert ((Get-Item -LiteralPath $debugDll).Length -gt 0) 'Empty DLL was not recovered.'
    Assert (-not (Test-Path -LiteralPath $debugLib)) 'Stale import library was not removed.'
    Assert (Test-Path -LiteralPath $debugStamp) 'Successful build was not recorded.'

    $failBuild = $true
    Expect-Failure Debug 'compilation failed'
    Assert (-not (Test-Path -LiteralPath $debugStamp)) 'Failed build kept a success stamp.'
    Assert ((Get-Item -LiteralPath $debugDll).Length -eq 0) 'Failed LINK was not simulated.'
    $failBuild = $false
    & $buildScript -Configuration Debug
    Assert (Test-Path -LiteralPath $debugStamp) 'Retry did not record success.'

    $emptyDll = $true
    Expect-Failure Debug 'empty DLL'
    Assert (-not (Test-Path -LiteralPath $debugStamp)) 'Empty DLL was marked successful.'
    $emptyDll = $false

    $emptyAnalysisDll = $true
    Expect-Failure Debug 'empty DLL: arib_t98_analysis_native'
    Assert (-not (Test-Path -LiteralPath $debugStamp)) 'Empty analysis DLL was marked successful.'
    $emptyAnalysisDll = $false

    & $buildScript -Configuration Release
    Assert (Test-Path -LiteralPath (Join-Path $testDirectory 'build/Release/arib_t98_fec_native.buildstamp')) 'Release stamp missing.'
    & $buildScript -Configuration Debug
    & $buildScript -Configuration DebugPublic
    Assert (Test-Path -LiteralPath $publicStamp) 'DebugPublic stamp missing.'
    Assert (-not (Test-Path -LiteralPath $debugStamp)) 'Shared Debug stamp was not invalidated.'
    $publicDll = Join-Path $publicDirectory 'arib_t98_fec_native.dll'
    Assert ((Get-FileHash -LiteralPath $debugDll).Hash -eq (Get-FileHash -LiteralPath $publicDll).Hash) 'DebugPublic DLL differs from Debug.'
    Assert ((Get-FileHash -LiteralPath (Join-Path $debugDirectory 'arib_t98_analysis_native.dll')).Hash -eq
        (Get-FileHash -LiteralPath (Join-Path $publicDirectory 'arib_t98_analysis_native.dll')).Hash) 'DebugPublic analysis DLL differs from Debug.'

    & $buildScript -Configuration Debug
    $failConfigure = $true
    Expect-Failure DebugPublic 'configuration failed'
    Assert (-not (Test-Path -LiteralPath $publicStamp)) 'Configure failure kept the DebugPublic stamp.'
    Assert (-not (Test-Path -LiteralPath $debugStamp)) 'Configure failure kept the shared Debug stamp.'
    Write-Output 'PASS ARIB T98 native build recovery, failure stamps, empty DLL rejection, and configurations'
}
finally {
    # Only remove the fixture directory created under this native build directory.
    $resolvedTestDirectory = [IO.Path]::GetFullPath($testDirectory)
    $expectedParent = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'build'))
    if ([IO.Path]::GetDirectoryName($resolvedTestDirectory) -ne $expectedParent -or
        [IO.Path]::GetFileName($resolvedTestDirectory) -notlike 'build-script-tests-*') {
        throw 'Refusing to remove a fixture outside the native build directory.'
    }
    Remove-Item -LiteralPath $resolvedTestDirectory -Recurse -Force
}
