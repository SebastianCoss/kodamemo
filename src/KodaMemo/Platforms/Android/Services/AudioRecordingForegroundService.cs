using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.OS;
using androidx_core = AndroidX.Core.App;

namespace KodaMemo.Platforms.Android.Services;

[Service(
    Name = "io.github.sebastiancoss.kodamemo.AudioRecordingForegroundService",
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeMicrophone)]
public class AudioRecordingForegroundService : Service, AudioManager.IOnAudioFocusChangeListener
{
    public const string ChannelId = "audio_ia_recording_channel";
    public const int NotificationId = 4101;

    public const string ActionStart = "ACTION_START_RECORDING_FG";
    public const string ActionUpdate = "ACTION_UPDATE_RECORDING_FG";
    public const string ActionPauseFromNotification = "ACTION_PAUSE_FROM_NOTIF";
    public const string ActionResumeFromNotification = "ACTION_RESUME_FROM_NOTIF";
    public const string ActionStopFromNotification = "ACTION_STOP_FROM_NOTIF";
    public const string ActionStopService = "ACTION_STOP_SERVICE_FG";

    public const string ExtraTitle = "EXTRA_TITLE";
    public const string ExtraStatusText = "EXTRA_STATUS_TEXT";
    public const string ExtraIsPaused = "EXTRA_IS_PAUSED";

    private PowerManager.WakeLock? _wakeLock;
    private AudioManager? _audioManager;
    private AudioFocusRequestClass? _focusRequest;

    public static event EventHandler<string>? NotificationActionTriggered;
    public static event EventHandler<AudioFocus>? AudioFocusChanged;

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        EnsureNotificationChannel();
        _audioManager = (AudioManager?)GetSystemService(AudioService);
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        string action = intent?.Action ?? ActionStart;

        if (action == ActionPauseFromNotification ||
            action == ActionResumeFromNotification ||
            action == ActionStopFromNotification)
        {
            NotificationActionTriggered?.Invoke(this, action);
            return StartCommandResult.Sticky;
        }

        if (action == ActionStopService)
        {
            ReleaseWakeLock();
            AbandonAudioFocus();
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        string title = intent?.GetStringExtra(ExtraTitle) ?? "Sesión de audio";
        string statusText = intent?.GetStringExtra(ExtraStatusText) ?? "Grabando en segundo plano...";
        bool isPaused = intent?.GetBooleanExtra(ExtraIsPaused, false) ?? false;

        var notification = BuildNotification(title, statusText, isPaused);

        if (action == ActionStart)
        {
            AcquireWakeLock();
            RequestAudioFocus();

            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                StartForeground(NotificationId, notification, ForegroundService.TypeMicrophone);
            }
            else
            {
                StartForeground(NotificationId, notification);
            }
        }
        else
        {
            if (isPaused)
            {
                ReleaseWakeLock();
            }
            else
            {
                AcquireWakeLock();
            }

            var nm = (NotificationManager?)GetSystemService(NotificationService);
            nm?.Notify(NotificationId, notification);
        }

        return StartCommandResult.Sticky;
    }

    public void OnAudioFocusChange(AudioFocus focusChange)
    {
        AudioFocusChanged?.Invoke(this, focusChange);
    }

    private void RequestAudioFocus()
    {
        try
        {
            if (_audioManager is null) return;

            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                var attrs = new AudioAttributes.Builder()
                    .SetUsage(AudioUsageKind.Media)?
                    .SetContentType(AudioContentType.Speech)?
                    .Build();

                if (attrs is not null)
                {
                    _focusRequest = new AudioFocusRequestClass.Builder(AudioFocus.Gain)
                        .SetAudioAttributes(attrs)
                        .SetAcceptsDelayedFocusGain(false)
                        .SetOnAudioFocusChangeListener(this)
                        .Build();

                    if (_focusRequest is not null)
                    {
                        _audioManager.RequestAudioFocus(_focusRequest);
                    }
                }
            }
        }
        catch
        {
            // No bloquear la grabación si AudioFocus falla
        }
    }

    private void AbandonAudioFocus()
    {
        try
        {
            if (_audioManager is not null && _focusRequest is not null && Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                _audioManager.AbandonAudioFocusRequest(_focusRequest);
                _focusRequest = null;
            }
        }
        catch
        {
        }
    }

    private void AcquireWakeLock()
    {
        try
        {
            if (_wakeLock is null)
            {
                var pm = (PowerManager?)GetSystemService(PowerService);
                _wakeLock = pm?.NewWakeLock(WakeLockFlags.Partial, "KodaMemo::RecordingWakeLock");
                if (_wakeLock is not null)
                {
                    _wakeLock.SetReferenceCounted(false);
                }
            }

            if (_wakeLock is not null && !_wakeLock.IsHeld)
            {
                _wakeLock.Acquire();
            }
        }
        catch
        {
        }
    }

    private void ReleaseWakeLock()
    {
        try
        {
            if (_wakeLock is not null && _wakeLock.IsHeld)
            {
                _wakeLock.Release();
            }
        }
        catch
        {
        }
    }

    private void EnsureNotificationChannel()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var nm = (NotificationManager?)GetSystemService(NotificationService);
            if (nm?.GetNotificationChannel(ChannelId) is null)
            {
                var channel = new NotificationChannel(
                    ChannelId,
                    "Grabación de Sesiones Activas",
                    NotificationImportance.Low)
                {
                    Description = "Notificación persistente durante la grabación de audio con pantalla apagada"
                };
                nm?.CreateNotificationChannel(channel);
            }
        }
    }

    private Notification BuildNotification(string title, string statusText, bool isPaused)
    {
        var openAppIntent = new Intent(this, typeof(MainActivity));
        openAppIntent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        var contentPendingIntent = PendingIntent.GetActivity(
            this,
            100,
            openAppIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var toggleIntent = new Intent(this, typeof(AudioRecordingForegroundService));
        toggleIntent.SetAction(isPaused ? ActionResumeFromNotification : ActionPauseFromNotification);
        var togglePendingIntent = PendingIntent.GetService(
            this,
            101,
            toggleIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var stopIntent = new Intent(this, typeof(AudioRecordingForegroundService));
        stopIntent.SetAction(ActionStopFromNotification);
        var stopPendingIntent = PendingIntent.GetService(
            this,
            102,
            stopIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var builder = new androidx_core.NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle(title)!
            .SetContentText(statusText)!
            .SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay)!
            .SetOngoing(true)!
            .SetOnlyAlertOnce(true)!
            .SetContentIntent(contentPendingIntent)!
            .AddAction(
                isPaused ? global::Android.Resource.Drawable.IcMediaPlay : global::Android.Resource.Drawable.IcMediaPause,
                isPaused ? "Continuar" : "Pausar",
                togglePendingIntent)!
            .AddAction(
                global::Android.Resource.Drawable.IcDelete,
                "Detener",
                stopPendingIntent)!;

        return builder.Build()!;
    }

    public override void OnDestroy()
    {
        ReleaseWakeLock();
        AbandonAudioFocus();
        base.OnDestroy();
    }
}
