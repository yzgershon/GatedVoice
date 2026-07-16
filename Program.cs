using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Flow;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Whisper's native worker threads inherit the process priority. A small
        // boost keeps short dictations responsive when the desktop and terminal
        // are busy, while GatedVoice remains idle between captures.
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal; }
        catch { /* some managed environments do not permit priority changes */ }

        using var mutex = new Mutex(true, "GatedVoice_SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("GatedVoice is already running. Look for the mic icon in your system tray.",
                "GatedVoice", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Render crisply on high-DPI screens instead of being bitmap-upscaled.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        // Never pop a modal crash dialog; log to a file instead.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => LogError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogError(e.ExceptionObject as Exception);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var ctx = new FlowContext();
        Application.Run(ctx);
    }

    private static void LogError(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            File.AppendAllText(Path.Combine(AppSettings.DataDir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { /* ignore */ }
    }
}
