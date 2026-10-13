# Help Lockpick learn the game (capture kit)

Lockpick only trusts what it has seen the real game do. Play sessions recorded with the capture kit are how
new values (items, ammo, item box, Rebecca, more rooms and builds) get verified.

## Download

**[LockpickCapture.zip](https://github.com/mscrnt/lockpick/releases/download/capture-kit/LockpickCapture.zip)**
(always the latest build; self-contained, no .NET install needed)

## Use

1. Unzip anywhere (e.g. Desktop).
2. Start Resident Evil normally (GOG Galaxy or the desktop shortcut — not "as administrator").
3. Double-click `LockpickCapture.exe` (before or after starting the game). Windows SmartScreen may warn because
   the tool is unsigned: *More info → Run anyway*.
4. Play. Resets, deaths, title screen and game restarts are fine; recording continues.
5. Press **Ctrl+C** in the capture window (or close it). A `LockpickCapture-*.zip` appears in `captures\`.
6. Send the zip to the maintainers (or attach it to a GitHub issue).

## What is recorded

- Game values only: room/camera, HP, character, timer, position, inventory slots, equipped slot, item box
  entries and internal state flags, sampled 30 times per second (changes only).
- The game executable's fingerprint (file name, size, SHA-256, PE header fields), so unsupported builds can be added.
- Lockpick version and OS version.

Not recorded: screenshots, audio, input, file paths, computer or user names.

The kit is read-only — it never writes to the game's memory or files, the same way a LiveSplit autosplitter
works. If you submit runs to a leaderboard, check that leaderboard's rules for external tools.

## Especially useful (if it happens naturally)

- Using the item box (putting items in and taking them out)
- Playing as Rebecca
- Saving at a typewriter and loading the save from the title screen
- Many different items and weapons; speedruns cover a lot of ground quickly

Thank you! Contributors are credited in [CONTRIBUTORS.md](../CONTRIBUTORS.md).
