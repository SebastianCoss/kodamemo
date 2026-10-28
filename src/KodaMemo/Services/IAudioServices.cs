using KodaMemo.Models;

namespace KodaMemo.Services;

public class RecordingProgressEventArgs : EventArgs
{
    public int SessionId { get; init; }
    public double DurationSeconds { get; init; }
    public long DataBytesWritten { get; init; }
    public double NormalizedRmsLevel { get; init; }
}

public class RecordingStateChangedEventArgs : EventArgs
{
    public int? SessionId { get; init; }
    public SessionRecordingState State { get; init; }
    public string StatusMessage { get; init; } = string.Empty;
}

public interface IAudioRecordingService
{
    int? ActiveSessionId { get; }
    SessionRecordingState CurrentState { get; }
    double CurrentDurationSeconds { get; }
    long CurrentDataBytesWritten { get; }
    double CurrentRmsLevel { get; }
    bool IsMicrophoneHeldByRecorder { get; }

    event EventHandler<RecordingProgressEventArgs>? ProgressChanged;
    event EventHandler<RecordingStateChangedEventArgs>? StateChanged;

    Task<bool> EnsurePermissionsAsync();
    Task<RecordingSession> StartNewSessionAsync(string? customTitle = null);
    Task PauseAsync(bool pausedBySystemInterruption = false);
    Task ResumeAsync();
    Task<RecordingSession?> StopAndSaveAsync();
}

public interface IAudioPlaybackService
{
    bool IsPlaying { get; }
    string? CurrentFilePath { get; }
    double CurrentPositionSeconds { get; }
    double TotalDurationSeconds { get; }

    event EventHandler? PlaybackStateChanged;

    Task PlayOrResumeAsync(string wavFilePath);
    void Pause();
    void Stop();
    void SeekTo(double seconds);
}

public interface IMicrophoneArbitrationService
{
    bool CanAssistantUseMicrophone(out string reasonIfBlocked);
    bool ShouldMuteAssistantVoiceOutput();
}
