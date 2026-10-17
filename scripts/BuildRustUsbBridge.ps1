[CmdletBinding()]
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = Join-Path $root 'work\rust-iusbbridge-src'
$revision = '8a588b7f13becabe22f4bd511c0a977b18ddb1d6'
$dest = Join-Path $root 'dist\iUsbBridge.exe'
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'Git is required.' }
if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) { throw 'Rust MSVC Cargo is required.' }
if (Test-Path -LiteralPath $source) {
    $found = (& git -C $source rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $found -ne $revision) {
        throw "Stale iUsbBridge source. Remove $source and rerun the build."
    }
} else {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $source) | Out-Null
    & git clone --filter=blob:none --no-checkout https://github.com/RayrenSX/iUsbBridge.git $source
    if ($LASTEXITCODE -ne 0) { throw 'Failed to clone iUsbBridge.' }
    & git -C $source checkout --detach $revision
    if ($LASTEXITCODE -ne 0) { throw 'Failed to check out pinned iUsbBridge.' }
}
$script = Join-Path $source 'build.ps1'
if (-not (Test-Path -LiteralPath $script -PathType Leaf)) { throw 'Bridge build script missing.' }
& $script -BridgeOnly -BridgeOutputPath $dest -SkipTests:$SkipTests
if ($LASTEXITCODE -ne 0) { throw "Rust bridge build failed: $LASTEXITCODE" }
$license = Join-Path $source 'LICENSE'
if (-not (Test-Path -LiteralPath $license -PathType Leaf)) { throw 'Bridge license missing.' }
Copy-Item -LiteralPath $license -Destination (Join-Path $root 'dist\iUsbBridge-LICENSE.txt') -Force
Write-Host "Rust iUsbBridge source revision: $revision"
