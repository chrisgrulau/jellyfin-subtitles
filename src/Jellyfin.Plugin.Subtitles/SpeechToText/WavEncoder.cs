using System;
using System.Buffers.Binary;

namespace Jellyfin.Plugin.Subtitles.SpeechToText;

/// <summary>
/// Encodes samples as a 16-bit PCM WAV file, the format every speech-to-text service accepts.
/// </summary>
public static class WavEncoder
{
    /// <summary>
    /// Encodes mono samples.
    /// </summary>
    /// <param name="samples">Samples in [-1, 1] (values outside are clipped).</param>
    /// <param name="sampleRate">Samples per second.</param>
    /// <returns>The WAV file bytes.</returns>
    public static byte[] Encode(ReadOnlySpan<float> samples, int sampleRate = Audio.AudioFormat.SampleRate)
    {
        var data = samples.Length * 2;
        var wav = new byte[44 + data];
        var span = wav.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + data);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], data);
        for (var i = 0; i < samples.Length; i++)
        {
            var v = (short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * 32767f);
            BinaryPrimitives.WriteInt16LittleEndian(span[(44 + (i * 2))..], v);
        }

        return wav;
    }
}
