# Agent Guide 15 — Engine Development, CI-Only Testing & Releasing

> **Scope**: Rules and workflows for modifying **Bring Engine** itself (`bratyxd/bring-engine`), running CI verification on GitHub Actions, and publishing both **code releases** and **fast docs-only releases**.

---

## 1. When to Change the Engine (vs. Game Code)

Games consume released SDK archives (`bring-sdk-vX.Y.Z.zip`) from GitHub Releases rather than copying engine sources:
- Only modify Bring Engine when it has a **bug, crash, violated contract, or missing general capability** a game cannot reasonably build on its own.
- **Nothing game-specific** goes into the engine.
- **Never patch** files inside a downloaded `~/.bring/sdk/<tag>/` cache folder — always edit in the `bratyxd/bring-engine` repository and publish a new release.

---

## 2. Mandatory Testing Rules (Code Changes)

1. **Every engine feature or bug fix MUST ship with tests in `tests/BringEngine.Tests/`**:
   - New features/APIs: cover happy paths, edge cases, zero-GC hot loops (`GC.GetAllocatedBytesForCurrentThread() == 0`), and zero native leaks (`NativeMemoryTracker.LiveBytes == 0`).
   - Bug fixes: add a regression test that fails without the fix and passes with it.
   - Put the code change and its tests in the same commit.
2. **Run engine tests on GitHub Actions CI ONLY — do NOT keep the local machine busy**:
   - **NEVER** run `BringEngine.Tests` or heavy multi-project publishes locally on the developer's machine.
   - Commit and push your changes to GitHub and let `.github/workflows/build.yml` run the full cross-platform CoreCLR matrix:
     ```bash
     git push origin main
     gh run watch --exit-status
     ```
3. **Keep documentation synchronized**:
   - Update `AGENTS.md`, `README.md`, `src/ShowcaseGame/AGENT_README.md`, and the relevant `docs/agents/NN-*.md` guide whenever public APIs, defaults, or workflows change.

---

## 3. GitHub Actions CI (`.github/workflows/build.yml`)

Every push (excluding `v*` tags) and pull request starts with a lightweight `detect-changes` job:

- **Code, project, shader, or workflow changes**:
  - Runs the full 3-OS Desktop CoreCLR matrix:
    - **`windows-x64`** (`windows-latest`): `dotnet build`, full `BringEngine.Tests` (WARP D3D12 + Headless), ReadyToRun `BringPacker` smoke test, ReadyToRun `ShowcaseGame` (`--headless` + `--verify-render`).
    - **`linux-x64`** (`ubuntu-24.04`): `dotnet build`, full `BringEngine.Tests` (lavapipe Vulkan under `xvfb-run` + Headless), ReadyToRun `BringPacker`, ReadyToRun `ShowcaseGame` (`--headless` + `--verify-render`).
    - **`macos-arm64`** (`macos-latest`): `dotnet build`, headless `BringEngine.Tests`, ReadyToRun `BringPacker`, ReadyToRun `ShowcaseGame --headless`.
- **Docs / README-only changes (`*.md` and `docs/**` only)**:
  - **Skips all 3 test jobs** (`run_tests=false`) so documentation updates finish in seconds without wasting CI minutes.

---

## 4. Releasing Bring Engine (`vX.Y.Z`)

A release is **public and immutable**: never move or reuse a tag; fix any issue in the next patch release.

### Path A — Docs / README-Only Release (Automatic, No Tests)

Whenever you push a commit to `main` that changes **only** READMEs or documentation (`*.md`, `docs/**`) and **zero code files have changed since the latest `vX.Y.Z` tag**:

1. `.github/workflows/build.yml` detects no code changes and **skips the 3-OS test matrix**.
2. Its `docs-only-release` job automatically:
   - Increments the latest semver patch tag (e.g. `v0.4.0` → `v0.4.1`) **without modifying `EngineInfo.cs`**.
   - Packages `bring-sdk-vX.Y.Z.zip` and verifies the archive layout (`AGENTS.md` + `src/BringEngine/BringEngine.csproj`).
   - Pushes the new tag and publishes the GitHub Release immediately (~20 seconds total).

```bash
# Docs-only update: just commit and push to main — CI auto-bumps the patch tag & releases!
git add AGENTS.md README.md docs/agents/...
git commit -m "docs: update agent guides"
git push origin main
gh run watch --exit-status
```

- **Safety guard**: If unreleased code commits already exist between the latest `v*` tag and `HEAD`, `docs-only-release` will **not** trigger (preventing untested code from sneaking into a docs-only release).
- **Manual docs-only tags**: If you manually push a `vX.Y.Z` tag whose diff from the previous `v*` tag contains zero code changes, `.github/workflows/release.yml` also detects `docs_only=true`, skips the 3-OS test matrix and `EngineInfo.Version` check, packages `bring-sdk-vX.Y.Z.zip`, and publishes the release.

### Path B — Code Release (Full 3-OS Verified Release)

Whenever engine, tool, shader, or project code has changed since the last release:

1. Check the latest published tag (`git tag -l 'v*' --sort=-v:refname | head -n 5`) so your new version advances past any intermediate docs-only patch tags.
2. Bump `EngineInfo.Version` in `src/BringEngine/EngineInfo.cs` (patch for fixes, minor for new features/APIs):
   ```bash
   # 1. Confirm main CI is green
   gh run list --branch main --limit 1

   # 2. Bump EngineInfo.Version, commit & push
   git add src/BringEngine/EngineInfo.cs
   git commit -m "build: version X.Y.Z"
   git push origin main
   gh run watch --exit-status

   # 3. Tag and push to trigger full verified GitHub release
   git tag vX.Y.Z
   git push origin vX.Y.Z
   gh run watch --exit-status
   gh release view vX.Y.Z
   ```
3. `.github/workflows/release.yml` verifies `EngineInfo.Version == tag`, runs the full Windows/Linux/macOS test matrix, builds and verifies standalone `BringPacker` + `ShowcaseGame` against the extracted `bring-sdk-vX.Y.Z.zip`, and publishes the GitHub Release only if every check passes.
