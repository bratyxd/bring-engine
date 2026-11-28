# Agent Guide 15 — Engine Development, PR Review, CI-Only Testing & Releasing

> **Scope**: Rules and workflows for modifying **Bring Engine** itself (`bratyxd/bring-engine`), running the mandatory post-implementation PR review & fix phase, running CI verification on GitHub Actions, and publishing both **code releases** and **fast docs-only releases**.

---

## 1. When to Change the Engine (vs. Game Code)

Games consume released SDK archives (`bring-sdk-vX.Y.Z.zip`) from GitHub Releases rather than copying engine sources:
- Only modify Bring Engine when it has a **bug, crash, violated contract, or missing general capability** a game cannot reasonably build on its own.
- **Nothing game-specific** goes into the engine.
- **Mandatory Engine Async Contract**: Every new async or background subsystem added to core `BringEngine` (e.g., networking, async file/save I/O, audio streaming, background asset loading) MUST follow the **32-bit Generational Handle + Quarantined Background Worker + Native Ring-Buffer Mailbox + Zero-GC Frame Polling + `Cancel`/`Release`/`Stop`** pattern (matching `JobSystem`, `HttpSystem`, and `WebSocketSystem`). Core engine public APIs MUST NEVER expose `Task<T>`, `async`/`await`, or managed callback closures (`Action<T>`).
- **Selective Engine Logging Discipline**: Core engine code MUST use `LogSystem` selectively — never spam hot loops at `Info`+, but always log key startup/shutdown, scene/bundle, device/shader, fallback, and error milestones with detailed context (see [`17-logging.md`](17-logging.md)).
- **Never patch** files inside a downloaded `~/.bring/sdk/<tag>/` cache folder — always edit in the `bratyxd/bring-engine` repository and publish a new release.
- **Git & GitHub Account (`bratyxd`)**: When multiple Git / GitHub CLI accounts are configured on the machine, ALWAYS use **`bratyxd`** for committing, pushing, tagging, and `gh` operations on `bratyxd/bring-engine`:
  ```bash
  gh auth switch --user bratyxd
  git config user.name    # must be bratyxd (pin repo-local user.name/user.email if needed)
  ```

---

## 2. Mandatory Post-Implementation PR Review & Fix Phase (Engine Code Changes)

> **Binding for every agent**: Whenever **engine code** is updated (any change to `src/BringEngine/`, `tools/BringPacker/`, `src/ShowcaseGame/`, shaders, project files, or `tests/BringEngine.Tests/`), work is **NOT** complete when the implementor agent finishes. Every agent MUST run a PR-style review and fix phase before pushing to CI, tagging a release, or reporting done.

1. **Implementation Phase (Implementor Agent)**:
   - Implement the engine change, add/update tests in `tests/BringEngine.Tests/`, update affected docs, and confirm a clean `dotnet build BringEngine.sln -c Release` (zero warnings).
