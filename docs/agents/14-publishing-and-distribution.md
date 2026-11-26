# Agent Guide 14 — Cross-Platform Building, Publishing & Distribution

> **Scope**: Building, cross-OS publishing, packaging, signing, and distributing games built on **Bring Engine** for **Windows**, **Linux (including Steam Deck / SteamOS)**, and **macOS** — including how to publish all targets from a single Windows (or Linux) PC without owning a Mac.

---

## 1. Runtime Model — Desktop CoreCLR (.NET 10) + ReadyToRun

Bring Engine games run on **Desktop CoreCLR (.NET 10)** — **not** NativeAOT and **not** Mono:

- `Directory.Build.props` sets `<TargetFramework>net10.0</TargetFramework>`.
- Game `.csproj` files enable **Self-Contained Single-File ReadyToRun** whenever a `RuntimeIdentifier` (`-r`) is supplied:
  ```xml
  <PropertyGroup Condition="'$(RuntimeIdentifier)' != ''">
    <SelfContained>true</SelfContained>
    <PublishSingleFile>true</PublishSingleFile>
    <PublishReadyToRun>true</PublishReadyToRun>
  </PropertyGroup>
  ```
- **Dev iteration**: `dotnet build` / `dotnet run` compiles in 1–2 seconds with full NuGet compatibility and Dynamic PGO JIT optimization.
- **Release builds**: `dotnet publish -c Release -r <rid>` bundles the .NET 10 CoreCLR runtime + AOT-precompiled ReadyToRun (`crossgen2`) code into a single native executable alongside the platform's SDL3 native libraries, shaders, and `.bpack` assets. Players need **no `.NET` install**.
- **Automatic cross-platform native libraries & shaders**:
  - `src/BringEngine/BringEngine.csproj` conditionally pulls the matching native NuGet packages based on `RuntimeIdentifier`:
    - `win-x64` / `win-arm64` → `SDL3-CS.Windows` + `SDL3-CS.Windows.Shadercross`
    - `linux-x64` / `linux-arm64` → `SDL3-CS.Linux` + `SDL3-CS.Linux.Shadercross`
    - `osx-arm64` / `osx-x64` → `SDL3-CS.MacOS` + `SDL3-CS.MacOS.Shadercross`
  - `ShaderSystem` compiles HLSL shaders at runtime via `SDL_shadercross` to **DXIL** (Windows D3D12), **SPIR-V** (Linux / Steam Deck Vulkan), or **MSL** (macOS Metal).

---

## 2. Pre-Publish Cross-Platform Checklist

Before publishing release builds:
1. **Pack all raw assets into compressed `.bpack` bundles** (`docs/agents/13-asset-compression-atlases-and-9slice.md`):
   ```bash
   dotnet run --project "$sdk/tools/BringPacker/BringPacker.csproj" -c Release -- \
     --input raw_assets/common --output assets/common.bpack --compression brotli
   ```
2. **Keep paths OS-agnostic and case-exact**:
   - Always use `Path.Combine(AppContext.BaseDirectory, ...)` — never hardcoded backslashes `\` or drive paths.
   - Linux filesystems are **case-sensitive**: engine built-in shaders live in `Shaders/` (`pbr_mesh.hlsl`, `sprite_3d.hlsl`, `sdf_text.hlsl`, `particle.hlsl`), while game shaders/assets typically live in lowercase `assets/shaders/` and `assets/`. Match exact casing everywhere.
   - Avoid Windows-only P/Invokes (`kernel32.dll`, `user32.dll`, etc.) — use `SDL3` (`SDL3-CS`) and standard cross-platform .NET APIs.
3. **Verify zero loose textures in the published folder**:
   ```bash
   dotnet run --project "$sdk/tools/BringPacker/BringPacker.csproj" -c Release -- \
     --verify-no-loose-textures dist
   ```

---

## 3. Cross-OS Publishing Commands (All Targets From One Machine)

.NET 10's `crossgen2` supports cross-OS ReadyToRun publishing with **zero C++ toolchain required**:
- From **Windows `x64`**: publish `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-arm64`, and `osx-x64`.
- From **Linux `x64` / `arm64`** or **macOS**: publish `linux-x64`, `linux-arm64`, `osx-arm64`, and `osx-x64`.

```powershell
# Windows (x64)
dotnet publish src/<Game>.csproj -c Release -r win-x64 -o dist/win-x64

# Linux & Steam Deck / SteamOS (x64)
dotnet publish src/<Game>.csproj -c Release -r linux-x64 -o dist/linux-x64

# macOS Apple Silicon (M1/M2/M3/M4+)
dotnet publish src/<Game>.csproj -c Release -r osx-arm64 -o dist/osx-arm64

