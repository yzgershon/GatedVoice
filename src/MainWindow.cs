using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Flow;

/// <summary>
/// Borderless window that hosts the HTML/CSS UI via WebView2. The custom title bar
/// and window controls live in the web page and call back through <see cref="FlowBridge"/>.
/// Closing hides the window (the app keeps running in the tray).
/// </summary>
public sealed class MainWindow : Form
{
    private readonly WebView2 _web = new();
    private readonly FlowBridge _bridge;
    private bool _pageReady;
    private bool _pendingScratch;

    public MainWindow(Action reloadData)
    {
        _bridge = new FlowBridge(this, reloadData);

        Text = "ShyVoice";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1180, 800);
        MinimumSize = new Size(960, 640);
        BackColor = Color.FromArgb(0xF7, 0xF5, 0xF1);
        DoubleBuffered = true;
        ShowInTaskbar = true;
        Icon = IconFactory.Create(true);

        _web.Dock = DockStyle.Fill;
        Controls.Add(_web);

        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            string udf = Path.Combine(AppSettings.DataDir, "WebView2");
            Directory.CreateDirectory(udf);
            var env = await CoreWebView2Environment.CreateAsync(null, udf, null);
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.AddHostObjectToScript("flow", _bridge);

            core.NavigationCompleted += (_, _) =>
            {
                _pageReady = true;
                if (_pendingScratch) { _pendingScratch = false; NavScratch(); }
            };

            string uiDir = Path.Combine(AppContext.BaseDirectory, "ui");
            core.SetVirtualHostNameToFolderMapping("flow.local", uiDir, CoreWebView2HostResourceAccessKind.Allow);
            core.Navigate("https://flow.local/index.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not start the ShyVoice window.\n\n" + ex.Message, "ShyVoice",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public void ShowWindow()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    // Called by the web UI's window buttons.
    public void MinimizeWindow() => WindowState = FormWindowState.Minimized;

    public void ToggleMaximize()
    {
        if (WindowState == FormWindowState.Maximized)
        {
            WindowState = FormWindowState.Normal;
        }
        else
        {
            MaximizedBounds = Screen.FromControl(this).WorkingArea;
            WindowState = FormWindowState.Maximized;
        }
    }

    public void HideWindow() => Hide();

    /// <summary>Begin a native resize from a web-page edge grip. <paramref name="edge"/> is an HT* code.</summary>
    public void StartResize(int edge)
    {
        if (WindowState == FormWindowState.Maximized) return;
        Native.ReleaseCapture();
        Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)edge, IntPtr.Zero);
    }

    /// <summary>Show the window and jump to the Scratchpad (used by the quick-thought hotkey).</summary>
    public void ShowScratchpad()
    {
        ShowWindow();
        if (_pageReady) NavScratch();
        else _pendingScratch = true;
    }

    private void NavScratch()
    {
        try { _web.CoreWebView2?.ExecuteScriptAsync("window.flowGoScratch && window.flowGoScratch()"); }
        catch { }
    }

    public void DragMove()
    {
        if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal;
        Native.ReleaseCapture();
        Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HT_CAPTION, IntPtr.Zero);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Size to the monitor's DPI so the CSS layout has comfortable room (all nav visible, not cramped).
        float s = DeviceDpi / 96f;
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        int w = Math.Min((int)(1060 * s), wa.Width - 40);
        int h = Math.Min((int)(730 * s), wa.Height - 40);
        MinimumSize = new Size((int)(820 * s), (int)(560 * s));
        ClientSize = new Size(w, h);
        Location = new Point(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2);

        // Windows 11 rounded corners on the borderless window.
        int pref = Native.DWMWCP_ROUND;
        try { Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)); }
        catch { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Keep the app alive in the tray when the user closes the window.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x0084;
        if (m.Msg == WM_NCHITTEST && WindowState == FormWindowState.Normal)
        {
            int lp = m.LParam.ToInt32();
            var pt = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
            const int g = 7;
            bool left = pt.X <= g, right = pt.X >= ClientSize.Width - g;
            bool top = pt.Y <= g, bottom = pt.Y >= ClientSize.Height - g;
            int res =
                top && left ? 13 : top && right ? 14 : bottom && left ? 16 : bottom && right ? 17 :
                left ? 10 : right ? 11 : top ? 12 : bottom ? 15 : 0;
            if (res != 0) { m.Result = (IntPtr)res; return; }
        }
        base.WndProc(ref m);
    }
}
