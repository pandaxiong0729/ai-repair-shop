using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using QiaoQiaoAI.UI;

namespace QiaoQiaoAI.Preview;

internal sealed class ResidentFeedbackController : IDisposable
{
    private readonly VentSurface _surface;
    private readonly string _directory;
    private readonly string _codexPath;
    private readonly Func<CancellationToken, Task<IResidentCodexConnection>> _connect;
    private readonly ResidentChatRegistry _registry = new();
    private readonly ResidentFeedbackCollector _collector;
    private readonly ResidentFeedbackJournal _journal;
    private readonly ResidentFeedbackCycleStore _cycleStore;
    private readonly CancellationTokenSource _stop = new();
    private readonly DispatcherTimer _timer;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private IResidentCodexConnection? _connection;
    private bool _started, _disposed, _sending;
    private DateTimeOffset _lastRefresh;
    private DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private ResidentFeedbackBatch? _lastBatch;
    private string? _queuedId;
    public event EventHandler? StatusChanged;
    public event EventHandler? StopRequested;
    public string TargetTitle => _collector.HasCycleActivity
        ? _collector.CycleTarget?.Title ?? "本轮未绑定聊天（重置后重试）"
        : _registry.Latest?.Title ?? "尚未找到聊天";
    public string ConnectionStatus { get; private set; } = "正在连接 Codex…";
    public string DeliveryStatus { get; private set; } = "停手 2 秒自动反馈一次；重置前继续玩不再发送。";

    public ResidentFeedbackController(VentSurface surface, string dataDir, string codexPath,
        Func<CancellationToken, Task<IResidentCodexConnection>>? connect = null)
    {
        _surface = surface; _directory = Path.GetFullPath(dataDir); _codexPath = codexPath;
        _connect = connect ?? (async token => await CodexResidentConnection.ConnectAsync(_codexPath, token));
        Directory.CreateDirectory(_directory);
        _journal = new ResidentFeedbackJournal(Path.Combine(_directory, "rounds"));
        try
        {
            var legacy = _journal.Snapshot().LastOrDefault(entry => entry.Batch.Target is not null);
            _cycleStore = new ResidentFeedbackCycleStore(_directory, legacy?.Batch.RoundId);
            var state = _cycleStore.Current;
            var previous = _journal.Snapshot().LastOrDefault(entry => entry.Batch.RoundId == state.CycleId);
            _collector = new ResidentFeedbackCollector(_registry, cycleId: state.CycleId,
                consumed: state.Consumed, previousTarget: previous?.Batch.Target);
            if (state.Consumed)
                DeliveryStatus = _cycleStore.RecoveryRequired
                    ? "上次反馈状态无法确认；请按重置开启新一轮。"
                    : "本轮反馈已结束；重置前继续玩不会重复发送。";
        }
        catch { _journal.Dispose(); throw; }
        _surface.HitRecorded += OnHit;
        _surface.InteractionReset += OnReset;
        _timer = new DispatcherTimer(DispatcherPriority.Background, surface.Dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += OnTick;
    }

    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;
        _timer.Start();
        await RefreshAsync();
    }

    private void OnHit(object? sender, HitRecordedEventArgs args)
    {
        if (_collector.IsConsumed) { Publish(); return; }
        if (_surface.IsPressActive) _collector.BeginGesture();
        _collector.Record(args.Tool, args.Character, args.Count);
        DeliveryStatus = $"本次 {_collector.PendingCount} 下 · 松手后自动反馈一次";
        Publish();
    }

    private void OnReset(object? sender, EventArgs args)
    {
        if (_disposed) return;
        try
        {
            var next = _cycleStore.Reset();
            _collector.ResetCycle(next.CycleId);
            _lastBatch = null;
            _queuedId = null;
            DeliveryStatus = "已重置；下一轮敲打自动反馈一次。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A reset is effective for feedback only after the new boundary is durable.
            _collector.BlockCycle();
            DeliveryStatus = "反馈重置尚未保存；本轮不会重新发送，请再按重置。";
            WriteError(error);
        }
        Publish();
    }

