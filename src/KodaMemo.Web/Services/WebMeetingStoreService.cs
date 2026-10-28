using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KodaMemo.Shared.Models;
using KodaMemo.Shared.Services;
using Microsoft.Data.Sqlite;

namespace KodaMemo.Web.Services;

public class SharedBudgetOperationEntry
{
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public string DeviceOrigin { get; init; } = "Android";
    public string OperationType { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public decimal CostUsd { get; init; }
}

public class AssistantChatMessage
{
    public bool IsUser { get; init; }
    public string Text { get; init; } = string.Empty;
    public string? EvidenceCitation { get; init; }
    public DateTime TimestampLocal { get; init; } = DateTime.Now;
}

public class WebMeetingStoreService
{
    private readonly string _dbFilePath;
    private readonly string _connectionString;
    private readonly object _syncLock = new();
    private readonly List<MeetingSessionDto> _meetings = new();
    private readonly List<SharedBudgetOperationEntry> _ledger = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    // Credenciales locales configurables (por entorno o valor por defecto de desarrollo local)
    public string ExpectedSyncBearerToken { get; }
    public string ExpectedWebUsername { get; }
    private string ExpectedWebPassword { get; }

    public string SqliteDatabasePath => _dbFilePath;
    public decimal MonthlyBudgetLimitUsd { get; set; } = 5.00m;
    public decimal TotalReconciledUsd => _ledger.Sum(e => e.CostUsd);
    public decimal ActiveReservedUsd { get; private set; }
    public IReadOnlyList<SharedBudgetOperationEntry> LedgerEntries => _ledger;

    public string PetName { get; set; } = "Koda";
    public string UserName { get; set; } = "Usuario";
    public PetAssistantVisualState PetState { get; set; } = PetAssistantVisualState.Idle;
    public bool IsPetVoiceMuted { get; set; }
    public List<AssistantChatMessage> AssistantHistory { get; } = new();

    public WebMeetingStoreService(IWebHostEnvironment env, IConfiguration config)
    {
        string dataDir = Path.Combine(env.ContentRootPath, "Data");
        Directory.CreateDirectory(dataDir);
        _dbFilePath = Path.Combine(dataDir, "audio_ia_web_server.db3");
        _connectionString = $"Data Source={_dbFilePath}";

        ExpectedSyncBearerToken = RequireSetting(config, "KodaMemo:SyncBearerToken");
        ExpectedWebUsername = RequireSetting(config, "KodaMemo:WebUsername");
        ExpectedWebPassword = RequireSetting(config, "KodaMemo:WebPassword");

        InitializeDatabaseAndLoad();
    }

    public bool ValidateBearerToken(string? providedToken)
    {
        if (string.IsNullOrWhiteSpace(providedToken)) return false;
        string clean = providedToken.Trim();
        if (clean.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean["Bearer ".Length..].Trim();
        }

        byte[] a = Encoding.UTF8.GetBytes(clean);
        byte[] b = Encoding.UTF8.GetBytes(ExpectedSyncBearerToken);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    public bool ValidateWebLogin(string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) return false;
        bool userOk = string.Equals(username.Trim(), ExpectedWebUsername, StringComparison.OrdinalIgnoreCase);
        byte[] a = Encoding.UTF8.GetBytes(password);
        byte[] b = Encoding.UTF8.GetBytes(ExpectedWebPassword);
        bool passOk = a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
        return userOk && passOk;
    }

    private void InitializeDatabaseAndLoad()
    {
        lock (_syncLock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS Meetings (
                        SessionUid TEXT PRIMARY KEY,
                        IsFromRealPhone INTEGER NOT NULL DEFAULT 0,
                        CreatedAtUtc TEXT NOT NULL,
                        UpdatedAtUtc TEXT NOT NULL,
                        PayloadJson TEXT NOT NULL
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            _meetings.Clear();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT PayloadJson FROM Meetings ORDER BY CreatedAtUtc DESC;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string json = reader.GetString(0);
                    var dto = JsonSerializer.Deserialize<MeetingSessionDto>(json, JsonOpts);
                    if (dto is not null)
                    {
                        _meetings.Add(dto);
                    }
                }
            }

            if (AssistantHistory.Count == 0)
            {
                AssistantHistory.Add(new AssistantChatMessage
                {
                    IsUser = false,
                    Text = $"¡Hola {UserName}! Soy {PetName}. Tus sesiones se cargaron desde SQLite del servidor ({_meetings.Count} registradas, {_meetings.Count(m => m.IsFromRealPhone)} sincronizadas del teléfono).",
                    EvidenceCitation = $"Persistencia SQLite activa: {Path.GetFileName(_dbFilePath)}"
                });
            }
        }
    }

