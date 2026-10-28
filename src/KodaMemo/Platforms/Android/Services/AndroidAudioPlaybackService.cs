using Android.Media;
using KodaMemo.Services;

namespace KodaMemo.Platforms.Android.Services;

public class AndroidAudioPlaybackService : IAudioPlaybackService
{
    private MediaPlayer? _player;
    private IDispatcherTimer? _timer;

    public bool IsPlaying => _player?.IsPlaying ?? false;
    public string? CurrentFilePath { get; private set; }
    public double CurrentPositionSeconds => (_player?.CurrentPosition ?? 0) / 1000.0;
    public double TotalDurationSeconds => (_player?.Duration ?? 0) / 1000.0;

    public event EventHandler? PlaybackStateChanged;

    public async Task PlayOrResumeAsync(string wavFilePath)
    {
        if (string.IsNullOrWhiteSpace(wavFilePath) || !File.Exists(wavFilePath))
        {
            throw new FileNotFoundException("El archivo WAV no existe en el almacenamiento local.", wavFilePath);
        }

        // Reparar encabezado por seguridad antes de reproducir si estuvo incompleto
        WavHeaderHelper.RepairWavFileOnDisk(wavFilePath);

        if (_player is not null && string.Equals(CurrentFilePath, wavFilePath, StringComparison.OrdinalIgnoreCase))
        {
            if (!_player.IsPlaying)
            {
                _player.Start();
                StartTimer();
                PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
            }
            return;
        }

        Stop();

        _player = new MediaPlayer();
        await _player.SetDataSourceAsync(wavFilePath);
        _player.Prepare();
        _player.Completion += OnPlaybackCompleted;
        CurrentFilePath = wavFilePath;
        _player.Start();

        StartTimer();
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        if (_player is not null && _player.IsPlaying)
        {
            _player.Pause();
            StopTimer();
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Stop()
    {
        StopTimer();
        if (_player is not null)
        {
            try
            {
                _player.Completion -= OnPlaybackCompleted;
                if (_player.IsPlaying)
                {
                    _player.Stop();
                }
                _player.Release();
                _player.Dispose();
            }
            catch
            {
            }
            _player = null;
        }
        CurrentFilePath = null;
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SeekTo(double seconds)
    {
        if (_player is not null)
        {
            int targetMs = (int)Math.Clamp(seconds * 1000.0, 0, _player.Duration);
            _player.SeekTo(targetMs);
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnPlaybackCompleted(object? sender, EventArgs e)
    {
        StopTimer();
        _player?.SeekTo(0);
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StartTimer()
    {
        if (_timer is null && Application.Current?.Dispatcher is not null)
        {
            _timer = Application.Current.Dispatcher.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(200);
            _timer.Tick += (_, _) => PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }
        _timer?.Start();
    }

    private void StopTimer()
    {
        _timer?.Stop();
    }
}
