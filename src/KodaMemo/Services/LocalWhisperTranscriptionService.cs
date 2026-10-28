using System.Diagnostics;
using System.Text;
using KodaMemo.Data;
using KodaMemo.Models;
using Whisper.net;

namespace KodaMemo.Services;

public class LocalSttBenchmarkResult
{
    public string ModelFileName { get; init; } = string.Empty;
    public long ModelFileSizeBytes { get; init; }
    public double AudioDurationSeconds { get; init; }
    public double ModelLoadTimeMs { get; init; }
    public double TranscriptionTimeMs { get; init; }
    public double RealTimeFactor => AudioDurationSeconds > 0
        ? (TranscriptionTimeMs / 1000.0) / AudioDurationSeconds
        : 0.0;
    public double NativeHeapBeforeMb { get; init; }
    public double NativeHeapPeakMb { get; init; }
    public double ManagedHeapDeltaMb { get; init; }
    public int TotalWindowsScanned { get; init; }
    public int ActiveVoiceWindowsProcessed { get; init; }
    public int SilentWindowsSkipped { get; init; }
    public int SegmentCount { get; init; }
    public int TranscriptVersionNumber { get; init; }
    public string TranscriptSha256 { get; init; } = string.Empty;
    public string FullTranscriptText { get; init; } = string.Empty;
}

public class LocalWhisperTranscriptionService
{
    private readonly AppDatabase _db;

    public LocalWhisperTranscriptionService(AppDatabase db)
    {
        _db = db;
    }

