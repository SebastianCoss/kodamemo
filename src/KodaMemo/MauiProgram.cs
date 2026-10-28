using KodaMemo.Data;
using KodaMemo.Services;
using KodaMemo.ViewModels;
using Microsoft.Extensions.Logging;

namespace KodaMemo;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		builder.Services.AddSingleton<AppDatabase>();
		builder.Services.AddSingleton<LocalWhisperTranscriptionService>();
		builder.Services.AddSingleton<MeetingSyncService>();

#if ANDROID
		builder.Services.AddSingleton<Platforms.Android.Services.AndroidAudioRecordingService>();
		builder.Services.AddSingleton<IAudioRecordingService>(sp =>
			sp.GetRequiredService<Platforms.Android.Services.AndroidAudioRecordingService>());
		builder.Services.AddSingleton<IMicrophoneArbitrationService>(sp =>
			sp.GetRequiredService<Platforms.Android.Services.AndroidAudioRecordingService>());
		builder.Services.AddSingleton<IAudioPlaybackService, Platforms.Android.Services.AndroidAudioPlaybackService>();
#endif

		builder.Services.AddSingleton<MainViewModel>();
		builder.Services.AddSingleton<MainPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
