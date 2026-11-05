# On Windows: .\scripts\Package-Public.ps1
# Use -Force to replace an existing release in the dedicated output directory.
[CmdletBinding()]
param(
    [string]$DotnetPath = 'dotnet',
    [string]$RuntimePackagesRoot,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\public'),
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$output = [IO.Path]::GetFullPath($output).TrimEnd('\', '/')
$parent = [IO.Path]::GetDirectoryName($output)
if (-not $parent) { throw 'Use a dedicated output directory.' }
$null = [IO.Directory]::CreateDirectory($parent)
$releaseFiles = @('AI-Repair-Shop-Windows-x64.zip', 'AI-Repair-Shop-Codex-Skill.zip', 'SHA256SUMS.txt')
$token = [Guid]::NewGuid().ToString('N')
$stage = Join-Path $parent ('.public-stage-' + $token)
$desktop = Join-Path $stage 'AI-Repair-Shop'
$skill = Join-Path $stage 'qiaoqiao-feedback'
$release = Join-Path $stage 'release'
$preserveStage = $false
$utf8 = [Text.UTF8Encoding]::new($false)
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Copy-File([string]$Source, [string]$Destination) {
    $item = Get-Item -LiteralPath $Source
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Expected a regular file: $Source" }
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Destination))
    [IO.File]::Copy($item.FullName, $Destination)
}
function Add-TreeToZip($Zip, [string]$Directory, [string]$Prefix) {
    # Stable ordering, path separators and timestamps keep packaging reproducible.
    [string[]]$files = [IO.Directory]::GetFiles($Directory, '*', [IO.SearchOption]::AllDirectories)
    [Array]::Sort($files, [StringComparer]::Ordinal)
    foreach ($file in $files) {
        $name = $Prefix + '/' + $file.Substring($Directory.Length + 1).Replace('\', '/')
        $entry = $Zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        $entryStream = $entry.Open()
        try {
            $source = [IO.File]::OpenRead($file)
            try { $source.CopyTo($entryStream) } finally { $source.Dispose() }
        } finally { $entryStream.Dispose() }
    }
}
function Assert-ReleaseDestination {
    if (-not (Test-Path -LiteralPath $output)) { return }
    $dir = Get-Item -LiteralPath $output
    if (-not $dir.PSIsContainer -or ($dir.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Expected a regular output directory: $output" }
    $entries = @(Get-ChildItem -LiteralPath $output -Force)
    if ($entries.Count -eq 0) { return }
    if (-not $Force) { throw 'The output directory is occupied. Use a new directory or -Force for an existing public release.' }
    if ($entries.Count -ne 3 -or @($entries | Where-Object {
        $_.PSIsContainer -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $_.Name -cnotin $releaseFiles
    }).Count) { throw 'Refusing to replace unrelated files. Choose a dedicated public release directory.' }
}

Assert-ReleaseDestination
$null = [IO.Directory]::CreateDirectory($release)
try {
    $build = Join-Path $stage 'build'
    $project = Join-Path $root 'src/QiaoQiaoAI.Preview/QiaoQiaoAI.Preview.csproj'
    $arguments = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '--artifacts-path', $build, '-o', $desktop, '-p:DebugType=None', '-p:DebugSymbols=false',
        '-p:DefaultItemExcludesInProjectFolder=bin/**%3Bobj/**')
    if ($RuntimePackagesRoot) {
        $packageRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($RuntimePackagesRoot)
        $arguments += "-p:RestorePackagesPath=$packageRoot"
    }
    & $DotnetPath @arguments
    if ($LASTEXITCODE -ne 0) { throw 'The win-x64 self-contained publish failed.' }

    # Reject any content that did not belong to the clean desktop publish.
    foreach ($item in (Get-ChildItem -LiteralPath $desktop -Recurse -Force)) {
        $name = $item.FullName.Substring($desktop.Length + 1).Replace('\', '/')
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Link in publish output: $name" }
        if ($item.PSIsContainer) { continue }
        if ($name -cnotmatch '^(?:[^/]+\.dll|(?:[a-z]{2}(?:-[A-Za-z]+)?)/[^/]+\.resources\.dll|createdump\.exe|QiaoQiaoAI\.Preview\.(?:exe|deps\.json|runtimeconfig\.json))$') {
            throw "Unexpected publish file: $name"
        }
    }
    foreach ($name in @('QiaoQiaoAI.Preview.exe', 'QiaoQiaoAI.Preview.dll', 'QiaoQiaoAI.UI.dll',
        'QiaoQiaoAI.Preview.deps.json', 'PresentationFramework.dll', 'System.Private.CoreLib.dll',
        'hostfxr.dll', 'hostpolicy.dll', 'SkiaSharp.dll', 'libSkiaSharp.dll')) {
        if (-not [IO.File]::Exists((Join-Path $desktop $name))) { throw "Missing published file: $name" }
    }
    $frameworks = (Get-Content -Raw -LiteralPath (Join-Path $desktop 'QiaoQiaoAI.Preview.runtimeconfig.json') |
        ConvertFrom-Json).runtimeOptions.includedFrameworks
    $coreVersion = ($frameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
    $desktopVersion = ($frameworks | Where-Object name -eq 'Microsoft.WindowsDesktop.App').version
    $assetsFile = Join-Path $build 'obj/QiaoQiaoAI.Preview/project.assets.json'
    $restore = Get-Content -Raw -LiteralPath $assetsFile | ConvertFrom-Json
    $licenseFiles = @(
        @("microsoft.netcore.app.runtime.win-x64/$coreVersion/LICENSE.TXT", 'dotnet-runtime-LICENSE.txt'),
        @("microsoft.netcore.app.runtime.win-x64/$coreVersion/THIRD-PARTY-NOTICES.TXT", 'dotnet-runtime-THIRD-PARTY-NOTICES.txt'),
        @("microsoft.windowsdesktop.app.runtime.win-x64/$desktopVersion/LICENSE", 'windowsdesktop-runtime-LICENSE.txt'),
        @('skiasharp/3.116.1/LICENSE.txt', 'SkiaSharp-LICENSE.txt'),
        @('skiasharp.nativeassets.win32/3.116.1/LICENSE.txt', 'SkiaSharp-NativeAssets-Win32-LICENSE.txt'),
        @('skiasharp.nativeassets.win32/3.116.1/THIRD-PARTY-NOTICES.txt', 'SkiaSharp-NativeAssets-Win32-THIRD-PARTY-NOTICES.txt')
    )
    foreach ($row in $licenseFiles) {
        $candidates = @($restore.packageFolders.PSObject.Properties.Name | ForEach-Object { Join-Path $_ $row[0] } |
            Where-Object { [IO.File]::Exists($_) })
        if (-not $candidates.Count) { throw "Required license missing from the restored packages: $($row[0])" }
        Copy-File $candidates[0] (Join-Path $desktop ('licenses/' + $row[1]))
    }
    foreach ($name in @('skin.json', 'quotes.json')) {
        Copy-File (Join-Path $root ('runtime-assets/codex/' + $name)) (Join-Path $desktop ('assets/' + $name))
    }
    [IO.File]::WriteAllText((Join-Path $desktop 'assets/public-codex.json'), '{"schema":1}', $utf8)
    foreach ($name in @('LICENSE', 'NOTICE')) {
        Copy-File (Join-Path $root $name) (Join-Path $desktop $name)
        Copy-File (Join-Path $root $name) (Join-Path $skill $name)
    }
    Copy-File (Join-Path $root 'packaging/public/Windows.zh-CN.txt') (Join-Path $desktop 'START-HERE.zh-CN.txt')
    Copy-File (Join-Path $root 'packaging/public/Skill.zh-CN.txt') (Join-Path $skill 'START-HERE.zh-CN.txt')
    foreach ($name in @('SKILL.md', 'agents/openai.yaml', 'references/protocol.md', 'references/resident.md',
        'scripts/Install-Resident.ps1', 'scripts/Read-Feedback.ps1', 'scripts/Resident-Common.ps1',
        'scripts/Start-Feedback.ps1', 'scripts/Start-Resident.ps1', 'scripts/Stop-Resident.ps1',
        'scripts/Test-Bridge.ps1', 'scripts/Uninstall-Resident.ps1')) {
        Copy-File (Join-Path $root ('skill/qiaoqiao-feedback/' + $name)) (Join-Path $skill $name)
    }
    $hashes = foreach ($file in $releaseFiles[0..1]) {
        $path = Join-Path $release $file
        $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
        try {
            if ($file -eq $releaseFiles[0]) { Add-TreeToZip $zip $desktop 'AI-Repair-Shop' }
            else {
                Add-TreeToZip $zip $skill 'qiaoqiao-feedback'
                Add-TreeToZip $zip $desktop 'qiaoqiao-feedback/assets/desktop'
            }
        } finally { $zip.Dispose() }
        '{0}  {1}' -f (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant(), $file
    }
    [IO.File]::WriteAllLines((Join-Path $release 'SHA256SUMS.txt'), [string[]]$hashes, $utf8)

    # All three files are complete before replacing the dedicated release directory.
    $lockPath = Join-Path $parent ('.' + [IO.Path]::GetFileName($output) + '.public-pack.lock')
    $lock = [IO.FileStream]::new($lockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None, 1, [IO.FileOptions]::DeleteOnClose)
    try {
        Assert-ReleaseDestination
        $previous = Join-Path $stage 'previous'
        if ([IO.Directory]::Exists($output)) {
            $resolvedOutput = (Resolve-Path -LiteralPath $output).ProviderPath
            if ($resolvedOutput -ne $output -or [IO.Path]::GetDirectoryName($resolvedOutput) -ne $parent) { throw 'Unexpected output path.' }
            [IO.Directory]::Move($resolvedOutput, $previous)
        }
        try { [IO.Directory]::Move($release, $output) }
        catch {
            if ([IO.Directory]::Exists($previous)) {
                $preserveStage = $true
                [IO.Directory]::Move($previous, $output)
                $preserveStage = $false
            }
            throw
        }
    } finally { $lock.Dispose() }
    foreach ($name in $releaseFiles) { Write-Output (Join-Path $output $name) }
} finally {
    $resolved = (Resolve-Path -LiteralPath $stage).ProviderPath
    if ($resolved -ne $stage -or [IO.Path]::GetDirectoryName($resolved) -ne $parent -or
        [IO.Path]::GetFileName($resolved) -ne ('.public-stage-' + $token)) { throw 'Unexpected staging path.' }
    if ($preserveStage) { Write-Warning "Previous release preserved at $resolved\previous" }
    else { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
