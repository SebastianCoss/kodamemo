using SQLite;

namespace KodaMemo.Models;

public enum SessionRecordingState
{
    Idle = 0,
    RecordingInProgress = 1,
    Paused = 2,
    PausedByInterruption = 3,
    Saved = 4,
    RecoveredAfterCrash = 5,
    Transcribing = 6,
    Transcribed = 7,
    Summarizing = 8,
    Completed = 9,
    Error = 10
}

public enum CitationMatchStatus
{
    NotEvaluated = 0,
    QuoteFoundExact = 1,
    QuoteFoundPartial = 2,
    QuoteNotFound = 3
}

public enum UserConfirmationStatus
{
    PendingReview = 0,
    ConfirmedByUser = 1
}

public enum BudgetReservationState
{
    Reserved = 0,
    Reconciled = 1,
    Released = 2,
    UnconfirmedTimeout = 3
}

[Table("RecordingSessions")]
public class RecordingSession
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed]
    public string SessionUid { get; set; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = string.Empty;

    public string WavFilePath { get; set; } = string.Empty;

    public SessionRecordingState State { get; set; } = SessionRecordingState.Idle;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public double DurationSeconds { get; set; }

    public long DataBytesWritten { get; set; }

    public int SampleRateHz { get; set; } = 16000;

    public int Channels { get; set; } = 1;

    public int BitsPerSample { get; set; } = 16;

    public int ActiveTranscriptVersion { get; set; }

    public string ActiveTranscriptSha256 { get; set; } = string.Empty;

    public bool WasRecoveredFromCrash { get; set; }

    public string RecoveryNotes { get; set; } = string.Empty;

    [Ignore]
    public string FormattedDuration => TimeSpan.FromSeconds(Math.Max(0, DurationSeconds)).ToString(@"mm\:ss");

    [Ignore]
    public string FormattedDateLocal => CreatedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");

    [Ignore]
    public string FormattedFileSize => DataBytesWritten > 0
        ? $"{(DataBytesWritten + 44) / 1024.0:F1} KB"
        : "0 KB";

    [Ignore]
    public string StateBadgeText => State switch
    {
        SessionRecordingState.RecordingInProgress => "🔴 Grabando",
        SessionRecordingState.Paused => "⏸️ En pausa",
        SessionRecordingState.PausedByInterruption => "⚠️ Pausado por interrupción de audio",
        SessionRecordingState.Saved => "✅ Guardado local (WAV 16kHz)",
        SessionRecordingState.RecoveredAfterCrash => "🛡️ Recuperado tras interrupción/cierre",
        SessionRecordingState.Transcribing => "⏳ Transcribiendo...",
        SessionRecordingState.Transcribed => "📝 Transcrito",
        SessionRecordingState.Summarizing => "⏳ Resumiendo...",
        SessionRecordingState.Completed => "✨ Completado",
        SessionRecordingState.Error => "❌ Error",
        _ => "Inactivo"
    };
}

[Table("TranscriptVersions")]
public class TranscriptVersionEntity
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed]
    public int SessionId { get; set; }

    public int VersionNumber { get; set; }

    public string ContentSha256 { get; set; } = string.Empty;

    public string TranscriptText { get; set; } = string.Empty;

    public string SourceProviderId { get; set; } = "local-stt";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

[Table("ModelPricingCatalog")]
public class ModelPricingCatalogEntity
{
    [PrimaryKey]
    public string ModelId { get; set; } = string.Empty;

    public string ProviderName { get; set; } = string.Empty;

    public decimal InputUsdPerMillionTokens { get; set; }

    public decimal OutputUsdPerMillionTokens { get; set; }

    public string OfficialSourceUrl { get; set; } = string.Empty;

    public string VerifiedDateIso { get; set; } = "2026-10-26";
}

[Table("UsageLedger")]
public class UsageLedgerEntry
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed]
    public string ReservationId { get; set; } = Guid.NewGuid().ToString("N");

    [Indexed]
    public string YearMonthKey { get; set; } = DateTime.UtcNow.ToString("yyyy-MM");

    public int? SessionId { get; set; }

    public string OperationType { get; set; } = string.Empty; // SummaryApi, AssistantChat, SttApi, PaidVoice

    public string ProviderId { get; set; } = string.Empty;

    public string ModelId { get; set; } = string.Empty;

    public int EstimatedInputTokens { get; set; }

    public int MaxReservedOutputTokens { get; set; }

    public int ActualInputTokens { get; set; }

    public int ActualOutputTokens { get; set; }

    public decimal ReservedCostUsd { get; set; }

    public decimal ActualCostUsd { get; set; }

    public BudgetReservationState State { get; set; } = BudgetReservationState.Reserved;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? ReconciledAtUtc { get; set; }
}

[Table("TaskChecklistItems")]
public class TaskChecklistEntity
{
    [PrimaryKey]
    public string TaskUid { get; set; } = Guid.NewGuid().ToString("N");

    [Indexed]
    public string SessionUid { get; set; } = string.Empty;

    public string SessionTitle { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Assignee { get; set; } = "No especificado";

    public string DueDateText { get; set; } = "No especificada";

    public bool IsCompleted { get; set; }

    public string EvidenceQuote { get; set; } = string.Empty;

    public string TimestampReference { get; set; } = string.Empty;

    public bool IsConfirmedByUser { get; set; }

    public string LastModifiedByDevice { get; set; } = "Android";

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
