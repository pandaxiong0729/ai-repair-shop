[CmdletBinding()]
param(
    [string] $ExePath,
    [string] $CodexPath,
    [string] $DataRoot,
    [switch] $NoStartup, # Compatibility only: installation no longer enables login startup.
    [switch] $NoLaunch
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Resident-Common.ps1')
$DataRoot = Get-ResidentDataRoot $DataRoot
if ([string]::IsNullOrWhiteSpace($ExePath)) { $ExePath = Join-Path $PSScriptRoot '..\assets\desktop\QiaoQiaoAI.Preview.exe' }
if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) { throw 'The complete resident desktop runtime is missing. Install the complete skill package or supply -ExePath.' }
$ExePath = (Resolve-Path -LiteralPath $ExePath).ProviderPath
$sourceRoot = Split-Path -Parent $ExePath
if ([IO.Path]::GetFileName($ExePath) -ne 'QiaoQiaoAI.Preview.exe') { throw 'ExePath must identify QiaoQiaoAI.Preview.exe.' }
if ((Test-ResidentChildPath $DataRoot $sourceRoot) -or $DataRoot.Equals($sourceRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'The resident data directory must not be the runtime source or nested inside it.' }
foreach ($name in @('QiaoQiaoAI.Preview.dll', 'QiaoQiaoAI.UI.dll', 'QiaoQiaoAI.Preview.runtimeconfig.json', 'System.Private.CoreLib.dll', 'assets\skin.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot $name) -PathType Leaf)) { throw "Runtime package is incomplete: $name" }
}
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $sourceRoot 'QiaoQiaoAI.Preview.dll')).FileVersion
$parsedVersion = $null
if (-not [Version]::TryParse($version, [ref] $parsedVersion) -or $parsedVersion -lt [Version]'0.6.1.0') {
    throw 'Reset-bounded resident feedback requires runtime version 0.6.1 or later. Install the current complete runtime package.'
}
if (-not [string]::IsNullOrWhiteSpace($CodexPath)) {
    if (-not (Test-Path -LiteralPath $CodexPath -PathType Leaf)) { throw "Codex executable is missing: $CodexPath" }
    $CodexPath = (Resolve-Path -LiteralPath $CodexPath).ProviderPath
}
$sourceFiles = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -Force)
if (@($sourceFiles | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -gt 0) {
    throw 'Runtime source must not contain junctions or symbolic links.'
}
$fingerprintText = (($sourceFiles | Where-Object { -not $_.PSIsContainer } | Sort-Object FullName | ForEach-Object {
    $_.FullName.Substring($sourceRoot.Length) + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
}) -join "`n")
$hasher = [Security.Cryptography.SHA256]::Create()
try { $fingerprint = ([BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($fingerprintText)))).Replace('-', '').ToLowerInvariant() }
finally { $hasher.Dispose() }
$runtimeRoot = Join-Path (Join-Path $DataRoot 'apps') ($version + '-' + $fingerprint.Substring(0, 16))
$installedExe = Join-Path $runtimeRoot 'QiaoQiaoAI.Preview.exe'
$marker = Join-Path $runtimeRoot '.resident-runtime.json'
$null = New-Item -ItemType Directory -Path $DataRoot -Force
$existing = Read-ResidentInstall $DataRoot
if ([string]::IsNullOrWhiteSpace($CodexPath) -and $null -ne $existing) { $CodexPath = $existing.codexPath }
if (Test-Path -LiteralPath $runtimeRoot) {
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) { throw "An incomplete runtime directory already exists; preserve it and choose a fresh DataRoot: $runtimeRoot" }
    $runtimeMarker = Get-Content -LiteralPath $marker -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($runtimeMarker.fingerprint -ne $fingerprint -or -not (Test-Path -LiteralPath $installedExe -PathType Leaf)) { throw 'Resident runtime fingerprint does not match.' }
}
else {
    $stageRoot = Join-Path (Join-Path $DataRoot 'apps') ('.install-' + [Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $stageRoot -Force
    Get-ChildItem -LiteralPath $sourceRoot -Force | Copy-Item -Destination $stageRoot -Recurse
    Write-ResidentJson (Join-Path $stageRoot '.resident-runtime.json') ([ordered]@{ schema = 1; fingerprint = $fingerprint; version = $version })
    if (-not (Test-ResidentChildPath $stageRoot (Join-Path $DataRoot 'apps')) -or -not (Test-ResidentChildPath $runtimeRoot (Join-Path $DataRoot 'apps'))) {
        throw 'Installation staging paths left the intended apps directory.'
    }
    # Complete before publishing the version path. Interrupted staging is preserved,
    # while the next install can use another stage without overwriting running files.
    Move-Item -LiteralPath $stageRoot -Destination $runtimeRoot
}
$null = Disable-ResidentStartup $DataRoot
$startupEnabled = $false
$manifest = [ordered]@{
    schema = 1; kind = 'QiaoQiaoAI.Resident.install'; version = $version; fingerprint = $fingerprint
    executable = $installedExe; codexPath = $CodexPath; dataRoot = $DataRoot
    startupPath = $null; startupEnabled = $false
    installedUtc = [DateTimeOffset]::UtcNow.ToString('o')
}
Write-ResidentJson (Join-Path $DataRoot 'install.json') $manifest
$processId = $null
if (-not $NoLaunch) { $processId = Start-ResidentProcess $installedExe $DataRoot $CodexPath }
[ordered]@{ status = $(if ($NoLaunch) { 'installed' } else { 'launch_requested' }); version = $version; executable = $installedExe; dataRoot = $DataRoot; statusFile = (Join-Path $DataRoot 'status.json'); startupEnabled = $startupEnabled; processId = $processId } | ConvertTo-Json -Compress
