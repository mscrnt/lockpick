# Help Lockpick learn the game (capture a play session)

Lockpick only trusts what it has seen the real game do. Recorded play sessions are how new values (items, ammo,
item box, Rebecca, more rooms and game builds) get verified. Capture is built into Lockpick.

## Steps

1. Install Lockpick: **[Lockpick-win-Setup.exe](https://github.com/mscrnt/lockpick/releases/latest/download/Lockpick-win-Setup.exe)**
   (Windows SmartScreen may warn because the build isn't code-signed yet: *More info → Run anyway*).
2. Start Resident Evil normally (GOG Galaxy or the desktop shortcut — not "as administrator").
3. In Lockpick open **Debug** and click **Start capture**. The card turns red while recording.
4. Play. Resets, deaths, the title screen and game restarts are fine — recording continues.
5. Click **Stop and save**. A `LockpickCapture-*.zip` is created; **Open captures folder** shows it.
6. Send the zip to the maintainers (or attach it to a GitHub issue).

If you close Lockpick while capturing, the capture is saved automatically.

## What is recorded

- Game values only: room/camera, HP, character, timer, position, inventory slots, equipped slot, item box entries
  and internal state flags, sampled 30 times per second (changes only).
- The game executable's fingerprint (file name, size, SHA-256, PE header fields), so unsupported builds can be added.
- Lockpick version and OS version.

Not recorded: screenshots, audio, input, file paths, computer or user names.

Capturing is read-only — it never writes to the game's memory or files, the same way a LiveSplit autosplitter works.
If you submit runs to a leaderboard, check that leaderboard's rules for external tools.

## Especially useful (if it happens naturally)

- Using the item box (putting items in and taking them out)
- Playing as Rebecca
- Saving at a typewriter and loading the save from the title screen
- Many different items and weapons; speedruns cover a lot of ground quickly

With your permission, contributors are credited in [CONTRIBUTORS.md](../CONTRIBUTORS.md).