    public void SaveMeeting(MeetingSessionDto meeting)
    {
        lock (_syncLock)
        {
            meeting.UpdatedAtUtc = DateTime.UtcNow;
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            PersistMeetingInternal(conn, meeting);
        }
    }

    private static void PersistMeetingInternal(SqliteConnection conn, MeetingSessionDto meeting)
    {
        string json = JsonSerializer.Serialize(meeting, JsonOpts);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Meetings (SessionUid, IsFromRealPhone, CreatedAtUtc, UpdatedAtUtc, PayloadJson)
            VALUES ($uid, $real, $created, $updated, $json)
            ON CONFLICT(SessionUid) DO UPDATE SET
                IsFromRealPhone = excluded.IsFromRealPhone,
                UpdatedAtUtc = excluded.UpdatedAtUtc,
                PayloadJson = excluded.PayloadJson;
            """;
        cmd.Parameters.AddWithValue("$uid", meeting.SessionUid);
        cmd.Parameters.AddWithValue("$real", meeting.IsFromRealPhone ? 1 : 0);
        cmd.Parameters.AddWithValue("$created", meeting.CreatedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$updated", meeting.UpdatedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$json", json);
        cmd.ExecuteNonQuery();
    }

    public SyncPushResultDto SyncFromPhonePayload(SyncPushPayloadDto payload)
    {
        lock (_syncLock)
        {
            int inserted = 0;
            int updated = 0;
            int conflictsPreserved = 0;

            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            foreach (var incoming in payload.Meetings)
            {
                incoming.IsFromRealPhone = true;
                incoming.SyncStatus = SyncState.Synced;

                var existing = _meetings.FirstOrDefault(m => m.SessionUid == incoming.SessionUid);
                if (existing is null)
                {
                    SummaryEvidenceValidator.ValidateMeetingEvidence(incoming);
                    if (incoming.MindMapRoot is null)
                    {
                        MindMapGenerator.BuildFromExistingSummary(incoming);
                    }
                    _meetings.Add(incoming);
                    PersistMeetingInternal(conn, incoming);
                    inserted++;
                }
                else
                {
                    existing.IsFromRealPhone = true;
                    existing.SyncStatus = SyncState.Synced;
                    existing.DurationSeconds = incoming.DurationSeconds > 0 ? incoming.DurationSeconds : existing.DurationSeconds;
                    existing.LocalWavFileName = !string.IsNullOrWhiteSpace(incoming.LocalWavFileName) ? incoming.LocalWavFileName : existing.LocalWavFileName;
                    existing.LocalWavSizeBytes = incoming.LocalWavSizeBytes > 0 ? incoming.LocalWavSizeBytes : existing.LocalWavSizeBytes;
                    existing.OriginDevice = incoming.OriginDevice;

                    // Fusión sin pérdida de versiones de transcripción (SHA-256)
                    foreach (var v in incoming.VersionHistory)
                    {
                        if (!existing.VersionHistory.Any(ev => string.Equals(ev.ContentSha256, v.ContentSha256, StringComparison.OrdinalIgnoreCase)))
                        {
                            v.VersionNumber = existing.VersionHistory.Count + 1;
                            existing.VersionHistory.Add(v);
                            conflictsPreserved++;
                        }
                    }

                    // Si la versión en servidor no fue editada en Web (solo tiene v1 igual o vacía), adoptar los segmentos del teléfono
                    bool webEditedTranscript = existing.VersionHistory.Any(v => v.ModifiedByDevice.Contains("Web", StringComparison.OrdinalIgnoreCase));
                    if (!webEditedTranscript && incoming.Segments.Count > 0)
                    {
                        existing.Segments = incoming.Segments;
                        existing.ActiveTranscriptVersion = incoming.ActiveTranscriptVersion;
                        existing.ActiveTranscriptSha256 = incoming.ActiveTranscriptSha256;
                    }

                    // Fusión de tareas por TaskUid respetando UpdatedAtUtc más reciente
                    foreach (var incTask in incoming.Tasks)
                    {
                        var exTask = existing.Tasks.FirstOrDefault(t => t.TaskUid == incTask.TaskUid);
                        if (exTask is null)
                        {
                            existing.Tasks.Add(incTask);
                        }
                        else if (incTask.UpdatedAtUtc > exTask.UpdatedAtUtc)
                        {
                            existing.Tasks.Remove(exTask);
                            existing.Tasks.Add(incTask);
                        }
                    }

                    SummaryEvidenceValidator.ValidateMeetingEvidence(existing);
                    MindMapGenerator.BuildFromExistingSummary(existing);
                    existing.UpdatedAtUtc = DateTime.UtcNow;
                    PersistMeetingInternal(conn, existing);
                    updated++;
                }
            }

            return new SyncPushResultDto
            {
                Success = true,
                InsertedCount = inserted,
                UpdatedCount = updated,
                ConflictVersionsPreserved = conflictsPreserved,
                Message = $"Sincronización completada en SQLite servidor ({inserted} insertadas, {updated} actualizadas)."
            };
        }
    }

    public List<MeetingSessionDto> SearchMeetings(string? query, SyncState? syncFilter = null)
    {
        IEnumerable<MeetingSessionDto> result = _meetings
            .OrderByDescending(m => m.IsFromRealPhone)
            .ThenByDescending(m => m.CreatedAtUtc);

        if (syncFilter.HasValue)
        {
            result = result.Where(m => m.SyncStatus == syncFilter.Value);
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            string normQ = SummaryEvidenceValidator.NormalizeForMatch(query);
            result = result.Where(m =>
                SummaryEvidenceValidator.NormalizeForMatch(m.Title).Contains(normQ) ||
                m.Segments.Any(s => SummaryEvidenceValidator.NormalizeForMatch(s.Text).Contains(normQ)) ||
                m.MainTopics.Any(t => SummaryEvidenceValidator.NormalizeForMatch(t.Title).Contains(normQ)) ||
                m.Agreements.Any(a => SummaryEvidenceValidator.NormalizeForMatch(a.Description).Contains(normQ)) ||
                m.Tasks.Any(t =>
                    SummaryEvidenceValidator.NormalizeForMatch(t.Description).Contains(normQ) ||
                    SummaryEvidenceValidator.NormalizeForMatch(t.Assignee).Contains(normQ)));
        }

        return result.ToList();
    }

    public MeetingSessionDto? GetMeetingByUid(string sessionUid) =>
        _meetings.FirstOrDefault(m => m.SessionUid == sessionUid);

    public List<TaskChecklistItemDto> GetAllChecklistTasks() =>
        _meetings.SelectMany(m => m.Tasks).OrderBy(t => t.IsCompleted).ThenByDescending(t => t.UpdatedAtUtc).ToList();

    public void UpdateTranscriptSegment(MeetingSessionDto meeting, int segmentNumber, string newText, string modifiedByDevice = "Web")
    {
        var seg = meeting.Segments.FirstOrDefault(s => s.SegmentNumber == segmentNumber);
        if (seg is null) return;

        seg.Text = newText.Trim();
        string fullText = string.Join("\n", meeting.Segments.Select(s => $"[{s.StartTimestamp} - {s.EndTimestamp}] {s.Text}"));
        string sha256 = SummaryEvidenceValidator.ComputeSha256(fullText);

        if (!string.Equals(sha256, meeting.ActiveTranscriptSha256, StringComparison.OrdinalIgnoreCase))
        {
            meeting.ActiveTranscriptVersion++;
            meeting.ActiveTranscriptSha256 = sha256;
            meeting.UpdatedAtUtc = DateTime.UtcNow;
            meeting.VersionHistory.Add(new TranscriptVersionHistoryDto
            {
                VersionNumber = meeting.ActiveTranscriptVersion,
                ContentSha256 = sha256,
                ModifiedByDevice = modifiedByDevice,
                CreatedAtUtc = DateTime.UtcNow,
                FullText = fullText
            });

            SummaryEvidenceValidator.ValidateMeetingEvidence(meeting);
            SaveMeeting(meeting);
        }
    }

    public void RevalidateAndSyncSummaryVersion(MeetingSessionDto meeting)
    {
        SummaryEvidenceValidator.ValidateMeetingEvidence(meeting);
        meeting.SummarySourceTranscriptVersion = meeting.ActiveTranscriptVersion;
        meeting.SummarySourceTranscriptSha256 = meeting.ActiveTranscriptSha256;
        MindMapGenerator.BuildFromExistingSummary(meeting);
        SaveMeeting(meeting);
    }

    public void AskAssistant(string question)
    {
        if (string.IsNullOrWhiteSpace(question)) return;

        AssistantHistory.Add(new AssistantChatMessage
        {
            IsUser = true,
            Text = question.Trim()
        });

        PetState = PetAssistantVisualState.Processing;

        string normQ = SummaryEvidenceValidator.NormalizeForMatch(question);

        if (normQ.StartsWith("agrega tarea") || normQ.StartsWith("crear tarea") || normQ.StartsWith("anota pendiente"))
        {
            string taskDesc = question.Substring(question.IndexOf(' ') + 1).Trim();
            var targetMeeting = _meetings.FirstOrDefault();
            if (targetMeeting is null)
            {
                PetState = PetAssistantVisualState.Idle;
                AssistantHistory.Add(new AssistantChatMessage
                {
                    Text = "Primero sincroniza o crea una reunión para asociar la tarea."
                });
                return;
            }
            targetMeeting.Tasks.Add(new TaskChecklistItemDto
            {
                SessionUid = targetMeeting.SessionUid,
                SessionTitle = targetMeeting.Title,
                Description = taskDesc,
                Assignee = UserName,
                DueDateText = "No especificada",
                Origin = TaskOriginType.UserDictated,
                ConfirmationStatus = UserConfirmationStatus.ConfirmedByUser,
                CitationStatus = CitationMatchStatus.NotEvaluated,
                EvidenceQuote = "Dictada directamente por el usuario al asistente",
                LastModifiedByDevice = "Web Assistant"
            });
            MindMapGenerator.BuildFromExistingSummary(targetMeeting);
            SaveMeeting(targetMeeting);

            PetState = PetAssistantVisualState.Speaking;
            AssistantHistory.Add(new AssistantChatMessage
            {
                IsUser = false,
                Text = $"Listo, {UserName}. Creé y guardé en SQLite la tarea como 'Dictada por el usuario': \"{taskDesc}\".",
                EvidenceCitation = "Origen: Dictada por el usuario (Persistida en SQLite servidor)"
            });
            return;
        }

        var matchingAgreements = _meetings
            .SelectMany(m => m.Agreements.Select(a => (Meeting: m, Agreement: a)))
            .Where(x =>
                normQ.Contains("acordamos") ||
                normQ.Contains("acuerdo") ||
                normQ.Contains("ayer") ||
                SummaryEvidenceValidator.NormalizeForMatch(x.Agreement.Description).Split(' ').Any(w => w.Length > 4 && normQ.Contains(w)))
            .Take(4)
            .ToList();

        if (normQ.Contains("cotizacion") || normQ.Contains("quien quedo responsable"))
        {
            var cotTask = GetAllChecklistTasks().FirstOrDefault(t =>
                SummaryEvidenceValidator.NormalizeForMatch(t.Description).Contains("cotizacion"));

            if (cotTask is not null)
            {
                PetState = PetAssistantVisualState.Speaking;
                AssistantHistory.Add(new AssistantChatMessage
                {
                    IsUser = false,
                    Text = $"Según tus registros, **{cotTask.Assignee}** quedó responsable de \"{cotTask.Description}\" con fecha compromiso: **{cotTask.DueDateText}**.",
                    EvidenceCitation = $"Respaldo: Sesión '{cotTask.SessionTitle}', Minuto [{cotTask.TimestampReference}] — Cita literal: \"{cotTask.EvidenceQuote}\""
                });
                return;
            }
        }

        if (normQ.Contains("acordamos") || normQ.Contains("acuerdo"))
        {
            if (matchingAgreements.Count > 0)
            {
                string list = string.Join(" | ", matchingAgreements.Select(x => $"• {x.Agreement.Description} ({x.Meeting.Title}, [{x.Agreement.TimestampReference}])"));
                string citations = string.Join(" ; ", matchingAgreements.Select(x => $"[{x.Meeting.Title} {x.Agreement.TimestampReference}]: \"{x.Agreement.EvidenceQuote}\""));
                PetState = PetAssistantVisualState.Speaking;
                AssistantHistory.Add(new AssistantChatMessage
                {
                    IsUser = false,
                    Text = $"Estos son los acuerdos registrados en tus sesiones: {list}",
                    EvidenceCitation = $"Fragmentos que respaldan la respuesta: {citations}"
                });
                return;
            }
        }

        if (normQ.Contains("pendiente") || normQ.Contains("tarea"))
        {
            var pending = GetAllChecklistTasks().Where(t => !t.IsCompleted).ToList();
            if (pending.Count > 0)
            {
                string items = string.Join(" | ", pending.Select(t => $"• {t.Description} (Resp: {t.Assignee}, Fecha: {t.DueDateText})"));
                PetState = PetAssistantVisualState.Speaking;
                AssistantHistory.Add(new AssistantChatMessage
                {
                    IsUser = false,
                    Text = $"Tienes {pending.Count} pendientes activos: {items}",
                    EvidenceCitation = $"Respaldo: Extraídos de {pending.Select(p => p.SessionTitle).Distinct().Count()} sesiones guardadas."
                });
                return;
            }
        }

        PetState = PetAssistantVisualState.Idle;
        AssistantHistory.Add(new AssistantChatMessage
        {
            IsUser = false,
            Text = "No encontré información sobre esa consulta en los fragmentos de transcripción ni en las tareas guardadas.",
            EvidenceCitation = "Búsqueda local en fragmentos completada (0 coincidencias)"
        });
    }

    private static string RequireSetting(IConfiguration configuration, string key)
    {
        string? value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Configure {key} using user secrets or environment variables.");
        return value;
    }
}
