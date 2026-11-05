using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using QiaoQiaoAI.UI;

namespace QiaoQiaoAI.Preview;

/// <summary>Uses the public App Server JSON-RPC protocol. Never resumes or starts a model turn.</summary>
internal interface IResidentCodexConnection : IDisposable
{
    Task<JsonElement> CallAsync(string method, object parameters, CancellationToken token);
}

internal sealed class CodexResidentConnection : IResidentCodexConnection
{
    private readonly Process _process;
    private readonly SemaphoreSlim _calls = new(1, 1);
    private int _nextId;
    private bool _disposed;
    public string Executable { get; }

    private CodexResidentConnection(string executable)
    {
        Executable = executable;
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
        };
        info.ArgumentList.Add("app-server");
        // This is an independent API client, not an impersonation of the launching model's tool call.
        foreach (var key in new[] { "CODEX_THREAD_ID", "CODEX_SESSION_ID", "CODEX_TOOL_CALL_ID", "CODEX_APP_TOOLS_PIPE_PATH" })
            info.Environment.Remove(key);
        _process = Process.Start(info) ?? throw new IOException("无法启动 Codex 连接程序。");
        _ = DrainErrorsAsync();
    }

    public static string FindExecutable(string? requested = null)
    {
        if (!string.IsNullOrWhiteSpace(requested) && File.Exists(requested)) return Path.GetFullPath(requested);
        var fromHost = Environment.GetEnvironmentVariable("CODEX_APP_RUNTIME_CLI_PATH");
        if (File.Exists(fromHost)) return fromHost!;
        var bin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (Directory.Exists(bin))
        {
            var candidates = Directory.EnumerateDirectories(bin).Select(dir => Path.Combine(dir, "codex.exe"))
                .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc);
            if (candidates.FirstOrDefault() is { } candidate) return candidate;
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (File.Exists(Path.Combine(dir, "codex.exe"))) return Path.Combine(dir, "codex.exe");
        throw new FileNotFoundException("没有找到 Codex CLI，请先安装并登录 Codex 桌面版。");
    }

    public static async Task<CodexResidentConnection> ConnectAsync(string? executable, CancellationToken token)
    {
        var connection = new CodexResidentConnection(FindExecutable(executable));
        try
        {
            await connection.CallAsync("initialize", new
            {
                clientInfo = new { name = "ai_repair_shop", version = "0.6.1" },
                capabilities = new { experimentalApi = true }
            }, token);
            await connection._process.StandardInput.WriteLineAsync("{\"method\":\"initialized\"}");
            await connection._process.StandardInput.FlushAsync(token);
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    public async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken token)
    {
        await _calls.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var id = ++_nextId;
            var line = JsonSerializer.Serialize(new { id, method, @params = parameters });
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), timeout.Token);
            await _process.StandardInput.FlushAsync(timeout.Token);
            while (true)
            {
                var response = await _process.StandardOutput.ReadLineAsync(timeout.Token);
                if (response is null) throw new IOException("Codex 连接已断开。");
                using var json = JsonDocument.Parse(response);
                var root = json.RootElement;
                if (!root.TryGetProperty("id", out var responseId) || !responseId.TryGetInt32(out var number) || number != id) continue;
                if (root.TryGetProperty("error", out var error))
                    throw new CodexRpcException(error.GetProperty("message").GetString() ?? "Codex 拒绝了请求。");
                return root.GetProperty("result").Clone();
            }
        }
        finally { _calls.Release(); }
    }

    private async Task DrainErrorsAsync()
    {
        try { while (await _process.StandardError.ReadLineAsync() is not null) { } }
        catch (Exception) when (_disposed) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _process.StandardInput.Close(); } catch (IOException) { }
        // Only the private read/queue API client created above is stopped, never the Desktop host.
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        finally { _process.Dispose(); }
    }
}

internal sealed class CodexRpcException(string message) : Exception(message);

internal static class CodexMessageRouting
{
    public static bool IsOwnFeedback(string text) => ResidentFeedbackBatch.IsAutomaticFeedback(text) ||
        text.StartsWith("【人工智障修理厂连接测试", StringComparison.Ordinal) ||
        text.StartsWith("【人工智障修理厂自动通道自测", StringComparison.Ordinal);

    public static string Text(JsonElement item) => item.TryGetProperty("content", out var content)
        ? string.Join("\n", content.EnumerateArray().Where(value => value.TryGetProperty("text", out _))
            .Select(value => value.GetProperty("text").GetString())) : string.Empty;

