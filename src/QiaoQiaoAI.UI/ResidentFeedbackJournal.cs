using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QiaoQiaoAI.UI;

public enum ResidentDeliveryState { Queued, Sending, Sent, Failed, Unknown }

public sealed record ResidentFeedbackEntry(int Schema, ResidentFeedbackBatch Batch,
    ResidentDeliveryState State, DateTimeOffset UpdatedUtc, string? Detail);

/// <summary>
/// Local delivery journal. Persist Sending before invoking a transport; only mark Sent
/// after the actual chat server acknowledges the request. Interrupted sends become
/// Unknown and are never retried automatically. FileShare.None enforces one writer.
/// </summary>
public sealed class ResidentFeedbackJournal : IDisposable
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly FileStream _lease;
    private readonly TimeProvider _clock;
    private readonly Dictionary<Guid, ResidentFeedbackEntry> _entries = [];
    private bool _disposed;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public ResidentFeedbackJournal(string directory, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        _lease = new FileStream(Path.Combine(_directory, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
            {
                if (new FileInfo(path).Length > 32768) throw new InvalidDataException("Feedback journal entry is too large.");
                var entry = JsonSerializer.Deserialize<ResidentFeedbackEntry>(File.ReadAllText(path), JsonOptions)
                    ?? throw new InvalidDataException("Feedback journal entry is empty.");
                Validate(entry);
                if (Path.GetFileNameWithoutExtension(path) != entry.Batch.RoundId.ToString("D"))
                    throw new InvalidDataException("Feedback journal ID does not match filename.");
                if (entry.State == ResidentDeliveryState.Sending)
                {
                    entry = entry with { State = ResidentDeliveryState.Unknown, UpdatedUtc = _clock.GetUtcNow(), Detail = "interrupted_send" };
                    Persist(entry);
                }
                _entries.Add(entry.Batch.RoundId, entry);
            }
        }
        catch { _lease.Dispose(); throw; }
    }

    public IReadOnlyList<ResidentFeedbackEntry> Snapshot()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _entries.Values.OrderBy(entry => entry.Batch.StartedUtc).ToArray();
        }
    }

    public ResidentFeedbackEntry Enqueue(ResidentFeedbackBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(batch.RoundId, out var existing)) return existing;
            var entry = new ResidentFeedbackEntry(1, batch,
                batch.Target is null ? ResidentDeliveryState.Failed : ResidentDeliveryState.Queued,
                _clock.GetUtcNow(), batch.Target is null ? "no_target_at_first_hit" : null);
            Validate(entry);
            Persist(entry);
            _entries.Add(batch.RoundId, entry);
            return entry;
        }
    }

    /// <summary>Claim once, durably, immediately before the external send attempt.</summary>
    public bool TryBeginDelivery(Guid roundId, out ResidentFeedbackEntry? entry)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(roundId, out entry) || entry.State != ResidentDeliveryState.Queued) return false;
            entry = entry with { State = ResidentDeliveryState.Sending, UpdatedUtc = _clock.GetUtcNow(), Detail = null };
            Persist(entry);
            _entries[roundId] = entry;
            return true;
        }
    }

    /// <summary>
    /// Sent requires server acceptance. Failed is a definite rejection/not sent; timeout,
    /// disconnect after dispatch or uncertain result is Unknown. None is auto-retriable.
    /// </summary>
    public bool CompleteDelivery(Guid roundId, ResidentDeliveryState outcome, string? detail = null)
    {
        if (outcome is not (ResidentDeliveryState.Sent or ResidentDeliveryState.Failed or ResidentDeliveryState.Unknown))
            throw new ArgumentOutOfRangeException(nameof(outcome));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(roundId, out var entry) || entry.State != ResidentDeliveryState.Sending) return false;
            var boundedDetail = detail?.Length > 160 ? detail[..160] : detail;
            entry = entry with { State = outcome, UpdatedUtc = _clock.GetUtcNow(), Detail = boundedDetail };
            Persist(entry);
            _entries[roundId] = entry;
            return true;
        }
    }

    private static void Validate(ResidentFeedbackEntry entry)
    {
        var batch = entry.Batch ?? throw new InvalidDataException("Missing feedback batch.");
        if (entry.Schema != 1 || batch.RoundId == Guid.Empty || batch.Total <= 0 ||
            !Enum.IsDefined(entry.State) || (entry.Detail?.Length ?? 0) > 160 ||
            batch.Tools is null || batch.Characters is null || batch.Tools.Count > 9 || batch.Characters.Count > 3 ||
            batch.Tools.Any(item => item is null || !Enum.TryParse<InteractionTool>(item.Name, out var tool) || !Enum.IsDefined(tool) || item.Count <= 0) ||
            batch.Characters.Any(item => item is null || !Enum.TryParse<CharacterKind>(item.Name, out var character) || !Enum.IsDefined(character) || item.Count <= 0) ||
            batch.Tools.Sum(item => (long)item.Count) != batch.Total || batch.Characters.Sum(item => (long)item.Count) != batch.Total ||
            batch.Tools.Select(item => item.Name).Distinct().Count() != batch.Tools.Count ||
            batch.Characters.Select(item => item.Name).Distinct().Count() != batch.Characters.Count)
            throw new InvalidDataException("Invalid feedback journal entry.");
        if (batch.Target is not null) ResidentChatRegistry.Validate(batch.Target);
        else if (entry.State != ResidentDeliveryState.Failed) throw new InvalidDataException("An unbound round cannot be delivered.");
    }

    private void Persist(ResidentFeedbackEntry entry)
    {
        var destination = Path.Combine(_directory, $"{entry.Batch.RoundId:D}.json");
        var temporary = Path.Combine(_directory, $".{entry.Batch.RoundId:D}-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, entry, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lease.Dispose();
        }
    }
}
