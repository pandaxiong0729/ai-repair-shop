using System.IO;
using System.Text.Json;
using QiaoQiaoAI.UI;

namespace QiaoQiaoAI.Preview;

internal static class ResidentConnectionCheck
{
    public static async Task<int> RunAsync(string path, string? executable)
    {
        try
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var connection = await CodexResidentConnection.ConnectAsync(executable, stop.Token);
            var list = await connection.CallAsync("thread/list", new
            { limit = 10, sortKey = "updated_at", useStateDbOnly = true, sourceKinds = new[] { "vscode", "appServer", "cli" } }, stop.Token);
            var candidates = new List<ResidentChatTarget>();
            foreach (var thread in list.GetProperty("data").EnumerateArray())
            {
                if (candidates.Count > 0 && DateTimeOffset.FromUnixTimeSeconds(thread.GetProperty("updatedAt").GetInt64() + 1) < candidates.Max(t => t.LastHumanMessageUtc)) break;
                var id = thread.GetProperty("id").GetString()!;
                var title = thread.GetProperty("name").GetString() ?? "Codex 聊天";
                var target = await CodexMessageRouting.ReadLatestHumanAsync(connection, id, title, stop.Token);
                if (target is not null) candidates.Add(target);
            }
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                passed = candidates.Count > 0, readOnly = true, executable = connection.Executable,
                target = candidates.OrderByDescending(t => t.LastHumanMessageUtc).FirstOrDefault(), checkedUtc = DateTimeOffset.UtcNow
            }));
            return candidates.Count > 0 ? 0 : 1;
        }
        catch (Exception error)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new { passed = false, readOnly = true, error = error.Message }));
            return 1;
        }
    }
}