    public static ResidentChatTarget? ParseEntry(string threadId, string title, JsonElement entry)
    {
        var item = entry.GetProperty("item");
        if (item.GetProperty("type").GetString() != "userMessage" || IsOwnFeedback(Text(item))) return null;
        if (!entry.TryGetProperty("startedAtMs", out var timestamp) ||
            timestamp.ValueKind != JsonValueKind.Number || !timestamp.TryGetInt64(out var milliseconds))
            throw new InvalidDataException("用户消息没有可确认的发送时间，已停止自动选取目标。");
        var id = item.GetProperty("id").GetString()!;
        try
        {
            return new ResidentChatTarget("local", threadId, title.Length > 120 ? title[..120] : title,
                id, DateTimeOffset.FromUnixTimeMilliseconds(milliseconds));
        }
        catch (ArgumentOutOfRangeException error)
        { throw new InvalidDataException("用户消息时间超出有效范围，已停止自动选取目标。", error); }
    }

    public static Task<ResidentChatTarget?> ReadLatestHumanAsync(IResidentCodexConnection connection,
        string threadId, string title, CancellationToken token, DateTimeOffset? atOrBefore = null) =>
        ReadLatestHumanAsync(connection.CallAsync, threadId, title, token, atOrBefore);

    internal static async Task<ResidentChatTarget?> ReadLatestHumanAsync(
        Func<string, object, CancellationToken, Task<JsonElement>> call,
        string threadId, string title, CancellationToken token, DateTimeOffset? atOrBefore = null)
    {
        string? cursor = null;
        // Summary pages avoid reading tool output. Read only the identified user items for exact timestamps.
        for (var page = 0; page < 20; page++)
        {
            var turns = await call("thread/turns/list", new
            { threadId, limit = 10, itemsView = "summary", sortDirection = "desc", cursor }, token);
            foreach (var turn in turns.GetProperty("data").EnumerateArray())
            {
                var turnId = turn.GetProperty("id").GetString();
                var ids = turn.GetProperty("items").EnumerateArray()
                    .Where(item => item.GetProperty("type").GetString() == "userMessage")
                    .Select(item => item.GetProperty("id").GetString()!).ToArray();
                if (ids.Length == 0) continue;
                if (ids.Length > 100) throw new InvalidDataException("聊天摘要中的消息数量超过可验证范围。");
                var items = await call("thread/items/read", new
                { threadId, turnId, itemIds = ids }, token);
                var entries = items.GetProperty("data").EnumerateArray().ToArray();
                // A missing item can be the newest human submission. Do not quietly fall back to another chat.
                if (!ids.All(id => entries.Any(entry => entry.GetProperty("item").GetProperty("id").GetString() == id)))
                    throw new InvalidDataException("聊天消息仍在更新，尚不能确认敲打时的目标。");
                var latest = LatestEligible(entries, threadId, title, atOrBefore);
                if (latest is not null) return latest;

                // A turn summary contains only the latest user message. A new steering message or our
                // queued feedback can hide an earlier human message in the SAME active turn. Read
                // backwards from the oldest summary user item instead of skipping straight to an older turn.
                object? itemCursor = new { type = "item", itemId = ids[0] };
                for (var itemPage = 0; itemPage < 100; itemPage++)
                {
                    var earlier = await call("thread/items/list", new
                    { threadId, turnId, limit = 100, sortDirection = "desc", cursor = itemCursor }, token);
                    latest = LatestEligible(earlier.GetProperty("data").EnumerateArray(), threadId, title, atOrBefore);
                    if (latest is not null) return latest;
                    itemCursor = earlier.GetProperty("nextCursor").GetString();
                    if (itemCursor is null) break;
                    if (itemPage == 99)
                        throw new InvalidDataException("本轮历史过长，尚不能确认敲打时的目标。");
                }
            }
            cursor = turns.GetProperty("nextCursor").GetString();
            if (cursor is null) return null;
        }
        throw new InvalidDataException("最近 200 轮均无可确认时间的人工消息，已停止自动选取目标。");
    }

    private static ResidentChatTarget? LatestEligible(IEnumerable<JsonElement> entries,
        string threadId, string title, DateTimeOffset? atOrBefore)
    {
        ResidentChatTarget? latest = null;
        foreach (var entry in entries)
        {
            var candidate = ParseEntry(threadId, title, entry);
            if (candidate is null || (atOrBefore.HasValue && candidate.LastHumanMessageUtc > atOrBefore.Value)) continue;
            if (latest is null || candidate.LastHumanMessageUtc > latest.LastHumanMessageUtc) latest = candidate;
        }
        return latest;
    }
}
