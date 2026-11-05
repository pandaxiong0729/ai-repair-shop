using System.IO;
using System.Text.Json;

namespace QiaoQiaoAI.UI;

public sealed record ResidentFeedbackCycleState(int Schema, Guid CycleId, bool Consumed, DateTimeOffset UpdatedUtc);

/// <summary>
/// Durable reset boundary. The owning controller holds the delivery journal's exclusive
/// writer lease. Persist consumption before any external request so restart cannot rearm it.
/// </summary>
public sealed class ResidentFeedbackCycleStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private ResidentFeedbackCycleState _current;
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ResidentFeedbackCycleStore(string directory, Guid? legacySubmittedRoundId = null)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(Path.GetFullPath(directory), "feedback-cycle.json");
        if (!File.Exists(_path))
        {
            var migrated = legacySubmittedRoundId is { } previous && previous != Guid.Empty;
            _current = new(1, migrated ? legacySubmittedRoundId!.Value : Guid.NewGuid(), migrated, DateTimeOffset.UtcNow);
            Persist(_current);
            return;
        }
        try
        {
            if (new FileInfo(_path).Length > 4096) throw new InvalidDataException("Feedback cycle state is too large.");
            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("consumed", out var consumed) ||
                consumed.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !document.RootElement.TryGetProperty("updatedUtc", out var updated) ||
                updated.ValueKind != JsonValueKind.String || !updated.TryGetDateTimeOffset(out _))
                throw new InvalidDataException("Incomplete feedback cycle state.");
            _current = document.RootElement.Deserialize<ResidentFeedbackCycleState>(Options)
                ?? throw new InvalidDataException("Feedback cycle state is empty.");
            if (_current.Schema != 1 || _current.CycleId == Guid.Empty)
                throw new InvalidDataException("Invalid feedback cycle state.");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            // Never interpret an unreadable latch as permission to send again. Preserve the
            // original file; an explicit reset can replace it with a fresh, durable cycle.
            _current = new(1, Guid.NewGuid(), true, DateTimeOffset.UtcNow);
            RecoveryRequired = true;
        }
    }

    public bool RecoveryRequired { get; private set; }
    public ResidentFeedbackCycleState Current { get { lock (_gate) return _current; } }

    public bool TryConsume(Guid expectedCycleId)
    {
        lock (_gate)
        {
            if (_current.CycleId != expectedCycleId || _current.Consumed) return false;
            var next = _current with { Consumed = true, UpdatedUtc = DateTimeOffset.UtcNow };
            Persist(next);
            _current = next;
            return true;
        }
    }

    public ResidentFeedbackCycleState Reset()
    {
        lock (_gate)
        {
            var next = new ResidentFeedbackCycleState(1, Guid.NewGuid(), false, DateTimeOffset.UtcNow);
            Persist(next);
            _current = next;
            RecoveryRequired = false;
            return next;
        }
    }

    private void Persist(ResidentFeedbackCycleState state)
    {
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, state, Options);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
