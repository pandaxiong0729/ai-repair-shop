# 在 Codex 里安装人工智障修理厂

把这一句话发给 Windows 上的 Codex 即可。

> 请按 https://github.com/pandaxiong0729/ai-repair-shop/blob/main/docs/INSTALL-CODEX.md 帮我安装并启动人工智障修理厂。

## 给执行安装的 Codex

Windows x64 的正式包已经包含运行时和 Skill 脚本。安装时使用本仓库 v0.7.0 的安装脚本，它从对应的 GitHub Release 下载完整 Skill ZIP、核对 SHA-256，并将 Skill 装到当前用户的技能目录。首次打开桌宠时，程序会向 OpenAI 下载官方 Codex 图集并缓存。

在 PowerShell 中运行以下命令。用户只要求安装时，请省略最后的 `-Start`。用户要求安装并启动时，请执行完整命令。

```powershell
$ErrorActionPreference = 'Stop'
$installer = Join-Path ([IO.Path]::GetTempPath()) ('Install-AI-Repair-Shop-' + [Guid]::NewGuid().ToString('N') + '.ps1')
Invoke-WebRequest -UseBasicParsing -Uri 'https://raw.githubusercontent.com/pandaxiong0729/ai-repair-shop/v0.7.0/scripts/Install-PublicSkill.ps1' -OutFile $installer
powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer -Start
if ($LASTEXITCODE -ne 0) { throw 'AI Repair Shop installation failed.' }
```

已经安装后，用户说“启动人工智障修理厂”就运行已安装 Skill 内的 `scripts/Start-Resident.ps1`，说“关闭人工智障修理厂”就运行 `scripts/Stop-Resident.ps1`。首次安装所在的聊天也可直接调用这些脚本。启动后可用 `Start-Resident.ps1 -StatusOnly` 查看连接状态。

## 手动安装

完整解压 [Codex Skill ZIP](https://github.com/pandaxiong0729/ai-repair-shop/releases/download/v0.7.0/AI-Repair-Shop-Codex-Skill.zip)，将其中的 `qiaoqiao-feedback` 文件夹放入当前用户的 `%USERPROFILE%\.codex\skills` 目录。重启 Codex 后说“启动人工智障修理厂”。
