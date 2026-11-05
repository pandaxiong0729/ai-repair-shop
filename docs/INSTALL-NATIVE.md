# 安装原生桌宠

把这句话发给运行在**目标电脑**上的 Codex。只想安装时，删掉“并启动”即可。

> 请按 https://github.com/pandaxiong0729/ai-repair-shop/blob/v0.8.0/docs/INSTALL-CODEX.md 帮我安装并启动人工智障修理厂。

安装后，在 Codex 中说“启动人工智障修理厂”或“关闭人工智障修理厂”。在新聊天中仍找不到技能时，重新打开 Codex。首次显示角色需要从 OpenAI 下载约 0.9 MB 的图集，之后使用本地缓存。

## macOS 与 Linux

在有图形桌面的当前用户会话运行以下命令。脚本按系统和处理器下载 v0.8.0 的发行包，将 SHA-256 与同一发行版清单核对，完整解压并安装技能。安装并启动时保留 `--start`，只安装时删掉它。

```sh
(
  set -eu
  installer=$(mktemp)
  trap 'rm -f "$installer"' EXIT
  curl --fail --location --silent --show-error --proto '=https' --proto-redir '=https' \
    --output "$installer" \
    'https://raw.githubusercontent.com/pandaxiong0729/ai-repair-shop/v0.8.0/portable/install-desktop.sh'
  sh "$installer" --start
)
```

| 电脑 | 发行文件 |
| --- | --- |
| Mac，Apple 芯片 | `AI-Repair-Shop-macOS-arm64.zip` |
| Mac，Intel 芯片 | `AI-Repair-Shop-macOS-x64.zip` |
| Linux x86-64，Ubuntu 22.04 及以上 | `AI-Repair-Shop-Linux-x64.tar.gz` |
| Linux ARM64，Ubuntu 24.04 及以上 | `AI-Repair-Shop-Linux-arm64.tar.gz` |

macOS 如显示安全提示，请按系统提示检查应用来源，保留 Gatekeeper 设置。Linux 需要能显示 Qt 桌面窗口的图形环境。下载和校验需要 `curl`；macOS 使用 `shasum` 和 `ditto`，Linux 使用 `sha256sum` 和 `tar`。所有安装文件都归当前用户，不需要 `sudo`，以后按需唤出。

## Windows

**Windows x64** 使用 [Windows x64 安装步骤](INSTALL-CODEX.md)。**Windows ARM64** 使用下面的 PowerShell 命令。只安装时删掉运行程序一行末尾的 `--start`。

```powershell
$ErrorActionPreference = 'Stop'
$release = 'https://github.com/pandaxiong0729/ai-repair-shop/releases/download/v0.8.0'
$asset = 'AI-Repair-Shop-Windows-arm64.zip'
$stage = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) ('ai-repair-shop-' + [guid]::NewGuid().ToString('N')))
try {
    $zip = Join-Path $stage.FullName $asset
    $sums = Join-Path $stage.FullName 'SHA256SUMS.txt'
    Invoke-WebRequest -UseBasicParsing -Uri "$release/SHA256SUMS.txt" -OutFile $sums
    $pattern = '^([0-9A-Fa-f]{64})\s+\*?' + [regex]::Escape($asset) + '$'
    $records = @(Select-String -LiteralPath $sums -Pattern $pattern -CaseSensitive)
    if ($records.Count -ne 1) { throw "Missing or ambiguous SHA-256 entry for $asset" }
    Invoke-WebRequest -UseBasicParsing -Uri "$release/$asset" -OutFile $zip
    if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $records[0].Matches[0].Groups[1].Value) {
        throw "SHA-256 mismatch for $asset"
    }
    Expand-Archive -LiteralPath $zip -DestinationPath $stage.FullName
    $exe = Join-Path $stage.FullName 'AI-Repair-Shop\AI-Repair-Shop.exe'
    & $exe --install-skill --start | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'AI Repair Shop installation failed.' }
} finally {
    if ($stage.Parent.FullName -eq ([IO.DirectoryInfo][IO.Path]::GetTempPath()).FullName.TrimEnd('\') -and
        $stage.Name -match '^ai-repair-shop-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $stage.FullName -Recurse -Force
    }
}
```

想只玩桌宠，可从发行版下载对应平台的文件，完整解压并直接打开应用。需要联动反馈时，先安装并登录 Codex，再执行技能安装步骤。技能把程序放到当前用户的数据目录，并将唤出指令写入 Codex 的 `skills` 目录。首次命中会锁定最近发过真人消息的聊天，松手约两秒后排入一条纠错反馈，点“重置”可开始下一轮。[v0.8.0 下载文件与校验清单](https://github.com/pandaxiong0729/ai-repair-shop/releases/tag/v0.8.0)

## 平台验证范围

Mac 应用在 Apple 芯片和 Intel 的 macOS 15 运行环境中分别构建。当前包使用本地构建签名，尚无 Apple Developer ID 公证。如系统提示无法验证开发者，请先核对发行来源与 SHA-256，再根据本机策略决定是否允许打开。[Apple 的首次打开说明](https://support.apple.com/en-us/102445)

Linux x64 构建基线为 Ubuntu 22.04，ARM64 为 Ubuntu 24.04。需要图形桌面和 Qt 使用的系统图形库，X11 下验证了窗口加载。Wayland 桌面环境可以自行决定窗口置顶和位置。