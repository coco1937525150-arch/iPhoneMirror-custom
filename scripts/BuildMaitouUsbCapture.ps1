[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = Join-Path $root 'work\maitou-capture-pinned'
$pinned = 'fb4a8d7eaa70c08538c5dd664350baa24d0e1fa3'
$destination = [IO.Path]::GetFullPath($OutputDirectory)
$validRoot = [IO.Path]::GetFullPath((Join-Path $root 'dist')).TrimEnd('\') + '\'
if (-not $destination.StartsWith($validRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Maitou capture output must remain within dist: $destination"
}
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'Git is required.' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET 10 SDK is required.' }

if (-not (Test-Path -LiteralPath $source)) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $source) | Out-Null
    & git clone --filter=blob:none --no-checkout https://github.com/NeoLin-9892/Maitou.git $source
    if ($LASTEXITCODE -ne 0) { throw 'Failed to fetch pinned Maitou source.' }
    & git -C $source checkout --detach $pinned
    if ($LASTEXITCODE -ne 0) { throw 'Failed to checkout pinned Maitou revision.' }
}
$actualRevision = (& git -C $source rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $actualRevision -ne $pinned) {
    throw "Maitou source revision mismatch; expected $pinned; found $actualRevision"
}

# Only build CaptureHost. The second GUI, virtual-camera registration and
# privileged driver installation workflows are NOT built or executed.
& (Join-Path $source 'scripts\build-native.ps1') -Configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Maitou CaptureHost native compilation failed.' }
$native = Join-Path $source 'artifacts\native\Release'
$deviceSetup = Join-Path $source 'native\Maitou.DeviceSetup\Maitou.DeviceSetup.csproj'

if (Test-Path -LiteralPath $destination) {
    $dir = Get-Item -LiteralPath $destination -Force
    if (($dir.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Refusing to replace a reparse-point Maitou output directory.'
    }
    Remove-Item -LiteralPath $destination -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $destination | Out-Null

# UNCONFIGURED signer and install-mode flags make this a read-only inspector.
$publishArgs = @(
    'publish', $deviceSetup, '--configuration', 'Release',
    '--runtime', 'win-x64', '-p:Platform=x64', '-p:RestoreLockedMode=true',
    '-p:MaitouDriverManifestSha256=UNCONFIGURED',
    '-p:MaitouLocalDriverMode=false',
    '-p:MaitouPrivateUseDriverMode=false', '-o', $destination
)
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'Maitou read-only DeviceSetup build failed.' }

foreach ($name in @(
    'Maitou.CaptureHost.exe', 'libusb0.dll', 'avcodec-62.dll',
    'avutil-60.dll', 'usb-compatibility.json'
)) {
    $path = Join-Path $native $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing pinned Maitou USB runtime: $path"
    }
    Copy-Item -LiteralPath $path -Destination $destination -Force
}

$policy = Get-Content -LiteralPath (Join-Path $destination 'usb-compatibility.json') -Raw | ConvertFrom-Json
if ($policy.schemaVersion -ne 2 -or @($policy.profiles).Count -eq 0) {
    throw 'Maitou USB driver compatibility policy is invalid.'
}
# The policy's libusbSha256 is the *kernel driver (.sys)* fingerprint.
# It must never be compared with the bundled user-mode libusb0.dll.
# Driver compatibility remains read-only and is enforced by DeviceSetup.
$expectedDriverHash = [string]$policy.profiles[0].libusbSha256
if ($expectedDriverHash -notmatch '^[0-9a-fA-F]{64}

$licenses = @(
    @('LICENSE-Maitou.txt', 'LICENSE'),
    @('LICENSE-QuickTime.txt', 'third_party\quicktime_core\LICENSE'),
    @('LICENSE-QuickTime-danielpaulus.txt',
        'third_party\quicktime_core\LICENSE.danielpaulus.txt'),
    @('LICENSE-FFmpeg-LGPL.txt', 'third_party\ffmpeg\COPYING.LGPLv2.1.txt'),
    @('LICENSE-libusb-LGPL.txt', 'third_party\libusb-win32\COPYING_LGPL.txt')
)
foreach ($pair in $licenses) {
    Copy-Item -LiteralPath (Join-Path $source $pair[1]) -Destination (Join-Path $destination $pair[0]) -Force
}
$smoke = & (Join-Path $destination 'Maitou.CaptureHost.exe') '--lifecycle-self-test'
if ($LASTEXITCODE -ne 0 -or -not ($smoke -match '"passed":true')) {
    throw 'Maitou CaptureHost lifecycle smoke test failed.'
}
Write-Host "Maitou USB video host bundled from pinned source: $pinned"
) {
    throw 'Maitou USB kernel-driver policy hash is missing or invalid.'
}

$licenses = @(
    @('LICENSE-Maitou.txt', 'LICENSE'),
    @('LICENSE-QuickTime.txt', 'third_party\quicktime_core\LICENSE'),
    @('LICENSE-QuickTime-danielpaulus.txt',
        'third_party\quicktime_core\LICENSE.danielpaulus.txt'),
    @('LICENSE-FFmpeg-LGPL.txt', 'third_party\ffmpeg\COPYING.LGPLv2.1.txt'),
    @('LICENSE-libusb-LGPL.txt', 'third_party\libusb-win32\COPYING_LGPL.txt')
)
foreach ($pair in $licenses) {
    Copy-Item -LiteralPath (Join-Path $source $pair[1]) -Destination (Join-Path $destination $pair[0]) -Force
}
Write-Host "Maitou USB video host bundled from pinned source: $pinned"
