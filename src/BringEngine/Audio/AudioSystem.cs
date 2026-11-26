using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using BringEngine.Assets.Packing;
using BringEngine.Core.Handles;
using BringEngine.Core.Logging;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using SDL3;

namespace BringEngine.Audio;

public enum AudioBus : byte
{
    Master = 0,
    Music = 1,
    Sfx = 2,
    Ui = 3,
    Count = 4,
}

public enum AudioVoiceState : byte
{
    Inactive = 0,
    Playing = 1,
    Paused = 2,
}

/// <summary>
/// Configuration for BringEngine's SDL3-backed audio mixer.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct AudioConfig
{
    public int SampleRate;
    public int Channels;
    public int MaxClips;
    public int MaxVoices;
    public int MixBufferFrames;
    public byte HeadlessFallback; // 1 = gracefully fall back to software/headless mode if no OS audio device exists (CI-safe)
    public byte ForceHeadless;    // 1 = skip SDL audio device initialization (deterministic headless testing)

    public static AudioConfig Default => new AudioConfig
    {
        SampleRate = 48000,
        Channels = 2,
        MaxClips = 128,
        MaxVoices = 64,
        MixBufferFrames = 512,
        HeadlessFallback = 1,
        ForceHeadless = 0,
    };

    public static AudioConfig Headless => new AudioConfig
    {
        SampleRate = 48000,
        Channels = 2,
        MaxClips = 128,
        MaxVoices = 64,
        MixBufferFrames = 512,
        HeadlessFallback = 1,
        ForceHeadless = 1,
    };
}

