using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;

namespace BringEngine.Audio;

/// <summary>
/// Native-allocated normalized interleaved Float32 PCM clip data ([-1, 1]).
/// Caller owns Samples and must free via WavDecoder.Free(ref decoded) (AudioSystem takes ownership when passed to CreateClipFromDecoded).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct DecodedPcmData
{
    public float* Samples;
    public nuint AllocBytes;
    public int FrameCount;
    public int Channels;
    public int SampleRate;

    public readonly int SampleCount => FrameCount * Channels;
    public readonly float DurationSeconds => SampleRate > 0 ? (float)FrameCount / SampleRate : 0f;
}

/// <summary>
/// Zero-dependency RIFF/WAVE PCM & IEEE-Float decoder + procedural waveform helpers.
/// Decodes 8/16/24/32-bit PCM and 32-bit float WAV files (mono, stereo, or multi-channel downmixed to stereo)
/// directly into 64-byte aligned native memory tracked by NativeMemoryTracker (0 leaks).
/// </summary>
public static unsafe class WavDecoder
{
    private const uint ChunkRiff = 0x46464952u; // "RIFF"
    private const uint FormatWave = 0x45564157u; // "WAVE"
    private const uint ChunkFmt = 0x20746D66u;  // "fmt "
    private const uint ChunkData = 0x61746164u; // "data"

    private const ushort FormatPcm = 1;
    private const ushort FormatIeeeFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    /// <summary>
    /// Decodes a RIFF/WAVE byte buffer into native-allocated interleaved Float32 samples (mono or stereo).
    /// Returns false without allocating native memory if the input is invalid or unsupported.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> wavBytes, out DecodedPcmData decoded)
    {
        decoded = default;
        if (wavBytes.Length < 44) return false;

        if (BinaryPrimitives.ReadUInt32LittleEndian(wavBytes.Slice(0, 4)) != ChunkRiff ||
            BinaryPrimitives.ReadUInt32LittleEndian(wavBytes.Slice(8, 4)) != FormatWave)
            return false;

        bool hasFmt = false;
        ushort formatTag = 0;
        ushort srcChannels = 0;
        int sampleRate = 0;
        ushort bitsPerSample = 0;
        int dataOffset = -1;
        int dataSize = 0;

        int pos = 12;
        while (pos + 8 <= wavBytes.Length)
        {
            uint chunkId = BinaryPrimitives.ReadUInt32LittleEndian(wavBytes.Slice(pos, 4));
            uint chunkSizeU = BinaryPrimitives.ReadUInt32LittleEndian(wavBytes.Slice(pos + 4, 4));
            pos += 8;

            long remaining = wavBytes.Length - pos;
            int chunkSize = chunkSizeU > (uint)remaining ? (int)remaining : (int)chunkSizeU;

            if (chunkId == ChunkFmt && chunkSize >= 16)
            {
                formatTag = BinaryPrimitives.ReadUInt16LittleEndian(wavBytes.Slice(pos, 2));
                srcChannels = BinaryPrimitives.ReadUInt16LittleEndian(wavBytes.Slice(pos + 2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(wavBytes.Slice(pos + 4, 4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(wavBytes.Slice(pos + 14, 2));

                if (formatTag == FormatExtensible && chunkSize >= 40)
                {
                    // KSDATAFORMAT_SUBTYPE_PCM (0x0001) or IEEE_FLOAT (0x0003) in first 2 bytes of SubFormat GUID at offset +24
                    formatTag = BinaryPrimitives.ReadUInt16LittleEndian(wavBytes.Slice(pos + 24, 2));
                }
                hasFmt = true;
            }
            else if (chunkId == ChunkData)
            {
                dataOffset = pos;
                dataSize = chunkSize;
                if (hasFmt) break;
            }

            // RIFF chunks are word-aligned (2 bytes)
            int advance = chunkSize + (chunkSize & 1);
            if (advance <= 0 || pos + advance > wavBytes.Length) break;
            pos += advance;
        }

        if (!hasFmt || dataOffset < 0 || dataSize <= 0) return false;
        if (srcChannels == 0 || sampleRate <= 0) return false;

        bool isPcm = formatTag == FormatPcm && (bitsPerSample == 8 || bitsPerSample == 16 || bitsPerSample == 24 || bitsPerSample == 32);
        bool isFloat = formatTag == FormatIeeeFloat && bitsPerSample == 32;
        if (!isPcm && !isFloat) return false;

        int bytesPerSample = bitsPerSample >> 3;
        int bytesPerFrame = bytesPerSample * srcChannels;
        if (bytesPerFrame <= 0) return false;

        int frameCount = dataSize / bytesPerFrame;
        if (frameCount <= 0) return false;

        int outChannels = srcChannels == 1 ? 1 : 2;
        nuint allocBytes = (nuint)((long)frameCount * outChannels * sizeof(float));
        float* dst = (float*)NativeMemoryTracker.AlignedAlloc(allocBytes, 64);

        ReadOnlySpan<byte> data = wavBytes.Slice(dataOffset, frameCount * bytesPerFrame);

        if (srcChannels <= 2)
        {
            int totalSamples = frameCount * srcChannels;
            if (isFloat)
            {
                for (int i = 0; i < totalSamples; i++)
                    dst[i] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.Slice(i * 4, 4)));
            }
            else if (bitsPerSample == 16)
            {
                const float inv16 = 1.0f / 32768.0f;
                for (int i = 0; i < totalSamples; i++)
                    dst[i] = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(i * 2, 2)) * inv16;
            }
            else if (bitsPerSample == 8)
            {
                const float inv8 = 1.0f / 128.0f;
                for (int i = 0; i < totalSamples; i++)
                    dst[i] = (data[i] - 128) * inv8;
            }
            else if (bitsPerSample == 24)
            {
                const float inv24 = 1.0f / 8388608.0f;
                for (int i = 0; i < totalSamples; i++)
                {
                    int o = i * 3;
                    int s = data[o] | (data[o + 1] << 8) | ((sbyte)data[o + 2] << 16);
                    dst[i] = s * inv24;
                }
            }
            else // 32-bit signed PCM
            {
                const float inv32 = 1.0f / 2147483648.0f;
                for (int i = 0; i < totalSamples; i++)
                    dst[i] = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(i * 4, 4)) * inv32;
            }
        }
        else
        {
            // Downmix >2 channels to stereo by taking front-left/right + center blend
            for (int f = 0; f < frameCount; f++)
            {
                int frameOff = f * bytesPerFrame;
                float ch0 = ReadSample(data, frameOff, isFloat, bitsPerSample);
                float ch1 = ReadSample(data, frameOff + bytesPerSample, isFloat, bitsPerSample);
                float center = srcChannels >= 3 ? ReadSample(data, frameOff + bytesPerSample * 2, isFloat, bitsPerSample) * 0.70710678f : 0f;
                dst[f * 2 + 0] = DetMath.Clamp(ch0 + center, -1f, 1f);
                dst[f * 2 + 1] = DetMath.Clamp(ch1 + center, -1f, 1f);
            }
        }

        decoded = new DecodedPcmData
        {
            Samples = dst,
            AllocBytes = allocBytes,
            FrameCount = frameCount,
            Channels = outChannels,
            SampleRate = sampleRate,
        };
        return true;
    }

