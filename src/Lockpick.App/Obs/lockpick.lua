--[[
Lockpick for OBS (#66). Add it in OBS: Tools -> Scripts -> + -> this file (it ships next to Lockpick.exe).

Lockpick writes two files for it in its data folder's obs folder (OBS's Lua has no network): %LOCALAPPDATA%\Lockpick\obs,
or the portable zip's own data\obs (found from where this script is):
  connection.json - the API's port and token: "Add Lockpick to this scene" makes the browser sources with them;
                    "websocket": true while Lockpick drives this OBS through its WebSocket server (Stream -> OBS),
                    when the script leaves recording, chapters, the replay buffer and the scene to Lockpick
  events.txt      - one line per moment: time-ms <TAB> kind <TAB> detail
                    (run-start, split, run-end, finished, new-best, death, challenge-fail, game-active, game-inactive)
and reads one the script writes:
  commands.txt    - OBS hotkeys for Lockpick (Settings -> Hotkeys in OBS): split, undo, skip, reset, try again, the
                    overlay, the map's view and zoom, a capture marker. Lockpick takes only these; never a cheat.

Every action is optional (the script's settings). Nothing here changes the game or Lockpick.
]]

obs = obslua

-- Lockpick's OBS folder, found the way Lockpick finds its data: the portable zip (a ".portable" marker in the folder
-- above "current", where this script ships in current\obs) keeps it in its own data folder; otherwise %LOCALAPPDATA%.
local function data_folder()
  local ok, here = pcall(script_path)
  if ok and type(here) == "string" and here ~= "" then
    local root = here:gsub("[/\\]+$", ""):gsub("[/\\][^/\\]+$", ""):gsub("[/\\][^/\\]+$", "")
    local marker = io.open(root .. "\\.portable", "rb")
    if marker then
      marker:close()
      return root .. "\\data\\obs\\"
    end
  end
  return (os.getenv("LOCALAPPDATA") or "") .. "\\Lockpick\\obs\\"
end

local folder = data_folder()
local options = {}
local offset = nil          -- how far events.txt has been read (nil: not yet opened; start at its end)
local run = 0               -- counts runs: a stop still waiting from the run before isn't this one's
local started_recording = false -- the recording is the script's (one the player started is left alone)
local scene_before_pause = nil
local pending_chapter = nil -- the run's chapter, waiting for the recording just started to run (a few ticks at most)
local pending_ticks = 0

-- OBS hotkeys for Lockpick's own actions (bound in OBS: Settings -> Hotkeys, "Lockpick: ...").
local hotkeys = {
  { action = "run-split", name = "Lockpick: split" },
  { action = "run-undo", name = "Lockpick: undo split" },
  { action = "run-skip", name = "Lockpick: skip split" },
  { action = "run-reset", name = "Lockpick: reset the run" },
  { action = "challenge-retry", name = "Lockpick: challenge, try again" },
  { action = "toggle-overlay", name = "Lockpick: overlay on / off" },
  { action = "map-view", name = "Lockpick: map, next view" },
  { action = "map-zoom", name = "Lockpick: map, zoom the floor" },
  { action = "marker", name = "Lockpick: capture marker" },
  { action = "overlay-widget", name = "Lockpick: overlay, next widget to size" },
  { action = "overlay-bigger", name = "Lockpick: overlay, widget bigger" },
  { action = "overlay-smaller", name = "Lockpick: overlay, widget smaller" },
}

local function send(action)
  local f = io.open(folder .. "commands.txt", "a")
  if not f then
    obs.script_log(obs.LOG_WARNING, "Lockpick: is Lockpick running? (its OBS folder isn't there)")
    return
  end
  f:write(string.format("%d\t%s\n", math.floor(os.time() * 1000), action))
  f:close()
end

-- ---------------------------------------------------------------- files

local function read_file(path)
  local f = io.open(path, "rb")
  if not f then return nil end
  local text = f:read("*a")
  f:close()
  return text
end

local function connection()
  local text = read_file(folder .. "connection.json")
  if not text then return nil end
  local data = obs.obs_data_create_from_json(text)
  if not data then return nil end
  local c = {
    api = obs.obs_data_get_bool(data, "api"),
    port = obs.obs_data_get_int(data, "port"),
    token = obs.obs_data_get_string(data, "token"),
    livesplit = obs.obs_data_get_bool(data, "livesplit"),
    websocket = obs.obs_data_get_bool(data, "websocket"),
    executable = obs.obs_data_get_string(data, "executable"),
  }
  obs.obs_data_release(data)
  return c
