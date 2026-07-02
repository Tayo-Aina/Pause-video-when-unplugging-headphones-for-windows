# EarphonePause — Developer Guide

Technical documentation for contributors and developers looking to understand, modify, or extend EarphonePause.

---

## Architecture Overview

EarphonePause is a single-file C# application (~500 lines) that runs as a background Windows process. It uses COM-based audio device notifications to detect hardware changes, then pauses media through multiple APIs depending on the target application.

```
┌─────────────────────────────────────────────────────┐
│                    Program.Main()                    │
│                                                     │
│  ┌──────────┐    ┌──────────────┐    ┌───────────┐  │
│  │  Mutex    │    │  Win32 Msg   │    │  Restart  │  │
│  │ (single  │    │  Loop (COM   │    │  Timer    │  │
│  │ instance)│    │  callbacks)  │    │  (4 hrs)  │  │
│  └──────────┘    └──────┬───────┘    └───────────┘  │
│                         │                            │
│              ┌──────────▼──────────┐                 │
│              │ AudioNotification   │                 │
│              │ Client (IMMNotif.)  │                 │
│              │                     │                 │
│              │ • OnDeviceState     │                 │
│              │   Changed           │                 │
│              │ • OnDefaultDevice   │                 │
│              │   Changed           │                 │
│              │ • OnDeviceRemoved   │                 │
│              └──────────┬──────────┘                 │
│                         │                            │
│              ┌──────────▼──────────┐                 │
│              │ PausePlayingMedia   │                 │
│              │ Async()             │                 │
│              │                     │                 │
│              │ Layer 1: GSMTC      │ ← Spotify,     │
│              │  TryPauseAsync()    │   browsers, etc │
│              │                     │                 │
│              │ Layer 2: VLC HTTP   │ ← pl_forcepause │
│              │  (port 8080)        │                 │
│              │                     │                 │
│              │ Layer 3: VLC        │ ← WM_APPCOMMAND│
│              │  Window Message     │   fallback      │
│              └─────────────────────┘                 │
└─────────────────────────────────────────────────────┘
```

---

## Tech Stack

| Component | Technology |
|-----------|-----------|
| Language | C# (.NET 9) |
| Target | `net9.0-windows10.0.17763.0` |
| Audio API | NAudio.Core + NAudio.Wasapi (v2.3.0) — provides `IMMNotificationClient` |
| Media Control | Windows.Media.Control (GSMTC / SMTC) — WinRT API |
| Win32 Interop | P/Invoke for message loop, `SendMessage`, `EnumWindows` |
| VLC Integration | HTTP REST API (`/requests/status.xml`) |
| Output Type | WinExe (no console window) |
| Publishing | Single-file, self-contained (`win-x64`) |

---

## Project Structure

```
Earphone pause when unplugged/
├── EarphonePause/                  ← Source code (this folder)
│   ├── EarphonePause.csproj        ← Project file, NuGet dependencies
│   ├── Program.cs                  ← All application logic (single file)
│   └── README.md                   ← This file
├── EarphonePause.exe               ← Published self-contained binary
├── EarphonePause.log               ← Runtime log (auto-created)
├── Killswitch.bat                  ← Stops process + removes from startup
├── SetupShortcut.ps1               ← Creates startup shortcut + launches
└── README.md                       ← User-facing documentation
```

---

## Key Concepts

### 1. Device Change Detection

The app implements `IMMNotificationClient` from the Windows Core Audio API (via NAudio). This interface receives callbacks whenever audio endpoint devices are added, removed, or change state.

**Relevant callbacks:**

| Callback | When it fires | Action taken |
|----------|--------------|--------------|
| `OnDeviceStateChanged` → `Active` | Device plugged in | Record timestamp, do NOT pause |
| `OnDeviceStateChanged` → `Unplugged`/`NotPresent` | Device unplugged | Trigger pause |
| `OnDefaultDeviceChanged` | Default audio output changed | Pause only if NOT a plug-in event |
| `OnDeviceRemoved` | Device physically removed | Trigger pause |

### 2. Context-Aware Plug vs Unplug

A `ConcurrentDictionary<string, DateTime>` tracks recently-activated device IDs. When `OnDeviceStateChanged` fires with `Active`, the device ID and timestamp are stored. When `OnDefaultDeviceChanged` fires shortly after (within 1500ms), the app checks whether the new default device was just activated — if so, it's a **plug-in** event and pausing is skipped.

This prevents the common bug where plugging earphones in causes a pause/toggle.

### 3. Debouncing

A single physical plug/unplug generates 4-10 rapid-fire callbacks (multiple device IDs, multiple roles). A 2-second debounce window ensures only the first callback triggers a pause action.

### 4. Media Pause Strategy

The pause logic runs in three layers, executed sequentially:

