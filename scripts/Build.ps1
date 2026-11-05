param(
    [string]$DotnetPath = 'dotnet',
    [Parameter(Mandatory = $true)][string]$VPetSourceRoot,
    [switch]$SelfContainedPreview
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$VPetSourceRoot = (Resolve-Path -LiteralPath $VPetSourceRoot).Path
$pluginProject = Join-Path $root 'src/QiaoQiaoAI.Plugin/QiaoQiaoAI.Plugin.csproj'
$previewProject = Join-Path $root 'src/QiaoQiaoAI.Preview/QiaoQiaoAI.Preview.csproj'
& $DotnetPath build $pluginProject -c Release "-p:VPetSourceRoot=$VPetSourceRoot" -o (Join-Path $root 'artifacts/plugin')
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
$previewArgs = @('publish', $previewProject, '-c', 'Release', '-o', (Join-Path $root 'artifacts/preview'))
if ($SelfContainedPreview) { $previewArgs += @('-r', 'win-x64', '--self-contained', 'true') }
else { $previewArgs += @('--self-contained', 'false') }
& $DotnetPath @previewArgs
if ($LASTEXITCODE -ne 0) { throw 'Preview build failed.' }
Write-Output 'Build completed. Official character assets are packaged separately.'
