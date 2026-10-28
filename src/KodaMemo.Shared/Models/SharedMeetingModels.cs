namespace KodaMemo.Shared.Models;

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

public enum TaskChecklistState
{
    Pending = 0,
    InProgress = 1,
    Completed = 2
}

public enum TaskOriginType
{
    ExtractedFromMeeting = 0,
    UserDictated = 1
}

public enum SyncState
{
    LocalOnly = 0,
    Synced = 1,
    PendingUpload = 2,
    ConflictPendingResolution = 3
}

public enum PetAssistantVisualState
{
    Idle = 0,
    Listening = 1,
    Processing = 2,
    Speaking = 3,
    Error = 4
}

public class MeetingSessionDto
{
    public string SessionUid { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public double DurationSeconds { get; set; }
    public string OriginDevice { get; set; } = "Android";
    public bool IsFromRealPhone { get; set; }
    public string LocalWavFileName { get; set; } = string.Empty;
    public long LocalWavSizeBytes { get; set; }
    public bool HasLocalAudioFile { get; set; } = true;
    public bool IsAudioSyncedToWeb { get; set; }
    public SyncState SyncStatus { get; set; } = SyncState.Synced;

    public int ActiveTranscriptVersion { get; set; } = 1;
    public string ActiveTranscriptSha256 { get; set; } = string.Empty;
    public List<TranscriptSegmentDto> Segments { get; set; } = new();
    public List<TranscriptVersionHistoryDto> VersionHistory { get; set; } = new();

    public int SummarySourceTranscriptVersion { get; set; } = 1;
    public string SummarySourceTranscriptSha256 { get; set; } = string.Empty;
    public string SummaryProviderModel { get; set; } = string.Empty;

    public List<MeetingTopicDto> MainTopics { get; set; } = new();
    public List<MeetingAgreementDto> Agreements { get; set; } = new();
    public List<TaskChecklistItemDto> Tasks { get; set; } = new();
    public MindMapNodeDto? MindMapRoot { get; set; }

    public bool IsSummaryStale =>
        SummarySourceTranscriptVersion != ActiveTranscriptVersion ||
        (!string.IsNullOrEmpty(SummarySourceTranscriptSha256) &&
         !string.Equals(SummarySourceTranscriptSha256, ActiveTranscriptSha256, StringComparison.OrdinalIgnoreCase));

    public string FormattedDuration => TimeSpan.FromSeconds(Math.Max(0, DurationSeconds)).ToString(@"mm\:ss");
    public string FormattedDateLocal => CreatedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
}

public class TranscriptSegmentDto
{
    public int SegmentNumber { get; set; }
    public string StartTimestamp { get; set; } = "00:00";
    public string EndTimestamp { get; set; } = "00:05";
    public string Text { get; set; } = string.Empty;
}

public class TranscriptVersionHistoryDto
{
    public int VersionNumber { get; set; }
    public string ContentSha256 { get; set; } = string.Empty;
    public string ModifiedByDevice { get; set; } = "Android";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string FullText { get; set; } = string.Empty;
}

public class MeetingTopicDto
{
    public string TopicUid { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string SummaryNotes { get; set; } = string.Empty;
    public string EvidenceQuote { get; set; } = string.Empty;
    public string TimestampReference { get; set; } = string.Empty;
    public CitationMatchStatus CitationStatus { get; set; } = CitationMatchStatus.NotEvaluated;
    public UserConfirmationStatus ConfirmationStatus { get; set; } = UserConfirmationStatus.PendingReview;
}

public class MeetingAgreementDto
{
    public string AgreementUid { get; set; } = Guid.NewGuid().ToString("N");
    public string Description { get; set; } = string.Empty;
    public string EvidenceQuote { get; set; } = string.Empty;
    public string TimestampReference { get; set; } = string.Empty;
    public CitationMatchStatus CitationStatus { get; set; } = CitationMatchStatus.NotEvaluated;
    public UserConfirmationStatus ConfirmationStatus { get; set; } = UserConfirmationStatus.PendingReview;
}

public class TaskChecklistItemDto
{
    public string TaskUid { get; set; } = Guid.NewGuid().ToString("N");
    public string? SessionUid { get; set; }
    public string SessionTitle { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Assignee { get; set; } = "No especificado";
    public string DueDateText { get; set; } = "No especificada";
    public TaskChecklistState State { get; set; } = TaskChecklistState.Pending;
    public TaskOriginType Origin { get; set; } = TaskOriginType.ExtractedFromMeeting;
    public string EvidenceQuote { get; set; } = string.Empty;
    public string TimestampReference { get; set; } = string.Empty;
    public CitationMatchStatus CitationStatus { get; set; } = CitationMatchStatus.NotEvaluated;
    public UserConfirmationStatus ConfirmationStatus { get; set; } = UserConfirmationStatus.PendingReview;
    public bool AssigneeVerifiedInTranscript { get; set; } = true;
    public bool DueDateVerifiedInTranscript { get; set; } = true;
    public string LastModifiedByDevice { get; set; } = "Android";
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public bool IsCompleted
    {
        get => State == TaskChecklistState.Completed;
        set => State = value ? TaskChecklistState.Completed : TaskChecklistState.Pending;
    }
}

public class MindMapNodeDto
{
    public string NodeId { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Label { get; set; } = string.Empty;
    public string Category { get; set; } = "root"; // root, topic, agreement, task, custom
    public string? ReferenceTimestamp { get; set; }
    public List<MindMapNodeDto> Children { get; set; } = new();
}

public class SyncPushPayloadDto
{
    public string DeviceId { get; set; } = "android-device";
    public DateTime SentAtUtc { get; set; } = DateTime.UtcNow;
    public List<MeetingSessionDto> Meetings { get; set; } = new();
}

public class SyncPushResultDto
{
    public bool Success { get; set; }
    public int InsertedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int ConflictVersionsPreserved { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class SyncPullResponseDto
{
    public DateTime ServerTimeUtc { get; set; }
    public string SqliteDbPath { get; set; } = string.Empty;
    public List<MeetingSessionDto> Meetings { get; set; } = new();
}

public class SyncPullResultDto
{
    public bool Success { get; set; }
    public int SessionsUpdatedFromWeb { get; set; }
    public int NewTranscriptVersionsImported { get; set; }
    public int ConcurrentConflictsDetectedAndPreserved { get; set; }
    public int TasksSyncedToPhone { get; set; }
    public string Message { get; set; } = string.Empty;
}
