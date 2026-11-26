# Agent Guide 19 — Core Audio System (`BringEngine.Audio`)

Bring Engine includes a **zero-OOP, zero-GC, zero-extra-dependency** core audio mixer (`BringEngine.Audio`) built directly on SDL3's native `SDL_AudioStream` (no extra native libraries — ships with SDL3 across Windows, Linux / Steam Deck, and macOS).

---

## 1. Architecture & Guarantees
- **Dedicated Background Mixer Thread (`BringEngine.AudioMixer`)**: Continuously software-mixes active voices into a single SDL3 `F32LE` playback stream (~32 ms buffered) so gameplay and rendering never stall.
- **Zero-GC Hot Path**: `Play`, `PlayOneShot`, `PlayLoop`, `StopVoice`, `SetVoice*`, `SetBusVolume`, `SetBusMuted`, `SetPaused`, and `RenderMixFrames` allocate **0 bytes** on the managed GC heap.
- **Zero Native Leaks**: All PCM clip buffers and mix scratch buffers are 64-byte aligned via `NativeMemoryTracker` and freed automatically on `UnloadClip` or `AudioSystem.Stop`.
- **Headless / CI Safe by Default**: `AudioConfig.Default` has `HeadlessFallback = 1`. If the OS/CI container has no physical audio output device, `AudioSystem` logs a `[WARN]` and runs cleanly in software/headless mode without throwing. Use `AudioConfig.Headless` (`ForceHeadless = 1`) in deterministic unit tests.
- **32-bit Generational Handles**: `AudioClipHandle` and `AudioVoiceHandle` reject stale handles after `UnloadClip`, `StopVoice`, completion, or voice stealing.

---

## 2. Startup & Shutdown
Allocate `AudioState*` from `EngineAllocators.Persistent` (passing optional `LogState*` for lifecycle/warning logs):

```csharp
using BringEngine.Audio;

AudioState* audio = AudioSystem.Start(ref alloc.Persistent, AudioConfig.Default, log);

// ... load clips, play audio during gameplay ...

// On shutdown (before alloc.Dispose()):
AudioSystem.Stop(audio);
```

---

## 3. Loading Audio Clips (WAV, `.bpack`, Raw PCM, Procedural)
Built-in zero-dependency `WavDecoder` supports **8/16/24/32-bit PCM** and **32-bit IEEE Float** RIFF/WAVE files (mono, stereo, or multi-channel downmixed to stereo).

```csharp
// Option A: From a compressed .bpack bundle (RECOMMENDED for shipped games — Brotli/LZ4 compressed!)
// Place raw .wav files in raw_assets/<bundle>/audio/ -> BringPacker packs them as compressed RawBlobs
AudioClipHandle hitSfx = AudioSystem.LoadClipFromBpack(
    audio, "assets/common.bpack", StringId.Hash("audio/hit.wav"), ref alloc.Scratch);

// Option B: Direct from disk or memory
AudioClipHandle bgm = AudioSystem.LoadClipFromWavFile(audio, "Assets/music.wav", StringId.Hash("bgm"));
AudioClipHandle uiClick = AudioSystem.LoadClipFromWavMemory(audio, wavBytes, StringId.Hash("click"));

// Option C: Procedural or custom-decoded PCM (e.g., if your game plugs in NVorbis for OGG streaming)
DecodedPcmData tone = WavDecoder.GenerateSineWave(frequencyHz: 440f, durationSeconds: 0.12f, sampleRate: 48000, amplitude: 0.5f, channels: 1);
AudioClipHandle beep = AudioSystem.CreateClipFromDecoded(audio, tone, StringId.Hash("beep"));

// Unload clip when a scene/bundle unloads (automatically stops voices using that clip and frees native RAM)
AudioSystem.UnloadClip(audio, hitSfx);
```

> **Need OGG/MP3 streaming?** Keep core engine lean — add a pure-C# decoder NuGet (e.g., `NVorbis`) directly to `<YourGame>.csproj`, decode into interleaved `float*`, and pass to `AudioSystem.CreateClipFromPcm(...)`.

---

## 4. Zero-GC Playback, Pitch, Pan & Volume Buses
Audio runs cleanly in `View/` (reacting to `SimEvent`s — **never** call `AudioSystem` from deterministic `Simulation/`):

```csharp
// Fire-and-forget one-shot SFX with pitch variation & stereo pan (-1 = left, 0 = center, +1 = right)
AudioSystem.PlayOneShot(audio, hitSfx, volume: 0.85f, pitch: 0.95f + rng * 0.1f, pan: -0.3f, bus: AudioBus.Sfx);

// Looping background music (preserved during voice stealing when MaxVoices is saturated!)
AudioVoiceHandle musicVoice = AudioSystem.PlayLoop(audio, bgm, volume: 0.6f, pitch: 1.0f, bus: AudioBus.Music);

// Live voice adjustments (all 0 GC)
AudioSystem.SetVoiceVolume(audio, musicVoice, 0.35f);
AudioSystem.SetVoicePitch(audio, musicVoice, 1.0f);
AudioSystem.SetVoicePan(audio, musicVoice, 0.0f);
AudioSystem.SetVoicePaused(audio, musicVoice, paused: true);
AudioSystem.StopVoice(audio, musicVoice);

// 4 built-in volume & mute buses: Master, Music, Sfx, Ui (bind directly to your Settings menu!)
AudioSystem.SetMasterVolume(audio, 0.9f);
AudioSystem.SetBusVolume(audio, AudioBus.Music, 0.65f);
AudioSystem.SetBusVolume(audio, AudioBus.Sfx, 1.0f);
AudioSystem.SetBusVolume(audio, AudioBus.Ui, 0.8f);
AudioSystem.SetBusMuted(audio, AudioBus.Music, false);
```

### Auto-Pause Audio on Alt-Tab / Focus Loss
Pair `AudioSystem.SetPaused` with `InputState` focus edges:
```csharp
if (input.WasWindowFocusLost)  AudioSystem.SetPaused(audio, true);
if (input.WasWindowFocusGained) AudioSystem.SetPaused(audio, false);
```

---

## 5. Deterministic Headless Testing
In unit tests or CI containers, use `AudioConfig.Headless` (`ForceHeadless = 1`) and drive the exact same software mixer deterministically via `AudioSystem.RenderMixFrames(audio, outBuf, frameCount)` — zero OS audio hardware required!
