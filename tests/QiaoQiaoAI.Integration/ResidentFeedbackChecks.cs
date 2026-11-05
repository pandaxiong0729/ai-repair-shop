using System.IO;
using QiaoQiaoAI.UI;

namespace QiaoQiaoAI.Integration;

internal static class ResidentFeedbackChecks
{
    public static void Run(Action<string, Action> check)
    {
        check("resident_latest_human_message_ignores_assistant_and_own_feedback", () =>
        {
            var registry = new ResidentChatRegistry();
            var a = Chat("a", 1);
            var b = Chat("b", 2);
            Require(registry.Observe(a, ChatMessageOrigin.Human), "Initial human message was ignored.");
            Require(registry.Observe(b, ChatMessageOrigin.Human), "Latest human message was ignored.");
            foreach (var origin in new[] { ChatMessageOrigin.Assistant, ChatMessageOrigin.Background, ChatMessageOrigin.AutomaticFeedback })
                Require(!registry.Observe(Chat("a", 3), origin), "Non-human event changed routing.");
            Require(!registry.Observe(b with { LastHumanMessageUtc = b.LastHumanMessageUtc.AddDays(1) }, ChatMessageOrigin.Human), "Duplicate message changed recency.");
            Require(registry.Latest?.ThreadId == "b", "Routing followed background activity.");
            Require(registry.Observe(Chat("a", 4), ChatMessageOrigin.Human) && registry.Latest?.ThreadId == "a", "Later human submission did not switch target.");
        });

        check("resident_first_hit_freezes_chat_and_long_hold_stays_one_round", () =>
        {
            var clock = new ManualClock();
            var registry = new ResidentChatRegistry();
            registry.Observe(Chat("a", 1), ChatMessageOrigin.Human);
            var collector = new ResidentFeedbackCollector(registry, clock);
            collector.BeginGesture();
            registry.Observe(Chat("b", 2), ChatMessageOrigin.Human);
            collector.Record(InteractionTool.Whip, CharacterKind.DeepSeek, 8);
            registry.Observe(Chat("a", 3), ChatMessageOrigin.Human);
            clock.Advance(TimeSpan.FromMinutes(1));
            Require(collector.TrySealDue() is null, "A held gesture was split by idle timer.");
            collector.Record(InteractionTool.Whip, CharacterKind.DeepSeek, 12);
            collector.EndGesture();
            clock.Advance(TimeSpan.FromMilliseconds(1999));
            Require(collector.TrySealDue() is null, "Round sealed before release quiet period.");
            clock.Advance(TimeSpan.FromMilliseconds(1));
            var batch = collector.TrySealDue() ?? throw new InvalidOperationException("Round did not automatically seal.");
            Require(batch.Target?.ThreadId == "b" && batch.Total == 20, "First actual hit did not freeze target/counts.");
            Require(collector.TrySealDue() is null, "A round was sealed twice.");
        });

        check("resident_every_tool_and_later_bursts_stay_blocked_until_explicit_reset", () =>
        {
            var clock = new ManualClock();
            var registry = new ResidentChatRegistry();
            registry.Observe(Chat("a", 1), ChatMessageOrigin.Human);
            var collector = new ResidentFeedbackCollector(registry, clock);
            foreach (var tool in Enum.GetValues<InteractionTool>())
            {
                collector.BeginGesture();
                collector.Record(tool, CharacterKind.Codex);
                collector.EndGesture();
                clock.Advance(TimeSpan.FromMilliseconds(100));
                Require(collector.TrySealDue() is null, "Rapid clicks produced more than one round.");
            }
            clock.Advance(TimeSpan.FromSeconds(2));
            var first = collector.TrySealDue()!;
            Require(first.Total == 9 && first.Tools.Count == 9, "Not all tools were aggregated.");
            registry.Observe(Chat("b", 3), ChatMessageOrigin.Human);
            foreach (var tool in Enum.GetValues<InteractionTool>())
            {
                collector.BeginGesture();
                collector.Record(tool, CharacterKind.DeepSeek, 100);
                collector.EndGesture();
                clock.Advance(TimeSpan.FromMinutes(2));
                Require(collector.TrySealDue() is null && !collector.HasPendingHits,
                    "A later pause, new chat or tool rearmed feedback without reset.");
            }
            collector.ResetCycle();
            collector.Record(InteractionTool.Hammer, CharacterKind.Doubao);
            clock.Advance(TimeSpan.FromSeconds(2));
            var second = collector.TrySealDue()!;
            Require(first.RoundId != second.RoundId && second.Total == 1 && second.Target?.ThreadId == "b",
                "Explicit reset did not create an independent new cycle.");
            Require(ResidentFeedbackBatch.IsAutomaticFeedback(second.Prompt), "Generated prompt cannot be filtered from human recency.");
        });

        check("resident_reset_before_quiet_discards_unsent_hits_without_submitting", () =>
        {
            var clock = new ManualClock();
            var registry = new ResidentChatRegistry();
            registry.Observe(Chat("a", 1), ChatMessageOrigin.Human);
            var collector = new ResidentFeedbackCollector(registry, clock);
            collector.Record(InteractionTool.Stamp, CharacterKind.Codex, 15);
            clock.Advance(TimeSpan.FromSeconds(1));
            collector.ResetCycle();
            collector.ResetCycle();
            clock.Advance(TimeSpan.FromMinutes(1));
            Require(collector.TrySealDue() is null && !collector.IsConsumed && !collector.HasPendingHits,
                "Reset submitted or kept the discarded draft.");
            collector.Record(InteractionTool.PaperReject, CharacterKind.Doubao, 2);
            clock.Advance(TimeSpan.FromSeconds(2));
            Require(collector.TrySealDue()?.Total == 2, "New cycle included pre-reset hits.");
        });

        check("resident_cycle_consumption_survives_restart_and_only_reset_rearms", () => WithDirectory(directory =>
        {
            var first = new ResidentFeedbackCycleStore(directory);
            var id = first.Current.CycleId;
            Require(first.TryConsume(id) && !first.TryConsume(id), "One cycle was consumed twice.");
            var restarted = new ResidentFeedbackCycleStore(directory);
            Require(restarted.Current.CycleId == id && restarted.Current.Consumed && !restarted.TryConsume(id),
                "Restart implicitly rearmed the consumed cycle.");
            var next = restarted.Reset();
            Require(next.CycleId != id && !next.Consumed && !restarted.TryConsume(id), "Old async work could claim the new cycle.");
            var resetPersisted = new ResidentFeedbackCycleStore(directory);
            Require(resetPersisted.Current.CycleId == next.CycleId && !resetPersisted.Current.Consumed,
                "Explicit reset did not survive restart.");
        }));

        check("resident_upgrade_and_corrupt_cycle_state_fail_closed_until_reset", () => WithDirectory(directory =>
        {
            var oldRound = Guid.NewGuid();
            var migrated = new ResidentFeedbackCycleStore(directory, oldRound);
            Require(migrated.Current.Consumed && migrated.Current.CycleId == oldRound,
                "Upgrade with previous feedback silently rearmed another message.");
            File.WriteAllText(Path.Combine(directory, "feedback-cycle.json"), "broken-json");
            var damaged = new ResidentFeedbackCycleStore(directory);
            Require(damaged.Current.Consumed && damaged.RecoveryRequired && !damaged.TryConsume(damaged.Current.CycleId),
                "Unreadable state permitted duplicate feedback.");
            var reset = damaged.Reset();
            Require(!reset.Consumed && !damaged.RecoveryRequired && damaged.TryConsume(reset.CycleId),
                "Explicit reset could not recover a damaged state file.");
            foreach (var invalid in new[] { "[]", "null", "{}", "{\"schema\":1,\"cycleId\":\"" + Guid.NewGuid() + "\"}" })
            {
                File.WriteAllText(Path.Combine(directory, "feedback-cycle.json"), invalid);
                var missing = new ResidentFeedbackCycleStore(directory);
                Require(missing.Current.Consumed && missing.RecoveryRequired, "Incomplete state silently armed a new cycle.");
            }
        }));

        check("resident_unbound_hits_are_never_redirected_to_later_chat", () =>
        {
            var clock = new ManualClock();
            var registry = new ResidentChatRegistry();
            var collector = new ResidentFeedbackCollector(registry, clock);
            collector.Record(InteractionTool.Egg, CharacterKind.Doubao);
            registry.Observe(Chat("a", 1), ChatMessageOrigin.Human);
            collector.Record(InteractionTool.Hammer, CharacterKind.Doubao);
            clock.Advance(TimeSpan.FromSeconds(2));
            var batch = collector.TrySealDue()!;
            Require(batch.Target is null && batch.Total == 2, "Unbound hits were redirected.");
            WithDirectory(directory =>
            {
                using var journal = new ResidentFeedbackJournal(directory, clock);
                var entry = journal.Enqueue(batch);
                Require(entry.State == ResidentDeliveryState.Failed && !journal.TryBeginDelivery(batch.RoundId, out _), "Unbound round was sendable.");
            });
        });

        check("resident_idle_clock_is_monotonic_and_invalid_counts_do_not_send", () =>
        {
            var clock = new ManualClock();
            var registry = new ResidentChatRegistry();
            registry.Observe(Chat("a", 1), ChatMessageOrigin.Human);
            var collector = new ResidentFeedbackCollector(registry, clock);
            collector.Record((InteractionTool)999, CharacterKind.Codex);
            collector.Record(InteractionTool.Hammer, CharacterKind.Codex, 0);
            Require(!collector.HasPendingHits, "Invalid hit started a round.");
            collector.Record(InteractionTool.Hammer, CharacterKind.Codex, int.MaxValue);
            collector.Record(InteractionTool.Hammer, CharacterKind.Codex, 1);
            clock.MoveWallClock(TimeSpan.FromDays(1));
            Require(collector.TrySealDue() is null, "Wall clock jump ended a round.");
            clock.Advance(TimeSpan.FromSeconds(2));
            Require(collector.TrySealDue()?.Total == int.MaxValue, "Count overflowed.");
        });

        check("resident_delivery_journal_claims_once_and_never_replays_unknown", () => WithDirectory(directory =>
        {
            var batch = Batch();
            using (var journal = new ResidentFeedbackJournal(directory))
            {
                journal.Enqueue(batch);
                Require(journal.TryBeginDelivery(batch.RoundId, out var claimed) && claimed?.State == ResidentDeliveryState.Sending, "Delivery not claimed.");
                Require(!journal.TryBeginDelivery(batch.RoundId, out _), "Concurrent duplicate delivery allowed.");
                var leaseHeld = false;
                try { using var competing = new ResidentFeedbackJournal(directory); }
                catch (IOException) { leaseHeld = true; }
                Require(leaseHeld, "Two resident writers opened the same journal.");
            }
            using var recovered = new ResidentFeedbackJournal(directory);
            Require(recovered.Snapshot().Single().State == ResidentDeliveryState.Unknown, "Interrupted delivery pretended to be queued/sent.");
            Require(!recovered.TryBeginDelivery(batch.RoundId, out _), "Unknown delivery was retried automatically.");
            Require(recovered.Enqueue(batch).State == ResidentDeliveryState.Unknown, "Duplicate enqueue erased delivery status.");
        }));

        check("resident_delivery_sent_requires_ack_and_survives_restart", () => WithDirectory(directory =>
        {
            var batch = Batch();
            using (var journal = new ResidentFeedbackJournal(directory))
            {
                journal.Enqueue(batch);
                Require(!journal.CompleteDelivery(batch.RoundId, ResidentDeliveryState.Sent), "Queued was marked sent without an attempt.");
                Require(journal.TryBeginDelivery(batch.RoundId, out _), "Queued batch unavailable.");
                Require(journal.CompleteDelivery(batch.RoundId, ResidentDeliveryState.Sent, "server_ack"), "Acknowledged attempt not recorded.");
                Require(!journal.CompleteDelivery(batch.RoundId, ResidentDeliveryState.Unknown), "Terminal result changed.");
            }
            using var recovered = new ResidentFeedbackJournal(directory);
            Require(recovered.Snapshot().Single().State == ResidentDeliveryState.Sent && !recovered.TryBeginDelivery(batch.RoundId, out _), "Sent batch was replayable after restart.");
        }));
    }

    private static ResidentChatTarget Chat(string id, int second) => new("local", id, "聊天 " + id, "message-" + second,
        new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero).AddSeconds(second));

    private static ResidentFeedbackBatch Batch() => new(Guid.NewGuid(), Chat("a", 1), DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow, 1, [new("Hammer", 1)], [new("Codex", 1)]);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void WithDirectory(Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "qiaoqiao-resident-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { test(directory); }
        finally
        {
            // Tests create only flat files; do not recursively delete an arbitrary path.
            foreach (var path in Directory.EnumerateFiles(directory)) File.Delete(path);
            Directory.Delete(directory, recursive: false);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        private DateTimeOffset _utc = new(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => _utc;
        public void Advance(TimeSpan duration) { _ticks += duration.Ticks; _utc += duration; }
        public void MoveWallClock(TimeSpan duration) => _utc += duration;
    }
}
