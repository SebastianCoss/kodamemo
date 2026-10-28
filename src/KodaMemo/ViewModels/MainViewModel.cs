using System.Collections.ObjectModel;
using KodaMemo.Data;
using KodaMemo.Models;
using KodaMemo.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace KodaMemo.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private string _syncServerUrl = Preferences.Default.Get("sync-server-url", "http://127.0.0.1:5188");

    [ObservableProperty]
    private string _syncTokenInput = string.Empty;

    [RelayCommand]
    private async Task SaveSyncSettingsAsync()
    {
        if (!Uri.TryCreate(SyncServerUrl, UriKind.Absolute, out var uri) ||
            !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            SyncStatusText = "Usa HTTPS o una dirección local para la conexión USB.";
            return;
        }
        Preferences.Default.Set("sync-server-url", SyncServerUrl.TrimEnd('/'));
        if (!string.IsNullOrWhiteSpace(SyncTokenInput))
        {
            await SecureStorage.Default.SetAsync("sync-token", SyncTokenInput);
            SyncTokenInput = string.Empty;
        }
        SyncStatusText = "Configuración de sincronización guardada.";
    }

    private int _transcriptLoadId;
    private readonly AppDatabase _db;
    private readonly IAudioRecordingService _recorder;
    private readonly IAudioPlaybackService _player;
    private readonly IMicrophoneArbitrationService _micArbitrator;
    private readonly LocalWhisperTranscriptionService _sttService;

    public ObservableCollection<RecordingSession> Sessions { get; } = new();
    public ObservableCollection<ModelPricingCatalogEntity> PricingCatalog { get; } = new();

    public ObservableCollection<TaskChecklistEntity> MeetingTasks { get; } = new();
    public IEnumerable<RecordingSession> RecentSessions => Sessions.Take(3);
    public int PendingTaskCount => MeetingTasks.Count(t => !t.IsCompleted);
    public bool HasTasks => MeetingTasks.Count > 0;

    [ObservableProperty]
    private string _activeRecordingTitle = "Nueva reunión";
    [ObservableProperty]
    private bool _isSessionSaved;
    [ObservableProperty]
    private bool _isTranscribing;

    [ObservableProperty]
    private string _newSessionTitle = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Listo para iniciar grabación manual (WAV 16kHz Mono)";

    [ObservableProperty]
    private string _elapsedTimeText = "00:00.0";

    [ObservableProperty]
    private string _bytesWrittenText = "0.0 KB";

    [ObservableProperty]
    private double _rmsLevel;

    [ObservableProperty]
    private bool _canStartRecording = true;

    [ObservableProperty]
    private bool _canPauseRecording;

    [ObservableProperty]
    private bool _canResumeRecording;

    [ObservableProperty]
    private bool _canStopRecording;

    [ObservableProperty]
    private string _micArbitrationText = "🟢 Micrófono libre (disponible para grabación o asistente)";

    [ObservableProperty]
    private string _recoveryBannerText = string.Empty;

    [ObservableProperty]
    private bool _hasRecoveryBanner;

    [ObservableProperty]
    private RecordingSession? _selectedPlaybackSession;

    [ObservableProperty]
    private bool _isPlayingAudio;

    [ObservableProperty]
    private string _playbackProgressText = "00:00 / 00:00";

    [ObservableProperty]
    private double _playbackProgressRatio;

    [ObservableProperty]
    private decimal _monthlyBudgetUsd = 5.00m;

    [ObservableProperty]
    private string _latestTranscriptText = string.Empty;

    [ObservableProperty]
    private string _latestSttMetricsText = "Sin transcripción ejecutada aún.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeTab))]
    [NotifyPropertyChangedFor(nameof(IsRecordTab))]
    [NotifyPropertyChangedFor(nameof(IsDetailTab))]
    [NotifyPropertyChangedFor(nameof(IsSettingsTab))]
    [NotifyPropertyChangedFor(nameof(IsSessionsTab))]
    [NotifyPropertyChangedFor(nameof(IsTasksTab))]
    [NotifyPropertyChangedFor(nameof(ShowCompactRecorder))]
    [NotifyPropertyChangedFor(nameof(PageHeading))]
    private string _activeMobileTab = "home";

    public bool IsHomeTab => ActiveMobileTab == "home";
    public bool IsRecordTab => ActiveMobileTab == "record";
    public bool IsDetailTab => ActiveMobileTab == "detail";
    public bool IsSettingsTab => ActiveMobileTab == "settings";
    public bool IsSessionsTab => ActiveMobileTab == "sessions";
    public bool IsTasksTab => ActiveMobileTab == "tasks";
    public bool ShowCompactRecorder => CanStopRecording && !IsRecordTab;
    public string RecordingStateLabel => CanPauseRecording ? "● Grabando" : CanResumeRecording ? "Ⅱ Grabación en pausa" : "Micrófono listo";
    public string PageHeading => ActiveMobileTab switch {
        "record" => "Tu grabación", "sessions" => "Tus reuniones", "tasks" => "Tus pendientes",
        "detail" => "Detalle de reunión", "settings" => "Configuración", _ => "KodaMemo"
    };

    [ObservableProperty]
    private bool _isKodaExpanded;

    [ObservableProperty]
    private bool _isPetVoiceMuted;

    [ObservableProperty]
    private string _kodaQuickReplyText = "Hola, soy Koda (Simulación por reglas locales). Pregúntame por tus grabaciones o pendientes.";

    [ObservableProperty]
    private string _syncStatusText = "📱 Offline-First SQLite activo • Listo para sincronizar con Web (/api/sync/push)";

    [ObservableProperty]
    private double _fontScaleMultiplier = 1.0;

    [ObservableProperty]
    private string _fontScaleBadgeText = "Texto: 100%";

    public double ScaledBodyFontSize => Math.Round(15.0 * FontScaleMultiplier, 1);
    public double ScaledHeadingFontSize => Math.Round(20.0 * FontScaleMultiplier, 1);
    public double ScaledTimerFontSize => Math.Round(44.0 * FontScaleMultiplier, 1);

    public MainViewModel(
        AppDatabase db,
        IAudioRecordingService recorder,
        IAudioPlaybackService player,
        IMicrophoneArbitrationService micArbitrator,
        LocalWhisperTranscriptionService sttService)
    {
        _db = db;
        _recorder = recorder;
        _player = player;
        _micArbitrator = micArbitrator;
        _sttService = sttService;

        _recorder.ProgressChanged += OnRecorderProgressChanged;
        _recorder.StateChanged += OnRecorderStateChanged;
        _player.PlaybackStateChanged += OnPlaybackStateChanged;
    }

    public async Task InitializeAsync()
    {
        MonthlyBudgetUsd = _db.GetConfiguredMonthlyBudgetUsd();

        int recovered = await _db.RecoverOrphanedSessionsAsync(_recorder.ActiveSessionId);
        if (recovered > 0)
        {
            HasRecoveryBanner = true;
            RecoveryBannerText = $"🛡️ Se recuperaron automáticamente {recovered} sesión(es) tras una interrupción o cierre inesperado. El encabezado WAV fue reparado y puedes reproducirlas abajo.";
        }

        await RefreshSessionsAsync();
        await LoadPricingCatalogAsync();
        await LoadLatestSavedTranscriptAsync();
        UpdateRecordingButtonsState();
    }

    private async Task LoadLatestSavedTranscriptAsync(int? targetSessionId = null)
    {
        int requestId = Interlocked.Increment(ref _transcriptLoadId);
        try
        {
            int? sid = targetSessionId ?? Sessions.FirstOrDefault()?.Id;
            LatestTranscriptText = string.Empty;
            LatestSttMetricsText = "Esta reunión todavía no tiene transcripción.";
            if (sid is null) return;
            var versions = await _db.GetTranscriptVersionsAsync(sid.Value);
            if (requestId != _transcriptLoadId) return;
            var latest = versions.OrderByDescending(v => v.VersionNumber).FirstOrDefault();
            if (latest is not null)
            {
                LatestTranscriptText = latest.TranscriptText;
                LatestSttMetricsText = $"SQLite Real • Sesión #{latest.SessionId} • v{latest.VersionNumber} • Proveedor: {latest.SourceProviderId} • SHA-256: {latest.ContentSha256[..Math.Min(12, latest.ContentSha256.Length)]}...";
            }
        }
        catch
        {
            // Ignorado si aún no existen versiones
        }
    }

    [RelayCommand]
    public void SelectTab(string? tab)
    {
        if (!string.IsNullOrWhiteSpace(tab))
        {
            ActiveMobileTab = tab.ToLowerInvariant();
            if (ActiveMobileTab == "record")
            {
                IsKodaExpanded = false;
            }
            else if (ActiveMobileTab == "detail")
            {
                _ = LoadLatestSavedTranscriptAsync(SelectedPlaybackSession?.Id);
            }
        }
    }

    [RelayCommand]
    public void OpenSessionDetail(RecordingSession? session)
    {
        IsSessionSaved = false;
        if (session is not null)
        {
            SelectedPlaybackSession = session;
            _ = LoadLatestSavedTranscriptAsync(session.Id);
        }
        ActiveMobileTab = "detail";
    }

    [RelayCommand]
    public void ToggleKoda()
    {
        if (_recorder.CurrentState == SessionRecordingState.RecordingInProgress)
        {
            IsPetVoiceMuted = true;
            KodaQuickReplyText = "🔇 Grabación en curso: micrófono exclusivo para la grabadora y voz de Koda silenciada.";
        }
        IsKodaExpanded = !IsKodaExpanded;
    }

    [RelayCommand]
    public void TogglePetMute()
    {
        IsPetVoiceMuted = !IsPetVoiceMuted;
    }

    [RelayCommand]
    public void AskKodaQuick(string? question)
    {
        if (string.IsNullOrWhiteSpace(question)) return;
        var pending = MeetingTasks.Where(t => !t.IsCompleted).Take(3).ToList();
        KodaQuickReplyText = pending.Count == 0
            ? "No encontré pendientes guardados. Puedes sincronizar con la web para actualizar tu lista."
            : string.Join("\n\n", pending.Select(t => $"{t.Description} · {t.Assignee} · {t.DueDateText}"));
    }

    [RelayCommand]
    public void CycleFontScale()
    {
        if (FontScaleMultiplier < 1.1)
        {
            FontScaleMultiplier = 1.15;
            FontScaleBadgeText = "Texto: 115%";
        }
        else if (FontScaleMultiplier < 1.25)
        {
            FontScaleMultiplier = 1.30;
            FontScaleBadgeText = "Texto: 130%";
        }
        else
        {
            FontScaleMultiplier = 1.0;
            FontScaleBadgeText = "Texto: 100%";
        }
        OnPropertyChanged(nameof(ScaledBodyFontSize));
        OnPropertyChanged(nameof(ScaledHeadingFontSize));
        OnPropertyChanged(nameof(ScaledTimerFontSize));
    }

    [RelayCommand]
    public async Task SyncToWebAsync()
    {
        try
        {
            SyncStatusText = "⏳ Sincronizando Android ⇄ Web (Pull + Push SQLite)...";
            string? syncToken = await SecureStorage.Default.GetAsync("sync-token");
            if (string.IsNullOrWhiteSpace(syncToken))
            {
                SyncStatusText = "Configura el token de sincronización en Ajustes.";
                return;
            }
            if (!Uri.TryCreate(SyncServerUrl, UriKind.Absolute, out var syncUri) ||
                !(syncUri.Scheme == Uri.UriSchemeHttps || (syncUri.Scheme == Uri.UriSchemeHttp && syncUri.IsLoopback)))
            {
                SyncStatusText = "La conexión requiere HTTPS o una dirección local.";
                return;
            }
            var syncSvc = new MeetingSyncService(_db);
            var pullRes = await syncSvc.PullWebChangesToLocalAsync(SyncServerUrl, syncToken);
            var pushRes = await syncSvc.PushLocalSessionsToWebAsync(SyncServerUrl, syncToken);
            await RefreshSessionsAsync();
            await LoadLatestSavedTranscriptAsync(SelectedPlaybackSession?.Id);

            SyncStatusText = (pullRes.Success && pushRes.Success)
                ? $"✅ Sync Bidireccional OK (Web→Tel: {pullRes.NewTranscriptVersionsImported} vers/{pullRes.TasksSyncedToPhone} tareas | Conflictos preservados: {pullRes.ConcurrentConflictsDetectedAndPreserved + pushRes.ConflictVersionsPreserved})"
                : $"⚠️ Sincronización diferida (Modo Offline): {pullRes.Message}";
        }
        catch (Exception ex)
        {
            SyncStatusText = $"📡 Guardado en SQLite local (Web sin red: {ex.Message})";
        }
    }

    [RelayCommand]
    public async Task RefreshSessionsAsync()
    {
        var items = await _db.GetSessionsAsync();
        var tasks = await _db.GetAllTasksAsync();
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            Sessions.Clear();
            foreach (var item in items)
            {
                Sessions.Add(item);
            }
            MeetingTasks.Clear();
            foreach (var task in tasks) MeetingTasks.Add(task);
            OnPropertyChanged(nameof(RecentSessions));
            OnPropertyChanged(nameof(PendingTaskCount));
            OnPropertyChanged(nameof(HasTasks));
        });
    }

    [RelayCommand]
    private async Task ToggleTaskAsync(TaskChecklistEntity? task)
    {
        if (task is null) return;
        task.IsCompleted = !task.IsCompleted;
        task.UpdatedAtUtc = DateTime.UtcNow;
        task.LastModifiedByDevice = "Android";
        await _db.UpsertTaskByUpdatedAtAsync(task);
        await RefreshSessionsAsync();
    }

    private async Task LoadPricingCatalogAsync()
    {
        var catalog = await _db.GetPricingCatalogAsync();
        MainThread.BeginInvokeOnMainThread(() =>
        {
            PricingCatalog.Clear();
            foreach (var item in catalog)
            {
                PricingCatalog.Add(item);
            }
        });
    }

    [RelayCommand]
    public async Task StartRecordingAsync()
    {
        ActiveMobileTab = "record";
        try
        {
            if (_player.IsPlaying)
            {
                _player.Stop();
            }

            bool granted = await _recorder.EnsurePermissionsAsync();
            if (!granted)
            {
                StatusMessage = "❌ Permiso de micrófono denegado. Actívalo para grabar.";
                return;
            }

            var session = await _recorder.StartNewSessionAsync(NewSessionTitle);
            ActiveRecordingTitle = session.Title;
            IsSessionSaved = false;
            IsKodaExpanded = false;
            NewSessionTitle = string.Empty;
            StatusMessage = $"🔴 Grabando '{session.Title}' (sigue grabando con pantalla apagada o al cambiar de app)";
            UpdateRecordingButtonsState();
            await RefreshSessionsAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error al iniciar grabación: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task PauseRecordingAsync()
    {
        try
        {
            await _recorder.PauseAsync(false);
            UpdateRecordingButtonsState();
            await RefreshSessionsAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error al pausar: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task ResumeRecordingAsync()
    {
        try
        {
            await _recorder.ResumeAsync();
            UpdateRecordingButtonsState();
            await RefreshSessionsAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error al continuar: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task StopRecordingAsync()
    {
        try
        {
            var saved = await _recorder.StopAndSaveAsync();
            UpdateRecordingButtonsState();
            await RefreshSessionsAsync();

            if (saved is not null)
            {
                StatusMessage = $"✅ Guardado '{saved.Title}' ({saved.FormattedDuration}, {saved.FormattedFileSize})";
                SelectedPlaybackSession = saved;
                IsSessionSaved = true;
                ActiveMobileTab = "detail";
                await LoadLatestSavedTranscriptAsync(saved.Id);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error al detener y guardar: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task PlaySessionAsync(RecordingSession? session)
    {
        if (session is null) return;

        try
        {
            if (_recorder.CurrentState == SessionRecordingState.RecordingInProgress)
            {
                StatusMessage = "⚠️ Pausa o detén la grabación activa antes de reproducir otra sesión.";
                return;
            }

            SelectedPlaybackSession = session;
            if (_player.IsPlaying && string.Equals(_player.CurrentFilePath, session.WavFilePath, StringComparison.OrdinalIgnoreCase))
            {
                _player.Pause();
            }
            else
            {
                await _player.PlayOrResumeAsync(session.WavFilePath);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error de reproducción: {ex.Message}";
        }
    }

    [RelayCommand]
    public void StopPlayback()
    {
        _player.Stop();
    }

    [RelayCommand]
    public async Task DeleteSessionWithConfirmationAsync(RecordingSession? session)
    {
        if (session is null) return;

        if (_recorder.ActiveSessionId == session.Id)
        {
            StatusMessage = "⚠️ No puedes eliminar una sesión que se está grabando actualmente.";
            return;
        }

        Page? currentPage = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (currentPage is not null)
        {
            bool confirm = await currentPage.DisplayAlertAsync(
                "Confirmar eliminación",
                $"¿Seguro que deseas eliminar permanentemente '{session.Title}' ({session.FormattedDuration}) del teléfono?",
                "Sí, eliminar",
                "Cancelar");

            if (!confirm)
            {
                return;
            }
        }

        if (SelectedPlaybackSession?.Id == session.Id)
        {
            _player.Stop();
            SelectedPlaybackSession = null;
        }

        await _db.DeleteSessionAsync(session);
        await RefreshSessionsAsync();
        StatusMessage = $"🗑️ Sesión '{session.Title}' eliminada.";
    }

    [RelayCommand]
    public async Task CheckCrashRecoveryNowAsync()
    {
        int recovered = await _db.RecoverOrphanedSessionsAsync(_recorder.ActiveSessionId);
        if (recovered > 0)
        {
            HasRecoveryBanner = true;
            RecoveryBannerText = $"🛡️ Se recuperaron {recovered} sesión(es) interrumpidas reparando su encabezado RIFF/WAVE.";
        }
        else
        {
            StatusMessage = "✅ Verificación completada: todas las sesiones WAV en disco están íntegras.";
        }
        await RefreshSessionsAsync();
    }

    [RelayCommand]
    public async Task TranscribeSessionLocalAsync(RecordingSession? session)
    {
        if (IsTranscribing) return;
        var target = session ?? SelectedPlaybackSession ?? Sessions.FirstOrDefault();
        if (target is null)
        {
            StatusMessage = "⚠️ Selecciona primero una sesión grabada para transcribir.";
            return;
        }

        try
        {
            IsTranscribing = true;
            SelectedPlaybackSession = target;
            StatusMessage = $"⏳ Transcribiendo '{target.Title}' localmente en español...";
            string modelPath = Path.Combine(LocalWhisperTranscriptionService.GetModelsDirectory(), "ggml-base.bin");
            if (!File.Exists(modelPath))
            {
                modelPath = Path.Combine(LocalWhisperTranscriptionService.GetModelsDirectory(), "ggml-tiny.bin");
            }

            var res = await Task.Run(() => _sttService.TranscribeSpanishWavAsync(target, modelPath, threads: 4));
            LatestTranscriptText = res.FullTranscriptText;
            LatestSttMetricsText =
                $"Modelo: {res.ModelFileName} | Audio: {res.AudioDurationSeconds:F1}s | " +
                $"Carga: {res.ModelLoadTimeMs:F0}ms | Transcripción: {res.TranscriptionTimeMs:F0}ms (RTF: {res.RealTimeFactor:F3}x) | " +
                $"RAM Nativa Pico: {res.NativeHeapPeakMb:F1} MB | Versión: v{res.TranscriptVersionNumber} ({res.TranscriptSha256[..8]})";
            StatusMessage = $"✅ Transcripción local completada (v{res.TranscriptVersionNumber}, RTF={res.RealTimeFactor:F3}x)";
            await RefreshSessionsAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Error al transcribir localmente: {ex.Message}";
        }
        finally { IsTranscribing = false; }
    }

    private void OnRecorderProgressChanged(object? sender, RecordingProgressEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var ts = TimeSpan.FromSeconds(Math.Max(0, e.DurationSeconds));
            ElapsedTimeText = $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 100}";
            BytesWrittenText = $"{(e.DataBytesWritten + 44) / 1024.0:F1} KB";
            RmsLevel = e.NormalizedRmsLevel;
        });
    }

    private void OnRecorderStateChanged(object? sender, RecordingStateChangedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            StatusMessage = e.StatusMessage;
            UpdateRecordingButtonsState();
            await RefreshSessionsAsync();
        });
    }

    private void OnPlaybackStateChanged(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            IsPlayingAudio = _player.IsPlaying;
            double pos = _player.CurrentPositionSeconds;
            double total = Math.Max(0.1, _player.TotalDurationSeconds);
            PlaybackProgressRatio = _player.TotalDurationSeconds > 0
                ? Math.Clamp(pos / total, 0.0, 1.0)
                : 0.0;
            PlaybackProgressText = $"{TimeSpan.FromSeconds(pos):mm\\:ss} / {TimeSpan.FromSeconds(_player.TotalDurationSeconds):mm\\:ss}";
        });
    }

    private void UpdateRecordingButtonsState()
    {
        var state = _recorder.CurrentState;
        CanStartRecording = state == SessionRecordingState.Idle ||
                            state == SessionRecordingState.Saved ||
                            state == SessionRecordingState.Completed;

        CanPauseRecording = state == SessionRecordingState.RecordingInProgress;

        CanResumeRecording = state == SessionRecordingState.Paused ||
                             state == SessionRecordingState.PausedByInterruption;

        CanStopRecording = state == SessionRecordingState.RecordingInProgress ||
                           state == SessionRecordingState.Paused ||
                           state == SessionRecordingState.PausedByInterruption;

        OnPropertyChanged(nameof(ShowCompactRecorder));
        OnPropertyChanged(nameof(RecordingStateLabel));
        if (!_micArbitrator.CanAssistantUseMicrophone(out string reason))
        {
            MicArbitrationText = $"🔒 {reason} (TTS de mascota silenciado)";
        }
        else
        {
            MicArbitrationText = "🟢 Micrófono libre (disponible para grabación o asistente)";
        }

        if (CanStartRecording)
        {
            RmsLevel = 0;
        }
    }
}
