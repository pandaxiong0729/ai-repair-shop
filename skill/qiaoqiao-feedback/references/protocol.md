# 本地一轮反馈协议

适用 v0.5；无网络请求、无账号凭据。普通玩具不构造 `FeedbackRound`，也不创建会话文件。

## 会话

`Start-Feedback.ps1` 创建随机 GUID 目录和 `session.json`，默认位于本机 LocalAppData/QiaoQiaoAI/feedback，随后启动桌宠 `--feedback-dir <目录>`。返回值只由调用它的当前聊天保留；不存在“自动寻找当前聊天”的逻辑。`-SessionRoot` 可指定本地目录；`-NoLaunch` 只用于测试协议，不代表启动了桌宠。会话配置有效期四小时。

这里的 GUID 标识反馈轮次，不是 Codex 聊天 ID；`target: current-agent` 是协议约定，不是聊天身份凭据。当前聊天的对应关系由技能保留返回路径和 ID、在同一聊天执行读取来维持。

```json
{"schema":1,"sessionId":"GUID","target":"current-agent","createdUtc":"ISO8601","expiresUtc":"ISO8601"}
```

`FeedbackRound` 校验该文件并持有 `writer.lock` 的操作系统独占句柄，避免同一会话被两个窗口同时记录。每击只更新内存中的计数。成功释放捏脸、持续拉扯的有效拍点和连续喷射可以计入多次；每次事件的 Count 加入同一个聚合，次数最多为 Int32.MaxValue。清理视觉效果不清空本轮反馈计数。

## 封存

点击“发送本轮反馈”或发送并关闭时调用 `Seal("send"/"close")`，临时文件写完后原子重命名为 `batch.json`。同一会话只允许一个文件，封存后停止记录，按钮禁用；仍可继续作为玩具。选择“不发送并关闭”调用 `Cancel()`，写 `ended.json` 和 `status: cancelled`，不能撤销已经封存的反馈。`Dispose()` 只退订事件并释放锁，不隐式发送。空轮写 `ended.json` 和 `status: empty`，不生成纠错提示。没有停手自动发送，以免把一轮切碎。

```json
{"schema":1,"sessionId":"GUID","roundId":"同一GUID","endedUtc":"ISO8601","trigger":"send","total":3,"tools":[{"tool":"Hammer","count":3}],"characters":[{"character":"Doubao","count":3}],"reason":"遗漏了我的限制条件"}
```

只接受九种已定义工具（包含 v0.5 的软鞭）；各工具次数之和等于 total。reason 最多 120 字符。回复卡文字不进入该文件。数据留在本地，不自动上传；需要清理时由用户删除对应会话目录，桥接脚本不执行递归清理。

## 领取与故障语义

`Read-Feedback.ps1` 要求调用者提供该会话的 GUID，最多等待 55 秒。读取完整封存文件、检查大小、会话 ID 和计数后，以 CreateNew 写 `claimed.json`，再返回一个 JSON 工具结果，其中 `prompt` 为几百字以内的纠错信息。并发消费者只有一个能建立领取标记；后续调用返回 `already_claimed`，不含提示词。

这是本地至多一次输出。写领取标记后进程崩溃、执行工具响应丢失，可能丢失交付；保留标记，禁止自动重试成第二条提示。不将“汇总已保存”或“已领取”冒充“模型已收到并改正”。按用户明确指示可在当前聊天重新表达问题，但不偷偷删除去重标记。

## 为什么使用技能接收

本机核查的 Codex CLI 0.164.0-alpha.11.1 提供 app-server 和 proxy，但外部桌宠没有已建立的当前桌面聊天连接。[官方 App Server 文档](https://learn.chatgpt.com/docs/app-server) 的 `turn/steer` 要求受控服务上的线程与活动 turn ID；它不构成可自动附着任意桌面聊天的承诺。因此本版不使用私有端口、数据库、自动键入或伪造消息。当前聊天执行接收脚本，结果自然进入其上下文。[官方技能说明](https://learn.chatgpt.com/docs/build-skills) 支持技能携带本地脚本与资源。

## 检查

执行 `scripts/Test-Bridge.ps1 -TestRoot <隔离临时目录>` 验证空轮、主动取消、单次领取、重复/并发领取、非法会话和计数及等待超时。它只使用合成数据，不给任何真实聊天发送测试消息。UI 事件和 `FeedbackRound` 的检查由项目集成测试覆盖；本协议测试不能代替实际 UI 测试。窗口崩溃可能不写结束文件，技能最多主动等待十分钟，不无限轮询或假装已经送达。
