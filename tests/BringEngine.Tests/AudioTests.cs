using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BringEngine.Assets.Compression;
using BringEngine.Assets.Packing;
using BringEngine.Audio;
using BringEngine.Core.Handles;
using BringEngine.Core.Logging;
using BringEngine.Core.Memory;

namespace BringEngine.Tests;

public static unsafe class AudioTests
{
    public static void RunAll()
    {
        Console.WriteLine("=== Core Audio System: WAV Decoder, Buses, Mixer, .bpack, Dedicated Thread & Zero-GC ===");
        Assert.Run("WavDecoder_Pcm16Float32MonoStereo_RoundTripAndRejectCorrupt", &TestWavDecoder);
        Assert.Run("WavDecoder_8_24_32Bit_Extensible_MultiChannel_ZeroByteChunks", &TestWavDecoderFormatsAndEdgeCases);
        Assert.Run("AudioSystem_HeadlessMixer_BusesPanPitchLoopStealBpackAndZeroLeak", &TestAudioMixerAndBpack);
        Assert.Run("AudioSystem_VoiceControls_Resampling_NaNSafety_GenWrap_AndErrors", &TestAudioVoiceBusResampleAndSafety);
        Assert.Run("AudioSystem_StartFailureCleanup_DefaultConfig_AndDecodedOwnership", &TestAudioStartConfigAndOwnership);
        Assert.Run("AudioSystem_ConcurrencyStress_ZeroCrashAndZeroLeak", &TestAudioConcurrencyStress);
        Assert.Run("AudioSystem_HighContentionPausePlayMix_MixClamp_DecodedGuard_AndWavHardening", &TestAudioAndWavRegressionFixes);
        Assert.Run("AudioSystem_HotPlaybackAndMixLoop_ZeroGc", &TestAudioHotLoopZeroGc);
    }

    private static void TestWavDecoder()
    {
        NativeMemoryTracker.ResetStatsForTest();

        // 1. Mono sine wave -> EncodeWav16 -> TryDecode round-trip
        DecodedPcmData sine = WavDecoder.GenerateSineWave(440f, 0.05f, 48000, 0.75f, channels: 1);
        Assert.Equal(2400, sine.FrameCount, "Sine wave frame count == 2400");
        Assert.Equal(1, sine.Channels, "Sine wave mono");

        byte[] wavMono = WavDecoder.EncodeWav16(sine.Samples, sine.FrameCount, sine.Channels, sine.SampleRate);
        Assert.True(WavDecoder.TryDecode(wavMono, out DecodedPcmData decMono), "Decoded PCM16 mono WAV");
        Assert.Equal(sine.FrameCount, decMono.FrameCount, "Mono frame count matches");
        Assert.Equal(1, decMono.Channels, "Mono channel count matches");
        Assert.Equal(48000, decMono.SampleRate, "Mono sample rate matches");
        for (int i = 0; i < 64; i++)
            Assert.Near(sine.Samples[i], decMono.Samples[i], 0.001f, "Mono PCM16 sample accuracy");

        WavDecoder.Free(ref sine);
        WavDecoder.Free(ref decMono);

        // 2. Stereo PCM16 round-trip
        float* stereoSrc = stackalloc float[16];
        for (int f = 0; f < 8; f++)
        {
            stereoSrc[f * 2 + 0] = 0.5f;
            stereoSrc[f * 2 + 1] = -0.25f;
        }
        byte[] wavStereo = WavDecoder.EncodeWav16(stereoSrc, 8, 2, 44100);
        Assert.True(WavDecoder.TryDecode(wavStereo, out DecodedPcmData decStereo), "Decoded PCM16 stereo WAV");
        Assert.Equal(8, decStereo.FrameCount, "Stereo frame count");
        Assert.Equal(2, decStereo.Channels, "Stereo channel count");
        Assert.Equal(44100, decStereo.SampleRate, "Stereo sample rate");
        Assert.Near(0.5f, decStereo.Samples[0], 0.001f, "Stereo L sample");
        Assert.Near(-0.25f, decStereo.Samples[1], 0.001f, "Stereo R sample");
        WavDecoder.Free(ref decStereo);

        // 3. IEEE Float32 WAV with extra JUNK chunk before data
        byte[] floatWav = BuildFloat32WavWithJunkChunk(new float[] { 0.25f, -0.75f, 0.5f, -0.125f }, channels: 1, sampleRate: 48000);
        Assert.True(WavDecoder.TryDecode(floatWav, out DecodedPcmData decFloat), "Decoded Float32 WAV with JUNK chunk");
        Assert.Equal(4, decFloat.FrameCount, "Float32 frame count");
        Assert.Near(0.25f, decFloat.Samples[0], 1e-5f, "Float32 sample 0");
        Assert.Near(-0.75f, decFloat.Samples[1], 1e-5f, "Float32 sample 1");
        WavDecoder.Free(ref decFloat);

        // 4. Reject corrupt / truncated inputs cleanly with 0 leaks
        Assert.True(!WavDecoder.TryDecode(ReadOnlySpan<byte>.Empty, out _), "Reject empty WAV");
        Assert.True(!WavDecoder.TryDecode(new byte[32], out _), "Reject truncated WAV");
        byte[] badMagic = new byte[64];
        Assert.True(!WavDecoder.TryDecode(badMagic, out _), "Reject bad RIFF magic");

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after WavDecoder tests");
        Assert.Equal(0L, NativeMemoryTracker.LiveBlocks, "Zero live blocks after WavDecoder tests");
    }

