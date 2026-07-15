using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Flow;

/// <summary>
/// Inserts text into whatever app currently has focus. In most apps it does this
/// by putting the text on the clipboard and sending Ctrl+V, then restoring the
/// previous clipboard contents. Terminals ignore a plain Ctrl+V, so there the text
/// is instead "typed" as real Unicode keystrokes via SendInput.
/// Runs on a dedicated STA thread so clipboard access is legal and the UI never blocks.
/// </summary>
public static class TextInjector
{
    private const byte VK_CONTROL = 0x11;
    private const byte VK_V = 0x56;

    // Terminal emulators that don't paste on a bare Ctrl+V. Matched against the
    // foreground process name (case-insensitive) when the window class isn't a
    // dead giveaway on its own.
    private static readonly HashSet<string> TerminalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "WindowsTerminal", "wt", "conhost",
        "alacritty", "wezterm", "mintty", "putty", "ConEmu", "ConEmu64",
        "Hyper", "Tabby", "kitty", "xterm", "rxvt", "cmder",
        // Electron terminals share the Chrome_WidgetWin_1 window class with every
        // other Chromium app, so they can only be spotted by process name.
        "GatedSpace",
    };

    /// <param name="paste">
    /// When true, use the fast clipboard-paste path in normal apps (terminals still
    /// get typed). When false, always type the text out as keystrokes.
    /// </param>
    public static void Insert(string text, bool paste = true)
    {
        if (string.IsNullOrEmpty(text)) return;

        // A plain Ctrl+V is swallowed by most terminals (it's interpreted as a raw
        // control character, not "paste"), so fall back to typing the text there
        // even when paste is the configured default.
        bool type = !paste || TargetIgnoresPaste();

        var t = new Thread(() => { if (type) DoType(text); else DoPaste(text); })
            { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    private static void DoPaste(string text)
    {
        string? backup = null;
        try { if (Clipboard.ContainsText()) backup = Clipboard.GetText(); } catch { }

        if (!TrySetClipboard(text))
        {
            Thread.Sleep(40);
            TrySetClipboard(text);
        }

        Thread.Sleep(30);
        Native.keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        Native.keybd_event(VK_V, 0, 0, UIntPtr.Zero);
        Native.keybd_event(VK_V, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Native.keybd_event(VK_CONTROL, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);

        Thread.Sleep(140);
        if (backup != null) TrySetClipboard(backup);
    }

    /// <summary>
    /// Types <paramref name="text"/> as a single atomic burst of synthesized
    /// Unicode keystrokes. Works everywhere Ctrl+V might not — terminals especially —
    /// and never touches the clipboard.
    /// </summary>
    private static void DoType(string text)
    {
        var inputs = new List<Native.INPUT>(text.Length * 2);
        foreach (char c in text)
        {
            if (c == '\r') continue;                 // fold CRLF into a single Enter
            if (c == '\n')
            {
                inputs.Add(KeyInput(Native.VK_RETURN, down: true));
                inputs.Add(KeyInput(Native.VK_RETURN, down: false));
                continue;
            }
            inputs.Add(UnicodeInput(c, down: true));
            inputs.Add(UnicodeInput(c, down: false));
        }

        if (inputs.Count == 0) return;
        Native.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Native.INPUT>());
    }

    private static Native.INPUT UnicodeInput(char c, bool down) => new()
    {
        type = Native.INPUT_KEYBOARD,
        U = new Native.InputUnion
        {
            ki = new Native.KEYBDINPUT
            {
                wVk = 0,
                wScan = c,
                dwFlags = Native.KEYEVENTF_UNICODE | (down ? 0 : Native.KEYEVENTF_KEYUP),
            }
        }
    };

    private static Native.INPUT KeyInput(ushort vk, bool down) => new()
    {
        type = Native.INPUT_KEYBOARD,
        U = new Native.InputUnion
        {
            ki = new Native.KEYBDINPUT
            {
                wVk = vk,
                dwFlags = down ? 0 : Native.KEYEVENTF_KEYUP,
            }
        }
    };

    /// <summary>True when the focused window is a terminal that won't honour Ctrl+V.</summary>
    private static bool TargetIgnoresPaste()
    {
        try
        {
            IntPtr hwnd = Native.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;

            var sb = new StringBuilder(256);
            Native.GetClassName(hwnd, sb, sb.Capacity);
            switch (sb.ToString())
            {
                case "ConsoleWindowClass":            // cmd, PowerShell, Python REPL, Git Bash…
                case "CASCADIA_HOSTING_WINDOW_CLASS": // Windows Terminal
                    return true;
            }

            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return false;
            using var p = Process.GetProcessById((int)pid);
            return TerminalProcesses.Contains(p.ProcessName);
        }
        catch { return false; }
    }

    private static bool TrySetClipboard(string text)
    {
        try { Clipboard.SetText(text); return true; }
        catch { return false; }
    }
}
