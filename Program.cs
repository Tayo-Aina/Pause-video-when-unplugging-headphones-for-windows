using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Windows.Media.Control;

namespace EarphonePause
{
    class Program
    {
        // --- Win32 message loop interop ---
        [DllImport("user32.dll")]
        static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
        [DllImport("user32.dll")]
        static extern bool TranslateMessage(ref MSG lpMsg);
        [DllImport("user32.dll")]
        static extern IntPtr DispatchMessage(ref MSG lpMsg);
        [DllImport("user32.dll")]
        static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();

        // --- Win32 interop for VLC window fallback ---
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);
        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const uint WM_APPCOMMAND = 0x0319;
        // APPCOMMAND_MEDIA_PAUSE = 47, shifted into lParam format
        private static readonly IntPtr APPCOMMAND_MEDIA_PAUSE_LPARAM = (IntPtr)(47 << 16);

        [StructLayout(LayoutKind.Sequential)]
        struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public POINT pt; }
        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int x; public int y; }

        private const uint WM_QUIT = 0x0012;

        private static Mutex? _mutex;
        private static uint _mainThreadId;

        // Self-restart every 4 hours to prevent memory leaks
        private static readonly TimeSpan RestartInterval = TimeSpan.FromHours(4);

        // VLC HTTP interface password (user should set this to match their VLC config)
        private const string VlcHttpPassword = "vlcremote";

        private static readonly string LogPath = GetLogPath();

        static string GetLogPath()
        {
            try
            {
                string? exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    string? dir = Path.GetDirectoryName(exePath);
                    if (!string.IsNullOrEmpty(dir))
                        return Path.Combine(dir, "EarphonePause.log");
                }
            }
            catch { }
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EarphonePause", "EarphonePause.log");
        }

        [STAThread]
        static void Main(string[] args)
        {
            // Trim the log file if it gets too large (> 1MB)
            TrimLogFile();

            bool createdNew;
            _mutex = new Mutex(true, "EarphonePause_BackgroundApp_Mutex", out createdNew);
            if (!createdNew)
            {
                Log("Another instance already running. Exiting.");
                return;
            }

            try
            {
                Log("=== EarphonePause started ===");
                Log($"Process ID: {Environment.ProcessId}");
                Log($"Next scheduled restart in {RestartInterval.TotalHours} hours.");
                _mainThreadId = GetCurrentThreadId();

                var enumerator = new MMDeviceEnumerator();
                var client = new AudioNotificationClient();
                enumerator.RegisterEndpointNotificationCallback(client);
                Log("Registered audio device notification callback. Listening...");

                // Schedule periodic self-restart to prevent memory leaks
                var restartTimer = new System.Threading.Timer(_ =>
                {
                    Log($"=== Scheduled restart triggered after {RestartInterval.TotalHours} hours ===");
                    DoRestart();
                }, null, RestartInterval, Timeout.InfiniteTimeSpan);

                // Win32 message loop — required for COM callbacks to be delivered
                MSG msg;
                while (GetMessage(out msg, IntPtr.Zero, 0, 0))
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }

                Log("Message loop exited. Cleaning up...");
                restartTimer.Dispose();
                enumerator.UnregisterEndpointNotificationCallback(client);
            }
            catch (Exception ex)
            {
                Log($"Fatal error: {ex}");
            }
            finally
            {
                try { _mutex?.ReleaseMutex(); } catch { }
                _mutex?.Dispose();
                _mutex = null;
            }
        }

        /// <summary>
        /// Releases the mutex, spawns a new instance of ourselves, then quits.
        /// </summary>
        static void DoRestart()
        {
            try
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                {
                    Log("Cannot restart: unable to determine own exe path.");
                    return;
                }

                // Release the mutex so the new instance can acquire it
                try { _mutex?.ReleaseMutex(); } catch { }
                _mutex?.Dispose();
                _mutex = null;

                Log($"Launching new instance: {exePath}");
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log($"Restart failed: {ex.Message}");
            }

            // Break the message loop to exit this process
            PostThreadMessage(_mainThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>
        /// Context-aware pause using Windows GSMTC (Global System Media Transport Controls).
        /// Only pauses sessions that are CURRENTLY PLAYING — never toggles paused media back on.
        /// Uses TryPauseAsync() which sends a dedicated PAUSE command, not a play/pause toggle.
        /// Works with Spotify, browsers, and any app that registers with SMTC.
        /// For VLC (which doesn't register with SMTC), a separate fallback is used.
        /// </summary>
        public static async Task PausePlayingMediaAsync()
        {
            try
            {
                var sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                var sessions = sessionManager.GetSessions();
                int pausedCount = 0;

                Log($"  Found {sessions.Count} SMTC media session(s).");

                foreach (var session in sessions)
                {
                    string appId = session.SourceAppUserModelId ?? "unknown";
                    try
                    {
                        var info = session.GetPlaybackInfo();
                        var status = info?.PlaybackStatus;

                        if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        {
                            bool result = await session.TryPauseAsync();
                            Log($"  ✓ Paused '{appId}' (was Playing) — success: {result}");
                            pausedCount++;
                        }
                        else
                        {
                            Log($"  – Skipped '{appId}' (status: {status})");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"  ✗ Error pausing '{appId}': {ex.Message}");
                    }
                }

                if (pausedCount == 0)
                    Log("  No SMTC sessions were actively playing.");
                else
                    Log($"  Paused {pausedCount} SMTC session(s).");
            }
            catch (Exception ex)
            {
                Log($"  GSMTC error: {ex.Message}");
            }

            // VLC fallback — VLC doesn't register with SMTC
            await PauseVlcAsync();
        }

        /// <summary>
        /// Pauses VLC media player using a multi-layer approach:
        /// Layer 1: HTTP interface with pl_forcepause (dedicated pause, not toggle)
        /// Layer 2: WM_APPCOMMAND sent to VLC window (fallback)
        /// </summary>
        static async Task PauseVlcAsync()
        {
            var vlcProcesses = Process.GetProcessesByName("vlc");
            if (vlcProcesses.Length == 0)
            {
                Log("  VLC: not running, skipped.");
                return;
            }

            Log($"  VLC: detected {vlcProcesses.Length} instance(s). Attempting pause...");

            // Layer 1: VLC HTTP interface with pl_forcepause (best — dedicated pause)
            bool httpPaused = await TryPauseVlcHttpAsync();
            if (httpPaused) return;

            // Layer 2: Send WM_APPCOMMAND to VLC window
            TryPauseVlcWindowMessage(vlcProcesses);
        }

        /// <summary>
        /// Attempts to pause VLC via its HTTP web interface.
        /// Uses pl_forcepause which is a dedicated pause (does nothing if already paused).
        /// Requires VLC to have the Web interface enabled with password set to VlcHttpPassword.
        /// </summary>
        static async Task<bool> TryPauseVlcHttpAsync()
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
                var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{VlcHttpPassword}"));
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Basic", auth);

                // Check current state first
                var statusResponse = await client.GetStringAsync(
                    "http://127.0.0.1:8080/requests/status.xml");

                if (statusResponse.Contains("<state>playing</state>"))
                {
                    await client.GetAsync(
                        "http://127.0.0.1:8080/requests/status.xml?command=pl_forcepause");
                    Log("  ✓ VLC: Paused via HTTP pl_forcepause (was Playing)");
                    return true;
                }
                else
                {
                    Log("  – VLC: HTTP reports not playing, skipped.");
                    return true; // Successfully checked — VLC isn't playing, no action needed
                }
            }
            catch (TaskCanceledException)
            {
                Log("  – VLC: HTTP interface timed out (likely not enabled).");
            }
            catch (HttpRequestException)
            {
                Log("  – VLC: HTTP interface not reachable (likely not enabled).");
            }
            catch (Exception ex)
            {
                Log($"  – VLC: HTTP error: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Fallback: Sends WM_APPCOMMAND with APPCOMMAND_MEDIA_PAUSE to VLC's main window.
        /// Note: VLC may or may not respond to this depending on version and configuration.
        /// </summary>
        static void TryPauseVlcWindowMessage(Process[] vlcProcesses)
        {
            try
            {
                foreach (var proc in vlcProcesses)
                {
                    try
                    {
                        IntPtr mainWindow = proc.MainWindowHandle;
                        if (mainWindow == IntPtr.Zero)
                        {
                            // Try to find VLC window by enumerating windows for this process
                            mainWindow = FindMainWindowForProcess((uint)proc.Id);
                        }

                        if (mainWindow != IntPtr.Zero)
                        {
                            SendMessage(mainWindow, WM_APPCOMMAND, mainWindow, APPCOMMAND_MEDIA_PAUSE_LPARAM);
                            Log($"  ✓ VLC: Sent APPCOMMAND_MEDIA_PAUSE to window 0x{mainWindow:X}");
                        }
                        else
                        {
                            Log($"  ✗ VLC: Could not find window for PID {proc.Id}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"  ✗ VLC: Window message error for PID {proc.Id}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"  ✗ VLC: Fallback failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Finds the main visible window for a given process ID by enumerating all windows.
        /// </summary>
        static IntPtr FindMainWindowForProcess(uint processId)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == processId && IsWindowVisible(hWnd))
                {
                    var sb = new StringBuilder(256);
                    GetWindowText(hWnd, sb, sb.Capacity);
                    string title = sb.ToString();
                    // VLC's main window typically has a title (video name or "VLC media player")
                    if (title.Length > 0)
                    {
                        found = hWnd;
                        return false; // Stop enumerating
                    }
                }
                return true; // Continue enumerating
            }, IntPtr.Zero);
            return found;
        }

        public static void Log(string message)
        {
            try
            {
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch { }
        }

        static void TrimLogFile()
        {
            try
            {
                if (File.Exists(LogPath))
                {
                    var fi = new FileInfo(LogPath);
                    if (fi.Length > 1_048_576) // > 1 MB
                    {
                        // Keep the last 200 lines
                        var lines = File.ReadAllLines(LogPath);
                        int keep = Math.Min(200, lines.Length);
                        File.WriteAllLines(LogPath, lines[^keep..]);
                    }
                }
            }
            catch { }
        }
    }

    class AudioNotificationClient : IMMNotificationClient
    {
        private DateTime _lastPause = DateTime.MinValue;
        private readonly object _lock = new object();

        // Track recently-activated devices to distinguish plug-in from unplug events
        private readonly ConcurrentDictionary<string, DateTime> _recentlyActivated = new();

        private void TryPause(string reason)
        {
            lock (_lock)
            {
                var elapsed = (DateTime.Now - _lastPause).TotalMilliseconds;
                if (elapsed < 2000) // 2-second debounce window
                {
                    Program.Log($"  (debounced, {elapsed:F0}ms since last) {reason}");
                    return;
                }
                _lastPause = DateTime.Now;
            }

            Program.Log($"→ Pause triggered: {reason}");
            // Run async pause on a thread pool thread (we're on a COM callback thread)
            Task.Run(() => Program.PausePlayingMediaAsync());
        }

        /// <summary>
        /// Periodically clean up stale entries from the recently-activated dictionary
        /// to prevent unbounded growth.
        /// </summary>
        private void CleanupStaleEntries()
        {
            var cutoff = DateTime.Now.AddSeconds(-5);
            foreach (var kvp in _recentlyActivated)
            {
                if (kvp.Value < cutoff)
                    _recentlyActivated.TryRemove(kvp.Key, out _);
            }
        }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            Program.Log($"Event: DefaultDeviceChanged flow={flow} role={role} id={defaultDeviceId}");
            if (flow == DataFlow.Render)
            {
                // If this device was just activated (plug-in event), skip pausing.
                // When earphones are plugged in, the sequence is:
                //   1. DeviceStateChanged → Active (for the earphone device)
                //   2. DefaultDeviceChanged → new default is the earphone device
                // We only want to pause when earphones are UNPLUGGED, not plugged in.
                if (!string.IsNullOrEmpty(defaultDeviceId) &&
                    _recentlyActivated.TryGetValue(defaultDeviceId, out var activatedAt) &&
                    (DateTime.Now - activatedAt).TotalMilliseconds < 1500)
                {
                    Program.Log($"  ↳ Skipped: device was just plugged in ({(DateTime.Now - activatedAt).TotalMilliseconds:F0}ms ago)");
                    return;
                }

                TryPause($"Default render device changed → {defaultDeviceId}");
            }
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            Program.Log($"Event: DeviceStateChanged id={deviceId} state={newState}");

            if (newState == DeviceState.Active)
            {
                // Device was plugged in — record timestamp, do NOT pause
                _recentlyActivated[deviceId] = DateTime.Now;
                Program.Log($"  ↳ Device activated (plug-in), not pausing.");
                CleanupStaleEntries();
                return;
            }

            if (newState == DeviceState.Unplugged || newState == DeviceState.NotPresent)
            {
                // Device was unplugged — remove from recently-activated and trigger pause
                _recentlyActivated.TryRemove(deviceId, out _);
                TryPause($"Device state → {newState}: {deviceId}");
            }
        }

        public void OnDeviceRemoved(string deviceId)
        {
            Program.Log($"Event: DeviceRemoved id={deviceId}");
            _recentlyActivated.TryRemove(deviceId, out _);
            TryPause($"Device removed: {deviceId}");
        }

        public void OnDeviceAdded(string pwstrDeviceId)
        {
            Program.Log($"Event: DeviceAdded id={pwstrDeviceId}");
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
