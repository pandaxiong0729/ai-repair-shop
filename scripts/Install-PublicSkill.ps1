#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $SkillRoot = (Join-Path $env:USERPROFILE '.codex\skills'),
    [switch] $Start
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # Avoid Windows PowerShell progress overhead for large downloads.
$releaseUrl = 'https://github.com/pandaxiong0729/ai-repair-shop/releases/download/v0.7.0'
$archiveName = 'AI-Repair-Shop-Codex-Skill.zip'
$skillName = 'qiaoqiao-feedback'

function Assert-CompleteSkill([string] $Path) {
    $required = @(
        'SKILL.md', 'scripts\Start-Resident.ps1', 'scripts\Stop-Resident.ps1',
        'scripts\Resident-Common.ps1', 'scripts\Install-Resident.ps1', 'scripts\Uninstall-Resident.ps1',
        'assets\desktop\QiaoQiaoAI.Preview.exe', 'assets\desktop\QiaoQiaoAI.Preview.dll',
        'assets\desktop\QiaoQiaoAI.UI.dll', 'assets\desktop\QiaoQiaoAI.Preview.runtimeconfig.json',
        'assets\desktop\hostfxr.dll', 'assets\desktop\hostpolicy.dll',
        'assets\desktop\System.Private.CoreLib.dll', 'assets\desktop\PresentationFramework.dll'
    )
    foreach ($directory in @($Path, (Join-Path $Path 'scripts'), (Join-Path $Path 'assets'), (Join-Path $Path 'assets\desktop'))) {
        $item = Get-Item -LiteralPath $directory -Force
        if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Expected a regular skill directory: $directory"
        }
    }
    foreach ($relative in $required) {
        $file = Get-Item -LiteralPath (Join-Path $Path $relative) -Force
        if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $file.Length -eq 0) {
            throw "Invalid skill package file: $relative"
        }
    }
    $metadata = Get-Content -LiteralPath (Join-Path $Path 'SKILL.md') -Encoding UTF8 -Raw
    $assembly = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $Path 'assets\desktop\QiaoQiaoAI.Preview.dll'))
    if ($metadata -notmatch '(?m)^name:\s*qiaoqiao-feedback\s*$' -or $assembly.Name -cne 'QiaoQiaoAI.Preview') {
        throw "Unrecognized skill in $Path"
    }
}

function Install-VerifiedArchive([string] $Stage, [string] $Target) {
    $archive = Join-Path $Stage $archiveName
    $manifest = Join-Path $Stage 'SHA256SUMS.txt'
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -UseBasicParsing -Uri "$releaseUrl/SHA256SUMS.txt" -OutFile $manifest
    $entryPattern = '^([a-fA-F0-9]{64})\s+\*?' + [regex]::Escape($archiveName) + '$'
    $entries = @(Get-Content -LiteralPath $manifest -Encoding UTF8 | Where-Object { $_ -cmatch $entryPattern })
    if ($entries.Count -ne 1) { throw "Expected a single SHA-256 entry for $archiveName." }
    $expected = $entries[0].Substring(0, 64)
    Invoke-WebRequest -UseBasicParsing -Uri "$releaseUrl/$archiveName" -OutFile $archive
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $expected) {
        throw "SHA-256 mismatch for $archiveName."
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if (-not $name.StartsWith("$skillName/", [StringComparison]::Ordinal) -or
                $name -match '(^|/)(\.{1,2}|[^/]*[. ])(/|$)|[<>:"|?*\x00-\x1f]|//') {
                throw "Unexpected path in skill archive: $name"
            }
        }
    } finally { $zip.Dispose() }
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $Stage)
    $source = (Resolve-Path -LiteralPath (Join-Path $Stage $skillName)).ProviderPath
    Assert-CompleteSkill $source
    if ([IO.Path]::GetDirectoryName($source) -ine $Stage -or [IO.Path]::GetDirectoryName($Target) -ine $SkillRoot) {
        throw 'Unexpected installation path.'
    }
    # Both paths are on the same volume. Move refuses to overwrite an existing target.
    try { [IO.Directory]::Move($source, $Target) }
    catch [IO.IOException] {
        if (-not (Test-Path -LiteralPath $Target -PathType Container)) { throw }
        Assert-CompleteSkill $Target # Another installer finished first.
    }
}

if ([string]::IsNullOrWhiteSpace($SkillRoot)) { throw 'SkillRoot must be a directory.' }
$SkillRoot = [IO.Path]::GetFullPath($SkillRoot)
if ($SkillRoot.TrimEnd('\') -eq [IO.Path]::GetPathRoot($SkillRoot).TrimEnd('\')) {
    throw 'SkillRoot must name a directory below the drive root.'
}
$null = [IO.Directory]::CreateDirectory($SkillRoot)
$SkillRoot = (Resolve-Path -LiteralPath $SkillRoot).ProviderPath.TrimEnd('\')
$target = Join-Path $SkillRoot $skillName
if (Test-Path -LiteralPath $target) {
    try { Assert-CompleteSkill $target }
    catch { throw "An existing item at $target could not be verified as a complete AI Repair Shop skill. It was preserved. $($_.Exception.Message)" }
    Write-Output "Using existing AI Repair Shop skill: $target"
} else {
    $stageName = '.qq-install-' + [Guid]::NewGuid().ToString('N')
    $stage = (New-Item -ItemType Directory -Path (Join-Path $SkillRoot $stageName)).FullName
    try {
        Install-VerifiedArchive $stage $target
        Write-Output "Installed AI Repair Shop v0.7.0: $target"
    } finally {
        $stageItem = Get-Item -LiteralPath $stage -Force
        if ($stageItem.Parent.FullName -ieq $SkillRoot -and $stageItem.Name -ceq $stageName -and
            -not ($stageItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            Remove-Item -LiteralPath $stageItem.FullName -Recurse -Force
        }
    }
}
if ($Start) { & (Join-Path $target 'scripts\Start-Resident.ps1') }
