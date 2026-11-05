# 人工智障修理厂 · 使用与开发

公开版 v0.7.0 面向 Windows x64。下载并解压玩具版后双击 `QiaoQiaoAI.Preview.exe`，首次打开时会获取官方 Codex 角色，以后可离线玩。[下载玩具版](https://github.com/pandaxiong0729/ai-repair-shop/releases/latest/download/AI-Repair-Shop-Windows-x64.zip)

想让敲打变成给 Codex 的反馈，可以直接把这句话发给 Codex。

> 请按 https://github.com/pandaxiong0729/ai-repair-shop/blob/main/docs/INSTALL-CODEX.md 帮我安装并启动人工智障修理厂。

下次说“启动人工智障修理厂”就能打开，说“关闭人工智障修理厂”就会收起。[Skill 安装说明](docs/INSTALL-CODEX.md)

## 怎么玩

1. 完整解压玩具包，双击 `QiaoQiaoAI.Preview.exe`。使用“↔ 拖动”手柄移动，避免拖动角色时误触玩法。
2. 鼠标移近角色可显示按钮；点底部“玩法”或面板“工具箱”，也可右键打开图标九宫格。玩法按钮和当前工具均只显示单个图标，悬停查看名称和操作说明。纸团、「滚」、捏脸、压扁脸和鞭子使用自绘矢量图标，其余四项保留 emoji。
3. 大多数玩法按住约 0.35 秒后连续触发；便便按住约 0.6 秒后连续喷射，松手停止。
4. 投掷的是带褶皱、折面阴影和厚度的纸团；印章直接在命中位置只盖一个红色“滚”字，不显示回复卡。
5. 切工具、换脸、清理、窗口失焦、失去鼠标捕获及退出都会停止连续互动。“一键清理”也清除红印和脸红。
6. “重置”清除画面、红印和可见计数，保留角色、工具与聊天连接，并开启下一轮反馈。重置本身不发送；尚未提交的互动会丢弃，已经提交的消息不会撤回。点击“×”退出；Esc 先收起列表，再按退出。常驻版退出不会撤回已进入 Codex 队列的消息。

| 玩法 | 怎么操作与看什么 |
| --- | --- |
| 🔨 | 点击挥锤，按住连敲，角色压扁回弹并冒出星星。 |
| 💩 | 短按丢一团；蓄力后连续喷射超级大便。 |
| 揉皱的纸团 | 扔出有不规则轮廓、褶皱和明暗折面的纸团，飞行时旋转，命中后弹落；按住可连扔。 |
| 滚 | 入口和实际印记均为单字「滚」；直接盖在命中位置，不显示回复卡或其他文字，按住可连续盖章。 |
| 手捏痛苦的脸 | 按住脸部小幅拖拽、持续拉扯，松手后约 0.95 秒弹回。 |
| 🥊 | 软拳套沿弹簧伸出后缩回，按住连续出拳。 |
| 压扁的脸 | 角色像果冻一样压缩回弹，按住持续晃动。 |
| 🥚 | 蛋壳碎开、蛋白蛋黄滴落；按住连续投掷。 |
| 鞭子 | 始终一条鞭子。直接抓鞭柄来回甩，鞭身随惯性弯曲、回摆，扫中角色才计数；长按角色可让同一条鞭子连续甩。 |

三角色本地评审包中的豆包与 DeepSeek 有待机和受击表情，捏脸会局部拉动角色。粉红印记与脸红会逐渐消退，不显示血液或伤口。24 条可配置气泡采用夸张的 AI 自嘲风格；它们只是本地台词，不会原样放进纠错反馈。

玩具版不创建反馈文件、不发送消息。常驻版通过公开 App Server 接口临时检查近期消息和摘要，按真人发消息时间选择目标；助手更新和本程序的反馈消息不改变目标。它在每次重置之间最多向 Codex 聊天队列提交一条反馈，不修改模型或权限，也不表示 AI 有真实感受。每约 2 秒的本地接口检查不调用模型；反馈触发的真实回答仍消耗正常用量。

队列接受与 AI 回答分开显示。已加载的空闲聊天通常约 10 秒检测外部队列；忙碌聊天在之后处理。关闭、未加载或中断的聊天可能需要打开或恢复。无需让技能在聊天里不断读取文件，实际数据范围与失败处理见 [反馈说明](docs/FEEDBACK.zh-CN.md)。

## 开发者本地评审包

| 文件 | 用途 |
| --- | --- |
| `QiaoQiaoAI-toy-local.zip` | Windows 64 位离线桌宠，内置运行时和三个角色；解压后直接玩，无需 VPet 或 .NET。 |
| `QiaoQiaoAI-feedback-skill-local.zip` | 包含脚本、桌宠和运行时；在 Codex 中说“启动人工智障修理厂”按需唤出，不随登录或打开 Codex 自动出现，每次重置之间最多反馈一条。 |
| `QiaoQiaoAI-local-review.zip` | VPet 插件；经典面板、离线玩法，未接入技能反馈。 |
| `QiaoQiaoAI-code-only.zip` | 不含角色图片的插件组件包；缺少素材，不能算完整可玩发行版。 |
| 源码包 | 用于阅读、修改和贡献代码；不包含官方参考图片、衍生动画、宿主程序或构建缓存。 |

反馈资格会跨程序重启保留，关闭再打开不会开始新一轮。旧 0.6.0 有已尝试发送记录时，升级后先显示本轮已用，请按一次“重置”开始新的反馈轮次。

两份用户包均只供本地评审。技能包内根目录为 `qiaoqiao-feedback`，不能只复制 EXE。安装与卸载见 [使用说明](docs/INSTALL.zh-CN.md)，实际测试范围见 [验证记录](docs/VALIDATION.md)。VPet 开发基线为 **1.10.74**，提交 `5a64ad2bf00a3f321227a26aa585bac6399b1a0e`；Steam 发行版兼容性单独记录。

## 从源码构建

要生成 GitHub Releases 使用的两个 Windows x64 安装包，安装 .NET 10 SDK 后在仓库根目录运行 `.\scripts\Package-Public.ps1`。它生成玩具 ZIP、完整 Skill ZIP 及校验文件，输出到 `artifacts/public/`。两种公开包共用同一个 Codex 图集缓存。

在 Windows 上准备 .NET 10 SDK（可构建本项目的 .NET 8 插件和 .NET 10 预览程序），并准备固定基线的 VPet 源码。

```powershell
git clone https://github.com/LorisYounger/VPet.git ..\VPet
git -C ..\VPet checkout 5a64ad2bf00a3f321227a26aa585bac6399b1a0e
.\scripts\Build.ps1 -DotnetPath dotnet -VPetSourceRoot ..\VPet
# 如需生成自带运行时的预览，将上面的命令加上 -SelfContainedPreview
```

构建输出分别位于 `artifacts/plugin` 和 `artifacts/preview`。角色素材不编入 DLL，也不由构建脚本自动下载；本地素材路径是 `runtime-assets/codex`。参见 [素材来源](docs/ASSETS.md) 准备可用且有权使用的素材。

```powershell
# 在已安装 .NET 10 Windows Desktop Runtime 的电脑上打开独立预览
.\artifacts\preview\QiaoQiaoAI.Preview.exe --assets .\runtime-assets\codex

# 使用经典面板，不启用反馈
.\artifacts\preview\QiaoQiaoAI.Preview.exe --assets .\runtime-assets\codex --classic

# 仅生成不含官方图片的组件包
.\scripts\Package.ps1 -BuildDir .\artifacts\plugin -OutputDirectory .\artifacts\packages

# 生成本地评审包；此开关不授予官方素材的公开分发权
.\scripts\Package.ps1 -BuildDir .\artifacts\plugin -OutputDirectory .\artifacts\packages `
  -AssetsDir .\runtime-assets\codex -IncludeOfficialAssets

# 使用本地素材运行 UI 和真实 Core/Interface 的非交互集成检查
.\scripts\Test.ps1 -DotnetPath dotnet -VPetSourceRoot ..\VPet -AssetsDir .\runtime-assets\codex

# 导出不带官方参考图片、衍生动画、宿主或构建缓存的源码 ZIP
.\scripts\Export-Source.ps1

# 已使用 -SelfContainedPreview 构建时，可生成解压即用的本地预览包。
# RuntimePackagesRoot 指向还原 .NET 运行时包的 NuGet 缓存，打包时保留其许可声明。
.\scripts\Package-Preview.ps1 -BuildDir .\artifacts\preview -AssetsDir .\runtime-assets\codex `
  -RuntimePackagesRoot "$env:USERPROFILE\.nuget\packages"

# PreviewDir 指向完整玩具包解压后含 EXE、assets、licenses 的目录。
# desktop-staged 是示例目录；不能直接使用缺少素材和许可文件的裸编译目录。
.\scripts\Package-Skill.ps1 -PreviewDir .\artifacts\desktop-staged

# 只导出技能文本、脚本与文档，不含角色和运行程序，供公开审阅。
.\scripts\Package-Skill.ps1 -SourceOnly
```

技能打包默认输出 `artifacts/packages/QiaoQiaoAI-feedback-skill-local.zip`；`-SourceOnly` 默认输出 `QiaoQiaoAI-feedback-skill-source.zip`，后者不能直接启动桌宠。可用 `-OutputZip` 指定 ZIP 路径。打包不会把运行时复制回源码技能目录。

## 开源贡献

欢迎通过本仓库的 Issues 和 Pull Requests 贡献。项目独立维护，VPet 由 LorisYounger 及其贡献者开发。[贡献指南](CONTRIBUTING.md) · [VPet 上游仓库](https://github.com/LorisYounger/VPet)
