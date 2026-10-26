# Lockpick API

Lockpick can be driven and read by other programs: your own scripts or LLM tools, OBS overlays, LiveSplit, a Stream
Deck or other macro tools, or a device on your network such as a phone or a Steam Deck. The API is **off** until you
turn it on in **Settings → API for other tools**.

- **Address:** `http://127.0.0.1:1996/api/v1` (the port is configurable). It listens on this PC only, unless you tick
  *Allow other devices on my network*; then use the PC's network address. Windows may ask to let Lockpick through the
  firewall.
- **Token:** every request needs the token shown in Settings. Use one of:
  - `Authorization: Bearer <token>`
  - `X-Lockpick-Token: <token>`
  - `?token=<token>` (for browser sources and tools that can't set headers)

  *New token* replaces it; programs using the old one stop working.
- **Trainer commands** need a second switch, *Allow trainer commands*, and work only while Trainer Mode is on. They
  go through the same checks as the app's own buttons: bounds such as HP up to max HP, read back, and logged with
  source `api`. In Challenge Mode they are refused.
- Responses are JSON (camelCase). A command that couldn't be done answers 409 with `error` explaining why. A missing
  or wrong token answers 401, and trainer commands that aren't allowed answer 403.

## Reading

| Request | What you get |
|---|---|
| `GET /api/v1` | Name, version, list of endpoints |
| `GET /api/v1/status` | Connection (`attached`, `searching`, …), game build, trainer mode and write access, capture running, API settings |
| `GET /api/v1/state` | The live game state: `inGame`, `character`, `health {current, max, condition}` (condition: `fine`, `caution`, `danger` or `poison`, as the game's status screen shows it), `room {id, stage, name}`, `camera`, `position {x, y, z}`, `gameTimeSeconds`, `inventory [{slot, itemId, name, quantity}]`, `itemBox`, `trainer` |
| `GET /api/v1/events?after=N` | Events after sequence number `N` (`{events: [...], last}`); poll with the last number you saw |
| `GET /api/v1/events/stream?after=N` | The same as a live [server-sent event](https://html.spec.whatwg.org/multipage/server-sent-events.html) stream: each message has `id` (sequence), `event` (type) and `data` (JSON); a comment every 15 s keeps it open |
| `GET /api/v1/trainer` | Trainer mode, write access, whether this run was changed, locks, infinite ammo, the last 50 actions |
| `GET /api/v1/challenges` | The ready-made challenges (`id`, `name`, `summary`, `rules`) and the rule names |
| `GET /api/v1/challenge` | The current challenge: `state` (`off`, `waiting`, `running`, `failed`), `attempt`, `failReason`, `failedAtGameTime` |
| `GET /api/v1/widget/<id>.png` | An overlay widget (`status`, `minimap`, `enemies`, `splits`, `feed`, `banner`) drawn as large as fits `width` × `height` (64–2000, default 400), the same picture as on the overlay; `204` while it has nothing to show |
| `GET /api/v1/livesplit.png` | LiveSplit's layout of the run, drawn by livesplit-core, at `width` × `height` |
| `GET /api/v1/run` | The run (docs/RUNS.md): `state` (`off`, `ready`, `running`, `finished`, `ended`), `set {id, name, category, verified}`, `attempt`, `current`, `gameTimeSeconds`, `realTimeSeconds`, `result`, `personalBestSeconds`, `sumOfBestSeconds`, `splits [{name, trigger, timeSeconds, personalBestSeconds, deltaSeconds, gold}]` |

Event types: `GameStarted`, `GameStopped`, `GameLoaded`, `GameSaved`, `RoomChanged`, `CameraChanged`,
`PlayerHealthChanged`, `PlayerDamaged`, `TrainerActionExecuted`, `RunInvalidated`, `ChallengeStarted`, `ChallengeFailed`
(the one an overlay shows when a challenge ends).

## OBS browser sources

Lockpick serves pages made for OBS's **Browser Source** (transparent background, updated four times a second). Add
a Browser Source, set its size to your canvas (e.g. 1920×1080) and use:

```text
http://127.0.0.1:1996/overlay/hud?token=<token>&corner=top-right&scale=1
```

| Page | Shows | Options |
|---|---|---|
| `/overlay/hud` | In-game time, HP, the room, the challenge (clean, or failed with the reason) | `corner` = `top-right` (default), `top-left`, `bottom-right`, `bottom-left`; `scale` 0.5–4 |
| `/overlay/splits` | The run's splits: the category, the splits around the current one with times and differences to the personal best (gold for a best segment), the in-game clock, PB and sum of best | `corner` = `top-left` (default) or another; `scale` 0.5–4; `rows` 3–30 (default 8) |
| `/overlay/widget` | Any overlay widget, live (`id` = `status`, `minimap`, `enemies`, `splits`, `feed` or `banner`), as large as fits, top left | `width` and `height` (size the source to them); `align` = `center` (default), `left`, `right`; `valign` = `top` (default), `middle`, `bottom`; `fps` 1–30 (default 10) |
| `/overlay/livesplit` | The run with LiveSplit's look (livesplit-core) | `width`, `height`, `fps` |
| `/overlay/feed` | What just happened, in plain words (pickups, kills, enemies appearing, damage, story events), the newest at the bottom, each line for a while | `corner` = `bottom-left` (default) or another; `scale`; `lines` 1–20 (default 5); `seconds` (default 20); `types` = event types to show (default `Happened`; e.g. add `SplitReached,RunEnded,ChallengeFailed`) |

The app lists every widget's address with its size and a **Copy OBS link** button (Stream → OBS).

### Lockpick's OBS script

`obs/lockpick.lua` ships next to Lockpick.exe (OBS → Tools → Scripts → +). OBS's Lua has no network, so Lockpick
writes two files for it in `%LOCALAPPDATA%\Lockpick\obs`: `connection.json` (the API's port and token, rewritten when
they change) and `events.txt` (emptied at each start; one line per moment: `time-ms<TAB>kind<TAB>detail`, kinds
`run-start`, `split`, `run-end`, `finished`, `new-best`, `death`, `challenge-fail`, `game-active`, `game-inactive`).
The script's settings, each optional: **Add Lockpick to this scene** (the browser sources above and a Game Capture of
the game), record each run (stop a few seconds after it ends), chapter markers at splits, deaths and fails (Hybrid
MP4, OBS 30.2+), save the replay buffer on a death, a fail or a new best, and switch to a "paused" scene while the
game isn't in front. Its **OBS hotkeys** (OBS Settings → Hotkeys, "Lockpick: …") split, undo, skip, reset, retry a
challenge, toggle the overlay, change the map's view or zoom, or drop a capture marker: the script appends them to
`commands.txt` (`time-ms<TAB>action`, emptied when Lockpick starts), and Lockpick does only those actions — never a
cheat. A popped-out widget
window (Settings → Overlay → Pop out) can be captured with Window Capture too.

`connection.json` also says `"websocket": true` while Lockpick drives this PC's OBS through its WebSocket server
(below): the script then leaves recording, chapters, the replay buffer and the scene to Lockpick, and its hotkeys keep
working. `"executable"` is the game's executable name (the running one, else the installed one), for the Game Capture.

### Lockpick's OBS WebSocket connection

Optional (Stream → OBS → OBS WebSocket, #65): Lockpick connects to the WebSocket server built into OBS 28+
(obs-websocket 5, default port 4455; checked against OBS 32.2.2 / WebSocket 5.7.4) and does what the script does, from
its side: `StartRecord` / `StopRecord` with runs (only a recording Lockpick started is stopped; a new attempt within
the stop delay keeps it going), `CreateRecordChapter` at the same moments as the script, `SaveReplayBuffer` on the
moments chosen, and `SetCurrentProgramScene` to the "paused" scene and back. **Set up OBS** makes a `Lockpick` scene
(`CreateScene`, `CreateInput`, `SetSceneItemTransform`) with the same source names as the script's button; sources
already there keep their place, only their address (and the game's executable) is updated. No OBS events are
subscribed to. The password is kept with Windows DPAPI (`%LOCALAPPDATA%\Lockpick\secrets`, never in settings) and
only the challenge answer is sent; **Fill in from OBS on this PC** reads OBS's own
`%APPDATA%\obs-studio\plugin_config\obs-websocket\config.json` when pressed. For OBS on another PC, the widgets'
address is this PC's on the network, so the API must allow other devices.

Lockpick's in-game overlay (Settings → Overlay) is not part of any OBS capture — it's a window of its own on top of
the game — so use these pages for the stream. Game Capture of the game works in every display mode
([DISPLAY.md](DISPLAY.md)).

## Commands (POST)

| Request | Body | Does |
|---|---|---|
| `/api/v1/challenge/start` | `{"id": "no-damage"}`, optional `"rules": ["noDamage", "knifeOnly"]` | Starts a challenge (Lockpick goes into Challenge Mode: no changes to the game) |
| `/api/v1/challenge/retry` | — | A new attempt |
| `/api/v1/challenge/stop` | — | Ends the challenge |
| `/api/v1/run/split` · `/undo` · `/skip` · `/reset` | — | The run keys (they change Lockpick's timing only, never the game); each answers with the run |
| `/api/v1/capture/start` | — | Starts a capture (as Debug → Start capture) |
| `/api/v1/capture/marker` | `{"note": "split 1"}` (optional) | Marks the moment in the running capture; returns the marker number |
| `/api/v1/capture/stop` | — | Stops and saves the capture; returns the file |
| `/api/v1/trainer/mode` | `{"mode": "readOnly" \| "trainer" \| "challenge"}` | Switching to read-only or Challenge is always allowed; to `trainer` only with trainer commands allowed |
| `/api/v1/trainer/health` | `{"value": 90}` or `{"full": true}`, optional `"lock": true` | Sets (or locks) HP |
| `/api/v1/trainer/health/unlock` | — | Releases the HP lock |
| `/api/v1/trainer/ammo` | `{"enabled": true}` | Infinite ammo on / off |
| `/api/v1/trainer/inventory/quantity` | `{"slot": 0, "quantity": 99}` | Changes the count of a held weapon (loaded rounds), ammo or ink ribbons; `slot` as in `state.inventory` |
| `/api/v1/trainer/itembox` | `{"itemId": 12, "quantity": 30}` | Puts an item in the first empty item-box entry; the player takes it out at any item box (`GET /api/v1/trainer/items` lists the ids) |

## Examples

```bash
TOKEN=...   # from Settings
curl -H "Authorization: Bearer $TOKEN" http://127.0.0.1:1996/api/v1/state
curl -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
     -d '{"note":"Yawn 1"}' http://127.0.0.1:1996/api/v1/capture/marker
curl -N -H "Authorization: Bearer $TOKEN" http://127.0.0.1:1996/api/v1/events/stream
```

```powershell
$h = @{ Authorization = "Bearer $env:LOCKPICK_TOKEN" }
Invoke-RestMethod http://127.0.0.1:1996/api/v1/state -Headers $h
Invoke-RestMethod http://127.0.0.1:1996/api/v1/trainer/health -Method Post -Headers $h `
  -ContentType application/json -Body '{"full":true}'
```

```python
import json, urllib.request
req = urllib.request.Request("http://127.0.0.1:1996/api/v1/state", headers={"Authorization": f"Bearer {TOKEN}"})
print(json.load(urllib.request.urlopen(req))["health"])
```

**Stream Deck and macro tools:** use an "HTTP request" or "website" action. Set it to POST to a command URL and add
the token as a header, or as `?token=` if the tool can't set headers.

**OBS:** a browser source can open `events/stream?token=...` with `EventSource`. A ready-made overlay page is planned
for 0.3.0.