    private async void OnTick(object? sender, EventArgs args)
    {
        try
        {
        if (_disposed) return;
        var stopFile = Path.Combine(_directory, "stop.request");
        if (File.Exists(stopFile) && File.GetLastWriteTimeUtc(stopFile) >= _startedAt.UtcDateTime)
        { StopRequested?.Invoke(this, EventArgs.Empty); return; }
        if (_surface.IsPressActive && !_collector.IsGestureActive) _collector.BeginGesture();
        else if (!_surface.IsPressActive) _collector.EndGesture();
        if (!_sending && _collector.TrySealDue() is { } batch)
        {
            // Consume synchronously, before waiting for a connection or making any RPC.
            // Continued hits and process restarts cannot open another round implicitly.
            if (!_cycleStore.TryConsume(batch.RoundId)) return;
            _sending = true;
            try { await DeliverAsync(batch); }
            finally { _sending = false; }
        }
        if (!_disposed && DateTimeOffset.UtcNow - _lastRefresh >= TimeSpan.FromSeconds(2))
            await RefreshAsync();
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception error) when (_disposed) { System.Diagnostics.Trace.TraceInformation(error.GetType().Name); }
        catch (Exception error) { WriteError(error); DeliveryStatus = "本次处理未完成；重置前不再尝试发送。"; Publish(); }
    }

    private async Task RefreshAsync()
    {
        if (!await _operations.WaitAsync(0)) return;
        _lastRefresh = DateTimeOffset.UtcNow;
        try
        {
            await EnsureConnectedAsync();
            var targets = await ReadTargetsAsync();
            _registry.ReplaceSnapshot(targets);
            ConnectionStatus = targets.Count == 0 ? "未找到带消息时间的本地聊天；暂不发送。" : "已连接 Codex · 跟随最近发过消息的聊天";
            await CheckReceiptAsync();
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception error)
        {
            _connection?.Dispose(); _connection = null;
            _registry.ReplaceSnapshot([]);
            ConnectionStatus = "连接中断 · 将自动重连；不会补发旧敲打";
            WriteError(error);
        }
        finally
        {
            _lastRefresh = DateTimeOffset.UtcNow;
            _operations.Release();
            if (!_disposed) Publish();
        }
    }

    private async Task<List<ResidentChatTarget>> ReadTargetsAsync(DateTimeOffset? atOrBefore = null)
    {
            var targets = new List<ResidentChatTarget>();
            string? cursor = null;
            bool done = false;
            for (var page = 0; page < 10 && !done; page++)
            {
                var list = await _connection!.CallAsync("thread/list", new
                {
                    limit = 20, sortKey = "updated_at", useStateDbOnly = true, cursor,
                    sourceKinds = new[] { "vscode", "appServer", "cli" }
                }, _stop.Token);
                foreach (var thread in list.GetProperty("data").EnumerateArray())
                {
                    var updated = thread.GetProperty("updatedAt").GetInt64();
                    if (targets.Count > 0 && DateTimeOffset.FromUnixTimeSeconds(updated + 1) < targets.Max(t => t.LastHumanMessageUtc))
                    { done = true; break; }
                    var id = thread.GetProperty("id").GetString()!;
                    var title = thread.TryGetProperty("name", out var name) ? name.GetString() : null;
                    title = string.IsNullOrWhiteSpace(title) ? "Codex 聊天 " + id[..8] : title;
                    var human = await CodexMessageRouting.ReadLatestHumanAsync(_connection, id, title, _stop.Token, atOrBefore);
                    if (human is not null) targets.Add(human);
                }
                cursor = list.GetProperty("nextCursor").GetString();
                if (cursor is null) done = true;
            }
            if (!done) throw new InvalidDataException("聊天记录变化过多，尚不能确定最近消息。");
            return targets;
    }

    private async Task EnsureConnectedAsync()
    {
        if (_connection is not null) return;
        var connected = await _connect(_stop.Token);
        if (_disposed) { connected.Dispose(); throw new OperationCanceledException(); }
        _connection = connected;
    }

