using System.Security.Cryptography;
using System.Text;
using KodaMemo.Models;
using KodaMemo.Services;
using SQLite;

namespace KodaMemo.Data;

public class AppDatabase
{
    private SQLiteAsyncConnection? _db;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _budgetLock = new(1, 1);

    public const string MonthlyBudgetPreferenceKey = "MonthlyBudgetUsd";
    public const decimal DefaultMonthlyBudgetUsd = 5.00m;

    private async Task<SQLiteAsyncConnection> GetConnectionAsync()
    {
        if (_db is not null)
        {
            return _db;
        }

        await _initLock.WaitAsync();
        try
        {
            if (_db is null)
            {
                string dbPath = Path.Combine(FileSystem.AppDataDirectory, "audio_ia_local.db3");
                _db = new SQLiteAsyncConnection(
                    dbPath,
                    SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.SharedCache);

                await _db.CreateTableAsync<RecordingSession>();
                await _db.CreateTableAsync<TranscriptVersionEntity>();
                await _db.CreateTableAsync<ModelPricingCatalogEntity>();
                await _db.CreateTableAsync<UsageLedgerEntry>();
                await _db.CreateTableAsync<TaskChecklistEntity>();

                await SeedPricingCatalogAsync(_db);
            }

            return _db;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static async Task SeedPricingCatalogAsync(SQLiteAsyncConnection db)
    {
        var existingCount = await db.Table<ModelPricingCatalogEntity>().CountAsync();
        if (existingCount > 0)
        {
            return;
        }

        var officialModels = new[]
        {
            new ModelPricingCatalogEntity
            {
                ModelId = "gemini-2.0-flash-lite",
                ProviderName = "Google Gemini API",
                InputUsdPerMillionTokens = 0.075m,
                OutputUsdPerMillionTokens = 0.30m,
                OfficialSourceUrl = "https://ai.google.dev/gemini-api/docs/pricing",
                VerifiedDateIso = "2026-10-26"
            },
            new ModelPricingCatalogEntity
            {
                ModelId = "gemini-2.5-flash-lite",
                ProviderName = "Google Gemini API",
                InputUsdPerMillionTokens = 0.10m,
                OutputUsdPerMillionTokens = 0.40m,
                OfficialSourceUrl = "https://ai.google.dev/gemini-api/docs/pricing",
                VerifiedDateIso = "2026-10-26"
            },
            new ModelPricingCatalogEntity
            {
                ModelId = "claude-3-5-haiku-20241022",
                ProviderName = "Anthropic API",
                InputUsdPerMillionTokens = 0.80m,
                OutputUsdPerMillionTokens = 4.00m,
                OfficialSourceUrl = "https://docs.anthropic.com/en/docs/about-claude/pricing",
                VerifiedDateIso = "2026-10-26"
            },
            new ModelPricingCatalogEntity
            {
                ModelId = "claude-haiku-4-5",
                ProviderName = "Anthropic API",
                InputUsdPerMillionTokens = 1.00m,
                OutputUsdPerMillionTokens = 5.00m,
                OfficialSourceUrl = "https://docs.anthropic.com/en/docs/about-claude/pricing",
                VerifiedDateIso = "2026-10-26"
            }
        };

        await db.InsertAllAsync(officialModels);
    }

    public async Task<List<RecordingSession>> GetSessionsAsync()
    {
        var db = await GetConnectionAsync();
        return await db.Table<RecordingSession>()
            .OrderByDescending(s => s.CreatedAtUtc)
            .ToListAsync();
    }

    public async Task<RecordingSession?> GetSessionByIdAsync(int id)
    {
        var db = await GetConnectionAsync();
        return await db.Table<RecordingSession>()
            .Where(s => s.Id == id)
            .FirstOrDefaultAsync();
    }

    public async Task<RecordingSession?> GetSessionByUidAsync(string sessionUid)
    {
        var db = await GetConnectionAsync();
        return await db.Table<RecordingSession>()
            .Where(s => s.SessionUid == sessionUid)
            .FirstOrDefaultAsync();
    }

    public async Task<int> SaveSessionAsync(RecordingSession session)
    {
        var db = await GetConnectionAsync();
        session.UpdatedAtUtc = DateTime.UtcNow;
        if (session.Id == 0)
        {
            await db.InsertAsync(session);
            return session.Id;
        }
        else
        {
            await db.UpdateAsync(session);
            return session.Id;
        }
    }

    public async Task DeleteSessionAsync(RecordingSession session)
    {
        var db = await GetConnectionAsync();
        if (!string.IsNullOrWhiteSpace(session.WavFilePath) && File.Exists(session.WavFilePath))
        {
            try
            {
                File.Delete(session.WavFilePath);
            }
            catch
            {
                // Ignorar error de borrado si el archivo ya fue eliminado
            }
        }

        await db.ExecuteAsync("DELETE FROM TranscriptVersions WHERE SessionId = ?", session.Id);
        await db.DeleteAsync(session);
    }

    /// <summary>
    /// Detecta y recupera sesiones que quedaron abiertas (RecordingInProgress / Paused / PausedByInterruption)
    /// tras un cierre inesperado del proceso, pérdida de energía o reinicio, reparando el encabezado RIFF/WAVE en disco.
    /// </summary>
    public async Task<int> RecoverOrphanedSessionsAsync(int? currentlyActiveSessionId = null)
    {
        var db = await GetConnectionAsync();
        var candidates = await db.Table<RecordingSession>()
            .Where(s =>
                s.State == SessionRecordingState.RecordingInProgress ||
                s.State == SessionRecordingState.Paused ||
                s.State == SessionRecordingState.PausedByInterruption)
            .ToListAsync();

        int recoveredCount = 0;
        foreach (var session in candidates)
        {
            if (currentlyActiveSessionId.HasValue && session.Id == currentlyActiveSessionId.Value)
            {
                continue;
            }

            var (repaired, dataBytes, durationSeconds) = WavHeaderHelper.RepairWavFileOnDisk(
                session.WavFilePath,
                session.SampleRateHz,
                (short)session.Channels,
                (short)session.BitsPerSample);

            if (repaired && dataBytes > 0)
            {
                session.DataBytesWritten = dataBytes;
                session.DurationSeconds = durationSeconds;
                session.State = SessionRecordingState.RecoveredAfterCrash;
                session.WasRecoveredFromCrash = true;
                session.RecoveryNotes = $"Recuperado automáticamente el {DateTime.Now:dd/MM/yyyy HH:mm:ss} ({durationSeconds:F1}s de audio íntegro).";
                session.UpdatedAtUtc = DateTime.UtcNow;
                await db.UpdateAsync(session);
                recoveredCount++;
            }
            else
            {
                session.State = SessionRecordingState.Error;
                session.RecoveryNotes = "Sesión interrumpida sin datos de audio suficientes en disco.";
                session.UpdatedAtUtc = DateTime.UtcNow;
                await db.UpdateAsync(session);
            }
        }

        return recoveredCount;
    }

    public async Task<List<TranscriptVersionEntity>> GetTranscriptVersionsAsync(int sessionId)
    {
        var db = await GetConnectionAsync();
        return await db.Table<TranscriptVersionEntity>()
            .Where(t => t.SessionId == sessionId)
            .OrderBy(t => t.VersionNumber)
            .ToListAsync();
    }

    public async Task<TranscriptVersionEntity> SaveNewTranscriptVersionAsync(
        int sessionId,
        string transcriptText,
        string sourceProviderId)
    {
        var db = await GetConnectionAsync();
        var latest = await db.Table<TranscriptVersionEntity>()
            .Where(t => t.SessionId == sessionId)
            .OrderByDescending(t => t.VersionNumber)
            .FirstOrDefaultAsync();

        int nextVersion = (latest?.VersionNumber ?? 0) + 1;
        string normalized = (transcriptText ?? string.Empty).Replace("\r\n", "\n").Trim();
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        string sha256Hex = Convert.ToHexString(hashBytes).ToLowerInvariant();

        var entity = new TranscriptVersionEntity
        {
            SessionId = sessionId,
            VersionNumber = nextVersion,
            ContentSha256 = sha256Hex,
            TranscriptText = transcriptText ?? string.Empty,
            SourceProviderId = sourceProviderId,
            CreatedAtUtc = DateTime.UtcNow
        };

        await db.InsertAsync(entity);

        var session = await GetSessionByIdAsync(sessionId);
        if (session is not null)
        {
            session.ActiveTranscriptVersion = nextVersion;
            session.ActiveTranscriptSha256 = sha256Hex;
            if (session.State == SessionRecordingState.Saved || session.State == SessionRecordingState.RecoveredAfterCrash)
            {
                session.State = SessionRecordingState.Transcribed;
            }
            await SaveSessionAsync(session);
        }

        return entity;
    }

    public async Task<(bool Imported, TranscriptVersionEntity Version)> ImportExternalTranscriptVersionIfMissingAsync(
        int sessionId,
        string contentSha256,
        string transcriptText,
        string sourceProviderId,
        DateTime createdAtUtc)
    {
        var db = await GetConnectionAsync();
        var existingVersions = await GetTranscriptVersionsAsync(sessionId);
        var match = existingVersions.FirstOrDefault(v =>
            string.Equals(v.ContentSha256, contentSha256, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return (false, match);
        }

        int nextVer = (existingVersions.Count > 0 ? existingVersions.Max(v => v.VersionNumber) : 0) + 1;
        var entity = new TranscriptVersionEntity
        {
            SessionId = sessionId,
            VersionNumber = nextVer,
            ContentSha256 = contentSha256.ToLowerInvariant(),
            TranscriptText = transcriptText ?? string.Empty,
            SourceProviderId = sourceProviderId,
            CreatedAtUtc = createdAtUtc
        };

        await db.InsertAsync(entity);
        return (true, entity);
    }

    public async Task<List<TaskChecklistEntity>> GetTasksBySessionUidAsync(string sessionUid)
    {
        var db = await GetConnectionAsync();
        return await db.Table<TaskChecklistEntity>()
            .Where(t => t.SessionUid == sessionUid)
            .ToListAsync();
    }

    public async Task<List<TaskChecklistEntity>> GetAllTasksAsync()
    {
        var db = await GetConnectionAsync();
        return await db.Table<TaskChecklistEntity>()
            .OrderByDescending(t => t.UpdatedAtUtc)
            .ToListAsync();
    }

    public async Task<bool> UpsertTaskByUpdatedAtAsync(TaskChecklistEntity incoming)
    {
        var db = await GetConnectionAsync();
        var existing = await db.Table<TaskChecklistEntity>()
            .Where(t => t.TaskUid == incoming.TaskUid)
            .FirstOrDefaultAsync();

        if (existing is null)
        {
            await db.InsertAsync(incoming);
            return true;
        }

        if (incoming.UpdatedAtUtc >= existing.UpdatedAtUtc)
        {
            await db.UpdateAsync(incoming);
            return true;
        }

        return false;
    }

    public async Task<List<ModelPricingCatalogEntity>> GetPricingCatalogAsync()
    {
        var db = await GetConnectionAsync();
        return await db.Table<ModelPricingCatalogEntity>().ToListAsync();
    }

    public decimal GetConfiguredMonthlyBudgetUsd()
    {
        double val = Preferences.Default.Get(MonthlyBudgetPreferenceKey, (double)DefaultMonthlyBudgetUsd);
        return (decimal)Math.Max(0.50, val);
    }

    public void SetConfiguredMonthlyBudgetUsd(decimal budgetUsd)
    {
        Preferences.Default.Set(MonthlyBudgetPreferenceKey, (double)Math.Max(0.50m, budgetUsd));
    }

    /// <summary>
    /// Reserva atómica de presupuesto para solicitudes concurrentes (Fase A).
    /// </summary>
    public async Task<(bool Allowed, string Message, UsageLedgerEntry? Reservation)> TryReserveBudgetAsync(
        string operationType,
        string providerId,
        string modelId,
        int estimatedInputTokens,
        int maxOutputTokens,
        int? sessionId = null)
    {
        await _budgetLock.WaitAsync();
        try
        {
            var db = await GetConnectionAsync();
            var pricing = await db.Table<ModelPricingCatalogEntity>()
                .Where(p => p.ModelId == modelId)
                .FirstOrDefaultAsync();

            decimal inputRate = pricing?.InputUsdPerMillionTokens ?? 1.00m;
            decimal outputRate = pricing?.OutputUsdPerMillionTokens ?? 5.00m;

            decimal reservedCost =
                (estimatedInputTokens / 1_000_000m * inputRate) +
                (maxOutputTokens / 1_000_000m * outputRate);

            string monthKey = DateTime.UtcNow.ToString("yyyy-MM");
            var monthEntries = await db.Table<UsageLedgerEntry>()
                .Where(e => e.YearMonthKey == monthKey)
                .ToListAsync();

            decimal reconciledTotal = monthEntries
                .Where(e => e.State == BudgetReservationState.Reconciled || e.State == BudgetReservationState.UnconfirmedTimeout)
                .Sum(e => e.ActualCostUsd);

            decimal activeReservedTotal = monthEntries
                .Where(e => e.State == BudgetReservationState.Reserved)
                .Sum(e => e.ReservedCostUsd);

            decimal budgetLimit = GetConfiguredMonthlyBudgetUsd();
            if (reconciledTotal + activeReservedTotal + reservedCost > budgetLimit)
            {
                return (false,
                    $"Presupuesto mensual alcanzado (Gastado: US${reconciledTotal:F4}, Reservado en curso: US${activeReservedTotal:F4}, Solicitud: US${reservedCost:F4} > Límite: US${budgetLimit:F2}).",
                    null);
            }

            var entry = new UsageLedgerEntry
            {
                YearMonthKey = monthKey,
                SessionId = sessionId,
                OperationType = operationType,
                ProviderId = providerId,
                ModelId = modelId,
                EstimatedInputTokens = estimatedInputTokens,
                MaxReservedOutputTokens = maxOutputTokens,
                ReservedCostUsd = reservedCost,
                ActualCostUsd = reservedCost,
                State = BudgetReservationState.Reserved,
                CreatedAtUtc = DateTime.UtcNow
            };

            await db.InsertAsync(entry);
            return (true, "Reserva aprobada", entry);
        }
        finally
        {
            _budgetLock.Release();
        }
    }

    /// <summary>
    /// Conciliación post-respuesta con el consumo real reportado por el proveedor (Fase B).
    /// </summary>
    public async Task ReconcileReservationAsync(string reservationId, int actualInputTokens, int actualOutputTokens)
    {
        await _budgetLock.WaitAsync();
        try
        {
            var db = await GetConnectionAsync();
            var entry = await db.Table<UsageLedgerEntry>()
                .Where(e => e.ReservationId == reservationId)
                .FirstOrDefaultAsync();

            if (entry is null)
            {
                return;
            }

            var pricing = await db.Table<ModelPricingCatalogEntity>()
                .Where(p => p.ModelId == entry.ModelId)
                .FirstOrDefaultAsync();

            decimal inputRate = pricing?.InputUsdPerMillionTokens ?? 1.00m;
            decimal outputRate = pricing?.OutputUsdPerMillionTokens ?? 5.00m;

            entry.ActualInputTokens = actualInputTokens;
            entry.ActualOutputTokens = actualOutputTokens;
            entry.ActualCostUsd =
                (actualInputTokens / 1_000_000m * inputRate) +
                (actualOutputTokens / 1_000_000m * outputRate);
            entry.State = BudgetReservationState.Reconciled;
            entry.ReconciledAtUtc = DateTime.UtcNow;

            await db.UpdateAsync(entry);
        }
        finally
        {
            _budgetLock.Release();
        }
    }

    public async Task ReleaseReservationAsync(string reservationId)
    {
        await _budgetLock.WaitAsync();
        try
        {
            var db = await GetConnectionAsync();
            var entry = await db.Table<UsageLedgerEntry>()
                .Where(e => e.ReservationId == reservationId)
                .FirstOrDefaultAsync();

            if (entry is null)
            {
                return;
            }

            entry.ActualCostUsd = 0m;
            entry.State = BudgetReservationState.Released;
            entry.ReconciledAtUtc = DateTime.UtcNow;
            await db.UpdateAsync(entry);
        }
        finally
        {
            _budgetLock.Release();
        }
    }

    public async Task<(decimal ReconciledUsd, decimal ReservedInFlightUsd, int ReconciledCount, int ReservedCount, int ReleasedCount)> GetMonthLedgerSummaryAsync()
    {
        await _budgetLock.WaitAsync();
        try
        {
            var db = await GetConnectionAsync();
            string monthKey = DateTime.UtcNow.ToString("yyyy-MM");
            var entries = await db.Table<UsageLedgerEntry>()
                .Where(e => e.YearMonthKey == monthKey)
                .ToListAsync();

            decimal reconciledUsd = entries
                .Where(e => e.State == BudgetReservationState.Reconciled || e.State == BudgetReservationState.UnconfirmedTimeout)
                .Sum(e => e.ActualCostUsd);

            decimal reservedUsd = entries
                .Where(e => e.State == BudgetReservationState.Reserved)
                .Sum(e => e.ReservedCostUsd);

            return (
                reconciledUsd,
                reservedUsd,
                entries.Count(e => e.State == BudgetReservationState.Reconciled),
                entries.Count(e => e.State == BudgetReservationState.Reserved),
                entries.Count(e => e.State == BudgetReservationState.Released));
        }
        finally
        {
            _budgetLock.Release();
        }
    }

    public async Task ClearLedgerEntriesByOperationAsync(string operationType)
    {
        await _budgetLock.WaitAsync();
        try
        {
            var db = await GetConnectionAsync();
            await db.ExecuteAsync("DELETE FROM UsageLedger WHERE OperationType = ?", operationType);
        }
        finally
        {
            _budgetLock.Release();
        }
    }
}
