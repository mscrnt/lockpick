# Display modes, capture and overlays (research for 0.3.0)

What Lockpick's 0.3.0 overlay, OBS integration and window-placement helper need to know about how Resident Evil (GOG,
1.0 hotfix4) puts itself on screen. Measured 2026-10-15 in game (a loaded save, room 618) for every display mode the
GOG wrapper offers, on two monitors with different scaling. Raw results (JSON lines, screenshots, OBS frames) stay local
in `%LOCALAPPDATA%\Lockpick\research\display-20261015`; the probe is `tools/display/probe.py` (dev only).

**Test machine.** Primary 5120×1440 at 100 % (0, 0; work area 5120×1392); secondary 1920×1080 at 150 % (5120, −220; work
area 1920×1008). All rectangles below are physical pixels, as a per-monitor-DPI-aware (v2) process sees them — which is
what Lockpick's overlay window will be. OBS 32.2.2 (WebSocket 5.7.4). Discord was running (its overlay hooks games).

## Lockpick sees the game in every mode

Lockpick attached to every restarted game within about a second and read the live state (room, in-game time) in all 15
display modes, windowed or exclusive fullscreen, on either monitor, focused or not, and while the game was minimized.
It reads memory, so the display mode never matters for state, events, challenges, the trainer or captures. Only the
overlay and screen capture depend on the window.

## The wrapper's settings (`dxcfg.ini`)

The game draws through GOG's DirectDraw wrapper (`ddraw.dll`), configured by `dxcfg.ini` in the install folder (GUI:
`dxcfg.exe`, "GOG.com DirectX wrapper" 1.1.0.0). Lockpick may read this file (read-only) to predict the window.

