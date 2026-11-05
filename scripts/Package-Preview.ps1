[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BuildDir,
    [Parameter(Mandatory = $true)][string]$AssetsDir,
    [Parameter(Mandatory = $true)][string]$RuntimePackagesRoot,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\packages'),
    [ValidateSet('QiaoQiaoAI-toy-local', 'QiaoQiaoAI-preview-local')]
    [string]$PackageName = 'QiaoQiaoAI-toy-local'
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$buildRoot = (Resolve-Path -LiteralPath $BuildDir).Path
$assetsRoot = (Resolve-Path -LiteralPath $AssetsDir).Path
$packagesRoot = (Resolve-Path -LiteralPath $RuntimePackagesRoot).Path
$runtime = Get-Content -LiteralPath (Join-Path $buildRoot 'QiaoQiaoAI.Preview.runtimeconfig.json') -Raw | ConvertFrom-Json
$coreVersion = ($runtime.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
$desktopVersion = ($runtime.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.WindowsDesktop.App').version
if (-not $coreVersion -or -not $desktopVersion) { throw 'Build a self-contained preview first: scripts/Build.ps1 -SelfContainedPreview.' }
foreach ($file in @('skin.json', 'spritesheet.png', 'doubao.png', 'doubao-skin.json', 'doubao-spritesheet.png', 'deepseek-reference.png', 'deepseek-skin.json', 'deepseek-spritesheet.png')) {
    if (-not (Test-Path -LiteralPath (Join-Path $assetsRoot $file))) { throw "Missing asset: $file" }
}
$skin = Get-Content -LiteralPath (Join-Path $assetsRoot 'skin.json') -Raw | ConvertFrom-Json
if ($skin.image -cne 'spritesheet.png') { throw 'skin.json must refer to spritesheet.png.' }
$doubaoSkin = Get-Content -LiteralPath (Join-Path $assetsRoot 'doubao-skin.json') -Raw | ConvertFrom-Json
if ($doubaoSkin.image -cne 'doubao-spritesheet.png') { throw 'doubao-skin.json must refer to doubao-spritesheet.png.' }
$whaleSkin = Get-Content -LiteralPath (Join-Path $assetsRoot 'deepseek-skin.json') -Raw | ConvertFrom-Json
if ($whaleSkin.image -cne 'deepseek-spritesheet.png') { throw 'deepseek-skin.json must refer to deepseek-spritesheet.png.' }
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$outputRoot = (Resolve-Path -LiteralPath $OutputDirectory).Path
if ($outputRoot.StartsWith($buildRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or $outputRoot -eq $buildRoot) {
    throw 'Keep OutputDirectory outside BuildDir.'
}
$token = [Guid]::NewGuid().ToString('N')
$stage = Join-Path $outputRoot ('.preview-stage-' + $token)
$target = Join-Path $stage $PackageName
$archive = Join-Path $outputRoot ($PackageName + '.zip')
$null = New-Item -ItemType Directory -Path $target -Force
try {
    Get-ChildItem -LiteralPath $buildRoot -Force | Where-Object Name -ne 'assets' | Copy-Item -Destination $target -Recurse -Force
    $null = New-Item -ItemType Directory -Path (Join-Path $target 'assets'), (Join-Path $target 'licenses'), (Join-Path $target 'docs') -Force
    foreach ($file in @('skin.json', 'spritesheet.png', 'doubao.png', 'doubao-skin.json', 'doubao-spritesheet.png', 'deepseek-reference.png', 'deepseek-skin.json', 'deepseek-spritesheet.png', 'quotes.json', 'dialogue.json', 'effects.json')) {
        $source = Join-Path $assetsRoot $file
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $target ('assets/' + $file)) }
    }
    foreach ($file in @('README.zh-CN.md', 'CONTRIBUTING.md', 'LICENSE', 'NOTICE')) {
        Copy-Item -LiteralPath (Join-Path $root $file) -Destination (Join-Path $target $file) -Force
    }
    foreach ($file in @('INSTALL.zh-CN.md', 'ASSETS.md', 'VALIDATION.md', 'RESIDENT.zh-CN.md', 'FEEDBACK.zh-CN.md', 'DIALOGUE-SOURCES.md', 'DOUBAO-ANIMATION-PROMPT.txt', 'DEEPSEEK-ANIMATION-PROMPT.txt')) {
        Copy-Item -LiteralPath (Join-Path $root ('docs/' + $file)) -Destination (Join-Path $target ('docs/' + $file)) -Force
    }
    $null = New-Item -ItemType Directory -Path (Join-Path $target 'docs/test-results') -Force
    foreach ($report in @('ui-tests.json', 'integration-tests.json', 'feedback-tests.json', 'package-checks.json', 'ondemand-startup-checks.json')) {
        $source = Join-Path $root ('docs/test-results/' + $report)
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $target ('docs/test-results/' + $report)) -Force }
    }
    Copy-Item -LiteralPath (Join-Path $packagesRoot "microsoft.netcore.app.runtime.win-x64/$coreVersion/LICENSE.TXT") -Destination (Join-Path $target 'licenses/dotnet-runtime-LICENSE.txt') -Force
    Copy-Item -LiteralPath (Join-Path $packagesRoot "microsoft.netcore.app.runtime.win-x64/$coreVersion/THIRD-PARTY-NOTICES.TXT") -Destination (Join-Path $target 'licenses/dotnet-runtime-THIRD-PARTY-NOTICES.txt') -Force
    Copy-Item -LiteralPath (Join-Path $packagesRoot "microsoft.windowsdesktop.app.runtime.win-x64/$desktopVersion/LICENSE") -Destination (Join-Path $target 'licenses/windowsdesktop-runtime-LICENSE.txt') -Force
    @'
人工智障修理厂 · v0.6.1 离线桌宠玩具

请完整解压，再双击 QiaoQiaoAI.Preview.exe。需要 Windows 64 位。
角色和运行时已包含，无需安装 VPet、.NET SDK 或配置 API 密钥。
默认是透明置顶桌宠，底部拖动手柄移动，右键或“玩法”展开九项工具；悬停显示角色切换和清理。
Codex / 豆包 / DeepSeek 鲸鱼均可使用。所有玩法支持长按角色连击；软鞭始终一条，抓住鞭柄可自由甩动，鞭身扫中角色才计数，松手回摆。角色会有逐渐消退的卡通泛红。
捏脸按住拖动、松手弹回。普通玩具不会给任何 AI 发送反馈。
投掷物是带褶皱和明暗折面的纸团；印章只印一个“滚”字，不显示回复卡。选便便后，短按丢一团，长按角色蓄力后连续喷射超级大便，松手停止。
豆包平时会眨眼，被敲会闭眼皱眉、撇嘴再恢复；这些表情帧由本项目参考官方头像制作。
一键清理、切工具或换脸也会停止手势。重置清空效果与计数；Esc先收起展开的列表，再按退出。台词离线预设，不会发送给 AI。

若要连接 Codex，请使用 qiaoqiao-feedback skill 版，在 Codex 里说“启动人工智障修理厂”叫出来，不用时说“关闭人工智障修理厂”收起，不会开机自启；详见 docs/RESIDENT.zh-CN.md。
此包含官方 Codex、豆包、DeepSeek 参考图片及衍生动画，其公开再分发条件尚未确认。
当前仅作为本地评审产物；公开发布前请完成 docs/ASSETS.md 中的条件。
'@ | Set-Content -LiteralPath (Join-Path $target '开始体验.txt') -Encoding UTF8
    $manifest = foreach ($file in (Get-ChildItem -LiteralPath $target -File -Recurse | Sort-Object FullName)) {
        '{0}  {1}' -f (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $file.FullName.Substring($target.Length + 1).Replace('\', '/')
    }
    $manifest | Set-Content -LiteralPath (Join-Path $target 'SHA256SUMS.txt') -Encoding UTF8
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $temporary = Join-Path $outputRoot ('.preview-' + $token + '.tmp.zip')
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $temporary, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    Move-Item -LiteralPath $temporary -Destination $archive -Force
    Write-Output $archive
}
finally {
    $resolved = (Resolve-Path -LiteralPath $stage).Path
    if (-not $resolved.StartsWith($outputRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -ne ('.preview-stage-' + $token)) {
        throw 'Refusing to remove an unexpected staging directory.'
    }
    # Windows may briefly keep a staged runtime DLL open after the archive is complete.
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        if (-not (Test-Path -LiteralPath $resolved)) { break }
        try { Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction Stop; break }
        catch {
            if ($attempt -eq 4) { Write-Warning "Could not clean temporary staging directory: $resolved. $($_.Exception.Message)" }
            else { Start-Sleep -Milliseconds (250 * ($attempt + 1)) }
        }
    }
}