    public static string GetModelsDirectory()
    {
        string dir = Path.Combine(FileSystem.AppDataDirectory, "models");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public async Task<LocalSttBenchmarkResult> TranscribeSpanishWavAsync(
        RecordingSession session,
        string modelFilePath,
        int threads = 4,
        CancellationToken ct = default)
    {
        if (!File.Exists(session.WavFilePath))
        {
            throw new FileNotFoundException("No se encontró el archivo WAV de la sesión.", session.WavFilePath);
        }

        if (!File.Exists(modelFilePath))
        {
            throw new FileNotFoundException("No se encontró el modelo local de Whisper (.bin).", modelFilePath);
        }

        var (_, dataBytes, audioSec) = WavHeaderHelper.RepairWavFileOnDisk(session.WavFilePath);
        var modelInfo = new FileInfo(modelFilePath);

        GC.Collect();
        long managedBefore = GC.GetTotalMemory(false);
        double nativeBeforeMb = GetNativeHeapMb();

        var swLoad = Stopwatch.StartNew();
        using var whisperFactory = WhisperFactory.FromPath(modelFilePath);
        using var processor = whisperFactory.CreateBuilder()
            .WithLanguage("es")
            .WithThreads(threads)
            .WithNoContext()
            .Build();
        swLoad.Stop();

        double nativePeakMb = Math.Max(nativeBeforeMb, GetNativeHeapMb());

        var sb = new StringBuilder();
        int segmentCount = 0;
        int totalWindows = 0;
        int activeWindows = 0;
        int silentSkipped = 0;

        var swTranscribe = Stopwatch.StartNew();

        if (audioSec <= 45.0)
        {
            totalWindows = 1;
            activeWindows = 1;
            await using var fileStream = new FileStream(session.WavFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await foreach (var segment in processor.ProcessAsync(fileStream, ct))
            {
                string cleanText = (segment.Text ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(cleanText))
                {
                    segmentCount++;
                    sb.AppendLine($"[{FormatTime(segment.Start)} - {FormatTime(segment.End)}] {cleanText}");
                }
            }
            nativePeakMb = Math.Max(nativePeakMb, GetNativeHeapMb());
        }
        else
        {
            // Procesamiento acotado por ventanas de 30s (960,000 bytes a 16kHz 16-bit Mono) con filtro RMS de silencio
            const int windowSeconds = 30;
            const int windowByteSize = 32000 * windowSeconds;
            byte[] pcmBuffer = new byte[windowByteSize];
            byte[] chunkWavBuffer = new byte[44 + windowByteSize];

            await using var fs = new FileStream(session.WavFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            fs.Position = 44;

            int bytesRead;
            int winIdx = 0;
            while ((bytesRead = await fs.ReadAsync(pcmBuffer, 0, windowByteSize, ct)) > 0)
            {
                totalWindows++;
                double offsetSec = winIdx * (double)windowSeconds;
                winIdx++;

                double rmsDbFs = ComputeRmsDbFs(pcmBuffer, bytesRead);
                if (rmsDbFs < -27.5)
                {
                    silentSkipped++;
                    continue;
                }

                activeWindows++;
                using (var hs = new MemoryStream(chunkWavBuffer, 0, 44, writable: true))
                {
                    WavHeaderHelper.WriteWavHeader(hs, bytesRead, 16000, 1, 16);
                }
                Buffer.BlockCopy(pcmBuffer, 0, chunkWavBuffer, 44, bytesRead);

                using var chunkStream = new MemoryStream(chunkWavBuffer, 0, 44 + bytesRead, writable: false);
                await foreach (var segment in processor.ProcessAsync(chunkStream, ct))
                {
                    string cleanText = (segment.Text ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(cleanText))
                    {
                        segmentCount++;
                        var startAbs = TimeSpan.FromSeconds(offsetSec) + segment.Start;
                        var endAbs = TimeSpan.FromSeconds(offsetSec) + segment.End;
                        sb.AppendLine($"[{FormatTime(startAbs)} - {FormatTime(endAbs)}] {cleanText}");
                    }
                }

                double curNative = GetNativeHeapMb();
                if (curNative > nativePeakMb)
                {
                    nativePeakMb = curNative;
                }
            }
        }

        swTranscribe.Stop();

        long managedAfter = GC.GetTotalMemory(false);
        double managedDeltaMb = Math.Max(0, (managedAfter - managedBefore) / (1024.0 * 1024.0));

        string finalTranscript = sb.ToString().Trim();
        if (string.IsNullOrWhiteSpace(finalTranscript))
        {
            finalTranscript = $"[00:00 - {FormatTime(TimeSpan.FromSeconds(audioSec))}] (Sin habla inteligible detectada en {activeWindows} ventanas acústicas activas de {totalWindows} totales)";
        }

        var versionEntity = await _db.SaveNewTranscriptVersionAsync(
            session.Id,
            finalTranscript,
            $"whisper.cpp-local-chunked:{modelInfo.Name}");

        return new LocalSttBenchmarkResult
        {
            ModelFileName = modelInfo.Name,
            ModelFileSizeBytes = modelInfo.Length,
            AudioDurationSeconds = audioSec,
            ModelLoadTimeMs = swLoad.Elapsed.TotalMilliseconds,
            TranscriptionTimeMs = swTranscribe.Elapsed.TotalMilliseconds,
            NativeHeapBeforeMb = nativeBeforeMb,
            NativeHeapPeakMb = nativePeakMb,
            ManagedHeapDeltaMb = managedDeltaMb,
            TotalWindowsScanned = totalWindows,
            ActiveVoiceWindowsProcessed = activeWindows,
            SilentWindowsSkipped = silentSkipped,
            SegmentCount = segmentCount,
            TranscriptVersionNumber = versionEntity.VersionNumber,
            TranscriptSha256 = versionEntity.ContentSha256,
            FullTranscriptText = finalTranscript
        };
    }

    private static double ComputeRmsDbFs(byte[] pcm, int length)
    {
        int samples = length / 2;
        if (samples <= 0) return -100.0;
        double sumSq = 0;
        for (int i = 0; i + 1 < length; i += 2)
        {
            int s = (short)(pcm[i] | (pcm[i + 1] << 8));
            sumSq += (double)s * s;
        }
        double rms = Math.Sqrt(sumSq / samples);
        return rms > 0 ? 20.0 * Math.Log10(rms / 32768.0) : -100.0;
    }

    private static string FormatTime(TimeSpan ts) =>
        ts.TotalHours >= 1 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss");

    private static double GetNativeHeapMb()
    {
#if ANDROID
        return global::Android.OS.Debug.NativeHeapAllocatedSize / (1024.0 * 1024.0);
#else
        return 0.0;
#endif
    }
}
