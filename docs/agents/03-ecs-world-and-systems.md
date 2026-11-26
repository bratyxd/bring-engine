# Agent Guide 03 — Data-Driven ECS World, Transforms & Input (Keyboard, Mouse & SDL3 Gamepads)

## Entity Lifecycle (`EntityTable` + `EntitySystem`)
Entities are lightweight 32-bit generational handles (`EntityId`), tagged with their owning `SceneId`.
```csharp
EntityId player = EntitySystem.Spawn(ref world.Entities, sceneId);
bool alive = EntitySystem.IsAlive(in world.Entities, player);
EntitySystem.Destroy(ref world.Entities, player);
```
- Dense iteration over all alive entities: `for (int i = 0; i < entities.AliveCount; i++) { EntityId e = entities.DenseAlive[i]; ... }`
- Scene cleanup: `EntitySystem.CollectByScene(in entities, sceneId, ref frameArena)` returns all entities owned by a scene with zero GC allocations.

## SoA Transform Hierarchy (`TransformTable` + `TransformSystem`)
Transforms are stored as 64-byte aligned Structure-of-Arrays (`LocalPosition`, `LocalRotation`, `LocalScale`, `Parent`, `WorldMatrix`, `WorldPosition`).
```csharp
TransformSystem.Add(ref world.Transforms, parent, new Vec3(0, 1, 0), Quat.Identity, Vec3.One);
TransformSystem.Add(ref world.Transforms, child, new Vec3(2, 0, 0), Quat.Identity, Vec3.One, parent);

// Once per fixed step / frame before rendering:
TransformSystem.PropagateHierarchy(ref world.Transforms);
Vec3 childWorldPos = TransformSystem.GetWorldPosition(in world.Transforms, child);
```
`PropagateHierarchy` runs linearly by hierarchy depth (0, 1, 2, ...) with zero recursion and zero allocations.

## Adding Custom Game Component Tables
Follow the exact same pattern in your game project:
1. Create a `NativeSparseSet<MyComponent>` (or SoA struct) allocated from `GameAllocators.Persistent` or `GameSceneArena`.
2. Iterate `0..set.Count` densely in `public static unsafe class MyGameSystem`.
3. Remove components cleanly when destroying an entity or unloading a scene.

## Platform-Agnostic `InputState` (Keyboard, Mouse & Up to 4 SDL3 Gamepads)
`InputState` is a single flat unmanaged struct (zero pointers, zero GC) that copies atomically across the 2-thread pipeline. In windowed mode, `PlatformSystem.PollEvents` populates keyboard, mouse, and all connected SDL3 gamepads (Xbox, PlayStation, Nintendo, and **Steam Deck** / Steam Input, including hot-plug and Steam Deck's 4 back grip paddles `LeftPaddle1`, `LeftPaddle2`, `RightPaddle1`, `RightPaddle2` and `Touchpad` click).

### Unified Keyboard + Controller Movement (Desktop & Steam Deck Ready!)
```csharp
// Combines WASD / Arrow keys + Gamepad Left Stick (scaled radial deadzone) + D-Pad, clamped to length <= 1.0
Vec2 move = input.GetMoveAxis(gamepadSlot: 0, deadzone: 0.15f);

// Direct gamepad queries (zero SDL references — 100% Simulation-safe & headless-testable!)
bool jump    = input.IsKeyPressed(KeyCode.Space) || input.IsGamepadButtonPressed(GamepadButton.South);
bool dash    = input.IsKeyPressed(KeyCode.LeftShift) || input.IsGamepadButtonPressed(GamepadButton.East)
               || input.IsGamepadButtonPressed(GamepadButton.LeftPaddle1);
bool pause   = input.IsKeyPressed(KeyCode.Escape) || input.IsAnyGamepadButtonPressed(GamepadButton.Start);
Vec2 aim     = input.GetGamepadRightStick(slot: 0, deadzone: 0.15f);
float fire   = input.GetGamepadRightTrigger(slot: 0); // [0, 1]
```

### Complete Keyboard, Mouse Release, Focus/Minimize, Cursor Lock & Text Input
- **Full `KeyCode` coverage**: Includes `A`–`Z`, `Num0`–`Num9`, `F1`–`F12`, arrows, `LeftShift`/`RightShift`, `LeftCtrl`/`RightCtrl`, `LeftAlt`/`RightAlt`, `LeftSuper`/`RightSuper`, `Delete`/`Insert`/`Home`/`End`/`PageUp`/`PageDown`, punctuation (`Minus`, `Equals`, `LeftBracket`, `RightBracket`, `Backslash`, `Semicolon`, `Apostrophe`, `Grave`, `Comma`, `Period`, `Slash`), and `Kp0`–`Kp9`/`KpEnter`.
- **Mouse buttons**: `input.IsMousePressed(btn)`, `input.IsMouseDown(btn)`, and `input.IsMouseReleased(btn)` (`0` = left, `1` = middle, `2` = right).
- **Window Focus & Minimize** (safe defaults in headless tests: `IsWindowFocused == true`, `IsWindowMinimized == false`):
```csharp
// Auto-pause, release cursor lock, and mute audio on Alt-Tab / Steam Deck overlay
if (input.WasWindowFocusLost)
{
    PlatformSystem.SetMouseLock(ref window, false); // Main Thread only
    AudioSystem.SetPaused(audio, true);
}
else if (input.WasWindowFocusGained)
{
    AudioSystem.SetPaused(audio, false);
}
bool pausedByOs = !input.IsWindowFocused || input.IsWindowMinimized;
```
- **Cursor Lock (Relative Mouse Mode) & Visibility** (Main Thread ONLY):
```csharp
// Lock mouse for 3D FPS / orbit camera (hides cursor & delivers raw MouseDeltaX/Y)
PlatformSystem.SetMouseLock(ref window, locked: true);
// Or show/hide OS cursor without locking (custom software cursor)
PlatformSystem.SetCursorVisible(ref window, visible: false);
```
- **Zero-GC Unicode Text Input** (for player names, chat, lobby codes, IP addresses):
```csharp
// Main Thread: turn on OS/IME text input when a text field gains focus
PlatformSystem.StartTextInput(ref window);
// ... later when leaving the text field:
PlatformSystem.StopTextInput(ref window);

// Logic / View Thread (0 GC): read this frame's UTF-8/UTF-16 typed characters
if (input.HasTextInput)
{
    Span<char> chars = stackalloc char[InputState.MaxTextInputBytes];
    int count = input.CopyTextInputChars(chars); // or input.CopyTextInputUtf8(byteSpan)
    // Append chars[..count] to your FixedString / text input buffer
}
```

### Controller Haptics / Rumble (Main Thread / View Only)
Trigger rumble on the Main Thread (never in `Simulation/` or the Logic Thread) when draining `SimEvent` feedback:
```csharp
// Dual-motor rumble (low/heavy left motor, high/sharp right motor, duration in ms)
PlatformSystem.RumbleGamepad(ref window, slot: 0, lowFreq01: 0.6f, highFreq01: 0.8f, durationMs: 140);

// Optional impulse triggers (Xbox controllers)
PlatformSystem.RumbleGamepadTriggers(ref window, slot: 0, left01: 0.3f, right01: 0.7f, durationMs: 100);
```

### Deterministic Headless Testing & Synthetic Replay
```csharp
InputState input = default;
input.SetGamepadConnected(0, true, instanceId: 1);
input.BeginFrame();
input.SetKey(KeyCode.W, true);
input.SetGamepadButton(0, GamepadButton.South, true);
input.SetGamepadAxis(0, GamepadAxis.LeftX, 0.75f); // +X = Right, +Y = Up
```
