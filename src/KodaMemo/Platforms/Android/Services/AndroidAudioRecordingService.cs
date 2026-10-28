using System.Diagnostics;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.OS;
using Android.Util;
using KodaMemo.Data;
using KodaMemo.Models;
using KodaMemo.Services;

namespace KodaMemo.Platforms.Android.Services;

public class RecordingIntegrityMetrics
{
    public double ActiveWallClockSeconds { get; init; }
    public double PcmAudioDurationSeconds { get; init; }
    public long ActualPcmBytesWritten { get; init; }
    public long ExpectedPcmBytesFromWallClock { get; init; }
    public double DifferenceMs => (PcmAudioDurationSeconds - ActiveWallClockSeconds) * 1000.0;
    public double LossOrDriftPercent => ActiveWallClockSeconds > 0
        ? ((ActiveWallClockSeconds - PcmAudioDurationSeconds) / ActiveWallClockSeconds) * 100.0
        : 0.0;
    public int TotalReadCalls { get; init; }
    public int NonPositiveReadCalls { get; init; }
}

public class AndroidAudioRecordingService : IAudioRecordingService, IMicrophoneArbitrationService
{
    private readonly AppDatabase _db;
    private readonly SemaphoreSlim _stateLock = new(1, 1);

    private RecordingSession? _activeSession;
    private FileStream? _wavFileStream;
    private AudioRecord? _audioRecord;
    private CancellationTokenSource? _captureCts;
    private Task? _captureTask;

    private readonly Stopwatch _activeCaptureStopwatch = new();
    private int _totalReadCalls;
    private int _nonPositiveReadCalls;

    private const int SampleRate = 16000;
    private const int BytesPerSample = 2; // 16-bit PCM mono = 2 bytes/sample = 32,000 bytes/sec
    private const int BytesPerSecond = SampleRate * BytesPerSample;
    public const long DefaultMinRequiredFreeBytes = 50L * 1024L * 1024L; // 50 MB mínimo libre

    /// <summary>
    /// Permite inyectar un límite simulado de espacio libre para probar determinísticamente
    /// el comportamiento ante falta de espacio al inicio o a mitad de grabación.
    /// </summary>
    public long? SimulatedFreeDiskBytesOverride { get; set; }

    public RecordingIntegrityMetrics? LastCompletedIntegrityMetrics { get; private set; }

    public int? ActiveSessionId => _activeSession?.Id;
    public SessionRecordingState CurrentState { get; private set; } = SessionRecordingState.Idle;
    public double CurrentDurationSeconds { get; private set; }
    public long CurrentDataBytesWritten { get; private set; }
    public double CurrentRmsLevel { get; private set; }
    public double CurrentActiveWallClockSeconds => _activeCaptureStopwatch.Elapsed.TotalSeconds;

    public bool IsMicrophoneHeldByRecorder =>
        CurrentState == SessionRecordingState.RecordingInProgress;

    public event EventHandler<RecordingProgressEventArgs>? ProgressChanged;
    public event EventHandler<RecordingStateChangedEventArgs>? StateChanged;

    public AndroidAudioRecordingService(AppDatabase db)
    {
        _db = db;

        AudioRecordingForegroundService.NotificationActionTriggered += OnNotificationActionTriggered;
        AudioRecordingForegroundService.AudioFocusChanged += OnAudioFocusChanged;
    }

    private async void OnNotificationActionTriggered(object? sender, string action)
    {
        try
        {
            if (action == AudioRecordingForegroundService.ActionPauseFromNotification)
            {
                await PauseAsync(false);
            }
            else if (action == AudioRecordingForegroundService.ActionResumeFromNotification)
            {
                await ResumeAsync();
            }
            else if (action == AudioRecordingForegroundService.ActionStopFromNotification)
            {
                await StopAndSaveAsync();
            }
        }
        catch
        {
        }
    }

    private async void OnAudioFocusChanged(object? sender, AudioFocus focusChange)
    {
        Log.Info("KodaMemoTest", $"OnAudioFocusChanged received: {focusChange}");
        if (focusChange == AudioFocus.Loss ||
            focusChange == AudioFocus.LossTransient ||
            focusChange == AudioFocus.LossTransientCanDuck)
        {
            if (CurrentState == SessionRecordingState.RecordingInProgress)
            {
                await PauseAsync(pausedBySystemInterruption: true);
            }
        }
    }

