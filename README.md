# 人工智障修理厂

被 AI 气到的时候，我想敲它两下，再让它把刚才的回答好好改改。这是一个可以挂在 Windows、Mac 和 Linux 桌面上的互动桌宠。

1. **只想玩**，从下表下载适合自己电脑的包，完整解压后打开里面的桌宠程序。
2. **想连接 Codex**，把这句话发给 Codex：**请按 https://github.com/pandaxiong0729/ai-repair-shop/blob/main/docs/INSTALL-CODEX.md 帮我安装并启动人工智障修理厂。**
3. 下次说“启动人工智障修理厂”就能叫出来，说“关闭人工智障修理厂”就会收起。

| 电脑 | 玩具下载 | 解压后打开 |
| --- | --- | --- |
| Windows，Intel 或 AMD 64 位处理器 | [Windows x64](https://github.com/pandaxiong0729/ai-repair-shop/releases/download/v0.7.0/AI-Repair-Shop-Windows-x64.zip) | `QiaoQiaoAI.Preview.exe` |
| Windows 11，ARM64 处理器（如 Snapdragon） | [Windows ARM64](https://github.com/pandaxiong0729/ai-repair-shop/releases/download/v0.8.0/AI-Repair-Shop-Windows-arm64.zip) | `AI-Repair-Shop.exe` |
| Mac，Apple 芯片（M 系列） | [Mac Apple 芯片版](https://github.com/pandaxiong0729/ai-repair-shop/releases/download/v0.8.0/AI-Repair-Shop-macOS-arm64.zip) | `AI-Repair-Shop.app` |
| Mac，Intel 处理器 | [Mac Intel 版](https://github.com/pandaxiong0729/ai-repair-shop/releases/download/v0.8.0/AI-Repair-Shop-macOS-x64.zip) | `AI-Repair-Shop.app` |
| Linux，Intel 或 AMD 64 位处理器 | [Linux x64](https://github.com/pandaxiong0729/ai-repair-shop/releases/download/v0.8.0/AI-Repair-Shop-Linux-x64.tar.gz) | `AI-Repair-Shop/AI-Repair-Shop` |
| Linux，ARM64 处理器 | [Linux ARM64](https://github.com/pandaxiong0729/ai-repair-shop/releases/download/v0.8.0/AI-Repair-Shop-Linux-arm64.tar.gz) | `AI-Repair-Shop/AI-Repair-Shop` |

公开版首次使用时从 OpenAI 获取约 0.9 MB 的官方 Codex 图集并缓存，此后玩具可以离线使用。桌宠有九种玩法，支持长按连击和拖动甩鞭。豆包和 DeepSeek 动画目前用于本地评审。

Skill 版会把“这次回答做得不好，请纠正”发到你敲第一下时最近发过真人消息的 Codex 聊天。松手约两秒后排入队列，每次重置之间最多一条。使用反馈功能需要本机已安装并登录支持聊天队列接口的 Codex。

macOS 包目前尚未经过 Apple 公证。Linux 构建基线分别为 Ubuntu 22.04 x64、Ubuntu 24.04 ARM64，桌面集成在 X11 下验证。详见[原生版安装与兼容说明](docs/INSTALL-NATIVE.md)。

[所有版本与校验文件](https://github.com/pandaxiong0729/ai-repair-shop/releases) · [Windows x64 与 VPet 源码构建](README.zh-CN.md#从源码构建) · [许可证](LICENSE) · [素材来源](docs/ASSETS.md)