2. **PR Review Plan — From Scratch (`Neon xhigh`)**:
   - Once the implementor agent is done, use **`Neon xhigh`** to conduct an independent PR review and **create a review plan from scratch** directly from the actual `git diff` and affected source/test/doc files (do **not** rely on or rubber-stamp the implementor's summary).
   - Audit every modified/added file against Bring Engine's non-negotiable contracts:
     - **Correctness & edge cases**: bounds/overflow safety, generational handle validation, null/empty/error paths, and clean rollback on OOM or partial failure.
     - **Zero-OOP**: no class inheritance, `virtual`, `override`, `abstract`, interface dispatch, or managed closures in engine APIs.
     - **Memory & Zero-GC**: explicit native allocators (`LinearArena`, `PoolAllocator<T>`, `FreeListAllocator`), `0` managed GC allocations in hot paths, and `0` native leaks (`NativeMemoryTracker.LiveBytes == 0`).
     - **Determinism & Sim/View split**: `Fix32`/`FixVec3`, `DetMath`, `DetRng`, and zero SDL3/GPU calls in simulation paths.
     - **Threading & Two-Zone Async**: Main Thread vs. Logic Thread vs. dedicated workers, 32-bit generational handles + native mailboxes + `Cancel`/`Release`/`Stop`, race-free shutdown ordering before `LogSystem.Stop` and arena disposal.
     - **GPU/rendering/shaders & cross-OS portability**: D3D12/Vulkan/Metal shader conventions, deferred GPU resource releases, and `win-x64` / `linux-x64` / `osx-arm64` path/platform safety.
     - **Logging, tests & docs**: selective high-signal `LogSystem` diagnostics, thorough tests in `tests/BringEngine.Tests/` (happy paths, regressions, 0-GC, 0-leak), and synchronized agent docs.
   - Produce a concrete, prioritized **PR Review Plan** (severity, exact file/line, root cause, required fix, and required test/doc update) — or record an explicit clean sign-off if zero actionable findings exist.
3. **Fix Phase (`Neon high`)**:
   - Hand the review plan to **`Neon high`** to **apply the review plan and run a dedicated fix phase**: implement every fix, add regression tests in `tests/BringEngine.Tests/` for bugs caught in review, sync any affected docs, and verify a clean `dotnet build BringEngine.sln -c Release` (continuing to obey the CI-only engine test rule below).
4. **Re-Check (when blocking/non-trivial fixes were applied)**:
   - Have **`Neon xhigh`** verify the fix-phase diff against the review plan so no finding is missed or regressed before pushing to GitHub Actions CI and releasing.
5. **Docs-Only Exemption**:
   - Pure documentation / README-only updates (`*.md`, `docs/**` with zero engine code changes) do **not** require this code PR review & fix cycle and proceed directly through Path A below.

---

## 3. Mandatory Testing Rules (Code Changes)

1. **Every engine feature or bug fix MUST ship with tests in `tests/BringEngine.Tests/`**:
   - New features/APIs: cover happy paths, edge cases, zero-GC hot loops (`GC.GetAllocatedBytesForCurrentThread() == 0`), and zero native leaks (`NativeMemoryTracker.LiveBytes == 0`).
   - Bug fixes (including bugs found during the PR review fix phase): add a regression test that fails without the fix and passes with it.
   - Put the code change and its tests in the same commit (or keep implementation + PR fix-phase commits clean and atomic before pushing).
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

## 4. GitHub Actions CI (`.github/workflows/build.yml`)

Every push (excluding `v*` tags) and pull request starts with a lightweight `detect-changes` job:

- **Code, project, shader, or workflow changes**:
  - Runs the full 3-OS Desktop CoreCLR matrix:
    - **`windows-x64`** (`windows-latest`): `dotnet build`, full `BringEngine.Tests` (WARP D3D12 + Headless), ReadyToRun `BringPacker` smoke test, ReadyToRun `ShowcaseGame` (`--headless` + `--verify-render`).
    - **`linux-x64`** (`ubuntu-24.04`): `dotnet build`, full `BringEngine.Tests` (lavapipe Vulkan under `xvfb-run` + Headless), ReadyToRun `BringPacker`, ReadyToRun `ShowcaseGame` (`--headless` + `--verify-render`).
    - **`macos-arm64`** (`macos-latest`): `dotnet build`, headless `BringEngine.Tests`, ReadyToRun `BringPacker`, ReadyToRun `ShowcaseGame --headless`.
- **Docs / README-only changes (`*.md` and `docs/**` only)**:
  - **Skips all 3 test jobs** (`run_tests=false`) so documentation updates finish in seconds without wasting CI minutes.

---

## 5. Releasing Bring Engine (`vX.Y.Z`)

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

1. **Complete the mandatory PR review & fix phase** (Section 2): after the implementor agent finishes, run **`Neon xhigh`** to build a PR review plan from scratch, then run **`Neon high`** to apply that review plan in a fix phase (with `Neon xhigh` re-verifying blocking fixes) before pushing to `main`.
2. Check the latest published tag (`git tag -l 'v*' --sort=-v:refname | head -n 5`) so your new version advances past any intermediate docs-only patch tags.
3. Bump `EngineInfo.Version` in `src/BringEngine/EngineInfo.cs` (patch for fixes, minor for new features/APIs):
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
4. `.github/workflows/release.yml` verifies `EngineInfo.Version == tag`, runs the full Windows/Linux/macOS test matrix, builds and verifies standalone `BringPacker` + `ShowcaseGame` against the extracted `bring-sdk-vX.Y.Z.zip`, and publishes the GitHub Release only if every check passes.
