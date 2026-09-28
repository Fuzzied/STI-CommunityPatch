// UI Fixes Early Window 1.0.0, a BepInEx patcher that ships with UI Fixes.
//
// Fuzzied, 26.09.2026: "Reposition has a little delay before it moves the window
// to the correct position", then "yes, build the patcher".
//
// UI Fixes 1.12.0 puts the game window back where it was last closed, but a
// plugin cannot do it any sooner than plugins load. Measured by polling the
// window every 10 ms from launch: Unity shows the window at 4.4 s in its own
// default spot, BepInEx starts loading plugins at 7.0 s, and the window moved
// at about 8.0 s. So for about 3.6 s every launch it sat in the wrong place.
//
// A patcher lives in BepInEx\patchers instead of BepInEx\plugins, and BepInEx
// runs it at about 0.6 s, before the window even exists. It patches nothing:
// it only starts a background thread that waits for the game's window to be
// shown and moves it the moment it is, which measured within 10 ms, too soon
// to see. Then the thread ends.
//
// It reads the spot UI Fixes saved and follows the same rules, so the two can
// never disagree about where the window belongs:
//   - Only when UI Fixes' RememberPosition is on and a LastPosition is saved.
//   - Windows only. Mac players get the same file and nothing here runs.
//   - Windowed only. A window with no title bar is full screen or borderless,
//     and a maximized or minimized one is left alone too.
//   - Only if the middle of the title bar would land on a screen that is
//     connected right now.
// UI Fixes itself still does everything else: it saves the spot while the
// game runs, and it still moves the window back if this file is missing, so
// this only makes the move earlier.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil;

[assembly: AssemblyVersion("1.0.0")]
[assembly: AssemblyFileVersion("1.0.0")]

public static class UIFixesEarlyWindow
{
    public const string Version = "1.0.0";

    // A patcher has to say which game assemblies it patches. None.
    public static IEnumerable<string> TargetDLLs { get { return new string[0]; } }

    public static void Patch(AssemblyDefinition assembly) { }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private const int GWL_STYLE = -16;
    private const int WS_CAPTION = 0x00C00000;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    // Asks the game's own thread to do the move instead of waiting for it,
    // since that thread is busy loading the game at this point.
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;
    private const uint MONITOR_DEFAULTTONULL = 0;
    // How long to wait for the window before giving up.
    private const int WAIT_SECONDS = 60;

    private static uint myPid;
    private static IntPtr window = IntPtr.Zero;
    // Kept in a field so the delegate cannot be collected while Windows is
    // still calling it back.
    private static readonly EnumProc matcher = Match;
    private static ManualLogSource log;

    public static void Initialize()
    {
        try
        {
            log = Logger.CreateLogSource("UI Fixes Early Window");
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                return;
            }
            int x, y;
            if (!SavedSpot(out x, out y))
            {
                return;
            }
            myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            Thread t = new Thread(() => Watch(x, y));
            t.IsBackground = true;
            t.Name = "UI Fixes Early Window";
            t.Start();
        }
        catch (Exception e)
        {
            Say("could not start, UI Fixes will move the window instead: " + e.Message);
        }
    }

    // UI Fixes' own settings, read as text because its config is not loaded
    // yet. False when remembering is off or nothing is saved.
    private static bool SavedSpot(out int x, out int y)
    {
        x = y = 0;
        string path = Path.Combine(Paths.ConfigPath, "sti.community.uifixes.cfg");
        if (!File.Exists(path))
        {
            return false;
        }
        bool inWindow = false;
        bool remember = true;
        string spot = null;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.StartsWith("["))
            {
                inWindow = line == "[Window]";
                continue;
            }
            if (!inWindow || line.StartsWith("#"))
            {
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }
            string key = line.Substring(0, eq).Trim();
            string value = line.Substring(eq + 1).Trim();
            if (key == "RememberPosition")
            {
                remember = !value.Equals("false", StringComparison.OrdinalIgnoreCase);
            }
            else if (key == "LastPosition")
            {
                spot = value;
            }
        }
        if (!remember || string.IsNullOrEmpty(spot))
        {
            return false;
        }
        string[] parts = spot.Split(',');
        return parts.Length == 2
            && int.TryParse(parts[0].Trim(), out x)
            && int.TryParse(parts[1].Trim(), out y);
    }

    private static void Watch(int x, int y)
    {
        string result;
        try
        {
            result = MoveWhenShown(x, y);
        }
        catch (Exception e)
        {
            result = "gave up, UI Fixes will move the window instead: " + e.Message;
        }
        // Nothing written before BepInEx has opened LogOutput.log reaches it,
        // and the window shows a couple of seconds before that. So the line
        // waits for the log file to be listening.
        for (int i = 0; i < WAIT_SECONDS * 10 && !LogReady(); i++)
        {
            Thread.Sleep(100);
        }
        Say(result);
    }

    private static string MoveWhenShown(int x, int y)
    {
        DateTime giveUp = DateTime.Now.AddSeconds(WAIT_SECONDS);
        while (DateTime.Now < giveUp)
        {
            if (window == IntPtr.Zero)
            {
                EnumWindows(matcher, IntPtr.Zero);
            }
            if (window != IntPtr.Zero && IsWindowVisible(window))
            {
                return Move(x, y);
            }
            Thread.Sleep(5);
        }
        return "the game window did not show within " + WAIT_SECONDS
            + " s, left to UI Fixes.";
    }

    private static string Move(int x, int y)
    {
        RECT r;
        if ((GetWindowLong(window, GWL_STYLE) & WS_CAPTION) != WS_CAPTION
            || IsIconic(window) || IsZoomed(window)
            || !GetWindowRect(window, out r))
        {
            return "not moved, the game is not in a normal window.";
        }
        if (r.Left == x && r.Top == y)
        {
            return "the window opened at " + x + "," + y + " already.";
        }
        POINT grip = new POINT { X = x + (r.Right - r.Left) / 2, Y = y + 10 };
        if (MonitorFromPoint(grip, MONITOR_DEFAULTTONULL) == IntPtr.Zero)
        {
            return x + "," + y + " is not on any connected screen, left at "
                + r.Left + "," + r.Top + ".";
        }
        SetWindowPos(window, IntPtr.Zero, x, y, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
        return "moved the window to " + x + "," + y + " as it appeared (Unity "
            + "opened it at " + r.Left + "," + r.Top + ").";
    }

    private static bool LogReady()
    {
        try
        {
            return Logger.Listeners.OfType<DiskLogListener>().Any();
        }
        catch (InvalidOperationException)
        {
            // The list changed while being read, which means BepInEx is
            // setting its logging up right now. Ask again next time.
            return false;
        }
    }

    private static void Say(string text)
    {
        if (log != null)
        {
            log.LogInfo("v" + Version + " " + text);
        }
    }

    // The game's own top level window: this process, Unity's window class.
    private static bool Match(IntPtr hWnd, IntPtr lParam)
    {
        uint pid;
        GetWindowThreadProcessId(hWnd, out pid);
        if (pid != myPid)
        {
            return true;
        }
        StringBuilder name = new StringBuilder(64);
        GetClassName(hWnd, name, name.Capacity);
        if (name.ToString() == "UnityWndClass")
        {
            window = hWnd;
            return false;
        }
        return true;
    }
}
