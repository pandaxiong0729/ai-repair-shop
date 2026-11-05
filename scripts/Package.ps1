[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $BuildDir,

    [ValidateNotNullOrEmpty()]
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\packages'),

    [string] $AssetsDir,

    [switch] $IncludeOfficialAssets
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-ChildPath {
    param([string] $Candidate, [string] $Parent)
    $parentFull = [System.IO.Path]::GetFullPath($Parent).TrimEnd([char[]]@('\', '/'))
    $childFull = [System.IO.Path]::GetFullPath($Candidate)
    if (-not $childFull.StartsWith($parentFull + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing an operation outside output directory: $childFull"
    }
}

$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath
$buildRoot = (Resolve-Path -LiteralPath $BuildDir).ProviderPath
$assemblyNames = @('QiaoQiaoAI.Plugin.dll', 'QiaoQiaoAI.UI.dll')
foreach ($assemblyName in $assemblyNames) {
    if (-not (Test-Path -LiteralPath (Join-Path $buildRoot $assemblyName) -PathType Leaf)) {
        throw "Missing plugin build output: $assemblyName. Run scripts/Build.ps1 first."
    }
}
$metadata = Join-Path $projectRoot 'packaging\info.lps'
if (-not (Test-Path -LiteralPath $metadata -PathType Leaf)) {
    throw "Missing package metadata: $metadata"
}

$assetRoot = $null
if ($IncludeOfficialAssets) {
    if ([string]::IsNullOrWhiteSpace($AssetsDir)) {
        throw 'AssetsDir is required when IncludeOfficialAssets is set.'
    }
    $assetRoot = (Resolve-Path -LiteralPath $AssetsDir).ProviderPath
    foreach ($requiredAsset in @('skin.json', 'spritesheet.png', 'doubao.png', 'doubao-skin.json', 'doubao-spritesheet.png', 'deepseek-reference.png', 'deepseek-skin.json', 'deepseek-spritesheet.png')) {
        if (-not (Test-Path -LiteralPath (Join-Path $assetRoot $requiredAsset) -PathType Leaf)) {
            throw "Missing required local review asset: $requiredAsset"
        }
    }
    $skin = Get-Content -LiteralPath (Join-Path $assetRoot 'skin.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($skin.image -cne 'spritesheet.png') {
        throw 'The review package requires skin.json image to be spritesheet.png.'
    }
    $doubaoSkin = Get-Content -LiteralPath (Join-Path $assetRoot 'doubao-skin.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($doubaoSkin.image -cne 'doubao-spritesheet.png') { throw 'doubao-skin.json must refer to doubao-spritesheet.png.' }
    $whaleSkin = Get-Content -LiteralPath (Join-Path $assetRoot 'deepseek-skin.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($whaleSkin.image -cne 'deepseek-spritesheet.png') { throw 'deepseek-skin.json must refer to deepseek-spritesheet.png.' }
    Write-Warning 'Including official artwork for LOCAL REVIEW. This does not establish redistribution permission. Do not treat this ZIP as a cleared public release.'
}

$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$outputRoot = (Resolve-Path -LiteralPath $OutputDirectory).ProviderPath
$packageName = if ($IncludeOfficialAssets) { 'QiaoQiaoAI-local-review' } else { 'QiaoQiaoAI-code-only' }
$token = [Guid]::NewGuid().ToString('N')
$stageRoot = Join-Path $outputRoot ('.package-stage-' + $token)
$temporaryArchive = Join-Path $outputRoot ('.' + $packageName + '-' + $token + '.tmp.zip')
$archivePath = Join-Path $outputRoot ($packageName + '.zip')
Assert-ChildPath -Candidate $stageRoot -Parent $outputRoot
Assert-ChildPath -Candidate $temporaryArchive -Parent $outputRoot
Assert-ChildPath -Candidate $archivePath -Parent $outputRoot

try {
    $modRoot = Join-Path $stageRoot 'QiaoQiaoAI'
    $pluginRoot = Join-Path $modRoot 'plugin'
    $targetAssets = Join-Path $modRoot 'assets'
    $null = New-Item -ItemType Directory -Path $pluginRoot -Force
    $null = New-Item -ItemType Directory -Path $targetAssets -Force

    Copy-Item -LiteralPath $metadata -Destination (Join-Path $modRoot 'info.lps')
    foreach ($assemblyName in $assemblyNames) {
        Copy-Item -LiteralPath (Join-Path $buildRoot $assemblyName) -Destination (Join-Path $pluginRoot $assemblyName)
    }
    foreach ($document in @('README.zh-CN.md', 'LICENSE', 'NOTICE', 'CONTRIBUTING.md')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot $document) -Destination (Join-Path $modRoot $document)
    }
    $targetDocs = Join-Path $modRoot 'docs'
    $null = New-Item -ItemType Directory -Path $targetDocs
    # Explicit allowlist: no recorded media, artwork, host binaries or user data.
    foreach ($document in @('INSTALL.zh-CN.md', 'ASSETS.md', 'VALIDATION.md', 'FEEDBACK.zh-CN.md', 'DIALOGUE-SOURCES.md', 'DOUBAO-ANIMATION-PROMPT.txt', 'DEEPSEEK-ANIMATION-PROMPT.txt')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot ('docs\' + $document)) -Destination (Join-Path $targetDocs $document)
    }
    $testResults = Join-Path $targetDocs 'test-results'
    $null = New-Item -ItemType Directory -Path $testResults -Force
    foreach ($report in @('ui-tests.json', 'integration-tests.json', 'feedback-tests.json', 'package-checks.json', 'ondemand-startup-checks.json')) {
        $source = Join-Path $projectRoot ('docs/test-results/' + $report)
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $testResults $report) }
    }

    if ($IncludeOfficialAssets) {
        foreach ($asset in @('skin.json', 'spritesheet.png', 'doubao.png', 'doubao-skin.json', 'doubao-spritesheet.png', 'deepseek-reference.png', 'deepseek-skin.json', 'deepseek-spritesheet.png', 'quotes.json', 'dialogue.json', 'effects.json')) {
            $source = Join-Path $assetRoot $asset
            if (Test-Path -LiteralPath $source -PathType Leaf) {
                Copy-Item -LiteralPath $source -Destination (Join-Path $targetAssets $asset)
            }
        }
        @'
LOCAL REVIEW PACKAGE - NOT CLEARED FOR PUBLIC REDISTRIBUTION

This package contains OpenAI Codex character artwork, official Doubao and DeepSeek
references and project-generated expressive derivatives. Permission to redistribute that artwork has not
been established. Apache-2.0 applies to original project code, not the artwork.
Keep this package and derived character recordings for local review until
the conditions documented in docs/ASSETS.md are resolved.
'@ | Set-Content -LiteralPath (Join-Path $modRoot 'PACKAGE-STATUS.txt') -Encoding UTF8
    }
    else {
        @'
CODE-ONLY COMPONENT PACKAGE - INCOMPLETE CHARACTER EXPERIENCE

Official Codex, Doubao and DeepSeek artwork is intentionally absent. This package cannot display
the intended characters without separately supplied assets that you have the
right to use. It is not the completed, artwork-inclusive product.
See docs/ASSETS.md for the asset format and outstanding release conditions.
'@ | Set-Content -LiteralPath (Join-Path $modRoot 'PACKAGE-STATUS.txt') -Encoding UTF8
        'Required: skin.json, spritesheet.png, doubao-skin.json, doubao-spritesheet.png, deepseek-skin.json and deepseek-spritesheet.png. doubao.png and deepseek-reference.png are references. See ../docs/ASSETS.md.' |
            Set-Content -LiteralPath (Join-Path $targetAssets 'README.txt') -Encoding UTF8
    }

    $manifest = foreach ($file in (Get-ChildItem -LiteralPath $modRoot -File -Recurse | Sort-Object FullName)) {
        $relative = $file.FullName.Substring($modRoot.Length + 1).Replace('\', '/')
        '{0}  {1}' -f (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $relative
    }
    $manifest | Set-Content -LiteralPath (Join-Path $modRoot 'SHA256SUMS.txt') -Encoding UTF8

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stageRoot, $temporaryArchive,
        [System.IO.Compression.CompressionLevel]::Optimal, $false)
    Move-Item -LiteralPath $temporaryArchive -Destination $archivePath -Force
    Write-Output $archivePath
}
finally {
    if (Test-Path -LiteralPath $temporaryArchive -PathType Leaf) {
        Assert-ChildPath -Candidate ((Resolve-Path -LiteralPath $temporaryArchive).ProviderPath) -Parent $outputRoot
        Remove-Item -LiteralPath $temporaryArchive -Force
    }
    if (Test-Path -LiteralPath $stageRoot -PathType Container) {
        $resolvedStage = (Resolve-Path -LiteralPath $stageRoot).ProviderPath
        Assert-ChildPath -Candidate $resolvedStage -Parent $outputRoot
        if ([System.IO.Path]::GetFileName($resolvedStage) -ne ('.package-stage-' + $token)) {
            throw "Refusing to remove an unexpected staging directory: $resolvedStage"
        }
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
