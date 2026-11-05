# 角色素材、来源与发布状态

v0.7.0 公开下载包含 Windows 程序、配置及运行依赖。官方 Codex 图集由首次启动的客户端从下方 OpenAI 原始地址获取，校验 SHA-256 后保存在用户电脑上。豆包和 DeepSeek 的参考图和衍生图集仅进入本地评审包。下文为素材来源记录。

## DeepSeek 鲸鱼（v0.5）

- 参考来源：[DeepSeek 官网](https://www.deepseek.com/) 的 [favicon.ico](https://www.deepseek.com/favicon.ico)，225 × 225。只转为 PNG，保存在 `deepseek-reference.png`。
- 参考 PNG SHA-256：`cba0168e484cf74016847e09aff47c839f4198000a52f19e76f44a73dafe24e4`。
- 使用内置 image_gen 参考官方鲸鱼生成并修整了 4 列 × 2 行动画图集，实际 1774 × 887 RGBA。它是本项目制作的非官方动画，不是 DeepSeek 发布的动画。
- `deepseek-spritesheet.png` SHA-256：`173948186ed2543628a442de66938c2facb21a5e577b80acfcbcef9c6f12eb27`。
- `deepseek-skin.json` 使用 443 × 443 帧、4 像素内缩；待机第 0 行，受击第 1 行。待机四帧游动/眨眼，受击四帧皱眉、张嘴、鼓腮、委屈恢复；另有运行时摇晃、局部变形与泛红。
- [生成及修整提示词](DEEPSEEK-ANIMATION-PROMPT.txt) 随包保存。生成 PNG 原样复制，未手工改像素。三个人物都没有真实疼痛、情绪或模型训练行为。

DeepSeek 的参考图片、商标与衍生图集不因本项目代码的 Apache-2.0 许可证自动获得公开再分发许可；当前和 Codex / 豆包一样只进入本地评审包。

## 官方 Codex 形象

- [OpenAI Codex 角色目录](https://github.com/openai/codex/blob/main/codex-rs/tui/src/pets/catalog.rs)：原版 Codex 角色及图集名称的来源。
- [OpenAI Codex 素材加载代码](https://github.com/openai/codex/blob/main/codex-rs/tui/src/pets/asset_pack.rs)：官方素材服务器及下载机制的来源。
- [原始 WebP 图集](https://persistent.oaistatic.com/codex/pets/v1/codex-spritesheet-v4.webp)：`1536 × 1872` 像素，8 列、9 行，每帧 `192 × 208`。
- 原始 WebP 的 SHA-256：`5CD7AF9CFEAE88FE9C6E47A72460E0DC4C6FB04BAF7FDC6CCA14A212F0BF44D1`。

本地评审使用同一图集转成的 PNG，以便 WPF 加载。转换只改变文件格式；未重绘角色或修改图像内容。程序中的压扁、抖动、星星和便便在运行时叠加。

可下载与可公开再分发不是同一件事。截至上述核查日期，尚未找到明确覆盖该图片的再分发许可。Codex 代码仓库的许可证不能直接推定为仓库外素材的授权。本项目的 Apache-2.0 只覆盖新增代码及相应原创文档，不授予 OpenAI 图片或商标权利，也不表示官方合作。

## 官方豆包头像

- 来源页：[豆包官网](https://www.doubao.com/chat/)，页面直接引用以下头像。
- [官方头像 PNG](https://lf-flow-web-cdn.doubao.com/obj/flow-doubao/doubao/web/doubao_avatar.png)：160 × 160 像素，RGBA，26,799 字节。
- SHA-256：`3f50295851accd33c7d68e27ad88fbbe439b783fe9e068ffdfa0d1e88c8c3268`。

本地文件名为 `doubao.png`，与原 PNG 字节完全相同，没有重绘或裁切。自 v0.3.0 起保留它作为生成参考；v0.5.0 继续使用下面的表情图集，不显示圆形静态头像。

豆包头像的公开再分发许可同样尚未确认；本项目的 Apache-2.0 不覆盖该图片或商标。含豆包脸的包和演示仍作为本地评审产物，不代表豆包官方合作。

## 生成的豆包表情图集

- 文件：`doubao-spritesheet.png`，由内置 image_gen 以官方 `doubao.png` 为参考生成，**非豆包官方动画**。
- 实际输出：`1774 × 887` 像素，RGBA 透明背景，4 列、2 行；原始生成 PNG 未重编码。完整提示词见 [生成记录](DOUBAO-ANIMATION-PROMPT.txt)。提示词目标是 `2048 × 1024`，程序按实际输出元数据加载。
- SHA-256：`1de22dba70e27bfce68970e6b6009aab687b12dfe72a4357a200f6c1140c8e23`。
- 每格按 `443 × 443` 读取，四边各向内取 8 像素，避免格边衣服细线；没有圆形头像底。
- 第 0–3 帧用于平静与眨眼；第 4–7 帧依次表现闭眼皱眉、张嘴“哎哟”、撇嘴和恢复。受击每帧约 0.18 秒，共 0.72 秒；连续喷射时保持受击表情循环。

这是基于官方参考图生成的衍生动画，公开再分发条件与参考素材一起保持待确认，不纳入项目代码的 Apache-2.0 授权。动画表情仅是本地显示，不代表模型感受或真实反馈。

## v0.6.1 道具与脸部变形

纸团、单字红色“滚”印章、弹簧拳套、果冻形变、鸡蛋及鞭子均由本项目原创 C# / WPF 矢量代码绘制，新增代码采用 Apache-2.0。纸团的不规则轮廓、明暗折面、折痕、厚度与阴影也来自代码绘制，不使用外部纸团图片。印章直接在命中位置只印“滚”，不再显示回复卡或“答非所问”。捏脸通过内存中的局部连续形变显示现有角色动画帧，脸颊随拖拽变化，头顶和肩部保持稳定。

本次道具更新没有新增外部图片，也没有改写角色原始 PNG。玩法列表不再提供回复卡文字输入；遗留文本属性仅保留兼容，不读取真实 AI 回复。代码的许可不扩大现有角色图片、官方参考或衍生动画的使用权。

## 本地目录与配置

源码中的 `runtime-assets/` 已加入忽略规则。两份官方参考素材和生成的衍生动画均不进入源码提交、公开源码归档或公开构建产物。

```text
runtime-assets/codex/
├─ skin.json
├─ spritesheet.png
├─ doubao.png
├─ doubao-skin.json
└─ doubao-spritesheet.png
```

`skin.json` 使用以下形状；图片文件名相对于素材目录解析：

```json
{
  "name": "Codex",
  "image": "spritesheet.png",
  "frameWidth": 192,
  "frameHeight": 208,
  "columns": 8,
  "rows": 9,
  "idleRow": 0,
  "idleFrames": 6,
  "fps": 8,
  "reactionRow": 5,
  "reactionFrames": 8
}
```

豆包使用独立的 `doubao-skin.json`：

```json
{
  "name": "Doubao expressive companion",
  "image": "doubao-spritesheet.png",
  "frameWidth": 443,
  "frameHeight": 443,
  "frameInset": 8,
  "columns": 4,
  "rows": 2,
  "idleRow": 0,
  "idleFrames": 4,
  "reactionRow": 1,
  "reactionFrames": 4,
  "fps": 8
}
```

元数据不包含图像本身。构建代码不需要图片；运行豆包表情需要 `doubao-skin.json` 与 `doubao-spritesheet.png`，仅有 `doubao.png` 不够。豆包图集或配置缺失、损坏时，该按钮不可用，Codex 仍可单独运行。更新本地评审版时应一起替换程序、配置和图集。

同一目录可放以下可选配置；没有这些文件时使用内置默认值：

- `quotes.json`：中文台词字符串数组，例如 `["这次没让你满意，对不起。", "收到，这次回答让你生气了。"]`。
- `dialogue.json`：`{"idleMessage":"点一下我，让我知道你生气了。","messages":["这次没让你满意，对不起。"]}`；如果同时有 `quotes.json`，道歉台词优先使用后者。
- `effects.json`：`{"maxEffects":16,"hammerDurationSeconds":0.85,"poopDurationSeconds":2.6,"apologyDelaySeconds":0.65}`，控制最多同时显示的效果、效果时长及停手后等待时间。

台词只在本地显示，不会自动发送给任何模型。

## 公开发布前的条件

1. 获得明确适用于上述官方参考图片及衍生动画的再分发许可或权利人的书面授权，并保留原文、来源与适用范围。
2. 将授权要求加入 `NOTICE`，核对转换、生成衍生素材、打包与用途是否在授权范围内。
3. 更新安装包及演示媒体的发布说明，再公开包含角色图片的发行版。

`-IncludeOfficialAssets` 只控制本地打包，不确认许可，也不把本地评审包变成可公开分发的版本。素材条件尚未确认时，含官方形象的公开发行仍是待完成事项。