    public bool CanAssistantUseMicrophone(out string reasonIfBlocked)
    {
        if (IsMicrophoneHeldByRecorder)
        {
            reasonIfBlocked = "El micrófono está reservado por la grabación de sesión activa. Pausa la grabación o escribe por texto.";
            return false;
        }

        reasonIfBlocked = string.Empty;
        return true;
    }

    public bool ShouldMuteAssistantVoiceOutput()
    {
        return CurrentState == SessionRecordingState.RecordingInProgress;
    }

    public long GetAvailableFreeDiskBytes()
    {
        if (SimulatedFreeDiskBytesOverride.HasValue)
        {
            return SimulatedFreeDiskBytesOverride.Value;
        }

        try
        {
            var stat = new StatFs(FileSystem.AppDataDirectory);
            return stat.AvailableBytes;
        }
        catch
        {
            return long.MaxValue;
        }
    }

    public async Task<bool> EnsurePermissionsAsync()
    {
        var micStatus = await Permissions.CheckStatusAsync<Permissions.Microphone>();
        if (micStatus != PermissionStatus.Granted)
        {
            // Verificar también a nivel de Android Context por si fue revocado vía ADB/Ajustes
            if (AndroidX.Core.Content.ContextCompat.CheckSelfPermission(
                    global::Android.App.Application.Context,
                    global::Android.Manifest.Permission.RecordAudio) != Permission.Granted)
            {
                return false;
            }
        }

        if (AndroidX.Core.Content.ContextCompat.CheckSelfPermission(
                global::Android.App.Application.Context,
                global::Android.Manifest.Permission.RecordAudio) != Permission.Granted)
        {
            return false;
        }

        return true;
    }

