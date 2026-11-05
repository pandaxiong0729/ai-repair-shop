using System.IO;
using System.Text.Json;
using QiaoQiaoAI.UI;

namespace QiaoQiaoAI.Preview;

/// <summary>Deterministic public-protocol fixtures: no Codex process and no live chat messages.</summary>
internal static class ResidentRoutingChecks
{
    public static async Task<IReadOnlyList<string>> RunAsync()
    {
        var passed = new List<string>();
        async Task Check(string name, Func<Task> run)
        {
            try { await run(); passed.Add(name); }
            catch (Exception error) { throw new InvalidOperationException(name, error); }
        }

        await Check("routing uses exact message milliseconds and opaque IDs", async () =>
        {
            var rpc = new ScriptedRpc();
            rpc.Add("thread/turns/list", Turns(null, Turn("turn-with-old-start", User("opaque-user-id", "Fix this"))));
            rpc.Add("thread/items/read", Entries(null, Entry("opaque-user-id", "Fix this", 1_234_567)));
            var target = await Read(rpc);
            Require(target?.HumanMessageId == "opaque-user-id" && target.LastHumanMessageUtc.ToUnixTimeMilliseconds() == 1_234_567);
            rpc.Complete();
        });

        await Check("first hit cutoff finds a hidden earlier user in the same active turn", async () =>
        {
            var rpc = new ScriptedRpc();
            rpc.Add("thread/turns/list", Turns(null, Turn("active-turn", User("after-hit", "New unrelated request"))));
            rpc.Add("thread/items/read", Entries(null, Entry("after-hit", "New unrelated request", 3_000)));
            rpc.Add("thread/items/list", Entries("older-items", AgentEntry("tool-between", 2_100)), parameters =>
            {
                Require(parameters.GetProperty("turnId").GetString() == "active-turn");
                Require(parameters.GetProperty("sortDirection").GetString() == "desc");
                Require(parameters.GetProperty("cursor").GetProperty("itemId").GetString() == "after-hit");
            });
            rpc.Add("thread/items/list", Entries(null, Entry("before-hit", "The request being criticized", 1_900)), parameters =>
                Require(parameters.GetProperty("cursor").GetString() == "older-items"));
            var target = await Read(rpc, 2_000);
            Require(target?.HumanMessageId == "before-hit");
            rpc.Complete();
        });

        await Check("cutoff traverses older turns and includes a message exactly at first hit", async () =>
        {
            var rpc = new ScriptedRpc();
            rpc.Add("thread/turns/list", Turns("older-turns", Turn("new-turn", User("new-human", "New"))));
            rpc.Add("thread/items/read", Entries(null, Entry("new-human", "New", 4_000)));
            rpc.Add("thread/items/list", Entries(null));
            rpc.Add("thread/turns/list", Turns(null, Turn("old-turn", User("at-hit", "Old"))), parameters =>
                Require(parameters.GetProperty("cursor").GetString() == "older-turns"));
            rpc.Add("thread/items/read", Entries(null, Entry("at-hit", "Old", 2_000)));
            Require((await Read(rpc, 2_000))?.HumanMessageId == "at-hit");
            rpc.Complete();
        });

        await Check("automatic feedback cannot hide the last human in the same turn", async () =>
        {
            var automatic = ResidentFeedbackBatch.PromptPrefix + Guid.Empty + "] automatic";
            var rpc = new ScriptedRpc();
            rpc.Add("thread/turns/list", Turns(null, Turn("turn", User("our-feedback", automatic))));
            rpc.Add("thread/items/read", Entries(null, Entry("our-feedback", automatic, 9_000)));
            rpc.Add("thread/items/list", Entries(null,
                Entry("our-old-test", "【人工智障修理厂连接测试】check", 8_000),
                Entry("real-human", "Actual human request", 7_000)));
            Require((await Read(rpc))?.HumanMessageId == "real-human");
            rpc.Complete();
        });

        await Check("missing human timestamp fails closed instead of choosing an older chat", async () =>
        {
            var rpc = new ScriptedRpc();
            rpc.Add("thread/turns/list", Turns(null, Turn("turn", User("unknown-time", "Human request"))));
            rpc.Add("thread/items/read", Entries(null, Entry("unknown-time", "Human request", null)));
            await ExpectInvalidData(() => Read(rpc, 2_000));
            rpc.Complete();
        });

        await Check("a disappearing summary user item fails closed", async () =>
        {
            var rpc = new ScriptedRpc();
            rpc.Add("thread/turns/list", Turns(null, Turn("turn", User("not-yet-readable", "Human request"))));
            rpc.Add("thread/items/read", Entries(null));
            await ExpectInvalidData(() => Read(rpc));
            rpc.Complete();
        });

        await Check("all messages after cutoff produce no historical target", async () =>
        {
            var rpc = new ScriptedRpc();
            rpc.Add("thread/turns/list", Turns(null, Turn("turn", User("too-new", "Human request"))));
            rpc.Add("thread/items/read", Entries(null, Entry("too-new", "Human request", 2_001)));
            rpc.Add("thread/items/list", Entries(null));
            Require(await Read(rpc, 2_000) is null);
            rpc.Complete();
        });

        return passed;
    }

    private static Task<ResidentChatTarget?> Read(ScriptedRpc rpc, long? cutoff = null) =>
        CodexMessageRouting.ReadLatestHumanAsync(rpc.Call, "chat", "Chat", CancellationToken.None,
            cutoff.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(cutoff.Value) : null);

    private static object User(string id, string text) => new
    { type = "userMessage", id, content = new[] { new { type = "text", text } } };
    private static object Turn(string id, params object[] items) => new { id, startedAt = 0, items };
    private static object Turns(string? cursor, params object[] turns) => new { data = turns, nextCursor = cursor };
    private static object Entries(string? cursor, params object[] entries) => new { data = entries, nextCursor = cursor };
    private static object Entry(string id, string text, long? milliseconds) => new
    { turnId = "turn", item = User(id, text), startedAtMs = milliseconds, completedAtMs = milliseconds };
    private static object AgentEntry(string id, long milliseconds) => new
    { turnId = "turn", item = new { type = "agentMessage", id, text = "not a human message" }, startedAtMs = milliseconds };

    private static void Require(bool value)
    { if (!value) throw new InvalidOperationException("Routing fixture assertion failed."); }

    private static async Task ExpectInvalidData(Func<Task<ResidentChatTarget?>> run)
    {
        try { await run(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Unknown message time must stop automatic routing.");
    }

    private sealed class ScriptedRpc
    {
        private readonly Queue<(string Method, JsonElement Result, Action<JsonElement>? Verify)> _steps = [];
        public void Add(string method, object result, Action<JsonElement>? verify = null) =>
            _steps.Enqueue((method, JsonSerializer.SerializeToElement(result), verify));

        public Task<JsonElement> Call(string method, object parameters, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_steps.Count == 0) throw new InvalidOperationException("Unexpected RPC call: " + method);
            var next = _steps.Dequeue();
            if (next.Method != method) throw new InvalidOperationException($"Expected {next.Method}, got {method}.");
            next.Verify?.Invoke(JsonSerializer.SerializeToElement(parameters));
            return Task.FromResult(next.Result);
        }

        public void Complete() => Require(_steps.Count == 0);
    }
}