    private async Task DeliverAsync(ResidentFeedbackBatch batch)
    {
        await _operations.WaitAsync();
        bool attempted = false;
        try
        {
            if (_disposed || !IsCurrentCycle(batch.RoundId)) return;
            if (batch.Target is not null)
            {
                await EnsureConnectedAsync();
                // Resolve against the first hit's timestamp, not the latest poll or the end of the burst.
                // A message sent to another window halfway through cannot take over this round.
                var atFirstHit = await ReadTargetsAsync(batch.StartedUtc);
                batch = batch with { Target = atFirstHit.OrderByDescending(t => t.LastHumanMessageUtc).FirstOrDefault() };
            }
            // Reset while resolving an unsent round cancels it; never send stale work later.
            if (_disposed || !IsCurrentCycle(batch.RoundId)) return;
            _collector.ResolveCycleTarget(batch.RoundId, batch.Target);
            _journal.Enqueue(batch);
            if (batch.Target is null)
            { SetCycleStatus(batch.RoundId, "本轮未发送：敲打时未连接聊天；按重置后重试。"); return; }
            if (!_journal.TryBeginDelivery(batch.RoundId, out _)) return;
            await EnsureConnectedAsync();
            if (_disposed || !IsCurrentCycle(batch.RoundId)) return;
            attempted = true;
            var result = await _connection!.CallAsync("thread/queue/add", new
            {
                threadId = batch.Target.ThreadId, clientUserMessageId = batch.RoundId.ToString("D"),
                input = new[] { new { type = "text", text = batch.Prompt, text_elements = Array.Empty<object>() } }
            }, _stop.Token);
            var queuedId = result.GetProperty("queuedSubmission").GetProperty("id").GetString();
            _journal.CompleteDelivery(batch.RoundId, ResidentDeliveryState.Sent, "codex_queue_accepted:" + queuedId);
            if (IsCurrentCycle(batch.RoundId))
            {
                _queuedId = queuedId;
                _lastBatch = batch;
                DeliveryStatus = $"已自动提交 1 条 → {batch.Target.Title}；等待 Codex 处理。重置前不再发送。";
            }
        }
        catch (Exception error)
        {
            if (_disposed) return;
            var state = attempted && error is not CodexRpcException ? ResidentDeliveryState.Unknown : ResidentDeliveryState.Failed;
            _journal.CompleteDelivery(batch.RoundId, state, error.GetType().Name);
            SetCycleStatus(batch.RoundId, state == ResidentDeliveryState.Unknown
                ? "本次发送结果不确定，不会重发；按重置开启下一轮。"
                : "本次未发送成功；请在连接恢复后按重置再试。");
            _connection?.Dispose(); _connection = null;
            WriteError(error);
        }
        finally { _operations.Release(); if (!_disposed) Publish(); }
    }

    private async Task CheckReceiptAsync()
    {
        var batch = _lastBatch;
        if (batch?.Target is not { } target || _queuedId is null || _connection is null) return;
        var turns = await _connection.CallAsync("thread/turns/list", new
        { threadId = target.ThreadId, limit = 5, itemsView = "summary", sortDirection = "desc" }, _stop.Token);
        if (!IsCurrentCycle(batch.RoundId) || _lastBatch?.RoundId != batch.RoundId) return;
        foreach (var turn in turns.GetProperty("data").EnumerateArray())
            foreach (var item in turn.GetProperty("items").EnumerateArray())
                if (item.GetProperty("type").GetString() == "userMessage" &&
                    CodexMessageRouting.Text(item).StartsWith($"{ResidentFeedbackBatch.PromptPrefix}{batch.RoundId:D}]", StringComparison.Ordinal))
                {
                    DeliveryStatus = $"聊天已收到 1 条反馈 → {target.Title}；重置前不再发送。";
                    _queuedId = null;
                    return;
                }
    }

    private bool IsCurrentCycle(Guid cycleId) => _cycleStore.Current.CycleId == cycleId;

    private void SetCycleStatus(Guid cycleId, string message)
    {
        if (IsCurrentCycle(cycleId)) DeliveryStatus = message;
    }

    private void Publish()
    {
        if (_disposed) return;
        try
        {
            var path = Path.Combine(_directory, "status.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new
            {
                schema = 1, pid = Environment.ProcessId, updatedUtc = DateTimeOffset.UtcNow,
                target = _collector.HasCycleActivity ? _collector.CycleTarget : _registry.Latest,
                connection = ConnectionStatus, delivery = DeliveryStatus, pendingHits = _collector.PendingCount,
                cycleId = _collector.CycleId, cycleConsumed = _collector.IsConsumed,
                lastRoundId = _lastBatch?.RoundId, queuedSubmissionId = _queuedId
            }));
            File.Move(path + ".tmp", path, true);
        }
        catch (IOException) { }
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void WriteError(Exception error)
    {
        // No prompts, history, environment or credentials are logged.
        try { File.WriteAllText(Path.Combine(_directory, "connection-error.txt"), error.GetType().Name + ": " + error.Message); }
        catch (IOException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _stop.Cancel(); _timer.Stop(); _timer.Tick -= OnTick;
        _surface.HitRecorded -= OnHit;
        _surface.InteractionReset -= OnReset;
        _connection?.Dispose();
        _journal.Dispose();
        StatusChanged = null;
        try { File.WriteAllText(Path.Combine(_directory, "status.json"), JsonSerializer.Serialize(new { schema = 1, pid = Environment.ProcessId, running = false, updatedUtc = DateTimeOffset.UtcNow })); }
        catch (IOException) { }
    }
}
