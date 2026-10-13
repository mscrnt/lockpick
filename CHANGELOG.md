# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.1.1] - 2026-10-13

### Added
- Installer and automatic updates (Velopack): `Lockpick-win-Setup.exe` per-user install, update checks 20 s after start
  and every 6 hours (optional; pre-release channel optional), *Update and restart* from Settings, and a sidebar version
  label that highlights available updates.
- First-time setup: welcome and safety notes, game location with build check and file browser, update preferences;
  re-runnable from Settings.
- Debug page (replaces Diagnostics) with one-click session capture: raw field traces, recordings, events and build
  fingerprints per game attachment, zipped on stop or on exit. No personal information recorded.
- `GameLoaded` event (new game / save loaded), derived from in-game timer discontinuities and tested against two live
  recordings; replay now detects events on recorded timestamps so playback speed can't fake a load.
- Second live fixture: Jill session with death, New Game, a route through 1F/2F, healing and loading a save.
- Profile: runtime-verified inventory slots, equipped slot and inventory pointer (read-only diagnostics).
- CONTRIBUTORS.md and docs/CAPTURE.md.

### Changed
- Releases are built with Velopack and are normal (non-pre-release) GitHub releases unless the version has a suffix.

## [0.1.0] - 2026-10-12

**Foundation.** Verified live against GOG `ResidentEvil.exe` 1.0 hotfix4 in Chris and Jill games.

### Added
- Game-agnostic core: `GameSnapshot` with quality-tagged `Field<T>` values (valid / stale / unavailable / unsupported),
  typed events and an event detector, adapter contract, version profiles with validation, exact build matching and
  runtime image verification (PE header + code anchors), `GameMonitor` with process lifecycle handling, session
  recording and replay, non-blocking JSON-lines logging.
- Windows platform layer: process discovery by executable name and full path, read-only process handles,
  `ReadProcessMemory` typed reads, high-resolution waitable timer for 30 Hz polling, GOG install lookup.
- Resident Evil adapter and profile for GOG `ResidentEvil.exe` 1.0 hotfix4 (USA): stage, room, camera, HP, max HP,
  character, in-game timer, position and session state, live-verified on 2026-10-12.
- Desktop app (Avalonia 12): dashboard (connection, fingerprint, game state, event log), diagnostics (raw fields with
  verification level, poll statistics, live log, recording), settings (install path override, build check, profiles).
  Responsive compact layout for portrait windows; Lucide icons.
- `lockpick-probe` tool: `identify`, `watch`, `trace`, recording.
- Tests: mock adapter, fake process memory, RE1 adapter, profile validation, monitor lifecycle and failure handling,
  fixture playback from a live game trace, Win32 integration tests. Benchmarks with BenchmarkDotNet.
- CI (build + test + data hygiene), CodeQL, scheduled benchmarks, tag-driven releases, Dependabot, issue templates.
- Documentation: architecture with decision records, roadmap with acceptance criteria, research, compatibility,
  memory map, performance, development guide.
