namespace QiaoQiaoAI.UI;

public enum ChatMessageOrigin { Human, AutomaticFeedback, Assistant, Background }

/// <summary>Routing metadata only; never stores a conversation or prompt body.</summary>
public sealed record ResidentChatTarget(string HostId, string ThreadId, string Title,
    string HumanMessageId, DateTimeOffset LastHumanMessageUtc);

/// <summary>Tracks actual human submissions, not window focus or assistant activity.</summary>
public sealed class ResidentChatRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Host, string Thread), ResidentChatTarget> _chats = [];

    public ResidentChatTarget? Latest
    {
        get
        {
            lock (_gate)
                return _chats.Values.OrderByDescending(chat => chat.LastHumanMessageUtc)
                    .ThenBy(chat => chat.HostId, StringComparer.Ordinal)
                    .ThenBy(chat => chat.ThreadId, StringComparer.Ordinal).FirstOrDefault();
        }
    }

    public IReadOnlyList<ResidentChatTarget> Snapshot()
    {
        lock (_gate) return _chats.Values.OrderByDescending(chat => chat.LastHumanMessageUtc).ToArray();
    }

    /// <summary>
    /// Call from a verified user-submission event. Own feedback and assistant/background
    /// events must retain their real origin, even if a server represents them as user items.
    /// </summary>
    public bool Observe(ResidentChatTarget chat, ChatMessageOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(chat);
        if (origin != ChatMessageOrigin.Human) return false;
        chat = chat with { Title = NormalizeTitle(chat.Title) };
        Validate(chat);
        lock (_gate)
        {
            var key = (chat.HostId, chat.ThreadId);
            if (_chats.TryGetValue(key, out var previous) &&
                (previous.HumanMessageId == chat.HumanMessageId || previous.LastHumanMessageUtc >= chat.LastHumanMessageUtc))
                return false;
            _chats[key] = chat;
            return true;
        }
    }

    public bool Remove(string hostId, string threadId)
    {
        lock (_gate) return _chats.Remove((hostId, threadId));
    }

    public void ReplaceSnapshot(IEnumerable<ResidentChatTarget> chats)
    {
        var snapshot = chats.ToArray();
        foreach (var chat in snapshot) Validate(chat);
        lock (_gate)
        {
            _chats.Clear();
            foreach (var chat in snapshot) _chats[(chat.HostId, chat.ThreadId)] = chat;
        }
    }

    internal static void Validate(ResidentChatTarget chat)
    {
        if (string.IsNullOrWhiteSpace(chat.HostId) || chat.HostId.Length > 256 ||
            string.IsNullOrWhiteSpace(chat.ThreadId) || chat.ThreadId.Length > 256 ||
            string.IsNullOrWhiteSpace(chat.HumanMessageId) || chat.HumanMessageId.Length > 256 ||
            (chat.Title?.Length ?? 0) > 120)
            throw new ArgumentException("Chat routing requires bounded host, thread and human-message IDs.", nameof(chat));
    }

    private static string NormalizeTitle(string title)
    {
        var normalized = string.Join(" ", (title ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 120 ? normalized : normalized[..120];
    }
}

public sealed record ResidentFeedbackCount(string Name, int Count);

public sealed record ResidentFeedbackBatch(Guid RoundId, ResidentChatTarget? Target,
    DateTimeOffset StartedUtc, DateTimeOffset EndedUtc, int Total,
    IReadOnlyList<ResidentFeedbackCount> Tools, IReadOnlyList<ResidentFeedbackCount> Characters)
{
    public const string PromptPrefix = "[人工智障修理厂反馈:";
    [System.Text.Json.Serialization.JsonIgnore]
    public string Prompt => $"{PromptPrefix}{RoundId:D}] 你这次做得不好，我很生气。请检查刚才的回答并纠正。";

    public static bool IsAutomaticFeedback(string? text) =>
        text?.StartsWith(PromptPrefix, StringComparison.Ordinal) == true;
}

/// <summary>
/// One explicit-reset cycle may seal once. The first quiet pause seals its collected
/// hits; subsequent hits are entertainment only until ResetCycle. This class never sends.
/// </summary>
public sealed class ResidentFeedbackCollector
{
    private readonly object _gate = new();
    private readonly ResidentChatRegistry _registry;
    private readonly TimeProvider _clock;
    private Draft? _draft;
    private bool _gestureActive;
    private long _lastActivity;
    private Guid _cycleId;
    private bool _consumed;
    private bool _cycleHasStarted;
    private ResidentChatTarget? _cycleTarget;

    public ResidentFeedbackCollector(ResidentChatRegistry registry, TimeProvider? clock = null, TimeSpan? quietPeriod = null,
        Guid? cycleId = null, bool consumed = false, ResidentChatTarget? previousTarget = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        _clock = clock ?? TimeProvider.System;
        QuietPeriod = quietPeriod ?? TimeSpan.FromSeconds(2);
        if (QuietPeriod <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(quietPeriod));
        _cycleId = cycleId ?? Guid.NewGuid();
        if (_cycleId == Guid.Empty) throw new ArgumentException("A cycle requires a nonempty ID.", nameof(cycleId));
        _consumed = consumed;
        _cycleHasStarted = consumed;
        _cycleTarget = previousTarget;
    }

    public TimeSpan QuietPeriod { get; }
    public bool IsGestureActive { get { lock (_gate) return _gestureActive; } }
    public bool HasPendingHits { get { lock (_gate) return _draft is not null; } }
    public int PendingCount { get { lock (_gate) return _draft?.Total ?? 0; } }
    public ResidentChatTarget? PendingTarget { get { lock (_gate) return _draft?.Target; } }
    public Guid CycleId { get { lock (_gate) return _cycleId; } }
    public bool IsConsumed { get { lock (_gate) return _consumed; } }
    public bool HasCycleActivity { get { lock (_gate) return _cycleHasStarted; } }
    public ResidentChatTarget? CycleTarget { get { lock (_gate) return _cycleTarget; } }

    /// <summary>Only an explicit reset arms a new cycle and discards any unsent draft.</summary>
    public void ResetCycle(Guid? cycleId = null)
    {
        var id = cycleId ?? Guid.NewGuid();
        if (id == Guid.Empty) throw new ArgumentException("A cycle requires a nonempty ID.", nameof(cycleId));
        lock (_gate)
        {
            _cycleId = id;
            _consumed = false;
            _cycleHasStarted = false;
            _cycleTarget = null;
            _draft = null;
            _gestureActive = false;
            _lastActivity = _clock.GetTimestamp();
        }
    }

    public void BlockCycle()
    {
        lock (_gate) { _consumed = true; _draft = null; _gestureActive = false; }
    }

    /// <summary>Reconcile the displayed target with the verified first-hit-time lookup.</summary>
    public void ResolveCycleTarget(Guid cycleId, ResidentChatTarget? target)
    {
        lock (_gate) { if (_cycleId == cycleId) _cycleTarget = target; }
    }

    public void BeginGesture()
    {
        lock (_gate)
        {
            if (_consumed) return;
            _gestureActive = true;
            _lastActivity = _clock.GetTimestamp();
        }
    }

    /// <summary>Also call on lost capture, reset, tool change and cancellation of a held gesture.</summary>
    public void EndGesture()
    {
        lock (_gate)
        {
            if (!_gestureActive) return;
            _gestureActive = false;
            _lastActivity = _clock.GetTimestamp();
        }
    }

    public void Record(InteractionTool tool, CharacterKind character, int count = 1)
    {
        if (count <= 0 || !Enum.IsDefined(tool) || !Enum.IsDefined(character)) return;
        lock (_gate)
        {
            if (_consumed) return;
            _draft ??= new Draft(_cycleId, _registry.Latest, _clock.GetUtcNow());
            _cycleHasStarted = true;
            _cycleTarget = _draft.Target;
            var accepted = Math.Min(count, int.MaxValue - _draft.Total);
            _lastActivity = _clock.GetTimestamp();
            if (accepted == 0) return;
            _draft.Total += accepted;
            _draft.Tools[tool] = _draft.Tools.GetValueOrDefault(tool) + accepted;
            _draft.Characters[character] = _draft.Characters.GetValueOrDefault(character) + accepted;
        }
    }

    /// <summary>
    /// Timer-driven automatic seal. Consumes this reset cycle, including an unbound
    /// cycle. Pausing, reconnecting and choosing another tool never rearm it.
    /// </summary>
    public ResidentFeedbackBatch? TrySealDue()
    {
        lock (_gate)
        {
            if (_consumed || _draft is null || _gestureActive || _clock.GetElapsedTime(_lastActivity) < QuietPeriod) return null;
            var draft = _draft;
            _draft = null;
            _consumed = true;
            return new ResidentFeedbackBatch(draft.Id, draft.Target, draft.StartedUtc, _clock.GetUtcNow(), draft.Total,
                Array.AsReadOnly(draft.Tools.OrderBy(pair => (int)pair.Key).Select(pair => new ResidentFeedbackCount(pair.Key.ToString(), pair.Value)).ToArray()),
                Array.AsReadOnly(draft.Characters.OrderBy(pair => (int)pair.Key).Select(pair => new ResidentFeedbackCount(pair.Key.ToString(), pair.Value)).ToArray()));
        }
    }

    private sealed class Draft(Guid id, ResidentChatTarget? target, DateTimeOffset startedUtc)
    {
        public Guid Id { get; } = id;
        public ResidentChatTarget? Target { get; } = target;
        public DateTimeOffset StartedUtc { get; } = startedUtc;
        public int Total { get; set; }
        public Dictionary<InteractionTool, int> Tools { get; } = [];
        public Dictionary<CharacterKind, int> Characters { get; } = [];
    }
}