/// <summary>
/// Zero-GC playback parameters for AudioSystem.Play.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct AudioPlayParams
{
    public AudioBus Bus;
    public float Volume;
    public float Pitch;
    public float Pan; // -1.0 = full left, 0.0 = center, +1.0 = full right
    public byte Loop;
    public byte StartPaused;

    public static AudioPlayParams OneShot(AudioBus bus = AudioBus.Sfx, float volume = 1f, float pitch = 1f, float pan = 0f) => new AudioPlayParams
    {
        Bus = bus,
        Volume = volume,
        Pitch = pitch,
        Pan = pan,
        Loop = 0,
        StartPaused = 0,
    };

    public static AudioPlayParams Looping(AudioBus bus = AudioBus.Music, float volume = 1f, float pitch = 1f, float pan = 0f) => new AudioPlayParams
    {
        Bus = bus,
        Volume = volume,
        Pitch = pitch,
        Pan = pan,
        Loop = 1,
        StartPaused = 0,
    };
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct AudioClipSlot
{
    public float* Samples;
    public nuint AllocBytes;
    public int FrameCount;
    public int Channels;
    public int SampleRate;
    public StringId Name;
    public byte Alive;
}

[StructLayout(LayoutKind.Sequential)]
public struct AudioVoiceSlot
{
    public int ClipIndex;
    public uint ClipGeneration;
    public double PlayheadFrame;
    public float Volume;
    public float Pitch;
    public float Pan;
    public ulong StartOrder;
    public AudioBus Bus;
    public byte Loop;
    public AudioVoiceState State;
}

/// <summary>
/// Unmanaged core audio mixer state (allocated in EngineAllocators.Persistent).
/// Feeds a single SDL3 AudioStream from a dedicated background thread (BringEngine.AudioMixer)
/// with 0 GC allocations during playback and deterministic software-mix support for headless tests.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct AudioState
{
    public AudioClipSlot* Clips;
    public uint* ClipGens;
    public int MaxClips;
    public int LoadedClipCount;

    public AudioVoiceSlot* Voices;
    public uint* VoiceGens;
    public int MaxVoices;
    public int ActiveVoiceCount;

    public float* MixScratch;
    public nuint MixScratchBytes;
    public int MixBufferFrames;
    public int SampleRate;
    public int Channels;

    public fixed float BusVolumes[(int)AudioBus.Count];
    public fixed byte BusMuted[(int)AudioBus.Count];

    public ulong NextStartOrder;
    public ulong MixedFramesTotal;
    public uint DeviceId;
    public nint AudioStream;
    public LogState* Log;

    public int SpinLock;
    public int SyncSlot;
    public int ShutdownFlag;
    public int MixerThreadId;

    public byte GlobalPaused;
    public byte IsHeadless;
    public byte SdlAudioInitialized;
    public byte Active;
}

/// <summary>
/// Zero-OOP, Zero-GC Core Audio System built on SDL3's native SDL_AudioStream (0 extra native libraries).
/// Features:
/// - 32-bit generational handles (AudioClipHandle, AudioVoiceHandle)
/// - 4 volume/mute buses (Master, Music, Sfx, Ui) + global pause (for Alt-Tab / focus loss)
/// - Linear-interpolated pitch & sample-rate conversion + equal-power stereo panning
/// - Oldest-voice stealing when MaxVoices is saturated
/// - Built-in WAV loader (+ .bpack RawBlob integration + raw PCM upload)
/// - Dedicated background mixer thread (BringEngine.AudioMixer) & deterministic headless RenderMixFrames()
/// </summary>
public static unsafe class AudioSystem
{
    private const int MaxAudioInstances = 4;

    private struct ManagedSync
    {
        public ManualResetEventSlim? StopSignal;
        public Thread? MixerThread;
        public bool InUse;
    }

    private static readonly ManagedSync[] s_sync = new ManagedSync[MaxAudioInstances];
    private static readonly object s_syncLock = new();

    /// <summary>
    /// Initializes AudioState in persistentArena, opens SDL3 playback stream (unless ForceHeadless or unavailable),
    /// and starts the dedicated background mixer thread (BringEngine.AudioMixer).
    /// </summary>
    public static AudioState* Start(ref LinearArena persistentArena, in AudioConfig config, LogState* log = null)
    {
        int maxHandleSlots = (int)HandlePacking.IndexMask + 1;
        int sampleRate = config.SampleRate > 0 ? config.SampleRate : 48000;
        int channels = config.Channels <= 0 ? 2 : (config.Channels == 1 ? 1 : 2);
        int maxClips = config.MaxClips > 0 ? config.MaxClips : 128;
        if (maxClips > maxHandleSlots) maxClips = maxHandleSlots;
        int maxVoices = config.MaxVoices > 0 ? config.MaxVoices : 64;
        if (maxVoices > maxHandleSlots) maxVoices = maxHandleSlots;
        int mixFrames = config.MixBufferFrames <= 0
            ? 512
            : (config.MixBufferFrames < 64 ? 64 : (config.MixBufferFrames > 65536 ? 65536 : config.MixBufferFrames));

        int syncSlot = -1;
        lock (s_syncLock)
        {
            for (int i = 0; i < MaxAudioInstances; i++)
            {
                if (!s_sync[i].InUse)
                {
                    s_sync[i].InUse = true;
                    s_sync[i].StopSignal = new ManualResetEventSlim(false);
                    syncSlot = i;
                    break;
                }
            }
        }
        if (syncSlot < 0)
            throw new InvalidOperationException("Maximum concurrent AudioSystem instances exceeded.");

        AudioState* state = null;
        Thread? thread = null;
        bool threadStarted = false;
        try
        {
            state = persistentArena.Alloc<AudioState>(1, 64);
            state->Clips = persistentArena.Alloc<AudioClipSlot>(maxClips, 64);
            state->ClipGens = persistentArena.AllocUninitialized<uint>(maxClips, 16);
            for (int i = 0; i < maxClips; i++) state->ClipGens[i] = 1;

            state->Voices = persistentArena.Alloc<AudioVoiceSlot>(maxVoices, 64);
            state->VoiceGens = persistentArena.AllocUninitialized<uint>(maxVoices, 16);
            for (int i = 0; i < maxVoices; i++) state->VoiceGens[i] = 1;

            state->MaxClips = maxClips;
            state->MaxVoices = maxVoices;
            state->MixBufferFrames = mixFrames;
            state->SampleRate = sampleRate;
            state->Channels = channels;
            state->SyncSlot = syncSlot;
            state->NextStartOrder = 1;
            state->Log = log;
            nuint scratchBytes = (nuint)((long)mixFrames * channels * sizeof(float));
            state->MixScratchBytes = scratchBytes;
            state->MixScratch = (float*)NativeMemoryTracker.AlignedAlloc(scratchBytes, 64);

            for (int b = 0; b < (int)AudioBus.Count; b++)
            {
                Volatile.Write(ref state->BusVolumes[b], 1.0f);
                Volatile.Write(ref state->BusMuted[b], 0);
            }

            if (config.ForceHeadless != 0)
            {
                state->IsHeadless = 1;
            }
            else
            {
                TryInitSdlAudio(state, config.HeadlessFallback != 0);
            }

            Volatile.Write(ref state->ShutdownFlag, 0);
            Volatile.Write(ref state->Active, 1);

            nint rawState = (nint)state;
            thread = new Thread(MixerThreadEntry)
            {
                Name = "BringEngine.AudioMixer",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
            };
            s_sync[syncSlot].MixerThread = thread;
            Volatile.Write(ref state->MixerThreadId, thread.ManagedThreadId);
            thread.Start(rawState);
            threadStarted = true;
        }
        catch
        {
            if (state != null)
            {
                Volatile.Write(ref state->Active, 0);
                Volatile.Write(ref state->ShutdownFlag, 1);
            }
            if (threadStarted)
            {
                try { s_sync[syncSlot].StopSignal?.Set(); } catch { }
                try { thread?.Join(); } catch { }
            }
            if (state != null)
            {
                if (state->AudioStream != nint.Zero)
                {
                    SDL.DestroyAudioStream(state->AudioStream);
                    state->AudioStream = nint.Zero;
                }
                if (state->DeviceId != 0)
                {
                    SDL.CloseAudioDevice(state->DeviceId);
                    state->DeviceId = 0;
                }
                if (state->SdlAudioInitialized != 0)
                {
                    SDL.QuitSubSystem(SDL.InitFlags.Audio);
                    state->SdlAudioInitialized = 0;
                }
                if (state->MixScratch != null)
                {
                    NativeMemoryTracker.AlignedFree(state->MixScratch, state->MixScratchBytes);
                    state->MixScratch = null;
                    state->MixScratchBytes = 0;
                }
            }
            lock (s_syncLock)
            {
                s_sync[syncSlot].StopSignal?.Dispose();
                s_sync[syncSlot] = default;
            }
            throw;
        }

        if (state->Log != null && state->Log->Active != 0)
        {
            LogSystem.Info(state->Log,
                $"AudioSystem started (mode={(state->IsHeadless != 0 ? "headless" : "sdl3")}, rate={sampleRate}Hz, ch={channels}, maxClips={maxClips}, maxVoices={maxVoices})");
        }

        return state;
    }

    private static void TryInitSdlAudio(AudioState* state, bool allowFallback)
    {
        if (!SDL.InitSubSystem(SDL.InitFlags.Audio))
        {
            HandleAudioInitFailure(state, allowFallback, "SDL_InitSubSystem(Audio)");
            return;
        }
        state->SdlAudioInitialized = 1;

        var spec = new SDL.AudioSpec
        {
            Format = SDL.AudioFormat.AudioF32LE,
            Channels = state->Channels,
            Freq = state->SampleRate,
        };

        uint dev = SDL.OpenAudioDevice(SDL.AudioDeviceDefaultPlayback, in spec);
        if (dev == 0)
        {
            if (state->SdlAudioInitialized != 0)
            {
                SDL.QuitSubSystem(SDL.InitFlags.Audio);
                state->SdlAudioInitialized = 0;
            }
            HandleAudioInitFailure(state, allowFallback, "SDL_OpenAudioDevice");
            return;
        }

        nint stream = SDL.CreateAudioStream(in spec, in spec);
        if (stream == nint.Zero || !SDL.BindAudioStream(dev, stream))
        {
            if (stream != nint.Zero) SDL.DestroyAudioStream(stream);
            SDL.CloseAudioDevice(dev);
            if (state->SdlAudioInitialized != 0)
            {
                SDL.QuitSubSystem(SDL.InitFlags.Audio);
                state->SdlAudioInitialized = 0;
            }
            HandleAudioInitFailure(state, allowFallback, "SDL_CreateAudioStream/BindAudioStream");
            return;
        }

        SDL.ResumeAudioDevice(dev);
        state->DeviceId = dev;
        state->AudioStream = stream;
        state->IsHeadless = 0;
    }

    private static void HandleAudioInitFailure(AudioState* state, bool allowFallback, string stage)
    {
        string err = SDL.GetError() ?? "unknown";
        if (!allowFallback)
            throw new InvalidOperationException($"AudioSystem {stage} failed: {err}");

        state->IsHeadless = 1;
        if (state->Log != null && state->Log->Active != 0)
            LogSystem.Warn(state->Log, $"AudioSystem falling back to headless mixer ({stage} failed: {err})");
    }

    private static void MixerThreadEntry(object? arg)
    {
        AudioState* state = (AudioState*)(nint)arg!;
        int syncSlot = state->SyncSlot;
        Volatile.Write(ref state->MixerThreadId, Environment.CurrentManagedThreadId);

        ManualResetEventSlim stopEvt = s_sync[syncSlot].StopSignal!;
        int chunkFrames = state->MixBufferFrames;
        int bytesPerFrame = state->Channels * sizeof(float);
        int chunkBytes = chunkFrames * bytesPerFrame;
        int targetQueuedBytes = chunkBytes * 3; // ~32ms buffered at 48kHz / 512 frames
        bool warnedPutFailure = false;

        while (Volatile.Read(ref state->ShutdownFlag) == 0)
        {
            if (state->IsHeadless == 0 && state->AudioStream != nint.Zero)
            {
                int queued = SDL.GetAudioStreamQueued(state->AudioStream);
                int safetyChunks = 0;
                while (queued >= 0 && queued < targetQueuedBytes && safetyChunks < 4 && Volatile.Read(ref state->ShutdownFlag) == 0)
                {
                    RenderMixFrames(state, state->MixScratch, chunkFrames);
                    if (!SDL.PutAudioStreamData(state->AudioStream, (nint)state->MixScratch, chunkBytes))
                    {
                        if (!warnedPutFailure && state->Log != null && state->Log->Active != 0)
                        {
                            warnedPutFailure = true;
                            LogSystem.Warn(state->Log, $"SDL_PutAudioStreamData failed: {SDL.GetError() ?? "unknown"}");
                        }
                        break;
                    }
                    queued += chunkBytes;
                    safetyChunks++;
                }
                stopEvt.Wait(4);
            }
            else
            {
                // Headless mode: stay idle until shutdown so callers/tests drive RenderMixFrames deterministically.
                stopEvt.Wait(50);
            }
        }
    }

    /// <summary>
    /// Stops the background mixer thread, closes SDL3 audio resources, and frees all clip & scratch native memory (0 leaks).
    /// </summary>
    public static void Stop(AudioState* audio)
    {
        if (audio == null || Volatile.Read(ref audio->Active) == 0) return;

        int syncSlot;
        AcquireLock(audio);
        try
        {
            if (Volatile.Read(ref audio->Active) == 0) return;
            Volatile.Write(ref audio->Active, 0);
            Volatile.Write(ref audio->ShutdownFlag, 1);
            syncSlot = audio->SyncSlot;
        }
        finally
        {
            ReleaseLock(audio);
        }

        if ((uint)syncSlot < MaxAudioInstances)
        {
            s_sync[syncSlot].StopSignal?.Set();
            s_sync[syncSlot].MixerThread?.Join();
            s_sync[syncSlot].StopSignal?.Dispose();
            lock (s_syncLock)
            {
                s_sync[syncSlot] = default;
            }
        }

        // Barrier with SpinLock so any concurrent caller (e.g. SetPaused) finishes before SDL/native teardown
        AcquireLock(audio);
        try
        {
            if (audio->AudioStream != nint.Zero)
            {
                SDL.DestroyAudioStream(audio->AudioStream);
                audio->AudioStream = nint.Zero;
            }
            if (audio->DeviceId != 0)
            {
                SDL.CloseAudioDevice(audio->DeviceId);
                audio->DeviceId = 0;
            }
            if (audio->SdlAudioInitialized != 0)
            {
                SDL.QuitSubSystem(SDL.InitFlags.Audio);
                audio->SdlAudioInitialized = 0;
            }

            // Free all loaded native clip buffers and mix scratch buffer
            for (int i = 0; i < audio->MaxClips; i++)
            {
                if (audio->Clips[i].Alive != 0 && audio->Clips[i].Samples != null)
                {
                    NativeMemoryTracker.AlignedFree(audio->Clips[i].Samples, audio->Clips[i].AllocBytes);
                    audio->Clips[i] = default;
                }
            }
            Volatile.Write(ref audio->LoadedClipCount, 0);

            for (int i = 0; i < audio->MaxVoices; i++)
                audio->Voices[i] = default;
            Volatile.Write(ref audio->ActiveVoiceCount, 0);

            if (audio->MixScratch != null)
            {
                NativeMemoryTracker.AlignedFree(audio->MixScratch, audio->MixScratchBytes);
                audio->MixScratch = null;
                audio->MixScratchBytes = 0;
            }
        }
        finally
        {
            ReleaseLock(audio);
        }

        if (audio->Log != null && audio->Log->Active != 0)
            LogSystem.Info(audio->Log, $"AudioSystem stopped (totalMixedFrames={audio->MixedFramesTotal})");
    }

    // ==================== Clip Management ====================

    public static AudioClipHandle LoadClipFromWavFile(AudioState* audio, string wavPath, StringId name = default)
    {
        if (audio == null || Volatile.Read(ref audio->Active) == 0) return AudioClipHandle.Invalid;
        if (string.IsNullOrEmpty(wavPath) || !File.Exists(wavPath))
        {
            LogSystem.Warn(audio->Log, $"AudioSystem.LoadClipFromWavFile: file not found '{wavPath ?? ""}'");
            return AudioClipHandle.Invalid;
        }
        byte[] bytes = File.ReadAllBytes(wavPath);
        if (name.Value == 0) name = StringId.Hash(Path.GetFileNameWithoutExtension(wavPath));
        return LoadClipFromWavMemory(audio, bytes, name);
    }

    public static AudioClipHandle LoadClipFromWavMemory(AudioState* audio, ReadOnlySpan<byte> wavBytes, StringId name = default)
    {
        if (audio == null || Volatile.Read(ref audio->Active) == 0) return AudioClipHandle.Invalid;
        if (!WavDecoder.TryDecode(wavBytes, out DecodedPcmData decoded))
        {
            LogSystem.Warn(audio->Log, $"AudioSystem.LoadClipFromWavMemory: failed to decode WAV (clip={name}, bytes={wavBytes.Length})");
            return AudioClipHandle.Invalid;
        }

        AudioClipHandle h = CreateClipFromDecoded(audio, ref decoded, name);
        if (!h.IsValid)
            LogSystem.Warn(audio->Log, $"AudioSystem.LoadClipFromWavMemory: clip table full or inactive (maxClips={audio->MaxClips}, clip={name})");
        return h;
    }

    /// <summary>
    /// Loads a compressed WAV audio clip stored as a RawBlob inside a .bpack asset bundle.
    /// </summary>
    public static AudioClipHandle LoadClipFromBpack(
        AudioState* audio,
        string bpackPath,
        StringId blobName,
        ref LinearArena scratchArena)
    {
        if (audio == null || Volatile.Read(ref audio->Active) == 0) return AudioClipHandle.Invalid;
        if (string.IsNullOrEmpty(bpackPath) || !File.Exists(bpackPath))
        {
            LogSystem.Warn(audio->Log, $"AudioSystem.LoadClipFromBpack: pack not found '{bpackPath ?? ""}' (blob={blobName})");
            return AudioClipHandle.Invalid;
        }
        ArenaMarker mark = scratchArena.SaveMarker();
        try
        {
            if (!AssetPackLoader.TryReadRawBlob(bpackPath, blobName, ref scratchArena, out NativeSlice<byte> raw))
            {
                LogSystem.Warn(audio->Log, $"AudioSystem.LoadClipFromBpack: missing or invalid blob {blobName} in '{bpackPath}'");
                return AudioClipHandle.Invalid;
            }
            return LoadClipFromWavMemory(audio, new ReadOnlySpan<byte>(raw.Ptr, raw.Length), blobName);
        }
        finally
        {
            scratchArena.Rollback(mark);
        }
    }

    /// <summary>
    /// Creates an audio clip by copying caller-provided interleaved Float32 PCM samples (mono or stereo) into native clip memory.
    /// </summary>
    public static AudioClipHandle CreateClipFromPcm(
        AudioState* audio,
        float* interleavedSamples,
        int frameCount,
        int channels,
        int sampleRate,
        StringId name = default)
    {
        if (audio == null || Volatile.Read(ref audio->Active) == 0 || interleavedSamples == null ||
            frameCount <= 0 || sampleRate <= 0 || channels <= 0 || channels > 2)
            return AudioClipHandle.Invalid;

        nuint bytes = (nuint)((long)frameCount * channels * sizeof(float));
        float* copy = (float*)NativeMemoryTracker.AlignedAlloc(bytes, 64);
        Buffer.MemoryCopy(interleavedSamples, copy, bytes, bytes);

        AudioClipHandle h = RegisterOwnedPcm(audio, copy, bytes, frameCount, channels, sampleRate, name);
        if (!h.IsValid)
            NativeMemoryTracker.AlignedFree(copy, bytes);
        return h;
    }

    /// <summary>
    /// Transfers ownership of a DecodedPcmData native buffer directly into AudioSystem (zero copy) and clears <paramref name="decoded"/>.
    /// On failure, frees decoded.Samples automatically and clears <paramref name="decoded"/>.
    /// </summary>
    public static AudioClipHandle CreateClipFromDecoded(AudioState* audio, ref DecodedPcmData decoded, StringId name = default)
    {
        bool dimsValid = decoded.FrameCount > 0 && decoded.SampleRate > 0 &&
                         decoded.Channels > 0 && decoded.Channels <= 2;
        nuint requiredBytes = dimsValid ? (nuint)((long)decoded.FrameCount * decoded.Channels * sizeof(float)) : 0;
        if (audio == null || Volatile.Read(ref audio->Active) == 0 ||
            decoded.Samples == null || !dimsValid || decoded.AllocBytes < requiredBytes)
        {
            if (decoded.Samples != null && decoded.AllocBytes > 0)
                WavDecoder.Free(ref decoded);
            decoded = default;
            return AudioClipHandle.Invalid;
        }
        AudioClipHandle h = RegisterOwnedPcm(audio, decoded.Samples, decoded.AllocBytes, decoded.FrameCount, decoded.Channels, decoded.SampleRate, name);
        if (!h.IsValid)
            WavDecoder.Free(ref decoded);
        decoded = default;
        return h;
    }

    /// <summary>
    /// By-value convenience overload; prefer <see cref="CreateClipFromDecoded(AudioState*, ref DecodedPcmData, StringId)"/> so caller's struct is cleared on ownership transfer.
    /// </summary>
    [Obsolete("Use CreateClipFromDecoded(AudioState*, ref DecodedPcmData, StringId) to prevent double-free.", error: false)]
    public static AudioClipHandle CreateClipFromDecoded(AudioState* audio, DecodedPcmData decoded, StringId name = default)
        => CreateClipFromDecoded(audio, ref decoded, name);

    private static AudioClipHandle RegisterOwnedPcm(AudioState* audio, float* ownedSamples, nuint allocBytes, int frameCount, int channels, int sampleRate, StringId name)
    {
        AcquireLock(audio);
        try
        {
            if (Volatile.Read(ref audio->Active) == 0) return AudioClipHandle.Invalid;

            int slot = -1;
            for (int i = 0; i < audio->MaxClips; i++)
            {
                if (audio->Clips[i].Alive == 0) { slot = i; break; }
            }
            if (slot < 0) return AudioClipHandle.Invalid;

            audio->Clips[slot] = new AudioClipSlot
            {
                Samples = ownedSamples,
                AllocBytes = allocBytes,
                FrameCount = frameCount,
                Channels = channels,
                SampleRate = sampleRate,
                Name = name,
                Alive = 1,
            };
            Volatile.Write(ref audio->LoadedClipCount, Volatile.Read(ref audio->LoadedClipCount) + 1);
            return AudioClipHandle.Create((uint)slot, audio->ClipGens[slot]);
        }
        finally
        {
            ReleaseLock(audio);
        }
    }

    /// <summary>
    /// Unloads an audio clip, immediately stopping any voices referencing it and freeing its native PCM buffer.
    /// </summary>
    public static bool UnloadClip(AudioState* audio, AudioClipHandle clip)
    {
        if (audio == null || Volatile.Read(ref audio->Active) == 0 || !clip.IsValid) return false;
        uint idx = clip.Index;
        if (idx >= (uint)audio->MaxClips) return false;

        float* toFree = null;
        nuint toFreeBytes = 0;
        AcquireLock(audio);
        try
        {
            if (Volatile.Read(ref audio->Active) == 0) return false;
            if (audio->Clips[idx].Alive == 0 || audio->ClipGens[idx] != clip.Generation)
                return false;

            // Stop all voices referencing this clip before freeing its sample buffer
            int activeVoices = Volatile.Read(ref audio->ActiveVoiceCount);
            for (int v = 0; v < audio->MaxVoices; v++)
            {
                ref AudioVoiceSlot vs = ref audio->Voices[v];
                if (vs.State != AudioVoiceState.Inactive && vs.ClipIndex == (int)idx && vs.ClipGeneration == clip.Generation)
                {
                    vs.State = AudioVoiceState.Inactive;
                    BumpGen(ref audio->VoiceGens[v]);
                    if (activeVoices > 0) activeVoices--;
                }
            }
            Volatile.Write(ref audio->ActiveVoiceCount, activeVoices);

            toFree = audio->Clips[idx].Samples;
            toFreeBytes = audio->Clips[idx].AllocBytes;
            audio->Clips[idx] = default;
            BumpGen(ref audio->ClipGens[idx]);
            int loadedClips = Volatile.Read(ref audio->LoadedClipCount);
            if (loadedClips > 0) Volatile.Write(ref audio->LoadedClipCount, loadedClips - 1);
        }
        finally
        {
            ReleaseLock(audio);
        }

        if (toFree != null) NativeMemoryTracker.AlignedFree(toFree, toFreeBytes);
        return true;
    }

    // ==================== Zero-GC Playback & Voice Control ====================

    public static AudioVoiceHandle Play(AudioState* audio, AudioClipHandle clip, in AudioPlayParams p)
    {
        if (audio == null || Volatile.Read(ref audio->Active) == 0 || !clip.IsValid) return AudioVoiceHandle.Invalid;
        uint clipIdx = clip.Index;
        if (clipIdx >= (uint)audio->MaxClips) return AudioVoiceHandle.Invalid;

        AcquireLock(audio);
        try
        {
            if (Volatile.Read(ref audio->Active) == 0) return AudioVoiceHandle.Invalid;
            if (audio->Clips[clipIdx].Alive == 0 || audio->ClipGens[clipIdx] != clip.Generation)
                return AudioVoiceHandle.Invalid;

            int slot = -1;
            int oldestNonLoop = -1;
            ulong oldestNonLoopOrder = ulong.MaxValue;
            int oldestAny = -1;
            ulong oldestAnyOrder = ulong.MaxValue;

            for (int i = 0; i < audio->MaxVoices; i++)
            {
                ref AudioVoiceSlot v = ref audio->Voices[i];
                if (v.State == AudioVoiceState.Inactive)
                {
                    slot = i;
                    break;
                }
                if (v.Loop == 0 && v.StartOrder < oldestNonLoopOrder)
                {
                    oldestNonLoopOrder = v.StartOrder;
                    oldestNonLoop = i;
                }
                if (v.StartOrder < oldestAnyOrder)
                {
                    oldestAnyOrder = v.StartOrder;
                    oldestAny = i;
                }
            }

            bool stolen = false;
            if (slot < 0)
            {
                slot = oldestNonLoop >= 0 ? oldestNonLoop : oldestAny;
                if (slot < 0) return AudioVoiceHandle.Invalid;
                BumpGen(ref audio->VoiceGens[slot]);
                stolen = true;
            }

            AudioBus bus = (byte)p.Bus < (byte)AudioBus.Count ? p.Bus : AudioBus.Sfx;
            float vol = float.IsFinite(p.Volume) ? DetMath.Clamp(p.Volume, 0f, 4f) : 0f;
            float pitch = float.IsFinite(p.Pitch) ? DetMath.Clamp(p.Pitch <= 0f ? 1f : p.Pitch, 0.125f, 4f) : 1f;
            float pan = float.IsFinite(p.Pan) ? DetMath.Clamp(p.Pan, -1f, 1f) : 0f;

            audio->Voices[slot] = new AudioVoiceSlot
            {
                ClipIndex = (int)clipIdx,
                ClipGeneration = clip.Generation,
                PlayheadFrame = 0.0,
                Volume = vol,
                Pitch = pitch,
                Pan = pan,
                StartOrder = audio->NextStartOrder++,
                Bus = bus,
                Loop = p.Loop,
                State = p.StartPaused != 0 ? AudioVoiceState.Paused : AudioVoiceState.Playing,
            };

            if (!stolen) Volatile.Write(ref audio->ActiveVoiceCount, Volatile.Read(ref audio->ActiveVoiceCount) + 1);
            return AudioVoiceHandle.Create((uint)slot, audio->VoiceGens[slot]);
        }
        finally
        {
            ReleaseLock(audio);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AudioVoiceHandle PlayOneShot(
        AudioState* audio,
        AudioClipHandle clip,
        float volume = 1f,
        float pitch = 1f,
        float pan = 0f,
        AudioBus bus = AudioBus.Sfx)
    {
        var p = AudioPlayParams.OneShot(bus, volume, pitch, pan);
        return Play(audio, clip, in p);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AudioVoiceHandle PlayLoop(
        AudioState* audio,
        AudioClipHandle clip,
        float volume = 1f,
        float pitch = 1f,
        AudioBus bus = AudioBus.Music)
    {
        var p = AudioPlayParams.Looping(bus, volume, pitch, 0f);
        return Play(audio, clip, in p);
    }

    public static void StopVoice(AudioState* audio, AudioVoiceHandle voice)
    {
        if (audio == null || Volatile.Read(ref audio->Active) == 0 || !voice.IsValid) return;
        uint idx = voice.Index;
        if (idx >= (uint)audio->MaxVoices) return;

        AcquireLock(audio);
        try
        {
            if (Volatile.Read(ref audio->Active) == 0) return;
            if (audio->VoiceGens[idx] == voice.Generation && audio->Voices[idx].State != AudioVoiceState.Inactive)
            {
                audio->Voices[idx].State = AudioVoiceState.Inactive;
                BumpGen(ref audio->VoiceGens[idx]);
                int active = Volatile.Read(ref audio->ActiveVoiceCount);
                if (active > 0) Volatile.Write(ref audio->ActiveVoiceCount, active - 1);
            }
        }
        finally
        {
            ReleaseLock(audio);
        }
    }

    public static void StopBus(AudioState* audio, AudioBus bus)
    {
        if (audio == null || Volatile.Read(ref audio->Active) == 0) return;
        AcquireLock(audio);
        try
        {
            if (Volatile.Read(ref audio->Active) == 0) return;
            int active = Volatile.Read(ref audio->ActiveVoiceCount);
            for (int i = 0; i < audio->MaxVoices; i++)
            {
                if (audio->Voices[i].State != AudioVoiceState.Inactive &&
                    (bus == AudioBus.Master || audio->Voices[i].Bus == bus))
                {
                    audio->Voices[i].State = AudioVoiceState.Inactive;
                    BumpGen(ref audio->VoiceGens[i]);
                    if (active > 0) active--;
                }
            }
            Volatile.Write(ref audio->ActiveVoiceCount, active);
        }
        finally
        {
            ReleaseLock(audio);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StopAll(AudioState* audio) => StopBus(audio, AudioBus.Master);

    public static void SetVoicePaused(AudioState* audio, AudioVoiceHandle voice, bool paused)
    {
        if (audio == null || Volatile.Read(ref audio->Active) == 0 || !voice.IsValid) return;
        uint idx = voice.Index;
        if (idx >= (uint)audio->MaxVoices) return;

        AcquireLock(audio);
        try
        {
            if (Volatile.Read(ref audio->Active) == 0) return;
            if (audio->VoiceGens[idx] == voice.Generation && audio->Voices[idx].State != AudioVoiceState.Inactive)
                audio->Voices[idx].State = paused ? AudioVoiceState.Paused : AudioVoiceState.Playing;
        }
        finally
        {
            ReleaseLock(audio);
        }
    }

    public static void SetVoiceVolume(AudioState* audio, AudioVoiceHandle voice, float volume)
    {
        if (audio == null || Volatile.Read(ref audio->Active) == 0 || !voice.IsValid) return;
        uint idx = voice.Index;
        if (idx >= (uint)audio->MaxVoices) return;

        float v = float.IsFinite(volume) ? DetMath.Clamp(volume, 0f, 4f) : 0f;
        AcquireLock(audio);
        try
        {
            if (Volatile.Read(ref audio->Active) == 0) return;
            if (audio->VoiceGens[idx] == voice.Generation && audio->Voices[idx].State != AudioVoiceState.Inactive)
                audio->Voices[idx].Volume = v;
        }
        finally
        {
            ReleaseLock(audio);
        }
    }

    public static void SetVoicePitch(AudioState* audio, AudioVoiceHandle voice, float pitch)
    {
        if (audio == null || Volatile.Read(ref audio->Active) == 0 || !voice.IsValid) return;
        uint idx = voice.Index;
        if (idx >= (uint)audio->MaxVoices) return;

        float p = float.IsFinite(pitch) ? DetMath.Clamp(pitch, 0.125f, 4f) : 1f;
        AcquireLock(audio);
        try
        {
            if (Volatile.Read(ref audio->Active) == 0) return;
            if (audio->VoiceGens[idx] == voice.Generation && audio->Voices[idx].State != AudioVoiceState.Inactive)
                audio->Voices[idx].Pitch = p;
        }
        finally
        {
            ReleaseLock(audio);
        }
    }

    public static void SetVoicePan(AudioState* audio, AudioVoiceHandle voice, float pan)
    {
        if (audio == null || Volatile.Read(ref audio->Active) == 0 || !voice.IsValid) return;
        uint idx = voice.Index;
        if (idx >= (uint)audio->MaxVoices) return;

        float p = float.IsFinite(pan) ? DetMath.Clamp(pan, -1f, 1f) : 0f;
        AcquireLock(audio);
        try
        {
            if (Volatile.Read(ref audio->Active) == 0) return;
            if (audio->VoiceGens[idx] == voice.Generation && audio->Voices[idx].State != AudioVoiceState.Inactive)
                audio->Voices[idx].Pan = p;
        }
        finally
        {
            ReleaseLock(audio);
        }
    }

    public static AudioVoiceState GetVoiceState(AudioState* audio, AudioVoiceHandle voice)
    {
        if (audio == null || Volatile.Read(ref audio->Active) == 0 || !voice.IsValid) return AudioVoiceState.Inactive;
        uint idx = voice.Index;
        if (idx >= (uint)audio->MaxVoices) return AudioVoiceState.Inactive;

        AcquireLock(audio);
        try
        {
            if (Volatile.Read(ref audio->Active) == 0) return AudioVoiceState.Inactive;
            if (audio->VoiceGens[idx] != voice.Generation) return AudioVoiceState.Inactive;
            return audio->Voices[idx].State;
        }
        finally
        {
            ReleaseLock(audio);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsVoicePlaying(AudioState* audio, AudioVoiceHandle voice)
        => GetVoiceState(audio, voice) == AudioVoiceState.Playing;

    // ==================== Volume Buses & Global Pause ====================

    public static void SetBusVolume(AudioState* audio, AudioBus bus, float volume)
    {
        if (audio == null || (byte)bus >= (byte)AudioBus.Count) return;
        float v = float.IsFinite(volume) ? DetMath.Clamp(volume, 0f, 2f) : 0f;
        Volatile.Write(ref audio->BusVolumes[(int)bus], v);
    }

    public static float GetBusVolume(AudioState* audio, AudioBus bus)
    {
        if (audio == null || (byte)bus >= (byte)AudioBus.Count) return 0f;
        return Volatile.Read(ref audio->BusVolumes[(int)bus]);
    }

    public static void SetBusMuted(AudioState* audio, AudioBus bus, bool muted)
    {
        if (audio == null || (byte)bus >= (byte)AudioBus.Count) return;
        Volatile.Write(ref audio->BusMuted[(int)bus], (byte)(muted ? 1 : 0));
    }

    public static bool IsBusMuted(AudioState* audio, AudioBus bus)
    {
        if (audio == null || (byte)bus >= (byte)AudioBus.Count) return true;
        return Volatile.Read(ref audio->BusMuted[(int)bus]) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetMasterVolume(AudioState* audio, float volume) => SetBusVolume(audio, AudioBus.Master, volume);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetMasterMuted(AudioState* audio, bool muted) => SetBusMuted(audio, AudioBus.Master, muted);

    /// <summary>
    /// Pauses or resumes all audio playback globally (ideal when InputState.IsWindowFocused becomes false on Alt-Tab).
    /// </summary>
    public static void SetPaused(AudioState* audio, bool paused)
    {
        if (audio == null || Volatile.Read(ref audio->Active) == 0) return;
        AcquireLock(audio);
        try
        {
            if (Volatile.Read(ref audio->Active) == 0) return;
            Volatile.Write(ref audio->GlobalPaused, (byte)(paused ? 1 : 0));
            if (audio->DeviceId != 0)
            {
                if (paused) SDL.PauseAudioDevice(audio->DeviceId);
                else SDL.ResumeAudioDevice(audio->DeviceId);
            }
        }
        finally
        {
            ReleaseLock(audio);
        }
    }

    public static bool IsPaused(AudioState* audio) => audio == null || Volatile.Read(ref audio->GlobalPaused) != 0;
    public static int GetActiveVoiceCount(AudioState* audio) => audio == null ? 0 : Volatile.Read(ref audio->ActiveVoiceCount);
    public static int GetLoadedClipCount(AudioState* audio) => audio == null ? 0 : Volatile.Read(ref audio->LoadedClipCount);

    // ==================== Deterministic Software Mixer ====================

    /// <summary>
    /// Mixes <paramref name="frameCount"/> interleaved Float32 frames into <paramref name="outInterleaved"/> (0 GC).
    /// Used automatically by BringEngine.AudioMixer for SDL3 playback, and directly callable in headless tests
    /// to verify mixing, pitch resampling, bus gain, panning, looping, and voice completion deterministically.
    /// </summary>
    public static void RenderMixFrames(AudioState* audio, float* outInterleaved, int frameCount)
    {
        if (audio == null || outInterleaved == null || frameCount <= 0) return;

        int outCh = audio->Channels;
        if (outCh <= 0) return;
        long totalOutSamplesLong = (long)frameCount * outCh;
        if (totalOutSamplesLong > int.MaxValue) return;
        int totalOutSamples = (int)totalOutSamplesLong;
        new Span<float>(outInterleaved, totalOutSamples).Clear();

        if (Volatile.Read(ref audio->Active) == 0 || Volatile.Read(ref audio->GlobalPaused) != 0)
            return;

        AcquireLock(audio);
        try
        {
            if (Volatile.Read(ref audio->Active) == 0 || Volatile.Read(ref audio->GlobalPaused) != 0)
                return;

            float masterVolRaw = Volatile.Read(ref audio->BusMuted[(int)AudioBus.Master]) != 0 ? 0f : Volatile.Read(ref audio->BusVolumes[(int)AudioBus.Master]);
            float masterVol = float.IsFinite(masterVolRaw) ? masterVolRaw : 0f;
            int outRate = audio->SampleRate;

            for (int v = 0; v < audio->MaxVoices; v++)
            {
                ref AudioVoiceSlot voice = ref audio->Voices[v];
                if (voice.State != AudioVoiceState.Playing) continue;

                int cIdx = voice.ClipIndex;
                if ((uint)cIdx >= (uint)audio->MaxClips ||
                    audio->Clips[cIdx].Alive == 0 ||
                    audio->ClipGens[cIdx] != voice.ClipGeneration)
                {
                    voice.State = AudioVoiceState.Inactive;
                    BumpGen(ref audio->VoiceGens[v]);
                    int active = Volatile.Read(ref audio->ActiveVoiceCount);
                    if (active > 0) Volatile.Write(ref audio->ActiveVoiceCount, active - 1);
                    continue;
                }

                ref AudioClipSlot clip = ref audio->Clips[cIdx];
                if (clip.FrameCount <= 0 || clip.Samples == null)
                {
                    voice.State = AudioVoiceState.Inactive;
                    BumpGen(ref audio->VoiceGens[v]);
                    int active = Volatile.Read(ref audio->ActiveVoiceCount);
                    if (active > 0) Volatile.Write(ref audio->ActiveVoiceCount, active - 1);
                    continue;
                }

                int busIdx = (int)voice.Bus;
                float busVolRaw = ((uint)busIdx < (uint)AudioBus.Count && Volatile.Read(ref audio->BusMuted[busIdx]) == 0)
                    ? Volatile.Read(ref audio->BusVolumes[busIdx])
                    : 0f;
                float busVol = float.IsFinite(busVolRaw) ? busVolRaw : 0f;
                float voiceVol = float.IsFinite(voice.Volume) ? voice.Volume : 0f;
                float gain = masterVol * (busIdx == (int)AudioBus.Master ? 1f : busVol) * voiceVol;
                if (!float.IsFinite(gain)) gain = 0f;

                double step = ((double)clip.SampleRate / outRate) * voice.Pitch;
                if (!double.IsFinite(step) || step <= 0.0) step = 1.0;

                // Equal-power stereo panning (pan = 0 -> 1.0 unity each for mono; pan = +/-1 -> 1.4142 / 0.0)
                float voicePan = float.IsFinite(voice.Pan) ? DetMath.Clamp(voice.Pan, -1f, 1f) : 0f;
                float pan01 = (voicePan + 1f) * 0.5f; // [0, 1]
                float panL, panR;
                if (clip.Channels == 1)
                {
                    // Center pan (0.0) outputs unity 1.0 on both channels; hard left/right scales opposite side to 0
                    float angle = pan01 * DetMath.HalfPi;
                    panL = DetMath.Cos(angle) * 1.41421356f;
                    panR = DetMath.Sin(angle) * 1.41421356f;
                }
                else
                {
                    // Balance control for stereo clips: pan=0 -> (1, 1), pan=-1 -> (1, 0), pan=+1 -> (0, 1)
                    panL = voicePan > 0f ? (1f - voicePan) : 1f;
                    panR = voicePan < 0f ? (1f + voicePan) : 1f;
                }

                float gainL = gain * panL;
                float gainR = gain * panR;
                double playhead = voice.PlayheadFrame;
                if (!double.IsFinite(playhead) || playhead < 0.0) playhead = 0.0;
                int clipFrames = clip.FrameCount;
                int clipCh = clip.Channels;
                float* src = clip.Samples;
                bool loop = voice.Loop != 0;
                bool finished = false;

                if (gain <= 1e-6f)
                {
                    // Advance playhead even when muted so music/sfx stay in sync when unmuted
                    playhead += step * frameCount;
                    if (playhead >= clipFrames)
                    {
                        if (loop) playhead %= clipFrames;
                        else finished = true;
                    }
                }
                else
                {
                    for (int f = 0; f < frameCount; f++)
                    {
                        if (playhead >= clipFrames)
                        {
                            if (loop)
                            {
                                playhead %= clipFrames;
                            }
                            else
                            {
                                finished = true;
                                break;
                            }
                        }

                        int i0 = (int)playhead;
                        float frac = (float)(playhead - i0);
                        int i1 = i0 + 1;
                        if (i1 >= clipFrames) i1 = loop ? 0 : (clipFrames - 1);

                        float sL, sR;
                        if (clipCh == 1)
                        {
                            float s = src[i0] + (src[i1] - src[i0]) * frac;
                            sL = s;
                            sR = s;
                        }
                        else
                        {
                            int o0 = i0 * 2;
                            int o1 = i1 * 2;
                            sL = src[o0] + (src[o1] - src[o0]) * frac;
                            sR = src[o0 + 1] + (src[o1 + 1] - src[o0 + 1]) * frac;
                        }

                        if (outCh == 2)
                        {
                            int dstIdx = f * 2;
                            outInterleaved[dstIdx + 0] += sL * gainL;
                            outInterleaved[dstIdx + 1] += sR * gainR;
                        }
                        else
                        {
                            outInterleaved[f] += (sL + sR) * 0.5f * gain;
                        }

                        playhead += step;
                    }
                }

                if (finished)
                {
                    voice.State = AudioVoiceState.Inactive;
                    BumpGen(ref audio->VoiceGens[v]);
                    int active = Volatile.Read(ref audio->ActiveVoiceCount);
                    if (active > 0) Volatile.Write(ref audio->ActiveVoiceCount, active - 1);
                }
                else
                {
                    voice.PlayheadFrame = playhead;
                }
            }

            // Clamp final mix to [-1, 1] and sanitize non-finite samples
            for (int i = 0; i < totalOutSamples; i++)
            {
                float x = outInterleaved[i];
                if (!float.IsFinite(x)) outInterleaved[i] = 0f;
                else if (x > 1f) outInterleaved[i] = 1f;
                else if (x < -1f) outInterleaved[i] = -1f;
            }

            audio->MixedFramesTotal += (ulong)frameCount;
        }
        finally
        {
            ReleaseLock(audio);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AcquireLock(AudioState* audio)
    {
        if (Interlocked.CompareExchange(ref audio->SpinLock, 1, 0) == 0) return;
        int spins = 0;
        while (Interlocked.CompareExchange(ref audio->SpinLock, 1, 0) != 0)
        {
            if (++spins < 32) Thread.SpinWait(16);
            else Thread.Yield();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReleaseLock(AudioState* audio)
    {
        Volatile.Write(ref audio->SpinLock, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BumpGen(ref uint gen)
    {
        gen = (gen + 1u) & HandlePacking.GenerationMask;
        if (gen == 0) gen = 1;
    }
}