end

-- New lines of events.txt since the last look (it starts empty each time Lockpick starts).
local function new_events()
  local f = io.open(folder .. "events.txt", "rb")
  if not f then return {} end
  local size = f:seek("end")
  if offset == nil or size < offset then
    offset = (offset == nil) and size or 0 -- first look: skip what happened before; Lockpick restarted: from the top
  end
  local events = {}
  if size > offset then
    f:seek("set", offset)
    local chunk = f:read(size - offset) or ""
    local consumed = 0
    for line in chunk:gmatch("([^\n]*)\n") do
      consumed = consumed + #line + 1
      local _, kind, detail = line:match("^(%d+)\t([%w%-]+)\t?(.*)$")
      if kind then table.insert(events, { kind = kind, detail = (detail or ""):gsub("\r$", "") }) end
    end
    offset = offset + consumed -- a half-written last line waits for the next look
  end
  f:close()
  return events
end

-- ---------------------------------------------------------------- actions

local function chapter(name)
  if options.chapters and obs.obs_frontend_recording_active() and obs.obs_frontend_recording_add_chapter then
    obs.obs_frontend_recording_add_chapter(name) -- OBS 30.2+, Hybrid MP4
  end
end

local function save_replay()
  if obs.obs_frontend_replay_buffer_active() then
    obs.obs_frontend_replay_buffer_save()
  end
end

local function stop_recording_later()
  local this_run = run
  obs.timer_add(function()
    obs.remove_current_callback()
    if this_run == run and started_recording then
      started_recording = false
      if obs.obs_frontend_recording_active() then obs.obs_frontend_recording_stop() end
    end
  end, math.max(0, options.stop_delay) * 1000)
end

local function switch_scene(name)
  local source = obs.obs_get_source_by_name(name)
  if source then
    obs.obs_frontend_set_current_scene(source)
    obs.obs_source_release(source)
  end
end

local function current_scene_name()
  local source = obs.obs_frontend_get_current_scene()
  if not source then return nil end
  local name = obs.obs_source_get_name(source)
  obs.obs_source_release(source)
  return name
end

local function on_event(e)
  local k = e.kind
  if k == "run-start" then
    run = run + 1
    if options.record_with_runs and not started_recording and not obs.obs_frontend_recording_active() then
      obs.obs_frontend_recording_start()
      started_recording = true
      -- The recording starts a moment later, and a chapter before then is lost: tick adds it once it runs.
      pending_chapter = "Run: " .. e.detail
      pending_ticks = 12
    else
      chapter("Run: " .. e.detail)
    end
  elseif k == "split" then
    chapter(e.detail)
  elseif k == "death" then
    chapter("Death")
    if options.replay_on_death then save_replay() end
  elseif k == "challenge-fail" then
    chapter("Challenge failed: " .. e.detail)
    if options.replay_on_fail then save_replay() end
  elseif k == "new-best" then
    chapter("New personal best: " .. e.detail)
    if options.replay_on_best then save_replay() end
  elseif k == "finished" then
    chapter("Run finished: " .. e.detail)
  elseif k == "run-end" then
    chapter(e.detail)
    if started_recording then stop_recording_later() end
  elseif k == "game-inactive" then
    if options.pause_scene_enabled and options.pause_scene ~= "" then
      local now = current_scene_name()
      if now ~= options.pause_scene then scene_before_pause = now end
      switch_scene(options.pause_scene)
    end
  elseif k == "game-active" then
    if options.pause_scene_enabled and scene_before_pause then
      switch_scene(scene_before_pause)
      scene_before_pause = nil
    end
  end
end

local function tick()
  if pending_chapter then
    if obs.obs_frontend_recording_active() then
      chapter(pending_chapter)
      pending_chapter = nil
    else
      pending_ticks = pending_ticks - 1
      if pending_ticks <= 0 then pending_chapter = nil end
    end
  end
  local events = new_events()
  if #events == 0 then return end
  local c = connection()
  if c and c.websocket then return end -- Lockpick does these itself through OBS's WebSocket
  for _, e in ipairs(events) do on_event(e) end