    public async Task<RecordingSession> StartNewSessionAsync(string? customTitle = null)
    {
        await _stateLock.WaitAsync();
        try
        {
            if (CurrentState == SessionRecordingState.RecordingInProgress ||
                CurrentState == SessionRecordingState.Paused ||
                CurrentState == SessionRecordingState.PausedByInterruption)
            {
                throw new InvalidOperationException("Ya existe una sesión de grabación activa o en pausa.");
            }

            if (AndroidX.Core.Content.ContextCompat.CheckSelfPermission(
                    global::Android.App.Application.Context,
                    global::Android.Manifest.Permission.RecordAudio) != Permission.Granted)
            {
                throw new UnauthorizedAccessException("Permiso RECORD_AUDIO denegado por el sistema.");
            }

            long freeBytes = GetAvailableFreeDiskBytes();
            if (freeBytes < DefaultMinRequiredFreeBytes)
            {
                throw new IOException($"Espacio insuficiente en almacenamiento local ({freeBytes / (1024 * 1024)} MB libres; se requieren al menos {DefaultMinRequiredFreeBytes / (1024 * 1024)} MB).");
            }

            string recordingsDir = Path.Combine(FileSystem.AppDataDirectory, "recordings");
            Directory.CreateDirectory(recordingsDir);

            DateTime nowLocal = DateTime.Now;
            string fileName = $"sesion_{nowLocal:yyyyMMdd_HHmmss}.wav";
            string fullPath = Path.Combine(recordingsDir, fileName);

            string title = string.IsNullOrWhiteSpace(customTitle)
                ? $"Sesión {nowLocal:dd/MM HH:mm:ss}"
                : customTitle.Trim();

            var session = new RecordingSession
            {
                Title = title,
                WavFilePath = fullPath,
                State = SessionRecordingState.RecordingInProgress,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                SampleRateHz = SampleRate,
                Channels = 1,
                BitsPerSample = 16,
                DataBytesWritten = 0,
                DurationSeconds = 0
            };

            await _db.SaveSessionAsync(session);
            _activeSession = session;

            _wavFileStream = new FileStream(
                fullPath,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 16384);

            WavHeaderHelper.WriteWavHeader(_wavFileStream, 0, SampleRate, 1, 16);
            _wavFileStream.Seek(WavHeaderHelper.HeaderSize, SeekOrigin.Begin);
            _wavFileStream.Flush(true);

            CurrentDataBytesWritten = 0;
            CurrentDurationSeconds = 0;
            CurrentRmsLevel = 0;
            _totalReadCalls = 0;
            _nonPositiveReadCalls = 0;
            _activeCaptureStopwatch.Reset();
            CurrentState = SessionRecordingState.RecordingInProgress;

            StartOrUpdateForegroundService(
                AudioRecordingForegroundService.ActionStart,
                session.Title,
                "Grabando (00:00) — Activo con pantalla apagada",
                isPaused: false);

            StartAudioRecordLoop();

            RaiseStateChanged("Grabación en curso (WAV 16kHz Mono)");
            return session;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task PauseAsync(bool pausedBySystemInterruption = false)
    {
        await _stateLock.WaitAsync();
        try
        {
            if (_activeSession is null || CurrentState != SessionRecordingState.RecordingInProgress)
            {
                return;
            }

            await StopAudioRecordLoopAsync();
            await CheckpointWavAndDatabaseAsync(
                pausedBySystemInterruption
                    ? SessionRecordingState.PausedByInterruption
                    : SessionRecordingState.Paused);

            CurrentState = _activeSession.State;
            CurrentRmsLevel = 0;

            string statusMsg = pausedBySystemInterruption
                ? $"Pausado por interrupción de audio ({FormatTime(CurrentDurationSeconds)})"
                : $"En pausa ({FormatTime(CurrentDurationSeconds)}) — Toca Continuar";

            StartOrUpdateForegroundService(
                AudioRecordingForegroundService.ActionUpdate,
                _activeSession.Title,
                statusMsg,
                isPaused: true);

            RaiseStateChanged(statusMsg);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task ResumeAsync()
    {
        await _stateLock.WaitAsync();
        try
        {
            if (_activeSession is null ||
                (CurrentState != SessionRecordingState.Paused &&
                 CurrentState != SessionRecordingState.PausedByInterruption))
            {
                return;
            }

            long freeBytes = GetAvailableFreeDiskBytes();
            if (freeBytes < DefaultMinRequiredFreeBytes)
            {
                throw new IOException($"No se puede reanudar: espacio libre insuficiente ({freeBytes / (1024 * 1024)} MB).");
            }

            if (_wavFileStream is null)
            {
                _wavFileStream = new FileStream(
                    _activeSession.WavFilePath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.Read,
                    bufferSize: 16384);
            }

            _wavFileStream.Seek(0, SeekOrigin.End);

            _activeSession.State = SessionRecordingState.RecordingInProgress;
            await _db.SaveSessionAsync(_activeSession);

            CurrentState = SessionRecordingState.RecordingInProgress;

            StartOrUpdateForegroundService(
                AudioRecordingForegroundService.ActionUpdate,
                _activeSession.Title,
                $"Grabando ({FormatTime(CurrentDurationSeconds)}) — Activo con pantalla apagada",
                isPaused: false);

            StartAudioRecordLoop();
            RaiseStateChanged("Grabación reanudada");
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<RecordingSession?> StopAndSaveAsync()
    {
        await _stateLock.WaitAsync();
        try
        {
            return await StopAndSaveInternalAsync(stoppedByLowSpace: false);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task<RecordingSession?> StopAndSaveInternalAsync(bool stoppedByLowSpace)
    {
        if (_activeSession is null)
        {
            return null;
        }

        await StopAudioRecordLoopAsync();
        await CheckpointWavAndDatabaseAsync(SessionRecordingState.Saved);

        if (_wavFileStream is not null)
        {
            await _wavFileStream.DisposeAsync();
            _wavFileStream = null;
        }

        StopForegroundService();

        double wallSec = _activeCaptureStopwatch.Elapsed.TotalSeconds;
        double pcmSec = (double)CurrentDataBytesWritten / BytesPerSecond;
        LastCompletedIntegrityMetrics = new RecordingIntegrityMetrics
        {
            ActiveWallClockSeconds = wallSec,
            PcmAudioDurationSeconds = pcmSec,
            ActualPcmBytesWritten = CurrentDataBytesWritten,
            ExpectedPcmBytesFromWallClock = (long)Math.Round(wallSec * BytesPerSecond),
            TotalReadCalls = _totalReadCalls,
            NonPositiveReadCalls = _nonPositiveReadCalls
        };

        if (stoppedByLowSpace)
        {
            _activeSession.WasRecoveredFromCrash = true;
            _activeSession.RecoveryNotes =
                $"Grabación detenida y guardada preventivamente por falta de espacio libre ({pcmSec:F2}s preservados íntegramente).";
        }
        else
        {
            _activeSession.RecoveryNotes =
                $"Integridad medida: WallClock={wallSec:F2}s vs PCM={pcmSec:F2}s (Dif={LastCompletedIntegrityMetrics.DifferenceMs:+0.0;-0.0;0.0}ms, Pérdida/Deriva={LastCompletedIntegrityMetrics.LossOrDriftPercent:F3}%, Errores lectura={_nonPositiveReadCalls}/{_totalReadCalls}).";
        }

        await _db.SaveSessionAsync(_activeSession);

        Log.Info("KodaMemoTest",
            $"INTEGRITY_REPORT SessionId={_activeSession.Id} Title='{_activeSession.Title}' " +
            $"WallClockSec={wallSec:F3} PcmSec={pcmSec:F3} Bytes={CurrentDataBytesWritten} " +
            $"DiffMs={LastCompletedIntegrityMetrics.DifferenceMs:F1} LossPct={LastCompletedIntegrityMetrics.LossOrDriftPercent:F4}% " +
            $"ReadCalls={_totalReadCalls} ReadErrors={_nonPositiveReadCalls}");

        var finishedSession = _activeSession;
        _activeSession = null;
        CurrentState = SessionRecordingState.Idle;
        CurrentRmsLevel = 0;

        string msg = stoppedByLowSpace
            ? $"⚠️ Espacio agotado: se guardó íntegramente el audio previo '{finishedSession.Title}' ({finishedSession.FormattedDuration})"
            : $"✅ Guardado '{finishedSession.Title}' ({finishedSession.FormattedDuration}, Deriva/Pérdida: {LastCompletedIntegrityMetrics.LossOrDriftPercent:F3}%)";

        RaiseStateChanged(msg);
        return finishedSession;
    }

    private void StartAudioRecordLoop()
    {
        int minBufferBytes = AudioRecord.GetMinBufferSize(
            SampleRate,
            ChannelIn.Mono,
            Encoding.Pcm16bit);

        int bufferSizeBytes = Math.Max(minBufferBytes * 4, 32000); // ~1s hardware ring buffer to prevent overruns

        _audioRecord = new AudioRecord(
            AudioSource.Mic,
            SampleRate,
            ChannelIn.Mono,
            Encoding.Pcm16bit,
            bufferSizeBytes);

        _audioRecord.StartRecording();
        _activeCaptureStopwatch.Start();
        _captureCts = new CancellationTokenSource();
        CancellationToken ct = _captureCts.Token;

        _captureTask = Task.Run(async () =>
        {
            byte[] buffer = new byte[3200]; // 100ms chunks (1600 samples * 2 bytes)
            long bytesSinceLastCheckpoint = 0;
            int lastNotifiedSecond = -1;

            try
            {
                while (!ct.IsCancellationRequested && _audioRecord is not null)
                {
                    int bytesRead = _audioRecord.Read(buffer, 0, buffer.Length);
                    _totalReadCalls++;

                    if (bytesRead <= 0)
                    {
                        _nonPositiveReadCalls++;
                        await Task.Delay(10, ct);
                        continue;
                    }

                    if (_wavFileStream is not null)
                    {
                        await _wavFileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                        CurrentDataBytesWritten += bytesRead;
                        CurrentDurationSeconds = (double)CurrentDataBytesWritten / BytesPerSecond;
                        bytesSinceLastCheckpoint += bytesRead;
                    }

                    CurrentRmsLevel = ComputeNormalizedRms(buffer, bytesRead);

                    if (_activeSession is not null)
                    {
                        ProgressChanged?.Invoke(this, new RecordingProgressEventArgs
                        {
                            SessionId = _activeSession.Id,
                            DurationSeconds = CurrentDurationSeconds,
                            DataBytesWritten = CurrentDataBytesWritten,
                            NormalizedRmsLevel = CurrentRmsLevel
                        });
                    }

                    // Checkpoint crash-safe cada ~2 segundos (64,000 bytes)
                    if (bytesSinceLastCheckpoint >= BytesPerSecond * 2)
                    {
                        bytesSinceLastCheckpoint = 0;
                        await CheckpointWavAndDatabaseAsync(SessionRecordingState.RecordingInProgress);

                        // Verificar si el almacenamiento se agotó durante la grabación
                        long freeNow = GetAvailableFreeDiskBytes();
                        if (freeNow < DefaultMinRequiredFreeBytes)
                        {
                            Log.Warn("KodaMemoTest", $"Low disk space detected mid-recording ({freeNow} bytes). Stopping safely.");
                            _ = Task.Run(async () =>
                            {
                                await _stateLock.WaitAsync();
                                try
                                {
                                    await StopAndSaveInternalAsync(stoppedByLowSpace: true);
                                }
                                finally
                                {
                                    _stateLock.Release();
                                }
                            });
                            break;
                        }

                        int currentSec = (int)CurrentDurationSeconds;
                        if (currentSec != lastNotifiedSecond && _activeSession is not null)
                        {
                            lastNotifiedSecond = currentSec;
                            StartOrUpdateForegroundService(
                                AudioRecordingForegroundService.ActionUpdate,
                                _activeSession.Title,
                                $"Grabando ({FormatTime(CurrentDurationSeconds)}) — Pantalla apagada soportada",
                                isPaused: false);
                        }
                    }
                }
            }
            catch (System.OperationCanceledException)
            {
            }
            catch (IOException ioEx)
            {
                Log.Error("KodaMemoTest", $"IOException during capture (disk full/IO error): {ioEx.Message}");
                await CheckpointWavAndDatabaseAsync(SessionRecordingState.Saved);
            }
            catch (Exception)
            {
                await CheckpointWavAndDatabaseAsync(SessionRecordingState.PausedByInterruption);
            }
        }, ct);
    }

    private async Task StopAudioRecordLoopAsync()
    {
        if (_captureCts is not null)
        {
            await _captureCts.CancelAsync();
        }

        if (_captureTask is not null)
        {
            try
            {
                await _captureTask;
            }
            catch
            {
            }
            _captureTask = null;
        }

        _activeCaptureStopwatch.Stop();
        _captureCts?.Dispose();
        _captureCts = null;

        if (_audioRecord is not null)
        {
            try
            {
                if (_audioRecord.RecordingState == RecordState.Recording)
                {
                    _audioRecord.Stop();
                }
            }
            catch
            {
            }

            try
            {
                _audioRecord.Release();
                _audioRecord.Dispose();
            }
            catch
            {
            }

            _audioRecord = null;
        }
    }

    private async Task CheckpointWavAndDatabaseAsync(SessionRecordingState targetState)
    {
        try
        {
            if (_wavFileStream is not null)
            {
                WavHeaderHelper.WriteWavHeader(_wavFileStream, CurrentDataBytesWritten, SampleRate, 1, 16);
                _wavFileStream.Flush(true);
            }

            if (_activeSession is not null)
            {
                _activeSession.DataBytesWritten = CurrentDataBytesWritten;
                _activeSession.DurationSeconds = CurrentDurationSeconds;
                _activeSession.State = targetState;
                await _db.SaveSessionAsync(_activeSession);
            }
        }
        catch
        {
        }
    }

    private static double ComputeNormalizedRms(byte[] pcm16Buffer, int bytesCount)
    {
        int sampleCount = bytesCount / 2;
        if (sampleCount <= 0) return 0;

        double sumSquares = 0;
        for (int i = 0; i < sampleCount * 2; i += 2)
        {
            short sample = (short)(pcm16Buffer[i] | (pcm16Buffer[i + 1] << 8));
            double norm = sample / 32768.0;
            sumSquares += norm * norm;
        }

        double rms = Math.Sqrt(sumSquares / sampleCount);
        return Math.Clamp(rms * 4.5, 0.0, 1.0);
    }

    private static string FormatTime(double seconds)
    {
        return TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"mm\:ss");
    }

    private static void StartOrUpdateForegroundService(string action, string title, string statusText, bool isPaused)
    {
        var context = global::Android.App.Application.Context;
        var intent = new Intent(context, typeof(AudioRecordingForegroundService));
        intent.SetAction(action);
        intent.PutExtra(AudioRecordingForegroundService.ExtraTitle, title);
        intent.PutExtra(AudioRecordingForegroundService.ExtraStatusText, statusText);
        intent.PutExtra(AudioRecordingForegroundService.ExtraIsPaused, isPaused);

        if (action == AudioRecordingForegroundService.ActionStart && Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            context.StartForegroundService(intent);
        }
        else
        {
            context.StartService(intent);
        }
    }

    private static void StopForegroundService()
    {
        var context = global::Android.App.Application.Context;
        var intent = new Intent(context, typeof(AudioRecordingForegroundService));
        intent.SetAction(AudioRecordingForegroundService.ActionStopService);
        context.StartService(intent);
    }

    private void RaiseStateChanged(string message)
    {
        StateChanged?.Invoke(this, new RecordingStateChangedEventArgs
        {
            SessionId = _activeSession?.Id,
            State = CurrentState,
            StatusMessage = message
        });
    }
}
