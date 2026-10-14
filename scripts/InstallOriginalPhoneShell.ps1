# Install the *original* pre6 iPhone 16 Pro Max RGBA PNG into this checkout.
# This is a developer build-time operation, never a user-facing skin selector.
# Usage:
#   pwsh ./scripts/InstallOriginalPhoneShell.ps1 -Source "C:\Downloads\iPhoneMirror-pre6-original-shell-asset.zip"
#   pwsh ./scripts/InstallOriginalPhoneShell.ps1 -Source "C:\Downloads\frame-white-1160be33(3).png"
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Source
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$target = Join-Path $repoRoot 'src/App/Assets/iphone16_pro_max_shell.png'
$expectedSha256 = '0539067df832d4ec3da82baaf7e690ff51c584b7dae664e16062b236eea813b4'
$resolvedSource = (Resolve-Path -LiteralPath $Source -ErrorAction Stop).Path
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("iphoneMirror-pre6-shell-" + [guid]::NewGuid().ToString('N') + '.png')
try {
    if ([System.IO.Path]::GetExtension($resolvedSource).Equals('.zip', [StringComparison]::OrdinalIgnoreCase)) {
        Add-Type -AssemblyName System.IO.Compression
        $archive = [System.IO.Compression.ZipFile]::OpenRead($resolvedSource)
        try {
            $entry = $archive.GetEntry('src/App/Assets/iphone16_pro_max_shell.png')
            if ($null -eq $entry) { throw 'ZIP does not contain the original shell at src/App/Assets/iphone16_pro_max_shell.png.' }
            $inputStream = $entry.Open()
            try {
                $outputStream = [System.IO.File]::Create($staging)
                try { $inputStream.CopyTo($outputStream) }
                finally { $outputStream.Dispose() }
            }
            finally { $inputStream.Dispose() }
        }
        finally { $archive.Dispose() }
    } else {
        Copy-Item -LiteralPath $resolvedSource -Destination $staging -ErrorAction Stop
    }
    $digest = (Get-FileHash -LiteralPath $staging -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($digest -ne $expectedSha256) {
        throw "Refusing to install non-original shell (SHA256 $digest). Required $expectedSha256."
    }
    $data = [System.IO.File]::ReadAllBytes($staging)
    if ($data.Length -ne 317240) { throw "Incorrect original shell size $($data.Length)." }
    $pngSignature = [byte[]](137,80,78,71,13,10,26,10)
    for ($i = 0; $i -lt $pngSignature.Length; $i++) {
        if ($data[$i] -ne $pngSignature[$i]) { throw 'Expected a PNG file.' }
    }
    $width = [System.Buffers.Binary.BinaryPrimitives]::ReadInt32BigEndian([ReadOnlySpan[byte]]::new($data, 16, 4))
    $height = [System.Buffers.Binary.BinaryPrimitives]::ReadInt32BigEndian([ReadOnlySpan[byte]]::new($data, 20, 4))
    if ($width -ne 994 -or $height -ne 2048 -or $data[25] -ne 6) {
        throw "Unexpected PNG format: ${width}x${height}, color type $($data[25])."
    }
    Move-Item -LiteralPath $staging -Destination $target -Force
    Write-Host "Installed fixed original iPhone shell: $target"
    Write-Host "SHA256: $expectedSha256"
    Write-Host "The app will embed this file at build time. Commit the changed PNG before building CI artifacts."
}
finally { Remove-Item -LiteralPath $staging -Force -ErrorAction SilentlyContinue }