**Layer 1 — GSMTC (Global System Media Transport Controls)**
- Uses the WinRT `GlobalSystemMediaTransportControlsSessionManager` API
- Enumerates all registered media sessions
- For each session with `PlaybackStatus.Playing`, calls `TryPauseAsync()` — this is a **dedicated pause**, not a play/pause toggle
- Works with: Spotify, browsers (Chrome/Edge/Firefox), Windows media apps, and any app registering with SMTC
- Does NOT work with: VLC (which doesn't register with SMTC)

**Layer 2 — VLC HTTP Interface**
- Checks if `vlc.exe` is running via `Process.GetProcessesByName()`
- Attempts to connect to VLC's HTTP interface on `127.0.0.1:8080`
- Checks playback state via `/requests/status.xml`
- If playing, sends `pl_forcepause` — a **dedicated pause command** (not a toggle)
- Uses Basic Auth with password configured in `VlcHttpPassword` constant
- Times out after 1 second if HTTP interface isn't enabled

**Layer 3 — VLC Window Message (fallback)**
- Sends `WM_APPCOMMAND` with `APPCOMMAND_MEDIA_PAUSE` to VLC's main window
- Finds the window via `Process.MainWindowHandle` or `EnumWindows` fallback
- Note: VLC may or may not respond to this depending on version

### 5. Self-Restart Mechanism

A `System.Threading.Timer` triggers after 4 hours. The restart process:
1. Releases the named mutex (`EarphonePause_BackgroundApp_Mutex`)
2. Disposes the mutex
3. Spawns a new instance via `Process.Start()`
4. Posts `WM_QUIT` to the main thread to break the message loop
5. The old process exits gracefully

### 6. Single Instance Enforcement

A named `Mutex` ensures only one copy runs at a time. If a second instance detects the mutex is already held, it logs "Another instance already running" and exits immediately.

---

## Building

### Prerequisites
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)

### Build Commands

**Debug build (for development):**
```bash
cd EarphonePause
dotnet build
```

**Production single-file build:**
```bash
cd EarphonePause
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o ..
```

This produces a self-contained `EarphonePause.exe` (~70-90 MB) in the parent directory. No .NET runtime installation needed on the target machine.

### Switching to Console Mode (for debugging)

In `EarphonePause.csproj`, change:
```xml
<OutputType>WinExe</OutputType>
```
to:
```xml
<OutputType>Exe</OutputType>
```
This lets you see stdout/stderr output in a console window. Remember to switch back to `WinExe` before publishing.

---

## Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| `NAudio.Core` | 2.3.0 | Core audio types and interfaces |
| `NAudio.Wasapi` | 2.3.0 | `MMDeviceEnumerator`, `IMMNotificationClient` for device change detection |

The Windows.Media.Control (GSMTC) API comes from the Windows SDK target framework (`net9.0-windows10.0.17763.0`) and requires no additional NuGet package.

---

## Extending

### Adding Support for a New Media Player

If a media player doesn't register with SMTC, you can add a custom pause handler following the VLC pattern:

1. Add a new `PauseXxxAsync()` method in `Program.cs`
2. Check if the player's process is running
3. Use the player's API/IPC mechanism to send a pause command
4. Call it from `PausePlayingMediaAsync()` after the GSMTC loop
5. Log the result using `Log()`

### Changing the Restart Interval

Modify the `RestartInterval` constant in `Program.cs`:
```csharp
private static readonly TimeSpan RestartInterval = TimeSpan.FromHours(4);
```

### Changing the VLC HTTP Password

Modify the `VlcHttpPassword` constant in `Program.cs`:
```csharp
private const string VlcHttpPassword = "vlcremote";
```
This must match the password set in VLC's Lua HTTP preferences.

### Changing the Debounce Window

Modify the value in `AudioNotificationClient.TryPause()`:
```csharp
if (elapsed < 2000) // 2-second debounce window
```

### Changing the Plug-in Detection Window

Modify the value in `AudioNotificationClient.OnDefaultDeviceChanged()`:
```csharp
(DateTime.Now - activatedAt).TotalMilliseconds < 1500
```

---

## Logging

All activity is logged to `EarphonePause.log` in the same directory as the executable.

**Log format:**
```
[YYYY-MM-DD HH:mm:ss] message
```

**Key log patterns to look for:**

| Log message | Meaning |
|------------|---------|
| `=== EarphonePause started ===` | Fresh start or restart |
| `Registered audio device notification callback` | Successfully listening |
| `Event: DeviceStateChanged ... state=Active` | Device plugged in |
| `↳ Device activated (plug-in), not pausing` | Plug-in correctly ignored |
| `Event: DeviceStateChanged ... state=Unplugged` | Device unplugged |
| `→ Pause triggered` | Pause action initiated |
| `Found N SMTC media session(s)` | GSMTC enumeration |
| `✓ Paused 'appname'` | Successfully paused an app |
| `– Skipped 'appname' (status: Paused)` | Context-aware skip (already paused) |
| `↳ Skipped: device was just plugged in` | Plug-in event correctly ignored |
| `VLC: Paused via HTTP pl_forcepause` | VLC paused via HTTP |
| `VLC: HTTP interface timed out` | VLC HTTP not enabled |
| `=== Scheduled restart triggered ===` | 4-hour self-restart |

The log is auto-trimmed at 1 MB (keeps last 200 lines).

---

## Known Limitations

1. **VLC without HTTP interface**: Falls back to `WM_APPCOMMAND` which VLC may ignore depending on version. Enable the HTTP interface for reliable control.
2. **Apps not registering with SMTC**: Some older or niche media players may not expose SMTC sessions. Custom handlers must be added (see [Extending](#extending)).
3. **Bluetooth disconnection detection**: Bluetooth device disconnections may fire different event sequences. The debounce window handles most cases, but timing may need tuning.
4. **No plug-in resume**: The app only pauses on unplug. It does not automatically resume playback when earphones are plugged back in (this is intentional).
