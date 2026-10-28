using System.Text;

namespace KodaMemo.Services;

/// <summary>Writes and repairs classic PCM RIFF/WAVE headers for recordings owned by the app.</summary>
/// <remarks>Repair restores container lengths; it cannot recreate lost audio samples.</remarks>
public static class WavHeaderHelper
{
    public const int HeaderSize = 44;
    public const int DefaultSampleRate = 16000;
    public const short DefaultChannels = 1;
    public const short DefaultBitsPerSample = 16;

    /// <summary>
    /// Escribe un encabezado RIFF/WAVE vÃ¡lido de 44 bytes al inicio del stream.
    /// </summary>
    public static void WriteWavHeader(
        Stream stream,
        long pcmDataLengthBytes,
        int sampleRate = DefaultSampleRate,
        short channels = DefaultChannels,
        short bitsPerSample = DefaultBitsPerSample)
    {
        int byteRate = sampleRate * channels * (bitsPerSample / 8);
        short blockAlign = (short)(channels * (bitsPerSample / 8));
        uint subchunk2Size = (uint)Math.Min(uint.MaxValue, Math.Max(0, pcmDataLengthBytes));
        uint chunkSize = 36 + subchunk2Size;

        Span<byte> header = stackalloc byte[HeaderSize];
        Encoding.ASCII.GetBytes("RIFF", header.Slice(0, 4));
        BitConverter.TryWriteBytes(header.Slice(4, 4), chunkSize);
        Encoding.ASCII.GetBytes("WAVE", header.Slice(8, 4));
        Encoding.ASCII.GetBytes("fmt ", header.Slice(12, 4));
        BitConverter.TryWriteBytes(header.Slice(16, 4), 16u); // Subchunk1Size (PCM)
        BitConverter.TryWriteBytes(header.Slice(20, 2), (ushort)1); // AudioFormat = 1 (PCM)
        BitConverter.TryWriteBytes(header.Slice(22, 2), (ushort)channels);
        BitConverter.TryWriteBytes(header.Slice(24, 4), (uint)sampleRate);
        BitConverter.TryWriteBytes(header.Slice(28, 4), (uint)byteRate);
        BitConverter.TryWriteBytes(header.Slice(32, 2), (ushort)blockAlign);
        BitConverter.TryWriteBytes(header.Slice(34, 2), (ushort)bitsPerSample);
        Encoding.ASCII.GetBytes("data", header.Slice(36, 4));
        BitConverter.TryWriteBytes(header.Slice(40, 4), subchunk2Size);

        long originalPos = stream.Position;
        stream.Seek(0, SeekOrigin.Begin);
        stream.Write(header);
        if (originalPos > HeaderSize)
        {
            stream.Seek(originalPos, SeekOrigin.Begin);
        }
    }

    /// <summary>
    /// Repara el encabezado RIFF/WAVE de un archivo en disco tras una interrupciÃ³n o cierre inesperado,
    /// calculando los tamaÃ±os reales a partir de la longitud fÃ­sica del archivo.
    /// </summary>
    public static (bool Repaired, long DataBytes, double DurationSeconds) RepairWavFileOnDisk(
        string wavFilePath,
        int sampleRate = DefaultSampleRate,
        short channels = DefaultChannels,
        short bitsPerSample = DefaultBitsPerSample)
    {
        if (string.IsNullOrWhiteSpace(wavFilePath) || !File.Exists(wavFilePath))
        {
            return (false, 0, 0);
        }

        var fileInfo = new FileInfo(wavFilePath);
        if (fileInfo.Length <= HeaderSize)
        {
            return (false, 0, 0);
        }

        long dataBytes = fileInfo.Length - HeaderSize;
        int bytesPerSecond = sampleRate * channels * (bitsPerSample / 8);
        double durationSeconds = bytesPerSecond > 0 ? (double)dataBytes / bytesPerSecond : 0.0;

        using var fs = new FileStream(wavFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        WriteWavHeader(fs, dataBytes, sampleRate, channels, bitsPerSample);
        fs.Flush(true);

        return (true, dataBytes, durationSeconds);
    }
}
