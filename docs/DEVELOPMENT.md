# Development Guide

Everything needed to build, test, verify and release Lockpick.

## Prerequisites

| Tool | Version | Notes |
|---|---|---|
| Windows | 10/11 x64 | Process access is Win32-only |
| .NET SDK | 10.0.1xx or later | Pinned by `global.json` (`rollForward: latestFeature`) |
| Resident Evil (GOG) | build in `data/games/re1/profiles/` | Only needed for live validation |
| Python 3 | optional | `tools/fixtures/trace_to_fixture.py`, static-analysis notes |

No C++ toolchain is required: v0.1 does all process access through P/Invoke. A native component will
only be added when it has a demonstrated advantage (see ARCHITECTURE.md, ADR-002).

## Build, run, test

```powershell
dotnet build Lockpick.sln -c Release          # warnings are errors
dotnet test tests/Lockpick.Tests              # unit + Win32 integration tests
dotnet run --project src/Lockpick.App         # desktop app
dotnet run --project src/Lockpick.App -- --replay tests/Lockpick.Tests/Fixtures/re1-live-chris-death-and-restart.jsonl
dotnet run -c Release --project benchmarks/Lockpick.Benchmarks -- --filter "*" --job short
```

App options: `--replay <recording.jsonl>` plays a recording instead of attaching; `--verbose` enables debug logging.

Run the app from a copy (or close it) before rebuilding — a running `Lockpick.exe` locks its DLLs.

Runtime files live in `%LOCALAPPDATA%\Lockpick\`: `logs\` (JSON lines), `recordings\`, `profiles\`
(user/override profiles), `settings.json`.

## Probe tool (headless validation)

```powershell
dotnet run --project tools/Lockpick.Probe -- identify            # fingerprint the GOG install
dotnet run --project tools/Lockpick.Probe -- identify "D:\x\ResidentEvil.exe"
dotnet run --project tools/Lockpick.Probe -- watch --seconds 60 --raw --record session.jsonl
dotnet run --project tools/Lockpick.Probe -- trace --seconds 600 > trace.txt
```

- `watch` runs the real monitor and prints status, events and normalized state.
- `trace` prints every raw profile-field change at 30 Hz (timer/position shown as context). This is the
  primary tool for verifying or refuting an address.

## Verifying a memory address (required before marking it trusted)

1. **Source it.** Record where the lead came from (decomp, SDK layout, autosplitter, own analysis).
2. **Static check against the exact build.** Find code in the fingerprinted executable that references the
   address with the right width and plausible semantics (e.g. the room loader builds `ROOM%x%02x%x.RDT`
   from stage/room). Add a code anchor (address + instruction bytes) to the profile.
3. **Runtime check.** Run `lockpick-probe trace` while performing actions that should change the value.
   Record what was observed, with the date, in the field's `evidence`.
4. Set `verification` to `static` or `runtime`. `unverified` fields are read for diagnostics only and never
   reach the normalized state.
5. Update `docs/MEMORY_MAP.md` and, for refuted claims, `docs/RESEARCH.md`.

Never guess an offset. If a value cannot be verified, leave it unsupported.

## Adding a game build

1. `lockpick-probe identify <exe>` → SHA-256, size, PE timestamp, image size.
2. Copy an existing profile in `data/games/re1/profiles/`, change the `executable` block and `id`.
3. Re-verify every anchor and field for the new build (addresses differ between builds).
4. Profiles are embedded at build time; users can also drop profiles in `%LOCALAPPDATA%\Lockpick\profiles\`.

## Recording fixtures

- In the app: Diagnostics → Start/Stop recording (writes `%LOCALAPPDATA%\Lockpick\recordings\*.jsonl`).
- From a raw trace: `python tools/fixtures/trace_to_fixture.py trace.txt out.jsonl` (applies the same
  normalization rules as the RE1 session; keep the trace beside the fixture as provenance).
- Fixtures under `tests/Lockpick.Tests/Fixtures/` are played back through the real event detector in tests.

## Volunteer captures

Volunteers run the capture kit (see [CAPTURE.md](CAPTURE.md)); build it locally with
`powershell -ExecutionPolicy Bypass -File tools/capture-kit/build-capture-kit.ps1`.
Processing a returned `LockpickCapture-*.zip`:

1. Read `capture-info.json`: if `Builds[].ProfileId` is null, the SHA-256/PE fields identify a new build to profile.
2. `trace-N.txt` has every raw field change (same format as `lockpick-probe trace`); look for new evidence there.
3. Convert interesting sessions to fixtures: `python tools/fixtures/trace_to_fixture.py trace-1.txt tests/Lockpick.Tests/Fixtures/<name>.jsonl`
   (keep the trace beside it), then add playback assertions.
4. Credit the volunteer in `CONTRIBUTORS.md`.

## Coding conventions

- C# latest, nullable enabled, file-scoped namespaces, `_camelCase` private fields, warnings as errors
  with the `latest-recommended` analyzer set (`Directory.Build.props`).
- Central package versions in `Directory.Packages.props`; pin exact versions.
- Game-specific code and offsets live only in `Lockpick.GameAdapter.<Game>` and its profile JSON.
- Memory access goes through `IProcessMemory` typed helpers (`TryReadU8/I16/U32…`) — no raw pointer
  arithmetic scattered through code; reads are confined by `ValidatedMemory`.
- Normalized state uses `Field<T>` (Valid / Stale / Unavailable / Unsupported). Events only fire on
  Valid→Valid transitions.
- Background threads never touch UI objects; the UI marshals via `LiveState`.
- No game binaries or assets in the repository (CI enforces this).

## CI and automation

| Workflow | Trigger | What it does |
|---|---|---|
| `ci.yml` | push to main, PRs | Release build (warnings as errors), all tests, test results artifact; profile JSON validation; check no game binaries are tracked |
| `codeql.yml` | push, PRs, weekly | CodeQL security analysis for C# (skipped while the repository is private) |
| `benchmarks.yml` | manual, weekly | BenchmarkDotNet short run, results artifact |
| `capture-kit.yml` | push to main (core/adapter/probe/data changes), manual | Tests, builds the volunteer capture kit and updates the rolling `capture-kit` pre-release (stable download link) |
| `release.yml` | tag `v*.*.*` or manual | Test, publish self-contained win-x64 app + probe, zip + SHA-256, GitHub release with CHANGELOG notes |
| `dependabot.yml` | weekly | NuGet and GitHub Actions updates |

## Release process

1. Confirm every acceptance criterion for the version in `docs/ROADMAP.md` (game-specific ones live).
2. Move `## [Unreleased]` entries in `CHANGELOG.md` under `## [x.y.z] - YYYY-MM-DD`.
3. Bump `<Version>` in `Directory.Build.props`.
4. Commit, then `git tag vx.y.z && git push --tags`. `release.yml` builds and publishes.
5. 0.x releases are marked as pre-releases automatically.
