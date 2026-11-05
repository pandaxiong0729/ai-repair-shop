using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QiaoQiaoAI.UI;

/// <summary>
/// Optional one-round local bridge. Construct only in explicitly bound skill mode.
/// Hits stay in memory; Seal writes one aggregate, never one message per hit.
/// </summary>
public sealed class FeedbackRound : IDisposable
{
    private readonly object _gate = new();
    private readonly VentSurface _surface;
    private readonly string _directory;
    private readonly FileStream _writerLease;
    private readonly Dictionary<InteractionTool, int> _tools = [];
    private readonly Dictionary<CharacterKind, int> _characters = [];
    private int _total;
    private string _reason = string.Empty;
    private bool _sealed;
    private bool _disposed;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public FeedbackRound(string sessionDirectory, VentSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        _surface = surface;
        _directory = Path.GetFullPath(sessionDirectory);
        var manifest = Path.Combine(_directory, "session.json");
        if (!File.Exists(manifest)) throw new InvalidDataException("反馈模式需要技能创建的 session.json；普通玩具请直接启动。");
        if (new FileInfo(manifest).Length > 8192) throw new InvalidDataException("反馈会话配置过大。");
        using var json = JsonDocument.Parse(File.ReadAllText(manifest, Encoding.UTF8));
        var root = json.RootElement;
        if (root.GetProperty("schema").GetInt32() != 1 || root.GetProperty("target").GetString() != "current-agent" ||
            !Guid.TryParse(root.GetProperty("sessionId").GetString(), out var id))
            throw new InvalidDataException("反馈会话配置无效。");
        if (root.TryGetProperty("expiresUtc", out var expires) && expires.GetDateTimeOffset() <= DateTimeOffset.UtcNow)
            throw new InvalidDataException("反馈会话已过期，请在当前聊天重新调用技能。");
        SessionId = id.ToString("D");
        // FileShare.None prevents two apps from collecting the same session concurrently.
        // Unlike a CreateNew sentinel, a crashed process releases this OS lease.
        _writerLease = new FileStream(Path.Combine(_directory, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        _sealed = File.Exists(Path.Combine(_directory, "batch.json")) || File.Exists(Path.Combine(_directory, "ended.json"));
        surface.HitRecorded += OnHit;
    }

    public string SessionId { get; }
    public int TotalCount { get { lock (_gate) return _total; } }
    public bool IsSealed { get { lock (_gate) return _sealed; } }
    public bool IsClaimed => File.Exists(Path.Combine(_directory, "claimed.json"));
    public string Reason
    {
        get { lock (_gate) return _reason; }
        set
        {
            lock (_gate)
            {
                if (_sealed || _disposed) return;
                var normalized = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
                _reason = normalized.Length <= 120 ? normalized : normalized[..120];
            }
        }
    }

    private void OnHit(object? sender, HitRecordedEventArgs args) => Record(args.Tool, args.Character, args.Count);

    /// <summary>Records counts only. Does not collect the editable reply card or any chat content.</summary>
    public void Record(InteractionTool tool, CharacterKind character, int count = 1)
    {
        lock (_gate)
        {
            if (_sealed || _disposed || count <= 0 || !Enum.IsDefined(tool) || !Enum.IsDefined(character)) return;
            int accepted = Math.Min(count, int.MaxValue - _total);
            if (accepted <= 0) return;
            _total += accepted;
            _tools[tool] = _tools.GetValueOrDefault(tool) + accepted;
            _characters[character] = _characters.GetValueOrDefault(character) + accepted;
        }
    }

    /// <summary>
    /// Atomically seals this session once. Returns false on repeated calls.
    /// An empty round creates ended.json rather than a corrective prompt.
    /// A successful seal means locally queued, not received or acted on by a model.
    /// </summary>
    public bool Seal(string trigger = "send")
    {
        lock (_gate)
        {
            if (_sealed || _disposed) return false;
            var filename = _total == 0 && string.IsNullOrWhiteSpace(_reason) ? "ended.json" : "batch.json";
            object payload = filename == "ended.json"
                ? new { schema = 1, sessionId = SessionId, roundId = SessionId, endedUtc = DateTimeOffset.UtcNow, status = "empty" }
                : new
                {
                    schema = 1,
                    sessionId = SessionId,
                    roundId = SessionId,
                    endedUtc = DateTimeOffset.UtcNow,
                    trigger = trigger == "close" ? "close" : "send",
                    total = _total,
                    tools = _tools.OrderBy(pair => (int)pair.Key).Select(pair => new { tool = pair.Key.ToString(), count = pair.Value }).ToArray(),
                    characters = _characters.OrderBy(pair => (int)pair.Key).Select(pair => new { character = pair.Key.ToString(), count = pair.Value }).ToArray(),
                    reason = _reason
                };
            var destination = Path.Combine(_directory, filename);
            var temporary = Path.Combine(_directory, $".batch-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(payload, JsonOptions), new UTF8Encoding(false));
                File.Move(temporary, destination, overwrite: false);
                _sealed = true;
                return true;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    /// <summary>Ends an unsent session without a corrective prompt. Cannot undo an already sealed batch.</summary>
    public bool Cancel()
    {
        lock (_gate)
        {
            if (_sealed || _disposed) return false;
            var destination = Path.Combine(_directory, "ended.json");
            var temporary = Path.Combine(_directory, $".end-{Guid.NewGuid():N}.tmp");
            try
            {
                var payload = new { schema = 1, sessionId = SessionId, roundId = SessionId, endedUtc = DateTimeOffset.UtcNow, status = "cancelled" };
                File.WriteAllText(temporary, JsonSerializer.Serialize(payload, JsonOptions), new UTF8Encoding(false));
                File.Move(temporary, destination, overwrite: false);
                _sealed = true;
                return true;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    /// <summary>Unsubscribes only; the window must explicitly call Seal on send/close.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _surface.HitRecorded -= OnHit;
            _writerLease.Dispose();
        }
    }
}
