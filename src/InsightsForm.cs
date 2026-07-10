using System;
using System.Drawing;
using System.Windows.Forms;

namespace Flow;

/// <summary>A small dark card showing dictation stats.</summary>
public sealed class InsightsForm : Form
{
    public InsightsForm(Insights i)
    {
        Text = "ShyVoice — Insights";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(420, 260);
        BackColor = Color.FromArgb(24, 24, 27);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10f);

        var tiles = new (string Value, string Label)[]
        {
            (i.TotalWords.ToString("N0"), "TOTAL WORDS"),
            (Math.Round(i.AverageWpm).ToString("N0"), "AVG WPM"),
            (i.TodayWords.ToString("N0"), "WORDS TODAY"),
            (i.CurrentStreak.ToString(), "DAY STREAK"),
            (i.TotalDictations.ToString("N0"), "DICTATIONS"),
            (i.LongestStreak.ToString(), "LONGEST STREAK"),
        };

        int cols = 3, pad = 16, tw = (ClientSize.Width - pad * (cols + 1)) / cols, th = 96;
        for (int idx = 0; idx < tiles.Length; idx++)
        {
            int r = idx / cols, c = idx % cols;
            var panel = new Panel
            {
                BackColor = Color.FromArgb(34, 34, 38),
                Location = new Point(pad + c * (tw + pad), pad + r * (th + pad)),
                Size = new Size(tw, th),
            };
            var value = new Label
            {
                Text = tiles[idx].Value,
                Font = new Font("Segoe UI Semibold", 20f, FontStyle.Bold),
                ForeColor = Color.FromArgb(0x3D, 0xC8, 0x9A),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Fill,
                Height = 56,
            };
            var label = new Label
            {
                Text = tiles[idx].Label,
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(150, 150, 155),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Bottom,
                Height = 26,
            };
            panel.Controls.Add(value);
            panel.Controls.Add(label);
            Controls.Add(panel);
        }
    }
}
