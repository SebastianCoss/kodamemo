using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using KodaMemo.Data;
using KodaMemo.Models;
using KodaMemo.Shared.Models;
using KodaMemo.Shared.Services;

namespace KodaMemo.Services;

public class MeetingSyncService
{
    private readonly AppDatabase _db;
    private static readonly HttpClient Http = new(new SocketsHttpHandler()) { Timeout = TimeSpan.FromSeconds(20) };

    private static string GetDeviceId()
    {
        const string key = "sync-device-id";
        string id = Preferences.Default.Get(key, string.Empty);
        if (string.IsNullOrEmpty(id))
        {
            id = Guid.NewGuid().ToString("N");
            Preferences.Default.Set(key, id);
        }
        return id;
    }

    public MeetingSyncService(AppDatabase db)
    {
        _db = db;
    }

    public async Task<SyncPushResultDto> PushLocalSessionsToWebAsync(string baseUrl, string bearerToken, CancellationToken ct = default)
    {
        var sessions = await _db.GetSessionsAsync();
        var payload = new SyncPushPayloadDto
        {
            DeviceId = GetDeviceId(),
            SentAtUtc = DateTime.UtcNow
        };

        foreach (var s in sessions)
        {
            var versions = await _db.GetTranscriptVersionsAsync(s.Id);
            var localTasks = await _db.GetTasksBySessionUidAsync(s.SessionUid);

            var dto = new MeetingSessionDto
            {
                SessionUid = s.SessionUid,
                Title = s.Title,
                CreatedAtUtc = s.CreatedAtUtc,
                UpdatedAtUtc = s.UpdatedAtUtc,
                DurationSeconds = s.DurationSeconds,
                OriginDevice = "Android",
                IsFromRealPhone = true,
                LocalWavFileName = Path.GetFileName(s.WavFilePath),
                LocalWavSizeBytes = s.DataBytesWritten > 0 ? s.DataBytesWritten + 44 : 0,
                HasLocalAudioFile = !string.IsNullOrWhiteSpace(s.WavFilePath) && File.Exists(s.WavFilePath),
                IsAudioSyncedToWeb = false,
                SyncStatus = SyncState.Synced,
                ActiveTranscriptVersion = Math.Max(1, s.ActiveTranscriptVersion),
                ActiveTranscriptSha256 = s.ActiveTranscriptSha256,
                SummarySourceTranscriptVersion = Math.Max(1, s.ActiveTranscriptVersion),
                SummarySourceTranscriptSha256 = s.ActiveTranscriptSha256,
                SummaryProviderModel = versions.Count > 0 ? $"Local:{versions.First().SourceProviderId}" : "Sin transcribir"
            };

            foreach (var v in versions.OrderBy(x => x.VersionNumber))
            {
                dto.VersionHistory.Add(new TranscriptVersionHistoryDto
                {
                    VersionNumber = v.VersionNumber,
                    ContentSha256 = v.ContentSha256,
                    ModifiedByDevice = v.SourceProviderId.Contains("Web", StringComparison.OrdinalIgnoreCase)
                        ? v.SourceProviderId
                        : $"Android ({v.SourceProviderId})",
                    CreatedAtUtc = v.CreatedAtUtc,
                    FullText = v.TranscriptText
                });
            }

            foreach (var t in localTasks)
            {
                dto.Tasks.Add(new TaskChecklistItemDto
                {
                    TaskUid = t.TaskUid,
                    SessionUid = t.SessionUid,
                    SessionTitle = t.SessionTitle,
                    Description = t.Description,
                    Assignee = t.Assignee,
                    DueDateText = t.DueDateText,
                    IsCompleted = t.IsCompleted,
                    EvidenceQuote = t.EvidenceQuote,
                    TimestampReference = t.TimestampReference,
                    ConfirmationStatus = t.IsConfirmedByUser
                        ? Shared.Models.UserConfirmationStatus.ConfirmedByUser
                        : Shared.Models.UserConfirmationStatus.PendingReview,
                    LastModifiedByDevice = t.LastModifiedByDevice,
                    UpdatedAtUtc = t.UpdatedAtUtc
                });
            }

            var activeVer = versions.FirstOrDefault(v => v.VersionNumber == s.ActiveTranscriptVersion) ?? versions.LastOrDefault();
            if (activeVer is not null && !string.IsNullOrWhiteSpace(activeVer.TranscriptText))
            {
                int segNum = 1;
                foreach (string rawLine in activeVer.TranscriptText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var match = Regex.Match(rawLine.Trim(), @"^\[(\d{2}:\d{2})\s*-\s*(\d{2}:\d{2})\]\s*(.+)$");
                    if (match.Success)
                    {
                        dto.Segments.Add(new TranscriptSegmentDto
                        {
                            SegmentNumber = segNum++,
                            StartTimestamp = match.Groups[1].Value,
                            EndTimestamp = match.Groups[2].Value,
                            Text = match.Groups[3].Value.Trim()
                        });
                    }
                    else
                    {
                        dto.Segments.Add(new TranscriptSegmentDto
                        {
                            SegmentNumber = segNum++,
                            StartTimestamp = "00:00",
                            EndTimestamp = s.FormattedDuration,
                            Text = rawLine.Trim()
                        });
                    }
                }
            }

            SummaryEvidenceValidator.ValidateMeetingEvidence(dto);
            MindMapGenerator.BuildFromExistingSummary(dto);
            payload.Meetings.Add(dto);
        }

        string endpoint = $"{baseUrl.TrimEnd('/')}/api/sync/push";
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload)
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<SyncPushResultDto>(cancellationToken: ct);
        return result ?? new SyncPushResultDto { Success = true, Message = "OK" };
    }

    /// <summary>
    /// Sincronización Web → Android (/api/sync/pull) con detección y preservación sin pérdida
    /// de modificaciones concurrentes (Android Offline vs. Web Blazor).
    /// </summary>
    public async Task<SyncPullResultDto> PullWebChangesToLocalAsync(string baseUrl, string bearerToken, CancellationToken ct = default)
    {
        string endpoint = $"{baseUrl.TrimEnd('/')}/api/sync/pull";
        using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var pullData = await resp.Content.ReadFromJsonAsync<SyncPullResponseDto>(cancellationToken: ct);
        if (pullData is null)
        {
            return new SyncPullResultDto { Success = false, Message = "Respuesta vacía en /api/sync/pull" };
        }

        int sessionsUpdated = 0;
        int versionsImported = 0;
        int conflictsPreserved = 0;
        int tasksSynced = 0;

        foreach (var webMeeting in pullData.Meetings)
        {
            var localSession = await _db.GetSessionByUidAsync(webMeeting.SessionUid);
            if (localSession is null)
            {
                continue;
            }

            var localVersionsBefore = await _db.GetTranscriptVersionsAsync(localSession.Id);
            bool hadLocalOfflineModification = localVersionsBefore.Any(lv =>
                lv.VersionNumber > 1 &&
                !webMeeting.VersionHistory.Any(wv => string.Equals(wv.ContentSha256, lv.ContentSha256, StringComparison.OrdinalIgnoreCase)));

            bool anyImportedForSession = false;
            TranscriptVersionEntity? lastImportedWebVersion = null;

            foreach (var webVer in webMeeting.VersionHistory.OrderBy(v => v.VersionNumber))
            {
                if (string.IsNullOrWhiteSpace(webVer.ContentSha256))
                {
                    continue;
                }

                string textToStore = !string.IsNullOrWhiteSpace(webVer.FullText)
                    ? webVer.FullText
                    : string.Join("\n", webMeeting.Segments.Select(s => $"[{s.StartTimestamp} - {s.EndTimestamp}] {s.Text}"));

                var (imported, entity) = await _db.ImportExternalTranscriptVersionIfMissingAsync(
                    localSession.Id,
                    webVer.ContentSha256,
                    textToStore,
                    webVer.ModifiedByDevice,
                    webVer.CreatedAtUtc);

                if (imported)
                {
                    versionsImported++;
                    anyImportedForSession = true;
                    lastImportedWebVersion = entity;
                }
            }

            if (anyImportedForSession && lastImportedWebVersion is not null)
            {
                if (hadLocalOfflineModification)
                {
                    // Conflicto concurrente detectado: Android tenía una edición offline no conocida por Web
                    // y Web tenía una edición diferente. Ambas quedan guardadas en TranscriptVersions sin sobrescribir.
                    conflictsPreserved++;
                    localSession.WasRecoveredFromCrash = true;
                    localSession.RecoveryNotes =
                        $"⚠️ Conflicto concurrente resuelto sin pérdida: se conservaron tanto la edición local Android (v{localSession.ActiveTranscriptVersion}) " +
                        $"como la edición Web importada (v{lastImportedWebVersion.VersionNumber}, SHA-256: {lastImportedWebVersion.ContentSha256[..8]}).";
                }
                else
                {
                    // Fast-forward limpio Web -> Android
                    localSession.ActiveTranscriptVersion = lastImportedWebVersion.VersionNumber;
                    localSession.ActiveTranscriptSha256 = lastImportedWebVersion.ContentSha256;
                    if (localSession.State == SessionRecordingState.Saved)
                    {
                        localSession.State = SessionRecordingState.Transcribed;
                    }
                }

                await _db.SaveSessionAsync(localSession);
                sessionsUpdated++;
            }

            // Sincronizar tareas (Checklist) Web -> Android por TaskUid y UpdatedAtUtc
            foreach (var webTask in webMeeting.Tasks)
            {
                bool upserted = await _db.UpsertTaskByUpdatedAtAsync(new TaskChecklistEntity
                {
                    TaskUid = webTask.TaskUid,
                    SessionUid = webMeeting.SessionUid,
                    SessionTitle = webMeeting.Title,
                    Description = webTask.Description,
                    Assignee = webTask.Assignee,
                    DueDateText = webTask.DueDateText,
                    IsCompleted = webTask.IsCompleted,
                    EvidenceQuote = webTask.EvidenceQuote,
                    TimestampReference = webTask.TimestampReference,
                    IsConfirmedByUser = webTask.ConfirmationStatus == Shared.Models.UserConfirmationStatus.ConfirmedByUser,
                    LastModifiedByDevice = webTask.LastModifiedByDevice,
                    UpdatedAtUtc = webTask.UpdatedAtUtc
                });
                if (upserted)
                {
                    tasksSynced++;
                }
            }
        }

        return new SyncPullResultDto
        {
            Success = true,
            SessionsUpdatedFromWeb = sessionsUpdated,
            NewTranscriptVersionsImported = versionsImported,
            ConcurrentConflictsDetectedAndPreserved = conflictsPreserved,
            TasksSyncedToPhone = tasksSynced,
            Message = $"Pull Web→Android OK (Sesiones: {sessionsUpdated}, Versiones importadas: {versionsImported}, Conflictos preservados: {conflictsPreserved}, Tareas: {tasksSynced})"
        };
    }
}
