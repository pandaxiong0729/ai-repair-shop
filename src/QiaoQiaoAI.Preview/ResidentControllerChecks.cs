using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using QiaoQiaoAI.UI;

namespace QiaoQiaoAI.Preview;

/// <summary>Exercises real surface events and dispatcher timing against an in-memory protocol peer.</summary>
internal static class ResidentControllerChecks
{
    public static void Run(Action<string, Action> check, string assets)
    {
        check("resident pauses and tool changes never resend before reset; reset arms one new feedback", () =>
        {
            using var fixture = new Fixture(assets);
            fixture.Peer.AddHuman("chat-a", "正在修的回答", DateTimeOffset.UtcNow.AddSeconds(-5));
            fixture.Start();
            Require(fixture.Controller.TargetTitle == "正在修的回答", "Initial chat was not bound.");

            fixture.Surface.SelectedTool = InteractionTool.Hammer;
            fixture.Surface.BeginPress(new Point(250, 275));
            fixture.Surface.AdvanceForTesting(.9);
            Require(fixture.Surface.IsPressActive && fixture.Surface.HitCount > 1, "Real hammer hold did not repeat.");
            Pump(TimeSpan.FromMilliseconds(30));
            fixture.Peer.AddHuman("chat-b", "后来发消息的窗口", DateTimeOffset.UtcNow);
            Pump(TimeSpan.FromMilliseconds(2200));
            Require(fixture.Peer.Submissions.Count == 0, "Held input was prematurely split into feedback rounds.");
            Require(fixture.Controller.TargetTitle == "正在修的回答", "The second chat stole a held round.");

            fixture.Surface.EndPress();
            for (var i = 0; i < 12; i++) fixture.Surface.Hit(InteractionTool.Hammer, new Point(250, 275));
            int hits = fixture.Surface.HitCount;
            PumpUntil(() => fixture.Peer.Submissions.Count == 1, TimeSpan.FromSeconds(3));
            var sent = fixture.Peer.Submissions.Single();
            Require(sent.ThreadId == "chat-a", "Feedback was delivered to the later window.");
            Require(ResidentFeedbackBatch.IsAutomaticFeedback(sent.Prompt), "Feedback has no recognizable deduplication prefix.");
            Require(fixture.Controller.DeliveryStatus.Contains("等待 Codex 处理", StringComparison.Ordinal),
                "A queue acknowledgement was mislabeled as a received chat message.");
            var round = ReadOnlyRound(fixture.Directory);
            Require(round.Batch.Total == hits && round.State == ResidentDeliveryState.Sent, "The delivered aggregate lost hits.");

            fixture.Peer.EchoAcceptedMessage(sent);
            Pump(TimeSpan.FromMilliseconds(2300));
            Require(fixture.Controller.DeliveryStatus.StartsWith("聊天已收到 1 条反馈", StringComparison.Ordinal),
                "Confirmed appearance in chat history did not update receipt status.");
            fixture.Surface.SelectedCharacter = CharacterKind.DeepSeek;
            fixture.Surface.SelectedTool = InteractionTool.Whip;
            fixture.Surface.ClearEffects();
            foreach (var tool in Enum.GetValues<InteractionTool>())
            {
                fixture.Surface.SelectedTool = tool;
                fixture.Surface.Hit(tool, new Point(250, 275));
            }
            fixture.Surface.SelectedTool = InteractionTool.Whip;
            Pump(TimeSpan.FromMilliseconds(2300));
            Require(fixture.Peer.Submissions.Count == 1, "A pause, clear or tool change submitted another feedback without reset.");
            fixture.Surface.ResetInteraction();
            fixture.Surface.ResetInteraction();
            Pump(TimeSpan.FromMilliseconds(150));
            Require(fixture.Peer.Submissions.Count == 1, "Reset sent an additional feedback message.");
            Require(fixture.Surface.HitCount == 0 && fixture.Surface.SelectedTool == InteractionTool.Whip &&
                fixture.Surface.SelectedCharacter == CharacterKind.DeepSeek, "Visual reset lost current selections.");
            fixture.Surface.Hit(InteractionTool.Whip, new Point(250, 275));
            PumpUntil(() => fixture.Peer.Submissions.Count == 2, TimeSpan.FromSeconds(3));
            Require(fixture.Peer.Submissions.Last().ThreadId == "chat-b" && fixture.Peer.Submissions.Last().ClientId != sent.ClientId,
                "A new reset cycle did not send once to the newly selected chat.");
        });

        check("resident never attaches an unbound hit to a chat discovered later", () =>
        {
            using var fixture = new Fixture(assets);
            fixture.Surface.Hit(InteractionTool.Hammer, new Point(250, 275));
            fixture.Peer.AddHuman("new-chat", "连接后出现的聊天", DateTimeOffset.UtcNow.AddSeconds(-1));
            fixture.Start();
            Pump(TimeSpan.FromMilliseconds(2300));
            Require(fixture.Peer.Submissions.Count == 0, "An earlier unbound hit was sent to a newly connected chat.");
            var round = ReadOnlyRound(fixture.Directory);
            Require(round.Batch.Target is null && round.State == ResidentDeliveryState.Failed,
                "Unbound feedback was not recorded as unsent.");

            // Connecting alone cannot rearm feedback; an explicit reset opens the next cycle.
            fixture.Surface.Hit(InteractionTool.Poop, new Point(250, 275));
            Pump(TimeSpan.FromMilliseconds(2300));
            Require(fixture.Peer.Submissions.Count == 0, "Connection recovery silently rearmed an unbound cycle.");
            fixture.Surface.ResetInteraction();
            fixture.Surface.Hit(InteractionTool.Poop, new Point(250, 275));
            PumpUntil(() => fixture.Peer.Submissions.Count == 1, TimeSpan.FromSeconds(3));
            Require(fixture.Peer.Submissions.Single().ThreadId == "new-chat", "A fresh bound interaction did not submit.");
        });

        check("resident reset cancels unsent hits and stale in-flight acknowledgement cannot overwrite new cycle", () =>
        {
            using var fixture = new Fixture(assets);
            fixture.Peer.AddHuman("chat-a", "重置边界测试", DateTimeOffset.UtcNow.AddSeconds(-1));
            fixture.Start();
            fixture.Surface.Hit(InteractionTool.PaperReject, new Point(250, 275));
            fixture.Surface.ResetInteraction();
            Pump(TimeSpan.FromMilliseconds(2300));
            Require(fixture.Peer.Submissions.Count == 0, "Reset before the quiet period submitted the discarded draft.");

            fixture.Peer.HoldNextSubmission = true;
            fixture.Surface.Hit(InteractionTool.Hammer, new Point(250, 275));
            PumpUntil(() => fixture.Peer.Submissions.Count == 1, TimeSpan.FromSeconds(3));
            var old = fixture.Peer.Submissions.Single();
            fixture.Surface.ResetInteraction();
            Require(fixture.Controller.DeliveryStatus.StartsWith("已重置", StringComparison.Ordinal), "Reset state was not visible.");
            fixture.Peer.EchoAcceptedMessage(old);
            fixture.Peer.CompleteHeldSubmission();
            Pump(TimeSpan.FromMilliseconds(250));
            Require(fixture.Controller.DeliveryStatus.StartsWith("已重置", StringComparison.Ordinal),
                "Old in-flight acknowledgement overwrote the new reset cycle.");
            fixture.Surface.Hit(InteractionTool.Egg, new Point(250, 275));
            PumpUntil(() => fixture.Peer.Submissions.Count == 2, TimeSpan.FromSeconds(3));
            Require(fixture.Peer.Submissions.Last().ClientId != old.ClientId, "Reset reused the prior delivery ID.");
        });

        check("resident restart keeps a consumed cycle blocked until reset", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "QiaoQiaoAI-Tests", "resident-restart-" + Guid.NewGuid().ToString("N"));
            var persistent = new ResidentFeedbackCycleStore(directory);
            Require(persistent.TryConsume(persistent.Current.CycleId), "Could not establish consumed restart fixture.");
            using var fixture = new Fixture(assets, directory);
            fixture.Peer.AddHuman("chat-a", "重启测试", DateTimeOffset.UtcNow.AddSeconds(-1));
            fixture.Start();
            fixture.Surface.Hit(InteractionTool.Hammer, new Point(250, 275));
            Pump(TimeSpan.FromMilliseconds(2300));
            Require(fixture.Peer.Submissions.Count == 0, "Restart automatically submitted another message without reset.");
            fixture.Surface.ResetInteraction();
            fixture.Surface.Hit(InteractionTool.Stamp, new Point(250, 275));
            PumpUntil(() => fixture.Peer.Submissions.Count == 1, TimeSpan.FromSeconds(3));
        });

        check("resident window has no manual send form and closing releases its controller", () =>
        {
            using var fixture = new Fixture(assets);
            fixture.Peer.AddHuman("window-chat", "状态展示测试", DateTimeOffset.UtcNow.AddSeconds(-1));
            fixture.Start();
            var window = new ResidentPetWindow(fixture.Surface, fixture.Controller);
            try
            {
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(410, 500));
                content.Arrange(new Rect(0, 0, 410, 500));
                content.UpdateLayout();
                Require(window.AllowsTransparency && window.Topmost && !window.ShowInTaskbar,
                    "Resident window is not a transparent desktop pet.");
                Require(window.TargetTitle.Contains("状态展示测试", StringComparison.Ordinal), "Target chat is not visible.");
                Require(!Descendants<FrameworkElement>(content).Any(element =>
                    Equals(element.Tag, "SendFeedbackRound") || Equals(element.Tag, "FeedbackReason")),
                    "The resident mode still presents a manual feedback form.");
                Require(Descendants<Button>(content).Any(button => Equals(button.Tag, "ResetInteraction")),
                    "The resident surface no longer exposes visual reset.");
            }
            finally { window.Close(); }
            Require(fixture.Peer.Disposed, "Window close did not dispose its API connection.");
            int calls = fixture.Peer.Calls;
            Pump(TimeSpan.FromMilliseconds(250));
            Require(fixture.Peer.Calls == calls, "The closed window kept its polling timer alive.");
        });
    }

    private static ResidentFeedbackEntry ReadOnlyRound(string directory)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(Path.Combine(directory, "rounds"), "*.json").Single()));
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        return document.RootElement.Deserialize<ResidentFeedbackEntry>(options)!;
    }

    private static void PumpUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate() && watch.Elapsed < timeout) Pump(TimeSpan.FromMilliseconds(50));
        Require(predicate(), "Dispatcher operation did not complete within its expected bound.");
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T result) yield return result;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; }
        public FakeConnection Peer { get; } = new();
        public VentSurface Surface { get; }
        public ResidentFeedbackController Controller { get; }

        public Fixture(string assets, string? directory = null)
        {
            Directory = directory ?? Path.Combine(Path.GetTempPath(), "QiaoQiaoAI-Tests", "resident-" + Guid.NewGuid().ToString("N"));
            Surface = new VentSurface(assets) { SoundEnabled = false };
            Surface.Measure(new Size(500, 650));
            Surface.Arrange(new Rect(0, 0, 500, 650));
            Surface.UpdateLayout();
            Controller = new ResidentFeedbackController(Surface, Directory, "unused-test-codex.exe",
                _ => Task.FromResult<IResidentCodexConnection>(Peer));
        }

        public void Start()
        {
            var task = Controller.StartAsync();
            PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(1));
            task.GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            Controller.Dispose();
            Surface.Dispose();
            // Test outputs are retained in the named temporary directory for failure diagnosis.
        }
    }

    private sealed record Submission(string ThreadId, string Prompt, string ClientId);
    private sealed record Message(string Id, string Text, long Milliseconds);
    private sealed record Chat(string Id, string Title, List<Message> Messages);

    private sealed class FakeConnection : IResidentCodexConnection
    {
        private readonly Dictionary<string, Chat> _chats = [];
        public List<Submission> Submissions { get; } = [];
        public bool Disposed { get; private set; }
        public int Calls { get; private set; }
        public bool HoldNextSubmission { get; set; }
        private TaskCompletionSource<JsonElement>? _heldSubmission;
        private string? _heldClientId;

        public void CompleteHeldSubmission()
        {
            var pending = _heldSubmission ?? throw new InvalidOperationException("No held submission.");
            pending.SetResult(JsonSerializer.SerializeToElement(new { queuedSubmission = new { id = "accepted-" + _heldClientId } }));
            _heldSubmission = null;
        }

        public void AddHuman(string id, string title, DateTimeOffset time)
        {
            if (!_chats.TryGetValue(id, out var chat)) _chats[id] = chat = new Chat(id, title, []);
            chat.Messages.Add(new Message(Guid.NewGuid().ToString("D"), "A human request", time.ToUnixTimeMilliseconds()));
        }

        public void EchoAcceptedMessage(Submission submission) => _chats[submission.ThreadId].Messages.Add(
            new Message(submission.ClientId, submission.Prompt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        public Task<JsonElement> CallAsync(string method, object parameters, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Disposed, this);
            Calls++;
            var args = JsonSerializer.SerializeToElement(parameters);
            if (method == "thread/list") return Result(new
            {
                data = _chats.Values.OrderByDescending(chat => chat.Messages.Max(message => message.Milliseconds))
                    .Select(chat => new { id = chat.Id, name = chat.Title, updatedAt = chat.Messages.Max(message => message.Milliseconds) / 1000 }).ToArray(),
                nextCursor = (string?)null
            });
            var thread = _chats[args.GetProperty("threadId").GetString()!];
            if (method == "thread/turns/list") return Result(new
            {
                data = new[] { new { id = "turn-" + thread.Id, items = new[] { User(thread.Messages.Last()) } } },
                nextCursor = (string?)null
            });
            if (method == "thread/items/read")
            {
                var ids = args.GetProperty("itemIds").EnumerateArray().Select(value => value.GetString()).ToHashSet();
                return Result(new { data = thread.Messages.Where(message => ids.Contains(message.Id)).Select(Entry).ToArray() });
            }
            if (method == "thread/items/list")
            {
                var anchor = args.GetProperty("cursor").GetProperty("itemId").GetString();
                var before = thread.Messages.FindIndex(message => message.Id == anchor);
                return Result(new { data = thread.Messages.Take(before).Reverse().Select(Entry).ToArray(), nextCursor = (string?)null });
            }
            if (method == "thread/queue/add")
            {
                Require(!args.TryGetProperty("model", out _) && !args.TryGetProperty("approvalPolicy", out _),
                    "Feedback changed the target chat's model or permission settings.");
                var prompt = args.GetProperty("input")[0].GetProperty("text").GetString()!;
                var clientId = args.GetProperty("clientUserMessageId").GetString()!;
                Submissions.Add(new Submission(thread.Id, prompt, clientId));
                if (HoldNextSubmission)
                {
                    HoldNextSubmission = false;
                    _heldClientId = clientId;
                    _heldSubmission = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
                    return _heldSubmission.Task;
                }
                return Result(new { queuedSubmission = new { id = "accepted-" + clientId } });
            }
            throw new InvalidOperationException("Unexpected resident test RPC: " + method);
        }

        private static object User(Message message) => new
        { type = "userMessage", id = message.Id, content = new[] { new { type = "text", text = message.Text } } };
        private static object Entry(Message message) => new
        { item = User(message), startedAtMs = message.Milliseconds, completedAtMs = message.Milliseconds };
        private static Task<JsonElement> Result(object value) => Task.FromResult(JsonSerializer.SerializeToElement(value));
        public void Dispose() => Disposed = true;
    }
}