# Optional: macOS Intel (older Macs)
dotnet publish src/<Game>.csproj -c Release -r osx-x64 -o dist/osx-x64
```

> **Optional — strip `.pdb` debug symbols** for cleaner end-user builds:
> append `-p:DebugType=none -p:DebugSymbols=false`.

---

## 4. Platform-Specific Release & Packaging Notes

### Windows (`win-x64`)
- Ships directly from `dist/win-x64/` (`<Game>.exe`, `SDL3.dll`, `SDL3_shadercross.dll`, `dxcompiler.dll`, `dxil.dll`, `spirv-cross-c-shared.dll`, `Shaders/`, `assets/`, `game.json`).
- Double-clicking `<Game>.exe` launches directly into Borderless Full Window at 60 FPS with VSync ON.

### Linux & Steam Deck / SteamOS (`linux-x64`)
- **Requires `glibc 2.28+`** (covers Steam Deck / SteamOS and all modern Linux distros).
- **Preserve the Unix executable bit (`+x`)**:
  - Windows zip tools do **not** store Unix file permissions. If you zip `dist/linux-x64` with Windows Explorer or `Compress-Archive`, Linux users will need to run `chmod +x <Game>`.
  - Fix: package with `tar` / Python `tarfile` / 7-Zip setting mode `0755` on `<Game>`, or upload via **SteamPipe** / **itch.io `butler`** (which mark the launch binary executable automatically).
- **Deduplicate versioned `.so` copies when publishing from Windows**:
  - Windows NuGet unpacks Unix symlinks (`libSDL3.so`, `libSDL3.so.0`, `libSDL3.so.0.4.20`, `libspirv-cross-c-shared.so*`, `libvkd3d*.so*`) as separate full copies (~23 MB extra).
  - Converting identical versioned `.so` duplicates back into symlinks when building a `.tar.gz` or Steam depot saves **~23 MB** of install size.

### macOS (`osx-arm64` / `osx-x64`) — Publishing Without Owning a Mac
You can **compile, sign, and notarize** a macOS build without owning a physical Mac, with a few Apple-enforced requirements:

1. **Built-in `.NET 10` ad-hoc Mach-O signing (works on Windows/Linux)**:
   - Apple Silicon (`osx-arm64`) strictly requires every Mach-O executable to have a code signature (otherwise macOS immediately kills it with `SIGKILL`).
   - `.NET 10`'s `Microsoft.NET.HostModel` embeds a valid ad-hoc `LC_CODE_SIGNATURE` directly on Windows and Linux during `dotnet publish -r osx-arm64`.
2. **Wrap in a `.app` bundle & deduplicate `.dylib` copies**:
   - Package the published output into `<Game>.app/Contents/MacOS/` with a minimal `<Game>.app/Contents/Info.plist` (`CFBundleExecutable`, `CFBundleIdentifier`, `CFBundleName`, `NSHighResolutionCapable=true`).
   - Because Bring Engine resolves `game.json`, `Shaders/`, and `assets/` relative to `AppContext.BaseDirectory`, placing them inside `Contents/MacOS/` alongside `<Game>` and the `.dylib`s works with zero code changes.
   - As on Linux, NuGet unpacks versioned `.dylib` symlinks as full copies on Windows; deduplicating them saves **~12 MB**.
3. **Gatekeeper & Notarization for public distribution**:
   - An ad-hoc signature is **not** trusted by macOS Gatekeeper for internet downloads (itch.io, direct web links) — players will see an "unverified / damaged" warning unless they bypass quarantine (`xattr -cr`).
   - Passing Gatekeeper requires a paid **Apple Developer Program** membership ($99/year, `Developer ID Application` certificate) regardless of what OS you build on.
   - **Signing & notarizing without a Mac**:
     - Use the cross-platform Rust tool **[`rcodesign`](https://gregoryszorc.com/docs/apple-codesign/main/)** (`cargo install apple-codesign`) directly from Windows/Linux to sign the `.app` bundle with Hardened Runtime, submit to Apple's Notary API, and staple the ticket — **or** run native `codesign` / `xcrun notarytool` on a GitHub Actions `macos-latest` runner.
   - **Required CoreCLR Hardened Runtime entitlements (`entitlements.plist`)**:
     ```xml
     <?xml version="1.0" encoding="UTF-8"?>
     <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
     <plist version="1.0">
     <dict>
       <key>com.apple.security.cs.allow-jit</key><true/>
       <key>com.apple.security.cs.allow-unsigned-executable-memory</key><true/>
       <key>com.apple.security.cs.disable-library-validation</key><true/>
     </dict>
     </plist>
     ```
     *(CoreCLR JIT needs `allow-jit` + `allow-unsigned-executable-memory`; `disable-library-validation` allows loading bundled `.dylib`s unless you re-sign every `.dylib` with the same Developer ID Team ID.)*
4. **Runtime verification**:
   - Cross-publishing produces valid binaries, but you still cannot visually test Metal rendering, `SDL_shadercross` MSL shader translation, macOS audio, or fullscreen behavior from Windows alone — verify on a Mac, with a macOS tester, or via macOS CI before public launch.

---

## 5. Typical Shipped Build Sizes (Uncompressed Baseline)

Over 95% of a small/medium game's shipped size is the self-contained .NET 10 CoreCLR runtime + SDL3/DXC/SPIRV-Cross shader compiler libraries, while compressed `.bpack` assets stay tiny:

| Target RID | Engine + Runtime + Native Libs | Notes |
|---|---|---|
| `win-x64` | **~107 MB** + your assets | ~83 MB CoreCLR R2R single-file `.exe` + ~23 MB native DLLs (`dxcompiler.dll`, `SDL3.dll`, `spirv-cross`, `dxil.dll`, `SDL3_shadercross.dll`) |
| `linux-x64` | **~151 MB** raw (**~128 MB** symlinked) + your assets | ~84 MB CoreCLR R2R binary + ~67 MB `.so` libs (~23 MB saved by symlinking duplicates) |
| `osx-arm64` | **~143 MB** raw (**~131 MB** symlinked) + your assets | ~94 MB CoreCLR R2R binary + ~49 MB `.dylib` libs (~12 MB saved by symlinking duplicates) |
