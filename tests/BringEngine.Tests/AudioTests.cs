using System;
using System.Buffers.Binary;
using System.IO;
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
        Assert.Run("AudioSystem_HeadlessMixer_BusesPanPitchLoopStealBpackAndZeroLeak", &TestAudioMixerAndBpack);
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
            AudioClipHandle clip = AudioSystem.CreateClipFromDecoded(audio, tone, StringId.Hash("tone"));
            Assert.True(clip.IsValid, "Clip created for zero-GC test");

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
}