| Key | Values (default first) |
|---|---|
| `display` | `desktop` (same as the desktop), `application` (the game's own 640×480), or `WxH@Hz` |
| `presentation` | `windowed`, `fullscreen` |
| `aspect` | `enabled` (keep 4:3), `disabled` (stretch) |
| `scaling` | `fit` (largest 4:3 picture), `integer` (whole multiples of 640×480) |
| `vsync`, `gamma`, `anisotropic`, `antialiasing` | `application` / values; no effect on placement |

- `WxH` must be a mode the **primary** monitor offers (EnumDisplaySettings). Anything else (e.g. 1280×960 on a
  5120×1440 primary) silently falls back to the desktop size. A helper that edits `display` must list the primary's
  modes, not invent sizes.
- The game always opens on the **primary** monitor: windowed centred in its work area, fullscreen covering it.

## Windowed

| `display` | Window | Client (picture area) |
|---|---|---|
| `application`, `640x480` | caption, fixed size | 640×480 |
| `800x600`, `1024x768`, `1280x720`, `1600x900`, `1920x1080` | caption, fixed size | as requested |
| a mode as tall as the monitor (`2560x1440` on a 1440 monitor) | **borderless popup** at that size | as requested, top of the screen |
| `desktop`, or a mode the primary lacks | **borderless popup covering the monitor** (not topmost) | 5120×1440 |

- Window styles: `WS_CAPTION | WS_SYSMENU` only — no resize border, no maximize. Frame: 3 px sides/bottom, 26 px title
  bar; window class and title `RESIDENT EVIL`.
- **Picture inside the client:** `fit` + `aspect=enabled` pillarboxes/letterboxes a 4:3 picture, centred (1280×720 →
  960×720 at x 160; 1920×1080 → 1440×1080 at x 240; 5120×1440 → 1920×1440 at x 1600). `integer` → the largest whole
  multiple of 640×480 that fits, centred (1920×1080 → 1280×960 at 320, 60). `aspect=disabled` → the whole client.
  The overlay can compute this from the client size and `dxcfg.ini`; no pixel inspection needed.
- **DPI:** the game window is per-monitor DPI aware (`GetWindowDpiAwarenessContext`). Moved to the 150 % monitor it
  keeps its pixel size (no bitmap scaling; `GetDpiForWindow` = 144 there) and its 26 px title bar. So a 1920×1080
  game window (1109 px tall with the frame) does not fit a 1080p monitor's work area; of the sizes tested, 1600×900 is the
  largest that fits there.
- Moving the window (to the other monitor, or straddling both) works; the game keeps rendering normally.
- `desktop` windowed is effectively **borderless fullscreen**: composited by Windows (not exclusive), so overlays,
  alt-tab and window capture behave as in a window. This is the mode to recommend to overlay users.

## Exclusive fullscreen (`presentation=fullscreen`)

- `desktop`, `1920x1080`, `application` (640×480): a topmost borderless popup covering the primary at that mode;
  Windows reports **D3D exclusive fullscreen** (SHQueryUserNotificationState = 3). `desktop` uses a flip-model
  window (`WS_EX_NOREDIRECTIONBITMAP`); the others change the primary's resolution.
- **Alt-tab / another window activated** → the game minimizes and **stops** (the in-game clock froze even with the
  dev harness's keep-running patch). Restoring it (taskbar) returns to exclusive fullscreen and the game continues.
- **Anything that activates a Lockpick window minimizes the game** — including the ask-mode question pop-up. In 0.3.0
  Lockpick must not activate its windows while the game is exclusive fullscreen (show without activation, or wait).
- Mode changes rearranged other windows on the primary (9 of the owner's windows each time), and once left the
  monitor layout changed (the secondary became primary) until the saved settings were re-applied. Tests should use
  `display=desktop`; players switching modes may see the same.

## Focus: the game pauses when it isn't the active window

Without keep-running (Trainer → Cheats since 0.3.2, data-only; MEMORY_MAP "Focus and input"), clicking
another window **pauses the game and blacks out its window** — both a window grab and a screen grab of it are black —
until it is active again. Measured in windowed 1280×720 and 1920×1080. Consequences for 0.3.0:

- **Every OBS capture breaks while the game is inactive:** measured windowed 1920×1080 with OBS — focused, Game
  Capture and both Window Captures show the game; after clicking another window, Window Capture (WGC and BitBlt)
  is black and Game Capture has no frame to render (the game stops presenting). Streamers who click into OBS,
  Lockpick or a browser lose the picture until they click back. The opt-in keep-running switch (Trainer Mode only,
  never in challenges) avoids it; challenge runners need to keep the game focused.
- With the patch the game kept running and rendering behind other windows; window capture kept working.

## Capture

| Method | Windowed (both monitors, straddling) | Exclusive fullscreen | Notes |
|---|---|---|---|
| OBS **Game Capture** | works | **works** | Captures the game's own 640×480 frame (before the wrapper scales it) — no pillarbox. Hooking takes a few seconds the first time the source is shown. |
| OBS **Window Capture (WGC)** | works | works | The client area, pillarbox included. |
| OBS **Window Capture (BitBlt)** | works | **black** | |
| `PrintWindow(PW_RENDERFULLCONTENT)` | works | works | Some calls return a black frame; take a few and keep the brightest. |
| Screen copy (`BitBlt` from the desktop) | works when the game is on top | see overlay note | Shows whatever is in front. |

**Screen copies on the 150 % monitor can't be trusted:** a desktop screen copy (GDI `BitBlt`) there showed the
game only partly (black elsewhere) even with no overlay at all — likely a hardware overlay plane the copy doesn't
see — while PrintWindow and OBS's window and game captures were complete. OBS's Display Capture keeps the monitor
layout from when OBS started (it still listed the secondary as portrait after it was rotated).

Our overlay window is **not** part of any window/game capture (PrintWindow, WGC, game capture) — only of a display
capture. For OBS, the HUD should therefore be a browser source (planned), not the on-screen overlay.

### OBS browser sources can control OBS

Verified 2026-10-15 in OBS 32.2.2 (obs-browser 2.26.9): a browser source whose `webpage_control_level` is 4 (the
setting's default is 1) gets `window.obsstudio` — it read the status and the current scene, received
`obsSceneChanged` events and a live server-sent event stream. Levels: 3 BASIC (`saveReplayBuffer`), 4 ADVANCED
(`setCurrentScene`, replay buffer start/stop), 5 ALL (recording, streaming). Chapter markers are not in this API;
an OBS script has them (`obs_frontend_recording_add_chapter`, OBS 30.2+). So Lockpick's HUD pages and its shipped
OBS script need no WebSocket password.

## Overlay

Test overlay: a layered, click-through, topmost, non-activating tool window (`WS_EX_LAYERED | WS_EX_TRANSPARENT |
WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, shown with `SW_SHOWNOACTIVATE`) over the game's client area.

- Windowed (both monitors, straddling, every size): visible over the game; the game **kept the foreground** (no
  pause); not in window captures (above).
- Exclusive fullscreen (desktop resolution): **works, owner-verified 2026-10-15**, the game staying exclusive. On the
  way there: the overlay first sat below the game. The game's own invisible IME window (`Default IME`, owned by
  the game, topmost) is always directly above it, and placing the overlay "just below the window above the game"
  — that IME window — puts it below the whole game, because owned windows stay above their owner. Lockpick now
  skips the game's own windows when choosing where to slot in. A plain layered window created from another process
  stayed above the game 100 % of the time, also when updated and re-placed ten times a second, so the game itself
  doesn't push windows down. Fullscreen at another resolution (a mode change) is untested.
- The overlay sits directly above the game in the window stack, not above everything (owner report: a topmost
  overlay floated over other apps); topmost only while the game is.
- Owner-verified on the 150 % monitor too: the overlay panel in the picture's corner, the game fully visible.
- **Widgets off the game** (owner, 2026-10-16: the overlay started to cover the picture): the same widgets also draw
  into ordinary windows (pop-outs: movable, resizable, dark or chroma-green background, optional always-on-top,
  place remembered) and onto the Stream page. Pop-outs are normal windows, so OBS's Window Capture (WGC) records
  them, unlike the layered overlay. One renderer (`WidgetRenderer`) draws each widget for all three; each place
  redraws only when the widget changed (every tick while the LiveSplit timer runs).
- **Discord's overlay** shows a "use the overlay" toast in the game's top-left corner the first time a game is
  focused, covering anything there. Keep Lockpick's widgets away from the top-left by default and let players move
  them.

## Requirements for 0.3.0

1. **Find the window:** the game PID's visible top-level window (class `RESIDENT EVIL`). Track its client rectangle
   in physical pixels (per-monitor-v2 overlay process), its monitor and DPI, minimized state, and whether it is
   exclusive fullscreen (topmost popup covering a monitor whose mode differs from the registry, or
   SHQueryUserNotificationState = 3 while the game is in front — that state is system-wide, so check the window too).
   Follow moves with `EVENT_OBJECT_LOCATIONCHANGE` (or poll at ~10 Hz).
2. **Picture rectangle:** compute from the client size and `dxcfg.ini` (`aspect`, `scaling`), so widgets can anchor to
   the 4:3 picture rather than the pillarbox.
3. **Overlay window:** the styles above; never activate; hide while the game is minimized; scale widgets by the
   monitor's DPI (the game itself doesn't scale); default position away from the top-left.
4. **Exclusive fullscreen:** the overlay works there (topmost like the game, slotted in above the game's own
   windows); never activate Lockpick windows while it is up (the game minimizes and stops).
5. **OBS:** HUD as a browser source; recommend Game Capture (works in every mode, no pillarbox) or Window Capture
   (WGC). Explain the black window when the game is inactive, and offer the keep-running option (Trainer Mode).
6. **Placement helper:** move/size the game window to a chosen monitor (works, including the scaled monitor; sizes
   larger than the work area don't fit with the title bar); offer only the primary's modes for `display`; warn that
   fullscreen modes other than `desktop` change the primary's resolution and can rearrange windows.

## How it was measured

`tools/display/probe.py` (monitors, window, windows, capture, overlay, move). A dev-only runner (not committed)
wrote `dxcfg.ini` for each mode (restored afterwards), restarted the game with the dev harness, loaded save slot 1 by
memory state only, checked Lockpick's header and Dashboard (UI Automation) or its log, then probed on the primary,
moved to the secondary, and (1280×720) straddling both; OBS sources were checked in a temporary scene that was removed
afterwards. Saves stayed read-only throughout.