end

-- ---------------------------------------------------------------- "Add Lockpick to this scene"

local function add_source(scene, id, name, settings, x, y)
  if obs.obs_scene_find_source(scene, name) ~= nil then return nil end -- already there
  local source = obs.obs_source_create(id, name, settings, nil)
  local item = obs.obs_scene_add(scene, source)
  local pos = obs.vec2()
  pos.x = x
  pos.y = y
  obs.obs_sceneitem_set_pos(item, pos)
  obs.obs_source_release(source)
  return item
end

-- Fits an item to the whole canvas, keeping its shape and centred (the game's 4:3 picture on a wide canvas).
local function fit_to_canvas(item, width, height)
  if item == nil then return end
  local bounds = obs.vec2()
  bounds.x = width
  bounds.y = height
  obs.obs_sceneitem_set_bounds_type(item, obs.OBS_BOUNDS_SCALE_INNER)
  obs.obs_sceneitem_set_bounds_alignment(item, 0) -- centre
  obs.obs_sceneitem_set_bounds(item, bounds)
end

local function browser(scene, name, url, width, height, x, y)
  local settings = obs.obs_data_create()
  obs.obs_data_set_string(settings, "url", url)
  obs.obs_data_set_int(settings, "width", width)
  obs.obs_data_set_int(settings, "height", height)
  obs.obs_data_set_bool(settings, "shutdown", false)
  add_source(scene, "browser_source", name, settings, x, y)
  obs.obs_data_release(settings)
end

local function add_to_scene(props, prop)
  local c = connection()
  if not c or not c.api or c.token == "" then
    obs.script_log(obs.LOG_WARNING, "Lockpick: turn on Lockpick's API (Settings -> API and profiles), then press the button again.")
    return false
  end
  local scene_source = obs.obs_frontend_get_current_scene()
  if not scene_source then return false end
  local scene = obs.obs_scene_from_source(scene_source)
  local video = obs.obs_video_info()
  obs.obs_get_video_info(video)
  local w, h = video.base_width, video.base_height
  local root = "http://127.0.0.1:" .. c.port .. "/overlay/"
  local token = "token=" .. c.token

  if options.add_game then
    local settings = obs.obs_data_create()
    obs.obs_data_set_string(settings, "capture_mode", "window")
    local exe = (c.executable ~= nil and c.executable ~= "") and c.executable or "ResidentEvil.exe"
    obs.obs_data_set_string(settings, "window", "Resident Evil:RESIDENT EVIL:" .. exe)
    obs.obs_data_set_int(settings, "priority", 2) -- match the executable
    fit_to_canvas(add_source(scene, "game_capture", "Resident Evil (game)", settings, 0, 0), w, h)
    obs.obs_data_release(settings)
  end
  local function widget(id, ww, wh, align)
    return root .. "widget?id=" .. id .. "&" .. token .. "&width=" .. ww .. "&height=" .. wh .. "&align=" .. (align or "center")
  end
  -- Widgets in the right-hand corners sit against the right edge of their box; big messages centred.
  browser(scene, "Lockpick: time, HP, room", widget("status", 420, 160, "right"), 420, 160, w - 440, 20)
  browser(scene, "Lockpick: minimap", widget("minimap", 360, 360, "right") .. "&valign=bottom", 360, 360, w - 380, h - 380)
  browser(scene, "Lockpick: enemies", widget("enemies", 320, 240, "right") .. "&valign=bottom", 320, 240, w - 760, h - 260)
  if c.livesplit then
    browser(scene, "Lockpick: splits", root .. "livesplit?" .. token .. "&width=300&height=500", 300, 500, 20, 20)
  else
    browser(scene, "Lockpick: splits", root .. "splits?" .. token, w, h, 0, 0)
  end
  browser(scene, "Lockpick: what just happened", root .. "feed?" .. token, w, h, 0, 0)
  browser(scene, "Lockpick: big messages", widget("banner", 900, 160), 900, 160, (w - 900) / 2, 40)
  obs.obs_source_release(scene_source)
  obs.script_log(obs.LOG_INFO, "Lockpick: sources added to the current scene (move and resize them as you like).")
  return false
end

-- ---------------------------------------------------------------- OBS script entry points

function script_description()
  return [[<b>Lockpick</b> — Resident Evil training and challenges on your stream.<br>
Records with each run, adds chapter markers at splits, deaths and fails, saves the replay buffer on the moments you
pick, and can switch to a "paused" scene while the game isn't in front. Its hotkeys (OBS Settings → Hotkeys,
"Lockpick: …") split, undo, skip, reset, retry a challenge, toggle the overlay or the map, or drop a capture marker.
Lockpick must be running; for the widgets, turn on its API (Settings → API and profiles).]]
end

