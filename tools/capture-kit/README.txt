LOCKPICK CAPTURE - Master of Unlocking.
=======================================

Thanks for helping! This little tool records what Resident Evil (1996, PC) is doing
while you play, so Lockpick can learn the game: rooms, cameras, health, items, ammo,
saves and loads.

What it does / doesn't do
- Read-only: it only reads the game's memory, the same way a LiveSplit autosplitter
  does. It never changes the game, its files, or your saves.
- Records game values only. No screenshots, no audio, no keystrokes, and no
  computer or user names.
- Uses almost no CPU (about 30 tiny reads per second). Safe to keep running while
  streaming.
- If you submit runs somewhere, check that leaderboard's rules about external tools
  (it behaves like an autosplitter).

How to use
1. Unzip this folder anywhere (Desktop is fine).
2. Start Resident Evil normally (GOG Galaxy or the desktop shortcut). Don't start it
   "as administrator" or from the installer's Launch button.
3. Double-click LockpickCapture.exe. You can do this before or after starting the game.
   - Windows may show "Windows protected your PC" because the tool isn't signed.
     Click "More info" -> "Run anyway".
4. Play! Resets, deaths, quitting to the title and restarting the game are all fine -
   it keeps recording. The window can stay in the background.
5. When you're done, click the capture window and press Ctrl+C (or just close it).
   A zip file is created in the "captures" folder and Explorer opens it.
6. Send that LockpickCapture-....zip file back.

Extra-helpful things to do if they come up naturally (not required):
- Put items in / take items out of the item box.
- Play as Rebecca when the game switches to her.
- Save at a typewriter and load that save from the title screen.
- Pick up and use lots of different items and weapons.

If the window says your game build isn't supported, keep going anyway: the zip still
contains your game's fingerprint, which tells us which version to add next.