    private static void TestAudioMixerAndBpack()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(2 * 1024 * 1024, 256 * 1024, 512 * 1024, 128 * 1024);
        string tempDir = Path.Combine(Path.GetTempPath(), "BringAudioTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        LogState* log = alloc.Persistent.Alloc<LogState>(1, 64);
        LogSystem.Start(log, ref alloc.Persistent, appName: "BringAudioTest", directoryOverride: tempDir, hookExceptions: false);

        AudioState* audio = null;
        try
        {
            var cfg = AudioConfig.Headless;
            cfg.MaxClips = 16;
            cfg.MaxVoices = 4; // Small cap to exercise oldest-voice stealing!
            audio = AudioSystem.Start(ref alloc.Persistent, in cfg, log);

            int mainTid = Environment.CurrentManagedThreadId;
            Assert.True(audio->Active != 0 && audio->IsHeadless != 0, "AudioSystem started in headless mode");
            Assert.True(audio->MixerThreadId != 0 && audio->MixerThreadId != mainTid, "Dedicated BringEngine.AudioMixer thread running");

            // Constant DC mono clip (0.4f for 480 frames @ 48kHz = 10ms)
            float* dcBuf = stackalloc float[480];
            for (int i = 0; i < 480; i++) dcBuf[i] = 0.4f;
            AudioClipHandle dcClip = AudioSystem.CreateClipFromPcm(audio, dcBuf, 480, 1, 48000, StringId.Hash("dc"));
            Assert.True(dcClip.IsValid, "Created PCM clip");
            Assert.Equal(1, AudioSystem.GetLoadedClipCount(audio), "1 loaded clip");

            // 1. Play one-shot on SFX bus + verify exact Master * Bus * Voice gain
            AudioSystem.SetMasterVolume(audio, 0.5f);
            AudioSystem.SetBusVolume(audio, AudioBus.Sfx, 0.5f);
            AudioVoiceHandle v1 = AudioSystem.PlayOneShot(audio, dcClip, volume: 1.0f, pitch: 1.0f, pan: 0f, bus: AudioBus.Sfx);
            Assert.True(v1.IsValid && AudioSystem.IsVoicePlaying(audio, v1), "Voice v1 playing");

            float* mix = stackalloc float[512 * 2];
            AudioSystem.RenderMixFrames(audio, mix, 64);
            // Expected = 0.4 (sample) * 0.5 (master) * 0.5 (sfx) * 1.0 (voice) = 0.10 on L & R
            Assert.Near(0.10f, mix[0], 0.002f, "Mixed L matches Master*Sfx*Voice gain");
            Assert.Near(0.10f, mix[1], 0.002f, "Mixed R matches Master*Sfx*Voice gain");

            // 2. Mute SFX bus -> silence while playhead keeps advancing
            AudioSystem.SetBusMuted(audio, AudioBus.Sfx, true);
            AudioSystem.RenderMixFrames(audio, mix, 64);
            Assert.Near(0f, mix[0], 1e-6f, "Muted SFX bus outputs silence");
            AudioSystem.SetBusMuted(audio, AudioBus.Sfx, false);

            // 3. Global Pause -> silence & playhead frozen
            AudioSystem.SetPaused(audio, true);
            AudioSystem.RenderMixFrames(audio, mix, 64);
            Assert.Near(0f, mix[0], 1e-6f, "Global pause outputs silence");
            Assert.True(AudioSystem.IsVoicePlaying(audio, v1), "Voice remains active while globally paused");
            AudioSystem.SetPaused(audio, false);

            // 4. Hard stereo panning (-1 left, +1 right)
            AudioSystem.StopAll(audio);
            AudioSystem.SetMasterVolume(audio, 1f);
            AudioSystem.SetBusVolume(audio, AudioBus.Sfx, 1f);
            AudioVoiceHandle vLeft = AudioSystem.PlayOneShot(audio, dcClip, volume: 0.5f, pitch: 1f, pan: -1f, bus: AudioBus.Sfx);
            AudioSystem.RenderMixFrames(audio, mix, 16);
            Assert.True(mix[0] > 0.25f, $"Hard-left pan L > 0.25 (got {mix[0]:F3})");
            Assert.Near(0f, mix[1], 0.001f, "Hard-left pan R == 0");
            AudioSystem.StopVoice(audio, vLeft);

            AudioVoiceHandle vRight = AudioSystem.PlayOneShot(audio, dcClip, volume: 0.5f, pitch: 1f, pan: 1f, bus: AudioBus.Sfx);
            AudioSystem.RenderMixFrames(audio, mix, 16);
            Assert.Near(0f, mix[0], 0.001f, "Hard-right pan L == 0");
            Assert.True(mix[1] > 0.25f, $"Hard-right pan R > 0.25 (got {mix[1]:F3})");
            AudioSystem.StopVoice(audio, vRight);

            // 5. Pitch = 2.0 finishes 480-frame clip in 240 output frames
            AudioVoiceHandle vFast = AudioSystem.PlayOneShot(audio, dcClip, volume: 1f, pitch: 2f, pan: 0f, bus: AudioBus.Sfx);
            AudioSystem.RenderMixFrames(audio, mix, 200);
            Assert.True(AudioSystem.IsVoicePlaying(audio, vFast), "2x pitch voice still playing after 200 frames");
            AudioSystem.RenderMixFrames(audio, mix, 60);
            Assert.True(!AudioSystem.IsVoicePlaying(audio, vFast), "2x pitch 480-frame voice completed after 260 output frames");

            // 6. Looping music voice wraps continuously past clip length until stopped
            AudioVoiceHandle vLoop = AudioSystem.PlayLoop(audio, dcClip, volume: 0.5f, pitch: 1f, bus: AudioBus.Music);
            AudioSystem.RenderMixFrames(audio, mix, 512);
            AudioSystem.RenderMixFrames(audio, mix, 512);
            Assert.True(AudioSystem.IsVoicePlaying(audio, vLoop), "Looping voice stays playing past 2x clip duration");
            Assert.Near(0.20f, mix[511 * 2], 0.002f, "Looping voice keeps outputting audio after wrap");

            // 7. Oldest non-looping voice stealing when MaxVoices (4) is saturated
            AudioVoiceHandle s1 = AudioSystem.PlayOneShot(audio, dcClip, 0.2f);
            AudioVoiceHandle s2 = AudioSystem.PlayOneShot(audio, dcClip, 0.2f);
            AudioVoiceHandle s3 = AudioSystem.PlayOneShot(audio, dcClip, 0.2f);
            Assert.Equal(4, AudioSystem.GetActiveVoiceCount(audio), "All 4 voices active");
            AudioVoiceHandle s4 = AudioSystem.PlayOneShot(audio, dcClip, 0.2f); // Steals s1 (oldest non-looping, preserves vLoop!)
            Assert.True(AudioSystem.IsVoicePlaying(audio, vLoop), "Looping music voice preserved during stealing");
            Assert.True(!AudioSystem.IsVoicePlaying(audio, s1), "Oldest one-shot voice s1 stolen & handle invalidated");
            Assert.True(AudioSystem.IsVoicePlaying(audio, s4), "New voice s4 active");
            Assert.Equal(4, AudioSystem.GetActiveVoiceCount(audio), "Active voice count remains capped at MaxVoices");

            // 8. UnloadClip stops dependent voices & frees native clip buffer
            Assert.True(AudioSystem.UnloadClip(audio, dcClip), "UnloadClip succeeded");
            Assert.Equal(0, AudioSystem.GetLoadedClipCount(audio), "0 clips after unload");
            Assert.Equal(0, AudioSystem.GetActiveVoiceCount(audio), "Voices using unloaded clip stopped automatically");
            Assert.True(!AudioSystem.PlayOneShot(audio, dcClip).IsValid, "Stale clip handle rejected");

            // 9. .bpack RawBlob WAV loading integration (Brotli-compressed audio inside .bpack)
            string rawDir = Path.Combine(tempDir, "raw_audio");
            Directory.CreateDirectory(rawDir);
            DecodedPcmData beep = WavDecoder.GenerateSineWave(880f, 0.02f, 48000, 0.6f, channels: 1);
            byte[] beepWav = WavDecoder.EncodeWav16(beep.Samples, beep.FrameCount, beep.Channels, beep.SampleRate);
            WavDecoder.Free(ref beep);
            File.WriteAllBytes(Path.Combine(rawDir, "beep.wav"), beepWav);

            string bpackPath = Path.Combine(tempDir, "audio_bundle.bpack");
            AssetPackBuilder.BuildPackFromDirectory(rawDir, bpackPath, PackCompressionMode.Brotli);
            AudioClipHandle packedClip = AudioSystem.LoadClipFromBpack(audio, bpackPath, StringId.Hash("beep.wav"), ref alloc.Scratch);
            Assert.True(packedClip.IsValid, "Loaded Brotli-compressed WAV clip directly from .bpack");
            AudioVoiceHandle pv = AudioSystem.PlayOneShot(audio, packedClip, 1f);
            AudioSystem.RenderMixFrames(audio, mix, 64);
            Assert.True(AudioSystem.IsVoicePlaying(audio, pv), "Clip loaded from .bpack plays cleanly");

            AudioSystem.Stop(audio);
            audio = null;
            LogSystem.Flush(log);
        }
        finally
        {
            if (audio != null) AudioSystem.Stop(audio);
            LogSystem.Stop(log);
            alloc.Dispose();
            try { Directory.Delete(tempDir, true); } catch { }
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after AudioSystem stop");
        Assert.Equal(0L, NativeMemoryTracker.LiveBlocks, "Zero live blocks after AudioSystem stop");
    }

    private static void TestAudioHotLoopZeroGc()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(1 * 1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        AudioState* audio = null;
        try
        {
            audio = AudioSystem.Start(ref alloc.Persistent, AudioConfig.Headless);
            DecodedPcmData tone = WavDecoder.GenerateSineWave(523.25f, 0.05f, 48000, 0.5f, 1);
            AudioClipHandle clip = AudioSystem.CreateClipFromDecoded(audio, ref tone, StringId.Hash("tone"));
            Assert.True(clip.IsValid, "Clip created for zero-GC test");
            Assert.True(tone.Samples == null, "CreateClipFromDecoded(ref) cleared caller struct");

            float* mix = stackalloc float[256 * 2];

            // Warmup JIT
            for (int w = 0; w < 5; w++)
            {
                AudioVoiceHandle vh = AudioSystem.PlayOneShot(audio, clip, 0.7f, 1.1f, -0.25f, AudioBus.Sfx);
                AudioSystem.SetVoiceVolume(audio, vh, 0.6f);
                AudioSystem.SetVoicePitch(audio, vh, 1.05f);
                AudioSystem.SetVoicePan(audio, vh, 0.25f);
                AudioSystem.RenderMixFrames(audio, mix, 256);
                AudioSystem.StopVoice(audio, vh);
            }

            long gcBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 200; i++)
            {
                AudioVoiceHandle vh = AudioSystem.PlayOneShot(audio, clip, 0.7f, 1.0f, 0f, AudioBus.Sfx);
                AudioSystem.SetBusVolume(audio, AudioBus.Sfx, 0.8f);
                AudioSystem.SetVoiceVolume(audio, vh, 0.6f);
                AudioSystem.SetVoicePitch(audio, vh, 1.05f);
                AudioSystem.SetVoicePan(audio, vh, 0.1f);
                bool playing = AudioSystem.IsVoicePlaying(audio, vh);
                AudioSystem.RenderMixFrames(audio, mix, 128);
                if (playing) AudioSystem.StopVoice(audio, vh);
            }
            long gcAfter = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(0L, gcAfter - gcBefore, "AudioSystem hot playback + software mix allocates 0 GC bytes");

            AudioSystem.Stop(audio);
            audio = null;
        }
        finally
        {
            if (audio != null) AudioSystem.Stop(audio);
            alloc.Dispose();
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after AudioSystem zero-GC test");
    }

    private static byte[] BuildFloat32WavWithJunkChunk(float[] samples, int channels, int sampleRate)
    {
        int dataBytes = samples.Length * 4;
        int junkBytes = 12;
        byte[] wav = new byte[12 + 24 + (8 + junkBytes) + (8 + dataBytes)];
        Span<byte> s = wav;
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(0, 4), 0x46464952u); // RIFF
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(4, 4), (uint)(wav.Length - 8));
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(8, 4), 0x45564157u); // WAVE

        // fmt (IEEE Float = 3)
        int o = 12;
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(o, 4), 0x20746D66u);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(o + 4, 4), 16u);
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(o + 8, 2), 3); // IEEE float
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(o + 10, 2), (ushort)channels);
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(o + 12, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(o + 16, 4), sampleRate * channels * 4);
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(o + 20, 2), (ushort)(channels * 4));
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(o + 22, 2), 32);
        o += 24;

        // JUNK chunk (must be skipped cleanly)
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(o, 4), 0x4B4E554Au); // JUNK
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(o + 4, 4), (uint)junkBytes);
        o += 8 + junkBytes;

        // data chunk
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(o, 4), 0x61746164u); // data
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(o + 4, 4), (uint)dataBytes);
        o += 8;
        for (int i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(s.Slice(o + i * 4, 4), BitConverter.SingleToInt32Bits(samples[i]));
        return wav;
    }

    private static void TestWavDecoderFormatsAndEdgeCases()
    {
        NativeMemoryTracker.ResetStatsForTest();

        // 1. 8-bit unsigned PCM
        byte[] wav8 = BuildCustomPcmWav(channels: 1, sampleRate: 22050, bitsPerSample: 8,
            dataBytes: new byte[] { 0, 128, 255 });
        Assert.True(WavDecoder.TryDecode(wav8, out DecodedPcmData dec8), "Decoded 8-bit PCM");
        Assert.Equal(3, dec8.FrameCount, "8-bit frame count");
        Assert.Near(-1.0f, dec8.Samples[0], 0.01f, "8-bit min sample");
        Assert.Near(0.0f, dec8.Samples[1], 0.001f, "8-bit zero sample");
        Assert.Near(127f / 128f, dec8.Samples[2], 0.01f, "8-bit max sample");
        WavDecoder.Free(ref dec8);

        // 2. 24-bit signed PCM (positive + negative sign extension)
        // +0.5 -> 0x400000 (LE: 00 00 40), -0.5 -> 0xC00000 (LE: 00 00 C0)
        byte[] wav24 = BuildCustomPcmWav(channels: 1, sampleRate: 48000, bitsPerSample: 24,
            dataBytes: new byte[] { 0x00, 0x00, 0x40, 0x00, 0x00, 0xC0 });
        Assert.True(WavDecoder.TryDecode(wav24, out DecodedPcmData dec24), "Decoded 24-bit PCM");
        Assert.Equal(2, dec24.FrameCount, "24-bit frame count");
        Assert.Near(0.5f, dec24.Samples[0], 1e-4f, "24-bit +0.5");
        Assert.Near(-0.5f, dec24.Samples[1], 1e-4f, "24-bit -0.5 sign-extended");
        WavDecoder.Free(ref dec24);

        // 3. 32-bit signed PCM
        byte[] pcm32Data = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(pcm32Data.AsSpan(0, 4), int.MaxValue / 2);
        BinaryPrimitives.WriteInt32LittleEndian(pcm32Data.AsSpan(4, 4), int.MinValue / 2);
        byte[] wav32 = BuildCustomPcmWav(channels: 1, sampleRate: 48000, bitsPerSample: 32, dataBytes: pcm32Data);
        Assert.True(WavDecoder.TryDecode(wav32, out DecodedPcmData dec32), "Decoded 32-bit signed PCM");
        Assert.Near(0.5f, dec32.Samples[0], 1e-4f, "32-bit +0.5");
        Assert.Near(-0.5f, dec32.Samples[1], 1e-4f, "32-bit -0.5");
        WavDecoder.Free(ref dec32);

        // 4. WAVE_FORMAT_EXTENSIBLE (0xFFFE) for PCM16 and Float32 + invalid GUID rejection
        byte[] extPcm16 = BuildExtensibleWav(subFormat: 1, bitsPerSample: 16, channels: 2, sampleRate: 48000, validGuid: true,
            new byte[] { 0x00, 0x40, 0x00, 0xC0 }); // +0.5, -0.5
        Assert.True(WavDecoder.TryDecode(extPcm16, out DecodedPcmData decExt16), "Decoded 0xFFFE PCM16");
        Assert.Near(0.5f, decExt16.Samples[0], 1e-3f, "0xFFFE PCM16 L");
        Assert.Near(-0.5f, decExt16.Samples[1], 1e-3f, "0xFFFE PCM16 R");
        WavDecoder.Free(ref decExt16);

        byte[] extFloat = BuildExtensibleWav(subFormat: 3, bitsPerSample: 32, channels: 1, sampleRate: 48000, validGuid: true,
            BitConverter.GetBytes(0.25f));
        Assert.True(WavDecoder.TryDecode(extFloat, out DecodedPcmData decExtF), "Decoded 0xFFFE Float32");
        Assert.Near(0.25f, decExtF.Samples[0], 1e-5f, "0xFFFE Float32 sample");
        WavDecoder.Free(ref decExtF);

        byte[] extBadGuid = BuildExtensibleWav(subFormat: 1, bitsPerSample: 16, channels: 1, sampleRate: 48000, validGuid: false,
            new byte[] { 0x00, 0x40 });
        Assert.True(!WavDecoder.TryDecode(extBadGuid, out _), "Reject 0xFFFE with invalid KSDATAFORMAT GUID tail");

        // 5. Multi-channel (3ch) downmix to stereo
        byte[] mcData = new byte[12]; // 1 frame x 3ch x 4 bytes float: L=0.2, R=-0.2, C=0.4
        BinaryPrimitives.WriteInt32LittleEndian(mcData.AsSpan(0, 4), BitConverter.SingleToInt32Bits(0.2f));
        BinaryPrimitives.WriteInt32LittleEndian(mcData.AsSpan(4, 4), BitConverter.SingleToInt32Bits(-0.2f));
        BinaryPrimitives.WriteInt32LittleEndian(mcData.AsSpan(8, 4), BitConverter.SingleToInt32Bits(0.4f));
        byte[] wav3ch = BuildWavWithChunks(channels: 3, sampleRate: 48000, formatTag: 3, bitsPerSample: 32, dataBytes: mcData);
        Assert.True(WavDecoder.TryDecode(wav3ch, out DecodedPcmData dec3ch), "Decoded 3ch Float32 downmixed to stereo");
        Assert.Equal(2, dec3ch.Channels, "3ch downmixed to 2 channels");
        Assert.Near(0.2f + 0.4f * 0.70710678f, dec3ch.Samples[0], 1e-3f, "3ch downmix L");
        Assert.Near(-0.2f + 0.4f * 0.70710678f, dec3ch.Samples[1], 1e-3f, "3ch downmix R");
        WavDecoder.Free(ref dec3ch);

        // 6. 0-byte RIFF chunk before data (AUD-03), odd-byte RIFF chunk padding, and data-before-fmt
        byte[] zeroChunkWav = BuildFloat32WavWithExtraChunks(new float[] { 0.5f, -0.5f }, extraChunkSizes: new[] { 0, 3 }, dataBeforeFmt: false);
        Assert.True(WavDecoder.TryDecode(zeroChunkWav, out DecodedPcmData decZeroChunk), "Decoded WAV with 0-byte and odd-byte JUNK chunks");
        Assert.Equal(2, decZeroChunk.FrameCount, "0-byte + odd-byte chunk WAV frame count");
        Assert.Near(0.5f, decZeroChunk.Samples[0], 1e-5f, "0-byte chunk WAV sample 0");
        WavDecoder.Free(ref decZeroChunk);

        byte[] dataBeforeFmtWav = BuildFloat32WavWithExtraChunks(new float[] { -0.25f, 0.75f }, extraChunkSizes: new[] { 0 }, dataBeforeFmt: true);
        Assert.True(WavDecoder.TryDecode(dataBeforeFmtWav, out DecodedPcmData decDbf), "Decoded WAV with data chunk before fmt chunk");
        Assert.Equal(2, decDbf.FrameCount, "data-before-fmt frame count");
        Assert.Near(-0.25f, decDbf.Samples[0], 1e-5f, "data-before-fmt sample 0");
        WavDecoder.Free(ref decDbf);

        // 7. NaN/Inf IEEE Float32 samples sanitized to 0f (AUD-01)
        byte[] nanWav = BuildFloat32WavWithJunkChunk(new float[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0.5f }, 1, 48000);
        Assert.True(WavDecoder.TryDecode(nanWav, out DecodedPcmData decNan), "Decoded Float32 WAV with NaN/Inf samples");
        Assert.Near(0f, decNan.Samples[0], 1e-6f, "NaN sample sanitized to 0");
        Assert.Near(0f, decNan.Samples[1], 1e-6f, "+Inf sample sanitized to 0");
        Assert.Near(0f, decNan.Samples[2], 1e-6f, "-Inf sample sanitized to 0");
        Assert.Near(0.5f, decNan.Samples[3], 1e-6f, "Valid sample preserved");
        WavDecoder.Free(ref decNan);

        // 8. Malformed headers & helper input guards (AUD-11)
        Assert.True(!WavDecoder.TryDecode(BuildCustomPcmWav(0, 48000, 16, new byte[4]), out _), "Reject Channels==0");
        Assert.True(!WavDecoder.TryDecode(BuildCustomPcmWav(1, 0, 16, new byte[4]), out _), "Reject SampleRate==0");
        Assert.True(!WavDecoder.TryDecode(BuildWavWithChunks(1, 48000, formatTag: 2, bitsPerSample: 4, new byte[8]), out _), "Reject ADPCM format tag");
        Assert.Equal(0, WavDecoder.EncodeWav16(null, 8, 1, 48000).Length, "EncodeWav16 null guard");
        float* oneSample = stackalloc float[1];
        oneSample[0] = 0.5f;
        Assert.Equal(0, WavDecoder.EncodeWav16(oneSample, 0, 1, 48000).Length, "EncodeWav16 frameCount<=0 guard");
        Assert.Equal(0, WavDecoder.EncodeWav16(oneSample, 1, 1, 0).Length, "EncodeWav16 sampleRate<=0 guard");

        DecodedPcmData badSine = WavDecoder.GenerateSineWave(440f, -1f, sampleRate: 0, amplitude: float.NaN, channels: 1);
        Assert.True(badSine.Samples != null && badSine.FrameCount >= 1 && badSine.SampleRate == 48000, "GenerateSineWave invalid args guarded");
        Assert.True(float.IsFinite(badSine.Samples[0]), "GenerateSineWave NaN amplitude sanitized");
        WavDecoder.Free(ref badSine);

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after WavDecoder edge case tests");
        Assert.Equal(0L, NativeMemoryTracker.LiveBlocks, "Zero live blocks after WavDecoder edge case tests");
    }

    private static void TestAudioVoiceBusResampleAndSafety()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(2 * 1024 * 1024, 256 * 1024, 512 * 1024, 128 * 1024);
        string tempDir = Path.Combine(Path.GetTempPath(), "BringAudioEdgeTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        AudioState* audio = null;
        try
        {
            var cfg = AudioConfig.Headless;
            cfg.MaxClips = 2;
            cfg.MaxVoices = 2;
            audio = AudioSystem.Start(ref alloc.Persistent, in cfg);

            // Mono 22050Hz clip (220 frames -> ~479 frames at 48000Hz)
            float* mono22k = stackalloc float[220];
            for (int i = 0; i < 220; i++) mono22k[i] = 0.5f;
            AudioClipHandle clip22k = AudioSystem.CreateClipFromPcm(audio, mono22k, 220, 1, 22050, StringId.Hash("m22k"));
            Assert.True(clip22k.IsValid, "Created 22050Hz mono clip");

            // Stereo 48000Hz clip (200 frames, L=0.6, R=0.3)
            float* stereoBuf = stackalloc float[400];
            for (int f = 0; f < 200; f++)
            {
                stereoBuf[f * 2 + 0] = 0.6f;
                stereoBuf[f * 2 + 1] = 0.3f;
            }
            AudioClipHandle clipStereo = AudioSystem.CreateClipFromPcm(audio, stereoBuf, 200, 2, 48000, StringId.Hash("st"));
            Assert.True(clipStereo.IsValid, "Created stereo clip");

            // MaxClips == 2 capacity exhaustion
            AudioClipHandle clipOver = AudioSystem.CreateClipFromPcm(audio, mono22k, 220, 1, 22050, StringId.Hash("over"));
            Assert.True(!clipOver.IsValid, "MaxClips capacity exhaustion returns Invalid");

            float* mix = stackalloc float[512 * 2];

            // 1. Voice pause/resume + GetVoiceState + SetVoiceVolume/Pitch
            AudioVoiceHandle vh = AudioSystem.PlayOneShot(audio, clipStereo, volume: 1f, pitch: 1f, pan: 0f, bus: AudioBus.Music);
            Assert.Equal((byte)AudioVoiceState.Playing, (byte)AudioSystem.GetVoiceState(audio, vh), "Voice initially Playing");
            AudioSystem.SetVoicePaused(audio, vh, true);
            Assert.Equal((byte)AudioVoiceState.Paused, (byte)AudioSystem.GetVoiceState(audio, vh), "Voice Paused");
            AudioSystem.RenderMixFrames(audio, mix, 32);
            Assert.Near(0f, mix[0], 1e-6f, "Paused voice emits silence");
            AudioSystem.SetVoicePaused(audio, vh, false);
            AudioSystem.SetVoiceVolume(audio, vh, 0.5f);
            AudioSystem.RenderMixFrames(audio, mix, 32);
            Assert.Near(0.30f, mix[0], 0.002f, "SetVoiceVolume updated L");
            Assert.Near(0.15f, mix[1], 0.002f, "SetVoiceVolume updated R");

            // 2. Stereo balance panning
            AudioSystem.SetVoicePan(audio, vh, -1f);
            AudioSystem.RenderMixFrames(audio, mix, 16);
            Assert.Near(0.30f, mix[0], 0.002f, "Stereo pan -1 keeps L");
            Assert.Near(0f, mix[1], 1e-5f, "Stereo pan -1 silences R");
            AudioSystem.SetVoicePan(audio, vh, 1f);
            AudioSystem.RenderMixFrames(audio, mix, 16);
            Assert.Near(0f, mix[0], 1e-5f, "Stereo pan +1 silences L");
            Assert.Near(0.15f, mix[1], 0.002f, "Stereo pan +1 keeps R");

            // 3. StopBus isolation (stopping Sfx preserves Music) + SetMasterMuted
            AudioVoiceHandle sfxV = AudioSystem.PlayOneShot(audio, clipStereo, volume: 1f, bus: AudioBus.Sfx);
            AudioSystem.StopBus(audio, AudioBus.Sfx);
            Assert.True(!AudioSystem.IsVoicePlaying(audio, sfxV), "Sfx voice stopped by StopBus(Sfx)");
            Assert.True(AudioSystem.IsVoicePlaying(audio, vh), "Music voice preserved by StopBus(Sfx)");

            AudioSystem.SetMasterMuted(audio, true);
            AudioSystem.RenderMixFrames(audio, mix, 16);
            Assert.Near(0f, mix[0], 1e-6f, "Master mute silences output");
            AudioSystem.SetMasterMuted(audio, false);
            AudioSystem.StopAll(audio);

            // 4. Stale voice handle rejection on mutators after completion and stealing
            AudioVoiceHandle doneV = AudioSystem.PlayOneShot(audio, clipStereo, volume: 1f, pitch: 2f, bus: AudioBus.Sfx);
            AudioSystem.RenderMixFrames(audio, mix, 150);
            Assert.Equal((byte)AudioVoiceState.Inactive, (byte)AudioSystem.GetVoiceState(audio, doneV), "Voice completed");
            AudioVoiceHandle newOccupant = AudioSystem.PlayOneShot(audio, clipStereo, volume: 1f, bus: AudioBus.Sfx);
            AudioSystem.SetVoiceVolume(audio, doneV, 0f);
            AudioSystem.SetVoicePaused(audio, doneV, true);
            AudioSystem.SetVoicePitch(audio, doneV, 4f);
            AudioSystem.SetVoicePan(audio, doneV, 1f);
            AudioSystem.StopVoice(audio, doneV);
            Assert.True(AudioSystem.IsVoicePlaying(audio, newOccupant), "Stale completed handle cannot mutate or stop new occupant");

            // Stealing test with MaxVoices = 2
            AudioVoiceHandle victim = AudioSystem.PlayOneShot(audio, clipStereo, volume: 1f, bus: AudioBus.Sfx);
            AudioVoiceHandle thief = AudioSystem.PlayOneShot(audio, clipStereo, volume: 1f, bus: AudioBus.Sfx);
            Assert.True(AudioSystem.IsVoicePlaying(audio, thief), "Thief active");
            AudioSystem.SetVoiceVolume(audio, newOccupant, 0f);
            AudioSystem.StopVoice(audio, victim); // no-op if stolen
            AudioSystem.StopAll(audio);

            // 5. Resampling 22050Hz on 48000Hz mixer
            AudioVoiceHandle rV = AudioSystem.PlayOneShot(audio, clip22k, volume: 1f, pitch: 1f, bus: AudioBus.Sfx);
            AudioSystem.RenderMixFrames(audio, mix, 240);
            Assert.True(AudioSystem.IsVoicePlaying(audio, rV), "22050Hz 220-frame clip still playing after 240 48kHz frames");
            AudioSystem.RenderMixFrames(audio, mix, 260);
            Assert.True(!AudioSystem.IsVoicePlaying(audio, rV), "22050Hz 220-frame clip finished after 500 48kHz frames");

            // 6. Additive saturation clamping [-1, 1]
            AudioVoiceHandle loud1 = AudioSystem.PlayOneShot(audio, clip22k, volume: 3f, bus: AudioBus.Sfx);
            AudioVoiceHandle loud2 = AudioSystem.PlayOneShot(audio, clip22k, volume: 3f, bus: AudioBus.Sfx);
            Assert.True(loud1.IsValid && loud2.IsValid, "Loud voices started");
            AudioSystem.RenderMixFrames(audio, mix, 32);
            Assert.Near(1.0f, mix[0], 1e-6f, "Additive clipping clamped to +1.0");
            AudioSystem.StopAll(audio);

            // 7. NaN / Infinity safety (AUD-01)
            var nanParams = new AudioPlayParams
            {
                Bus = AudioBus.Sfx,
                Volume = float.NaN,
                Pitch = float.PositiveInfinity,
                Pan = float.NegativeInfinity,
                Loop = 0,
            };
            AudioVoiceHandle nanV = AudioSystem.Play(audio, clip22k, in nanParams);
            Assert.True(nanV.IsValid, "Play with NaN/Inf parameters returns valid sanitized handle");
            AudioSystem.SetVoiceVolume(audio, nanV, float.NaN);
            AudioSystem.SetVoicePitch(audio, nanV, float.NaN);
            AudioSystem.SetVoicePan(audio, nanV, float.NaN);
            AudioSystem.SetBusVolume(audio, AudioBus.Sfx, float.NaN);
            AudioSystem.RenderMixFrames(audio, mix, 64);
            for (int i = 0; i < 128; i++)
                Assert.True(float.IsFinite(mix[i]), "RenderMixFrames output finite after NaN inputs");
            AudioSystem.SetBusVolume(audio, AudioBus.Sfx, 1f);
            AudioSystem.StopAll(audio);

            // 8. Handle generation wraparound (> 4096 cycles)
            for (int i = 0; i < 4200; i++)
            {
                AudioVoiceHandle cyc = AudioSystem.PlayOneShot(audio, clip22k, volume: 0.1f);
                Assert.True(cyc.IsValid && cyc.Generation != 0, "Generation never wraps to 0");
                AudioSystem.StopVoice(audio, cyc);
            }

            // 9. LoadClipFromWavFile & LoadClipFromBpack failure paths
            AudioClipHandle missingWav = AudioSystem.LoadClipFromWavFile(audio, Path.Combine(tempDir, "nope.wav"));
            Assert.True(!missingWav.IsValid, "Missing WAV file returns Invalid");

            nuint scratchBefore = alloc.Scratch.Offset;
            AudioClipHandle missingPack = AudioSystem.LoadClipFromBpack(audio, Path.Combine(tempDir, "nope.bpack"), StringId.Hash("x.wav"), ref alloc.Scratch);
            Assert.True(!missingPack.IsValid, "Missing .bpack returns Invalid without throwing");
            Assert.Equal(scratchBefore, alloc.Scratch.Offset, "Scratch arena unchanged on missing .bpack");

            string rawDir = Path.Combine(tempDir, "bad_raw");
            Directory.CreateDirectory(rawDir);
            File.WriteAllBytes(Path.Combine(rawDir, "corrupt.wav"), new byte[] { 1, 2, 3, 4 });
            string badBpack = Path.Combine(tempDir, "bad.bpack");
            AssetPackBuilder.BuildPackFromDirectory(rawDir, badBpack, PackCompressionMode.Lz4);
            AudioClipHandle missingBlob = AudioSystem.LoadClipFromBpack(audio, badBpack, StringId.Hash("missing.wav"), ref alloc.Scratch);
            Assert.True(!missingBlob.IsValid, "Missing blob in .bpack returns Invalid");
            Assert.Equal(scratchBefore, alloc.Scratch.Offset, "Scratch arena rolled back on missing blob");
            AudioClipHandle badWavBlob = AudioSystem.LoadClipFromBpack(audio, badBpack, StringId.Hash("corrupt.wav"), ref alloc.Scratch);
            Assert.True(!badWavBlob.IsValid, "Corrupt WAV in .bpack returns Invalid");
            Assert.Equal(scratchBefore, alloc.Scratch.Offset, "Scratch arena rolled back on corrupt WAV blob");

            AudioSystem.Stop(audio);
            audio = null;

            // 10. Mono mixer output (AudioConfig.Channels = 1)
            var monoCfg = AudioConfig.Headless;
            monoCfg.Channels = 1;
            audio = AudioSystem.Start(ref alloc.Persistent, in monoCfg);
            AudioClipHandle mClip = AudioSystem.CreateClipFromPcm(audio, mono22k, 128, 1, 48000, StringId.Hash("m"));
            AudioSystem.PlayOneShot(audio, mClip, volume: 1f);
            AudioSystem.RenderMixFrames(audio, mix, 32);
            Assert.Near(0.5f, mix[0], 0.002f, "Mono mixer frame 0");
            Assert.Near(0.5f, mix[31], 0.002f, "Mono mixer frame 31");
            AudioSystem.Stop(audio);
            audio = null;
        }
        finally
        {
            if (audio != null) AudioSystem.Stop(audio);
            alloc.Dispose();
            try { Directory.Delete(tempDir, true); } catch { }
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after voice/bus/resampling tests");
    }

    private static void TestAudioStartConfigAndOwnership()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(2 * 1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);

        try
        {
            // 1. default(AudioConfig) defaults to stereo (Channels == 2) (AUD-05)
            AudioConfig defCfg = default;
            defCfg.ForceHeadless = 1;
            AudioState* defAudio = AudioSystem.Start(ref alloc.Persistent, in defCfg);
            Assert.Equal(2, defAudio->Channels, "default(AudioConfig) defaults to stereo (2ch)");
            Assert.Equal(48000, defAudio->SampleRate, "default(AudioConfig) defaults to 48000Hz");
            AudioSystem.Stop(defAudio);

            // 2. CreateClipFromDecoded(ref) clears struct on success and on failure (AUD-06)
            AudioState* audio = AudioSystem.Start(ref alloc.Persistent, new AudioConfig
            {
                SampleRate = 48000,
                Channels = 2,
                MaxClips = 1,
                MaxVoices = 2,
                MixBufferFrames = 128,
                ForceHeadless = 1,
            });
            try
            {
                DecodedPcmData d1 = WavDecoder.GenerateSineWave(440f, 0.01f, 48000, 0.5f, 1);
                AudioClipHandle h1 = AudioSystem.CreateClipFromDecoded(audio, ref d1, StringId.Hash("d1"));
                Assert.True(h1.IsValid && d1.Samples == null, "Success transfers ownership and nulls DecodedPcmData.Samples");
                WavDecoder.Free(ref d1); // idempotent no-op

                DecodedPcmData d2 = WavDecoder.GenerateSineWave(440f, 0.01f, 48000, 0.5f, 1);
                AudioClipHandle h2 = AudioSystem.CreateClipFromDecoded(audio, ref d2, StringId.Hash("d2"));
                Assert.True(!h2.IsValid && d2.Samples == null, "Table-full frees and nulls DecodedPcmData.Samples");

                DecodedPcmData badCh = WavDecoder.GenerateSineWave(440f, 0.01f, 48000, 0.5f, 1);
                badCh.Channels = 3;
                AudioClipHandle hBad = AudioSystem.CreateClipFromDecoded(audio, ref badCh, StringId.Hash("badCh"));
                Assert.True(!hBad.IsValid && badCh.Samples == null, "Channels>2 rejected, freed, and nulled");
            }
            finally
            {
                AudioSystem.Stop(audio);
            }

            // 3. Start() failure cleanup: no s_sync slot leak and no MixScratch leak (AUD-02)
            // Exhausting persistent arena deterministically triggers Start() catch cleanup on all machines
            var tinyArena = LinearArena.Create(128, 64);
            try
            {
                for (int attempt = 0; attempt < 6; attempt++)
                {
                    bool threw = false;
                    try
                    {
                        _ = AudioSystem.Start(ref tinyArena, AudioConfig.Headless);
                    }
                    catch
                    {
                        threw = true;
                    }
                    Assert.True(threw, $"Start() with tiny arena threw cleanly (attempt {attempt})");
                    tinyArena.Reset();
                }
            }
            finally
            {
                tinyArena.Dispose();
            }

            // Verify all 4 s_sync slots remain usable after failed starts
            AudioState* s0 = AudioSystem.Start(ref alloc.Persistent, AudioConfig.Headless);
            AudioState* s1 = AudioSystem.Start(ref alloc.Persistent, AudioConfig.Headless);
            AudioState* s2 = AudioSystem.Start(ref alloc.Persistent, AudioConfig.Headless);
            AudioState* s3 = AudioSystem.Start(ref alloc.Persistent, AudioConfig.Headless);
            AudioSystem.Stop(s3);
            AudioSystem.Stop(s2);
            AudioSystem.Stop(s1);
            AudioSystem.Stop(s0);

            // 4. AudioConfig.Default (HeadlessFallback=1) works everywhere with 0 leaks
            AudioState* defSdlOrHeadless = AudioSystem.Start(ref alloc.Persistent, AudioConfig.Default);
            Assert.True(defSdlOrHeadless->Active != 0, "AudioConfig.Default started cleanly");
            AudioSystem.Stop(defSdlOrHeadless);

            // 5. HeadlessFallback=0 (either opens real device or throws cleanly without leaking)
            var strictCfg = AudioConfig.Default;
            strictCfg.HeadlessFallback = 0;
            AudioState* strictAudio = null;
            try
            {
                strictAudio = AudioSystem.Start(ref alloc.Persistent, in strictCfg);
                Assert.True(strictAudio->IsHeadless == 0, "Strict SDL audio opened physical device");
                AudioSystem.Stop(strictAudio);
                strictAudio = null;
            }
            catch (InvalidOperationException)
            {
                Assert.True(true, "Strict HeadlessFallback=0 threw cleanly on headless host");
            }
            finally
            {
                if (strictAudio != null) AudioSystem.Stop(strictAudio);
            }
        }
        finally
        {
            alloc.Dispose();
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after Start/ownership tests");
        Assert.Equal(0L, NativeMemoryTracker.LiveBlocks, "Zero live blocks after Start/ownership tests");
    }

    private static void TestAudioConcurrencyStress()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(2 * 1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        AudioState* audio = null;

        try
        {
            var cfg = AudioConfig.Headless;
            cfg.MaxClips = 16;
            cfg.MaxVoices = 16;
            audio = AudioSystem.Start(ref alloc.Persistent, in cfg);

            float* dc = stackalloc float[256];
            for (int i = 0; i < 256; i++) dc[i] = 0.25f;
            AudioClipHandle persistentClip = AudioSystem.CreateClipFromPcm(audio, dc, 256, 1, 48000, StringId.Hash("perm"));

            using var stopCts = new CancellationTokenSource();
            CancellationToken ct = stopCts.Token;

            Task tMixer = Task.Run(() =>
            {
                float* localMix = (float*)NativeMemoryTracker.AlignedAlloc(128 * 2 * sizeof(float), 64);
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        AudioSystem.RenderMixFrames(audio, localMix, 128);
                        for (int i = 0; i < 256; i++)
                        {
                            if (!float.IsFinite(localMix[i]))
                                throw new InvalidOperationException("Non-finite audio sample in stress mix");
                        }
                    }
                }
                finally
                {
                    NativeMemoryTracker.AlignedFree(localMix, 128 * 2 * sizeof(float));
                }
            });

            Task tCaller = Task.Run(() =>
            {
                for (int i = 0; i < 400; i++)
                {
                    AudioVoiceHandle vh = AudioSystem.PlayOneShot(audio, persistentClip, 0.8f, 1.1f, -0.5f);
                    AudioSystem.SetVoiceVolume(audio, vh, 0.5f);
                    AudioSystem.SetVoicePitch(audio, vh, 1.25f);
                    AudioSystem.SetVoicePan(audio, vh, 0.5f);
                    _ = AudioSystem.GetActiveVoiceCount(audio);
                    _ = AudioSystem.GetLoadedClipCount(audio);
                    if ((i & 3) == 0) AudioSystem.StopVoice(audio, vh);
                }
            });

            Task tClipChurn = Task.Run(() =>
            {
                float* tmp = (float*)NativeMemoryTracker.AlignedAlloc(64 * sizeof(float), 64);
                try
                {
                    for (int i = 0; i < 64; i++) tmp[i] = 0.2f;
                    for (int i = 0; i < 100; i++)
                    {
                        AudioClipHandle ch = AudioSystem.CreateClipFromPcm(audio, tmp, 64, 1, 48000, StringId.Hash("churn"));
                        if (ch.IsValid)
                        {
                            _ = AudioSystem.PlayOneShot(audio, ch, 0.5f);
                            AudioSystem.UnloadClip(audio, ch);
                        }
                    }
                }
                finally
                {
                    NativeMemoryTracker.AlignedFree(tmp, 64 * sizeof(float));
                }
            });

            Task.WaitAll(tCaller, tClipChurn);
            stopCts.Cancel();
            tMixer.Wait();

            AudioSystem.Stop(audio);
            audio = null;
        }
        finally
        {
            if (audio != null) AudioSystem.Stop(audio);
            alloc.Dispose();
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after Audio concurrency stress");
        Assert.Equal(0L, NativeMemoryTracker.LiveBlocks, "Zero live blocks after Audio concurrency stress");
    }

    private static void TestAudioAndWavRegressionFixes()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(2 * 1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);

        try
        {
            // 1. AUDIONET-09: MixBufferFrames clamping [64, 65536] & RenderMixFrames overflow guard
            var hugeCfg = AudioConfig.Headless;
            hugeCfg.MixBufferFrames = int.MaxValue;
            AudioState* hugeAudio = AudioSystem.Start(ref alloc.Persistent, in hugeCfg);
            try
            {
                Assert.Equal(65536, hugeAudio->MixBufferFrames, "MixBufferFrames clamped to 65536 upper bound");
                Assert.Equal((nuint)(65536 * 2 * sizeof(float)), hugeAudio->MixScratchBytes, "MixScratchBytes matches clamped frames");

                float* dummy = stackalloc float[4];
                dummy[0] = 0.75f;
                // frameCount * outCh overflows int.MaxValue -> must return safely without clearing or throwing
                AudioSystem.RenderMixFrames(hugeAudio, dummy, int.MaxValue);
                Assert.Near(0.75f, dummy[0], 1e-6f, "RenderMixFrames overflow guard returned early safely");
            }
            finally
            {
                AudioSystem.Stop(hugeAudio);
            }

            var tinyCfg = AudioConfig.Headless;
            tinyCfg.MixBufferFrames = 1;
            AudioState* tinyAudio = AudioSystem.Start(ref alloc.Persistent, in tinyCfg);
            try
            {
                Assert.Equal(64, tinyAudio->MixBufferFrames, "Small positive MixBufferFrames clamped to 64");
            }
            finally
            {
                AudioSystem.Stop(tinyAudio);
            }

            // 2. AUDIONET-10: CreateClipFromDecoded undersized AllocBytes & zero AllocBytes guards
            AudioState* decAudio = AudioSystem.Start(ref alloc.Persistent, AudioConfig.Headless);
            try
            {
                long bytesBefore = NativeMemoryTracker.LiveBytes;
                long blocksBefore = NativeMemoryTracker.LiveBlocks;

                // Undersized real allocation (16 bytes allocated, but claims 100 mono frames = 400 bytes)
                nuint smallBytes = 16;
                float* smallPtr = (float*)NativeMemoryTracker.AlignedAlloc(smallBytes, 64);
                var undersized = new DecodedPcmData
                {
                    Samples = smallPtr,
                    AllocBytes = smallBytes,
                    FrameCount = 100,
                    Channels = 1,
                    SampleRate = 48000,
                };
                AudioClipHandle badClip1 = AudioSystem.CreateClipFromDecoded(decAudio, ref undersized, StringId.Hash("undersized"));
                Assert.True(!badClip1.IsValid && undersized.Samples == null, "Undersized AllocBytes rejected, freed, and cleared");
                Assert.Equal(bytesBefore, NativeMemoryTracker.LiveBytes, "LiveBytes uncorrupted after undersized AllocBytes rejection");
                Assert.Equal(blocksBefore, NativeMemoryTracker.LiveBlocks, "LiveBlocks uncorrupted after undersized AllocBytes rejection");

                // AllocBytes == 0 with non-null pointer -> rejected without calling AlignedFree(..., 0)
                float* stackPcm = stackalloc float[16];
                var zeroAlloc = new DecodedPcmData
                {
                    Samples = stackPcm,
                    AllocBytes = 0,
                    FrameCount = 16,
                    Channels = 1,
                    SampleRate = 48000,
                };
                AudioClipHandle badClip2 = AudioSystem.CreateClipFromDecoded(decAudio, ref zeroAlloc, StringId.Hash("zero_alloc"));
                Assert.True(!badClip2.IsValid && zeroAlloc.Samples == null, "Zero AllocBytes rejected safely");
                Assert.Equal(bytesBefore, NativeMemoryTracker.LiveBytes, "LiveBytes uncorrupted after zero AllocBytes rejection");
                Assert.Equal(blocksBefore, NativeMemoryTracker.LiveBlocks, "LiveBlocks uncorrupted after zero AllocBytes rejection");
            }
            finally
            {
                AudioSystem.Stop(decAudio);
            }

            // 3. AUDIONET-08: High-contention SetPaused + PlayOneShot + RenderMixFrames concurrency stress
            AudioState* stressAudio = AudioSystem.Start(ref alloc.Persistent, AudioConfig.Headless);
            try
            {
                float* dc = stackalloc float[128];
                for (int i = 0; i < 128; i++) dc[i] = 0.3f;
                AudioClipHandle clip = AudioSystem.CreateClipFromPcm(stressAudio, dc, 128, 1, 48000, StringId.Hash("stress"));
                Assert.True(clip.IsValid, "Created stress clip");

                using var cts = new CancellationTokenSource();
                CancellationToken ct = cts.Token;

                Task tMix = Task.Run(() =>
                {
                    float* buf = (float*)NativeMemoryTracker.AlignedAlloc(128 * 2 * sizeof(float), 64);
                    try
                    {
                        while (!ct.IsCancellationRequested)
                            AudioSystem.RenderMixFrames(stressAudio, buf, 128);
                    }
                    finally
                    {
                        NativeMemoryTracker.AlignedFree(buf, 128 * 2 * sizeof(float));
                    }
                });

                Task tPause = Task.Run(() =>
                {
                    for (int i = 0; i < 500; i++)
                        AudioSystem.SetPaused(stressAudio, (i & 1) == 0);
                });

                Task tPlay = Task.Run(() =>
                {
                    for (int i = 0; i < 500; i++)
                        _ = AudioSystem.PlayOneShot(stressAudio, clip, 0.5f);
                });

                Task.WaitAll(tPause, tPlay);
                cts.Cancel();
                tMix.Wait();
            }
            finally
            {
                AudioSystem.Stop(stressAudio);
            }

            // 4. AUDIONET-12: WavDecoder quad downmix, GenerateSineWave(440f, 1e30f), high-sample-rate EncodeWav16
            byte[] quadData = new byte[16]; // 1 frame x 4ch x 4 bytes float: FL=0.4, FR=-0.4, RL=0.2, RR=-0.2
            BinaryPrimitives.WriteInt32LittleEndian(quadData.AsSpan(0, 4), BitConverter.SingleToInt32Bits(0.4f));
            BinaryPrimitives.WriteInt32LittleEndian(quadData.AsSpan(4, 4), BitConverter.SingleToInt32Bits(-0.4f));
            BinaryPrimitives.WriteInt32LittleEndian(quadData.AsSpan(8, 4), BitConverter.SingleToInt32Bits(0.2f));
            BinaryPrimitives.WriteInt32LittleEndian(quadData.AsSpan(12, 4), BitConverter.SingleToInt32Bits(-0.2f));
            byte[] wav4ch = BuildWavWithChunks(channels: 4, sampleRate: 48000, formatTag: 3, bitsPerSample: 32, dataBytes: quadData);
            Assert.True(WavDecoder.TryDecode(wav4ch, out DecodedPcmData dec4ch), "Decoded 4ch quad WAV");
            Assert.Equal(2, dec4ch.Channels, "4ch quad downmixed to stereo");
            Assert.Near(0.4f + 0.2f * 0.70710678f, dec4ch.Samples[0], 1e-3f, "4ch quad downmix L = FL + 0.707*RL");
            Assert.Near(-0.4f + (-0.2f) * 0.70710678f, dec4ch.Samples[1], 1e-3f, "4ch quad downmix R = FR + 0.707*RR");
            WavDecoder.Free(ref dec4ch);

            DecodedPcmData hugeSine = WavDecoder.GenerateSineWave(440f, 1e30f);
            Assert.True(hugeSine.Samples != null && hugeSine.FrameCount > 1, "GenerateSineWave(440f, 1e30f) clamped safely without negative overflow");
            Assert.True(float.IsFinite(hugeSine.Samples[0]) && float.IsFinite(hugeSine.Samples[hugeSine.FrameCount - 1]), "Clamped sine samples finite");
            WavDecoder.Free(ref hugeSine);

            float* hiSamples = stackalloc float[8];
            for (int i = 0; i < 8; i++) hiSamples[i] = 0.25f;
            byte[] hiRateWav = WavDecoder.EncodeWav16(hiSamples, 4, 2, int.MaxValue);
            Assert.Equal(44 + 16, hiRateWav.Length, "High-sample-rate EncodeWav16 produced valid buffer");
            uint encodedByteRate = BinaryPrimitives.ReadUInt32LittleEndian(hiRateWav.AsSpan(28, 4));
            Assert.Equal(uint.MaxValue, encodedByteRate, "High-sample-rate EncodeWav16 clamped uint byteRate without signed overflow");
        }
        finally
        {
            alloc.Dispose();
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after Audio & WavDecoder regression tests");
        Assert.Equal(0L, NativeMemoryTracker.LiveBlocks, "Zero live blocks after Audio & WavDecoder regression tests");
    }

    private static byte[] BuildCustomPcmWav(ushort channels, int sampleRate, ushort bitsPerSample, byte[] dataBytes)
        => BuildWavWithChunks(channels, sampleRate, formatTag: 1, bitsPerSample, dataBytes);

    private static byte[] BuildWavWithChunks(ushort channels, int sampleRate, ushort formatTag, ushort bitsPerSample, byte[] dataBytes)
    {
        byte[] wav = new byte[12 + 24 + 8 + dataBytes.Length];
        Span<byte> s = wav;
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(0, 4), 0x46464952u);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(4, 4), (uint)(wav.Length - 8));
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(8, 4), 0x45564157u);

        int o = 12;
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(o, 4), 0x20746D66u);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(o + 4, 4), 16u);
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(o + 8, 2), formatTag);
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(o + 10, 2), channels);
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(o + 12, 4), sampleRate);
        int blockAlign = channels * Math.Max(1, bitsPerSample >> 3);
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(o + 16, 4), sampleRate * blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(o + 20, 2), (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(o + 22, 2), bitsPerSample);
        o += 24;

        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(o, 4), 0x61746164u);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(o + 4, 4), (uint)dataBytes.Length);
        dataBytes.CopyTo(s.Slice(o + 8));
        return wav;
    }

    private static byte[] BuildExtensibleWav(ushort subFormat, ushort bitsPerSample, ushort channels, int sampleRate, bool validGuid, byte[] dataBytes)
    {
        byte[] wav = new byte[12 + 48 + 8 + dataBytes.Length];
        Span<byte> s = wav;
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(0, 4), 0x46464952u);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(4, 4), (uint)(wav.Length - 8));
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(8, 4), 0x45564157u);

        int o = 12;
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(o, 4), 0x20746D66u);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(o + 4, 4), 40u);
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(o + 8, 2), 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(o + 10, 2), channels);
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(o + 12, 4), sampleRate);
        int blockAlign = channels * (bitsPerSample >> 3);
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(o + 16, 4), sampleRate * blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(o + 20, 2), (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(o + 22, 2), bitsPerSample);
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(o + 24, 2), 22);
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(o + 26, 2), bitsPerSample);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(o + 28, 4), 3u);
        // SubFormat GUID at o + 8 + 24 = o + 32
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(o + 32, 2), subFormat);
        byte[] tail = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, (byte)(validGuid ? 0x71 : 0x72) };
        tail.CopyTo(s.Slice(o + 34, 14));
        o += 48;

        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(o, 4), 0x61746164u);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(o + 4, 4), (uint)dataBytes.Length);
        dataBytes.CopyTo(s.Slice(o + 8));
        return wav;
    }

    private static byte[] BuildFloat32WavWithExtraChunks(float[] samples, int[] extraChunkSizes, bool dataBeforeFmt)
    {
        int extraTotal = 0;
        foreach (int sz in extraChunkSizes) extraTotal += 8 + sz + (sz & 1);
        int dataBytes = samples.Length * 4;
        byte[] wav = new byte[12 + 24 + extraTotal + (8 + dataBytes)];
        Span<byte> s = wav;
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(0, 4), 0x46464952u);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(4, 4), (uint)(wav.Length - 8));
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(8, 4), 0x45564157u);

        int o = 12;
        if (!dataBeforeFmt)
        {
            WriteFmtFloat(s, ref o, samples.Length);
        }
        else
        {
            WriteDataFloat(s, ref o, samples);
        }

        foreach (int sz in extraChunkSizes)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(o, 4), 0x4B4E554Au); // JUNK
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(o + 4, 4), (uint)sz);
            o += 8 + sz + (sz & 1);
        }

        if (!dataBeforeFmt)
            WriteDataFloat(s, ref o, samples);
        else
            WriteFmtFloat(s, ref o, samples.Length);

        return wav;

        static void WriteFmtFloat(Span<byte> s, ref int o, int _)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(o, 4), 0x20746D66u);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(o + 4, 4), 16u);
            BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(o + 8, 2), 3);
            BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(o + 10, 2), 1);
            BinaryPrimitives.WriteInt32LittleEndian(s.Slice(o + 12, 4), 48000);
            BinaryPrimitives.WriteInt32LittleEndian(s.Slice(o + 16, 4), 48000 * 4);
            BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(o + 20, 2), 4);
            BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(o + 22, 2), 32);
            o += 24;
        }

        static void WriteDataFloat(Span<byte> s, ref int o, float[] samples)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(o, 4), 0x61746164u);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(o + 4, 4), (uint)(samples.Length * 4));
            o += 8;
            for (int i = 0; i < samples.Length; i++)
                BinaryPrimitives.WriteInt32LittleEndian(s.Slice(o + i * 4, 4), BitConverter.SingleToInt32Bits(samples[i]));
            o += samples.Length * 4;
        }
    }
}