    public static void Free(ref DecodedPcmData decoded)
    {
        if (decoded.Samples != null)
        {
            NativeMemoryTracker.AlignedFree(decoded.Samples, decoded.AllocBytes);
            decoded = default;
        }
    }

    private static float ReadSample(ReadOnlySpan<byte> data, int byteOffset, bool isFloat, ushort bits)
    {
        if (isFloat)
            return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.Slice(byteOffset, 4)));
        return bits switch
        {
            8 => (data[byteOffset] - 128) * (1.0f / 128.0f),
            16 => BinaryPrimitives.ReadInt16LittleEndian(data.Slice(byteOffset, 2)) * (1.0f / 32768.0f),
            24 => (data[byteOffset] | (data[byteOffset + 1] << 8) | ((sbyte)data[byteOffset + 2] << 16)) * (1.0f / 8388608.0f),
            32 => BinaryPrimitives.ReadInt32LittleEndian(data.Slice(byteOffset, 4)) * (1.0f / 2147483648.0f),
            _ => 0f,
        };
    }

    /// <summary>
    /// Encodes normalized interleaved Float32 samples into a standard 16-bit PCM RIFF/WAVE byte array.
    /// Useful for tests, procedural SFX baking, and tooling.
    /// </summary>
    public static byte[] EncodeWav16(float* interleavedSamples, int frameCount, int channels, int sampleRate)
    {
        int ch = channels <= 1 ? 1 : 2;
        int totalSamples = frameCount * ch;
        int dataBytes = totalSamples * 2;
        byte[] wav = new byte[44 + dataBytes];
        Span<byte> span = wav;

        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0, 4), ChunkRiff);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4, 4), (uint)(36 + dataBytes));
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8, 4), FormatWave);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12, 4), ChunkFmt);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16, 4), 16u);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(20, 2), FormatPcm);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(22, 2), (ushort)ch);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(28, 4), sampleRate * ch * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(32, 2), (ushort)(ch * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(34, 2), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(36, 4), ChunkData);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(40, 4), (uint)dataBytes);

        for (int i = 0; i < totalSamples; i++)
        {
            float clamped = DetMath.Clamp(interleavedSamples[i], -1f, 1f);
            short s16 = (short)(clamped >= 1f ? 32767 : (clamped <= -1f ? -32768 : (int)(clamped * 32767f)));
            BinaryPrimitives.WriteInt16LittleEndian(span.Slice(44 + i * 2, 2), s16);
        }
        return wav;
    }

    /// <summary>
    /// Generates a deterministic sine-wave PCM clip in native memory (caller frees Samples or passes ownership to AudioSystem).
    /// </summary>
    public static DecodedPcmData GenerateSineWave(
        float frequencyHz,
        float durationSeconds,
        int sampleRate = 48000,
        float amplitude = 0.5f,
        int channels = 1)
    {
        int ch = channels <= 1 ? 1 : 2;
        int frames = (int)(durationSeconds * sampleRate);
        if (frames < 1) frames = 1;

        nuint allocBytes = (nuint)((long)frames * ch * sizeof(float));
        float* buf = (float*)NativeMemoryTracker.AlignedAlloc(allocBytes, 64);
        float phaseStep = DetMath.TwoPi * frequencyHz / sampleRate;
        for (int f = 0; f < frames; f++)
        {
            float s = DetMath.Sin(f * phaseStep) * amplitude;
            if (ch == 1)
            {
                buf[f] = s;
            }
            else
            {
                buf[f * 2 + 0] = s;
                buf[f * 2 + 1] = s;
            }
        }
        return new DecodedPcmData
        {
            Samples = buf,
            AllocBytes = allocBytes,
            FrameCount = frames,
            Channels = ch,
            SampleRate = sampleRate,
        };
    }
}
