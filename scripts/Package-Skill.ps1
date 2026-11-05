[CmdletBinding(DefaultParameterSetName = 'LocalReview')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'LocalReview')]
    [ValidateNotNullOrEmpty()][string] $PreviewDir,

    [Parameter(Mandatory = $true, ParameterSetName = 'SourceOnly')]
    [switch] $SourceOnly,

    [string] $OutputZip
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath
$skillSource = Join-Path $projectRoot 'skill\qiaoqiao-feedback'
$isSourceOnly = $PSCmdlet.ParameterSetName -eq 'SourceOnly'
if ([string]::IsNullOrWhiteSpace($OutputZip)) {
    $filename = if ($isSourceOnly) { 'QiaoQiaoAI-feedback-skill-source.zip' } else { 'QiaoQiaoAI-feedback-skill-local.zip' }
    $OutputZip = Join-Path $projectRoot ('artifacts\packages\' + $filename)
}
$archivePath = [System.IO.Path]::GetFullPath($OutputZip)
if ([System.IO.Path]::GetExtension($archivePath) -ine '.zip') { throw 'OutputZip must end with .zip.' }
$outputRoot = [System.IO.Path]::GetDirectoryName($archivePath)

function Is-Within([string] $Candidate, [string] $Parent) {
    $parentFull = [System.IO.Path]::GetFullPath($Parent).TrimEnd([char[]]@('\', '/'))
    $candidateFull = [System.IO.Path]::GetFullPath($Candidate)
    return $candidateFull.Equals($parentFull, [StringComparison]::OrdinalIgnoreCase) -or
        $candidateFull.StartsWith($parentFull + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}
function Assert-DirectChild([string] $Candidate, [string] $Parent) {
    if (-not (Is-Within $Candidate $Parent) -or [System.IO.Path]::GetFullPath($Candidate) -eq [System.IO.Path]::GetFullPath($Parent)) {
        throw "Refusing an operation outside output directory: $Candidate"
    }
}
function Require-File([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required package file is missing: $Path" }
}

Require-File (Join-Path $skillSource 'SKILL.md')
foreach ($name in @('Start-Resident.ps1', 'Stop-Resident.ps1', 'Install-Resident.ps1', 'Uninstall-Resident.ps1', 'Resident-Common.ps1')) {
    Require-File (Join-Path $skillSource ('scripts\' + $name))
}
if (Is-Within $outputRoot $skillSource) { throw 'Keep OutputZip outside the skill source directory.' }
$previewRoot = $null
if (-not $isSourceOnly) {
    $previewRoot = (Resolve-Path -LiteralPath $PreviewDir).ProviderPath
    if (Is-Within $outputRoot $previewRoot) { throw 'Keep OutputZip outside PreviewDir.' }
    foreach ($relative in @(
        'QiaoQiaoAI.Preview.exe', 'QiaoQiaoAI.Preview.dll', 'QiaoQiaoAI.UI.dll', 'QiaoQiaoAI.Preview.runtimeconfig.json',
        'System.Private.CoreLib.dll', 'PresentationFramework.dll', 'hostfxr.dll', 'hostpolicy.dll',
        'LICENSE', 'NOTICE', 'licenses\dotnet-runtime-LICENSE.txt', 'licenses\dotnet-runtime-THIRD-PARTY-NOTICES.txt',
        'licenses\windowsdesktop-runtime-LICENSE.txt', 'docs\ASSETS.md', 'docs\RESIDENT.zh-CN.md',
        'assets\skin.json', 'assets\spritesheet.png', 'assets\doubao.png', 'assets\doubao-skin.json',
        'assets\doubao-spritesheet.png', 'assets\deepseek-reference.png', 'assets\deepseek-skin.json',
        'assets\deepseek-spritesheet.png', 'assets\quotes.json'
    )) { Require-File (Join-Path $previewRoot $relative) }
    $runtime = Get-Content -LiteralPath (Join-Path $previewRoot 'QiaoQiaoAI.Preview.runtimeconfig.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $frameworks = @($runtime.runtimeOptions.includedFrameworks | ForEach-Object { $_.name })
    if ($frameworks -notcontains 'Microsoft.NETCore.App' -or $frameworks -notcontains 'Microsoft.WindowsDesktop.App') {
        throw 'PreviewDir must contain a self-contained Windows Desktop runtime.'
    }
    if (@(Get-ChildItem -LiteralPath $previewRoot -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -gt 0) {
        throw 'PreviewDir must be a staged folder, not a tree containing links to other locations.'
    }
    Write-Warning 'This complete skill includes official reference artwork and generated derivatives. It is a LOCAL REVIEW package, not a cleared public release.'
}

$null = New-Item -ItemType Directory -Path $outputRoot -Force
$outputRoot = (Resolve-Path -LiteralPath $outputRoot).ProviderPath
$token = [Guid]::NewGuid().ToString('N')
$stageRoot = Join-Path $outputRoot ('.skill-stage-' + $token)
$temporary = Join-Path $outputRoot ('.skill-' + $token + '.tmp.zip')
$skillTarget = Join-Path $stageRoot 'qiaoqiao-feedback'
Assert-DirectChild $stageRoot $outputRoot
Assert-DirectChild $temporary $outputRoot
try {
    $null = New-Item -ItemType Directory -Path $skillTarget -Force
    Copy-Item -LiteralPath (Join-Path $skillSource 'SKILL.md') -Destination (Join-Path $skillTarget 'SKILL.md')
    foreach ($folder in @('scripts', 'references', 'agents')) {
        $source = Join-Path $skillSource $folder
        if (Test-Path -LiteralPath $source -PathType Container) {
            if (@(Get-ChildItem -LiteralPath $source -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -gt 0) { throw "Skill source contains links: $folder" }
            Copy-Item -LiteralPath $source -Destination (Join-Path $skillTarget $folder) -Recurse
        }
    }
    # Source packages contain only authored skill files and textual project documentation.
    # Never copy skill/assets or the project's runtime-assets/artifacts directories here.
    foreach ($name in @('README.zh-CN.md', 'CONTRIBUTING.md', 'LICENSE', 'NOTICE')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination (Join-Path $skillTarget $name)
    }
    $docTarget = Join-Path $skillTarget 'docs'
    $null = New-Item -ItemType Directory -Path $docTarget
    foreach ($name in @('INSTALL.zh-CN.md', 'ASSETS.md', 'RESIDENT.zh-CN.md', 'FEEDBACK.zh-CN.md', 'VALIDATION.md', 'DIALOGUE-SOURCES.md', 'DOUBAO-ANIMATION-PROMPT.txt', 'DEEPSEEK-ANIMATION-PROMPT.txt')) {
        $source = Join-Path $projectRoot ('docs\' + $name)
        if (Test-Path -LiteralPath $source -PathType Leaf) { Copy-Item -LiteralPath $source -Destination (Join-Path $docTarget $name) }
    }

    if ($isSourceOnly) {
        @'
SKILL SOURCE REVIEW PACKAGE

Contains the skill, scripts and textual documentation only. It contains no
desktop executable, runtime or character artwork, and cannot launch the pet.
Use the separate complete local-review package for an installed experience.
'@ | Set-Content -LiteralPath (Join-Path $skillTarget 'PACKAGE-STATUS.txt') -Encoding UTF8
    }
    else {
        $desktopTarget = Join-Path $skillTarget 'assets\desktop'
        $null = New-Item -ItemType Directory -Path $desktopTarget -Force
        Get-ChildItem -LiteralPath $previewRoot -Force | Copy-Item -Destination $desktopTarget -Recurse -Force
        Require-File (Join-Path $desktopTarget 'QiaoQiaoAI.Preview.exe')
        @'
COMPLETE LOCAL REVIEW SKILL - NOT CLEARED FOR PUBLIC REDISTRIBUTION

Copy the entire qiaoqiao-feedback folder to your Codex skills directory.
Say "启动人工智障修理厂" in Codex to install and start the desktop pet on demand.
Say "关闭人工智障修理厂" to close it; ask again whenever you want it back.
It does not start when you sign in to Windows or open Codex.
It follows the latest human message among local Codex chats. The first quiet
period queues at most one feedback message per Reset cycle; no Send button.
Further hits and pauses never send again until Reset. This lock survives restart.
Reset cancels unsent hits and starts a fresh cycle; it cannot retract queued messages.
Queue acceptance is not an AI reply. An unloaded chat may need to be opened.
The bundled desktop runtime is at assets/desktop/QiaoQiaoAI.Preview.exe.
No separate executable, API key, or VPet installation is required.

Official reference images and generated derivatives remain pending public
redistribution clearance. See docs/ASSETS.md and docs/RESIDENT.zh-CN.md.
'@ | Set-Content -LiteralPath (Join-Path $skillTarget 'PACKAGE-STATUS.txt') -Encoding UTF8
    }
    $manifest = foreach ($file in (Get-ChildItem -LiteralPath $skillTarget -File -Recurse | Sort-Object FullName)) {
        '{0}  {1}' -f (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $file.FullName.Substring($skillTarget.Length + 1).Replace('\', '/')
    }
    $manifest | Set-Content -LiteralPath (Join-Path $skillTarget 'SHA256SUMS.txt') -Encoding UTF8
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stageRoot, $temporary, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    $zip = [System.IO.Compression.ZipFile]::OpenRead($temporary)
    try {
        $names = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        if (@($names | Where-Object { -not $_.StartsWith('qiaoqiao-feedback/', [StringComparison]::Ordinal) -or $_.Contains('/../') }).Count -gt 0) { throw 'Invalid ZIP root layout.' }
        if (@($names | Where-Object { $_ -ceq 'qiaoqiao-feedback/SKILL.md' }).Count -ne 1) { throw 'ZIP must contain one root skill entrypoint.' }
        $exeEntry = 'qiaoqiao-feedback/assets/desktop/QiaoQiaoAI.Preview.exe'
        if (-not $isSourceOnly -and @($names | Where-Object { $_ -ceq $exeEntry }).Count -ne 1) { throw 'ZIP is missing the default desktop executable.' }
        if ($isSourceOnly -and @($names | Where-Object { $_ -notmatch '(\.(md|txt|ps1|py|yaml|yml|json)$|/(LICENSE|NOTICE)$|/$)' }).Count -gt 0) { throw 'Source package unexpectedly contains files outside the text-only allowlist.' }
    }
    finally { $zip.Dispose() }
    Move-Item -LiteralPath $temporary -Destination $archivePath -Force
    Write-Output $archivePath
}
finally {
    if (Test-Path -LiteralPath $temporary -PathType Leaf) {
        Assert-DirectChild ((Resolve-Path -LiteralPath $temporary).ProviderPath) $outputRoot
        Remove-Item -LiteralPath $temporary -Force
    }
    if (Test-Path -LiteralPath $stageRoot -PathType Container) {
        $resolved = (Resolve-Path -LiteralPath $stageRoot).ProviderPath
        Assert-DirectChild $resolved $outputRoot
        if ([IO.Path]::GetFileName($resolved) -ne ('.skill-stage-' + $token)) { throw 'Refusing to remove an unexpected staging directory.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