function script_properties()
  local p = obs.obs_properties_create()
  obs.obs_properties_add_button(p, "add", "Add Lockpick to this scene", add_to_scene)
  obs.obs_properties_add_bool(p, "add_game", "…with a Game Capture of Resident Evil")
  obs.obs_properties_add_bool(p, "record_with_runs", "Record each run (start with the run, stop when it ends)")
  obs.obs_properties_add_int(p, "stop_delay", "Keep recording after a run ends (seconds)", 0, 120, 1)
  obs.obs_properties_add_bool(p, "chapters", "Chapter markers at splits, deaths and fails (Hybrid MP4, OBS 30.2+)")
  obs.obs_properties_add_bool(p, "replay_on_death", "Save the replay buffer on a death")
  obs.obs_properties_add_bool(p, "replay_on_fail", "Save the replay buffer when a challenge fails")
  obs.obs_properties_add_bool(p, "replay_on_best", "Save the replay buffer on a new personal best")
  obs.obs_properties_add_bool(p, "pause_scene_enabled", "Switch scene while the game isn't in front")
  local list = obs.obs_properties_add_list(p, "pause_scene", "\"Paused\" scene", obs.OBS_COMBO_TYPE_LIST, obs.OBS_COMBO_FORMAT_STRING)
  local scenes = obs.obs_frontend_get_scene_names()
  if scenes then
    for _, name in ipairs(scenes) do obs.obs_property_list_add_string(list, name, name) end
  end
  return p
end

function script_defaults(settings)
  obs.obs_data_set_default_bool(settings, "add_game", true)
  obs.obs_data_set_default_bool(settings, "record_with_runs", false)
  obs.obs_data_set_default_int(settings, "stop_delay", 5)
  obs.obs_data_set_default_bool(settings, "chapters", true)
  obs.obs_data_set_default_bool(settings, "replay_on_death", false)
  obs.obs_data_set_default_bool(settings, "replay_on_fail", false)
  obs.obs_data_set_default_bool(settings, "replay_on_best", true)
  obs.obs_data_set_default_bool(settings, "pause_scene_enabled", false)
end

function script_update(settings)
  options = {
    add_game = obs.obs_data_get_bool(settings, "add_game"),
    record_with_runs = obs.obs_data_get_bool(settings, "record_with_runs"),
    stop_delay = obs.obs_data_get_int(settings, "stop_delay"),
    chapters = obs.obs_data_get_bool(settings, "chapters"),
    replay_on_death = obs.obs_data_get_bool(settings, "replay_on_death"),
    replay_on_fail = obs.obs_data_get_bool(settings, "replay_on_fail"),
    replay_on_best = obs.obs_data_get_bool(settings, "replay_on_best"),
    pause_scene_enabled = obs.obs_data_get_bool(settings, "pause_scene_enabled"),
    pause_scene = obs.obs_data_get_string(settings, "pause_scene"),
  }
end

function script_load(settings)
  script_update(settings)
  for _, h in ipairs(hotkeys) do
    h.id = obs.obs_hotkey_register_frontend("lockpick_" .. h.action, h.name, function(pressed)
      if pressed then send(h.action) end
    end)
    local saved = obs.obs_data_get_array(settings, "lockpick_" .. h.action)
    obs.obs_hotkey_load(h.id, saved)
    obs.obs_data_array_release(saved)
  end
  obs.timer_add(tick, 250)
end

function script_save(settings)
  for _, h in ipairs(hotkeys) do
    if h.id then
      local saved = obs.obs_hotkey_save(h.id)
      obs.obs_data_set_array(settings, "lockpick_" .. h.action, saved)
      obs.obs_data_array_release(saved)
    end
  end
end

function script_unload()
  obs.timer_remove(tick)
end
