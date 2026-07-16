using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Flow;

/// <summary>
/// The running app: owns the tray icon, the global hotkeys, the recorder,
/// the transcriber and the overlay, and drives the dictation loop.
/// </summary>
public sealed class FlowContext : ApplicationContext
{
    private readonly AppSettings _settings;
    private readonly NotifyIcon _tray;
    private readonly AudioRecorder _recorder = new();
    private readonly OverlayForm _overlay = new();

    private FlowDictionary _dict;
    private Snippets _snips;
    private Insights _insights;
    private AiTransformer _ai;
    private readonly History _history = History.Load();
    private MainWindow? _main;

    private HotKeyListener? _dictateKey;
    private HotKeyListener? _polishKey;
    private HotKeyListener? _scratchKey;
    private Transcriber? _transcriber;
    private Transcriber? _liveTranscriber;

    private enum CaptureMode { Dictate, Polish, Scratch }

    private ToolStripMenuItem _statusItem = null!;
    private ToolStripMenuItem _enabledItem = null!;
    private bool _busy;
    private bool _modelReady;
    private CaptureMode _mode;
    private DateTime _captureStart;
    private TextInjector.TargetSnapshot? _textTarget;
    private CancellationTokenSource? _partialCts;
    private Task? _partialTask;

    public FlowContext()
    {
        _settings = AppSettings.Load();
        _dict = FlowDictionary.Load();
        _snips = Snippets.Load();
        _insights = Insights.Load();
        _ai = new AiTransformer(_settings);

        _tray = new NotifyIcon
        {
            Icon = IconFactory.Create(_settings.Enabled),
            Text = "GatedVoice — loading model…",
            Visible = true,
        };
        BuildMenu();
        _tray.DoubleClick += (_, _) => OpenMain();

        _overlay.Prime();
        _overlay.LevelSource = () => _recorder.Level;
        _overlay.Theme = PillThemes.Get(_settings.Theme);
        InstallHotkeys();

        _ = InitTranscriberAsync();

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--open") >= 0) OpenMain();
    }

    private void InstallHotkeys()
    {
        _dictateKey = new HotKeyListener(_settings.Modifier, _settings.Key);
        _dictateKey.Activated += () => StartCapture(CaptureMode.Dictate);
        _dictateKey.Deactivated += StopCapture;
        _dictateKey.Start();

        _polishKey = new HotKeyListener(_settings.TransformModifier, _settings.TransformKey);
        _polishKey.Activated += () => StartCapture(CaptureMode.Polish);
        _polishKey.Deactivated += StopCapture;
        _polishKey.Start();

        _scratchKey = new HotKeyListener(_settings.ScratchModifier, _settings.ScratchKey);
        _scratchKey.Activated += () => StartCapture(CaptureMode.Scratch);
        _scratchKey.Deactivated += StopCapture;
        _scratchKey.Start();
    }

    private async Task InitTranscriberAsync()
    {
        try
        {
            string model = AppSettings.ResolveModelPath(_settings.ModelPath);
            if (!File.Exists(model))
            {
                // First run with no model: fetch the compact base model automatically.
                SetStatus("Downloading model…");
                _tray.ShowBalloonTip(4000, "GatedVoice", "First run: downloading the speech model (~60 MB). One-time.", ToolTipIcon.Info);
                bool ok = await TryDownloadModelAsync(model,
                    "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en-q5_1.bin");
                if (!ok || !File.Exists(model))
                {
                    SetStatus("Model not found");
                    _tray.ShowBalloonTip(9000, "GatedVoice",
                        $"Couldn't download the speech model.\nPut a ggml model here and restart:\n{Path.GetDirectoryName(model)}",
                        ToolTipIcon.Warning);
                    return;
                }
            }

            // Whisper.net's 12-thread default badly oversubscribes this ARM64 CPU.
            // Three threads plus an input-sized context keeps short dictation near 1-2s.
            _transcriber = await Task.Run(() =>
                new Transcriber(model, _settings.Language, "main", threads: 3,
                    adaptiveAudioContext: true));

            // Load an existing live model before announcing readiness. If this is a
            // clean install, prepare the small model in the background instead of
            // delaying normal dictation.
            string liveModel = AppSettings.ResolveLiveModelPath(_settings.LiveModelPath);
            if (!string.IsNullOrEmpty(liveModel))
            {
                try { _liveTranscriber = await CreateLiveTranscriberAsync(liveModel); }
                catch { _liveTranscriber = null; }
            }

            _modelReady = true;
            SetStatus(_settings.Enabled ? "Ready" : "Disabled");
            _tray.ShowBalloonTip(3000, "GatedVoice is ready",
                $"Hold {_settings.Modifier} + {_settings.Key} and speak.", ToolTipIcon.Info);

            if (_liveTranscriber == null)
                _ = PrepareLiveTranscriberAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Model failed to load");
            _tray.ShowBalloonTip(6000, "GatedVoice error", ex.Message, ToolTipIcon.Error);
        }
    }

    private Task<Transcriber> CreateLiveTranscriberAsync(string modelPath) =>
        Task.Run(() => new Transcriber(modelPath, _settings.Language, "live",
            threads: 2, audioContextSize: 512, singleSegment: true));

    /// <summary>
    /// Downloads the compact live-preview model without blocking normal dictation.
    /// A failed download is harmless: final transcription remains available and the
    /// next launch tries again.
    /// </summary>
    private async Task PrepareLiveTranscriberAsync()
    {
        const string url =
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en-q5_1.bin";
        try
        {
            string path = AppSettings.DefaultLiveModelPath;
            _tray.ShowBalloonTip(2500, "GatedVoice",
                "Setting up fast live preview (32 MB, one time).", ToolTipIcon.Info);
            if (!await TryDownloadModelAsync(path, url) || !File.Exists(path)) return;

            _liveTranscriber = await CreateLiveTranscriberAsync(path);
            _tray.ShowBalloonTip(2000, "GatedVoice", "Live word preview is ready.", ToolTipIcon.Info);
        }
        catch { _liveTranscriber = null; }
    }

    /// <summary>Downloads a Whisper model to <paramref name="destPath"/>.</summary>
    private static async Task<bool> TryDownloadModelAsync(string destPath, string url)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            string tmp = destPath + ".part";

            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using var resp = await http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();

            await using (var src = await resp.Content.ReadAsStreamAsync())
            await using (var dst = File.Create(tmp))
                await src.CopyToAsync(dst);

            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(tmp, destPath);
            return true;
        }
        catch
        {
            try { File.Delete(destPath + ".part"); } catch { /* ignore */ }
            return false;
        }
    }

    // ---- dictation loop ----------------------------------------------------

    private void StartCapture(CaptureMode mode)
    {
        if (!_settings.Enabled || _busy) return;
        if (!_modelReady)
        {
            _tray.ShowBalloonTip(2000, "GatedVoice", "Still loading the model, one sec…", ToolTipIcon.Info);
            return;
        }
        if (_recorder.IsRecording) return;

        try
        {
            _mode = mode;
            _captureStart = DateTime.UtcNow;
            // GatedSpace can contain several xterm panes under one native window.
            // Remember the exact DOM-accessibility element before anything has a
            // chance to move focus while transcription is running.
            _textTarget = mode == CaptureMode.Scratch
                ? null
                : TextInjector.CaptureTarget();
            _overlay.SetText("");
            _recorder.Start();
            _overlay.ShowState("", OverlayForm.State.Listening);

            _partialCts?.Dispose();
            _partialCts = new CancellationTokenSource();
            _partialTask = RunPartialLoop(_partialCts.Token);
        }
        catch (Exception ex)
        {
            _textTarget = null;
            _tray.ShowBalloonTip(4000, "Microphone error", ex.Message, ToolTipIcon.Error);
        }
    }

    /// <summary>Transcribes the growing audio while you speak and streams the words into the field.</summary>
    private async Task RunPartialLoop(CancellationToken ct)
    {
        // Never run previews through the accurate model: doing so makes final
        // transcription wait behind preview work. A missing tiny model simply means
        // no preview for this capture while its one-time download finishes.
        Transcriber? live = _liveTranscriber;
        if (live == null) return;

        const int WindowBytes = 7 * 16000 * 2; // last ~7s keeps inference fast no matter how long you talk

        try
        {
            int lastLen = 0;
            while (!ct.IsCancellationRequested && _recorder.IsRecording)
            {
                await Task.Delay(350, ct);
                if (ct.IsCancellationRequested) break;

                byte[] full = _recorder.SnapshotPcm();
                if (full.Length < 16000) continue; // need >~0.5s of audio

                // Only refresh when the newly-added audio actually contains speech,
                // otherwise the text jitters while you're silent.
                int from = Math.Min(lastLen, full.Length);
                if (SegmentRms(full, from, full.Length) < 235) continue;
                lastLen = full.Length;

                byte[] windowed = full.Length <= WindowBytes ? full : TailBytes(full, WindowBytes);
                string partial = TextPipeline.Process(
                    await live.TranscribeAsync(windowed, _dict.BuildPrompt(), ct),
                    _dict, _snips);
                if (!string.IsNullOrWhiteSpace(partial) && !ct.IsCancellationRequested)
                    _overlay.SetText(partial);
            }
        }
        catch (OperationCanceledException) { }
        catch { /* partials are best-effort */ }
    }

    private static byte[] TailBytes(byte[] b, int maxBytes)
    {
        if (b.Length <= maxBytes) return b;
        int start = b.Length - maxBytes;
        if ((start & 1) == 1) start++;
        var slice = new byte[b.Length - start];
        Array.Copy(b, start, slice, 0, slice.Length);
        return slice;
    }

    /// <summary>Loudest 0.3s chunk, used to tell "you actually spoke" from "you pressed but said nothing".</summary>
    private static double MaxChunkRms(byte[] b, int chunkBytes)
    {
        double max = 0;
        for (int start = 0; start < b.Length; start += chunkBytes)
        {
            double r = SegmentRms(b, start, Math.Min(start + chunkBytes, b.Length));
            if (r > max) max = r;
        }
        return max;
    }

    /// <summary>RMS loudness of a slice of 16-bit PCM, used to detect speech vs. silence.</summary>
    private static double SegmentRms(byte[] b, int from, int to)
    {
        if (from < 0) from = 0;
        if ((from & 1) == 1) from++;
        if (to > b.Length) to = b.Length;
        int n = (to - from) / 2;
        if (n <= 0) return 0;
        double sum = 0;
        for (int i = from; i + 1 < to; i += 2)
        {
            short s = (short)(b[i] | (b[i + 1] << 8));
            sum += (double)s * s;
        }
        return Math.Sqrt(sum / n);
    }

    private async void StopCapture()
    {
        if (!_recorder.IsRecording) return;

        _partialCts?.Cancel();
        Task? partialTask = _partialTask;
        TextInjector.TargetSnapshot? textTarget = _textTarget;
        _busy = true;
        bool polish = _mode == CaptureMode.Polish && _ai.Configured;
        try
        {
            _overlay.ShowState("", OverlayForm.State.Transcribing);
            double seconds = (DateTime.UtcNow - _captureStart).TotalSeconds;

            byte[] pcm = await _recorder.StopAsync();
            // Give the cancellable preview a brief moment to release its CPU threads
            // before starting the accuracy pass. It uses a separate model and can
            // never hold the main transcriber's semaphore.
            if (partialTask != null)
            {
                try { await partialTask.WaitAsync(TimeSpan.FromMilliseconds(300)); }
                catch (OperationCanceledException) { }
                catch (TimeoutException) { }
                catch { /* preview is best-effort */ }
            }

            if (pcm.Length < 3200) { _overlay.HideState(); return; } // < ~0.1s of audio
            if (MaxChunkRms(pcm, 9600) < 235) { _overlay.HideState(); return; } // pressed but said nothing

            string raw = _transcriber == null
                ? ""
                : await _transcriber.TranscribeAsync(pcm, _dict.BuildPrompt());

            string text = TextPipeline.Process(raw, _dict, _snips);
            if (polish && !string.IsNullOrWhiteSpace(text))
                text = await _ai.PolishAsync(text);

            if (_settings.RemoveFillers) text = TextTools.RemoveFillers(text);

            if (!string.IsNullOrWhiteSpace(text))
            {
                int words = TextTools.WordCount(text);
                _insights.Record(words, seconds);
                _history.Add(text, words);

                if (_mode == CaptureMode.Scratch)
                {
                    AppendThought(text);
                    ShowScratch();
                }
                else
                {
                    _overlay.SetText(text);
                    TextInjector.Insert(
                        _settings.TrailingSpace ? text + " " : text,
                        _settings.Paste,
                        textTarget);
                }
                SetStatus("Ready");
            }
        }
        catch (Exception ex)
        {
            _tray.ShowBalloonTip(4000, "GatedVoice error", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            _partialTask = null;
            _partialCts?.Dispose();
            _partialCts = null;
            _overlay.HideState();
            _busy = false;
            _mode = CaptureMode.Dictate;
            _textTarget = null;
        }
    }

    /// <summary>Prepend a spoken thought to the "Quick thoughts" note in the Scratchpad.</summary>
    private static void AppendThought(string text)
    {
        var pad = Scratchpad.Load();
        var note = pad.Notes.FirstOrDefault(n => n.Title == "Quick thoughts");
        if (note == null)
        {
            note = new Note { Id = DateTime.Now.Ticks.ToString(), Title = "Quick thoughts", Body = "" };
            pad.Notes.Insert(0, note);
        }
        string line = $"[{DateTime.Now:MMM d, h:mm tt}]  {text.Trim()}";
        note.Body = string.IsNullOrWhiteSpace(note.Body) ? line : line + "\n" + note.Body;
        note.Updated = DateTime.Now.ToString("o");
        pad.Notes.Remove(note);
        pad.Notes.Insert(0, note);
        pad.Save();
    }

    private void ShowScratch()
    {
        _main ??= new MainWindow(() => ReloadData(false));
        _main.ShowScratchpad();
    }

    // ---- tray menu ---------------------------------------------------------

    private void BuildMenu()
    {
        var menu = new ContextMenuStrip();

        _statusItem = new ToolStripMenuItem("GatedVoice — loading…") { Enabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripMenuItem($"Hold {_settings.Modifier} + {_settings.Key} to talk") { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem($"Hold {_settings.TransformModifier} + {_settings.TransformKey} to polish") { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem($"Hold {_settings.ScratchModifier} + {_settings.ScratchKey} for a quick thought") { Enabled = false });

        menu.Items.Add(new ToolStripSeparator());

        var openItem = new ToolStripMenuItem("Open GatedVoice", null, (_, _) => OpenMain());
        openItem.Font = new Font(openItem.Font, FontStyle.Bold);
        menu.Items.Add(openItem);

        _enabledItem = new ToolStripMenuItem("Enabled", null, (_, _) => ToggleEnabled())
        {
            Checked = _settings.Enabled,
            CheckOnClick = true,
        };
        menu.Items.Add(_enabledItem);
        menu.Items.Add(new ToolStripMenuItem("Insights…", null, (_, _) => ShowInsights()));

        var startupItem = new ToolStripMenuItem("Launch at login", null, (s, _) =>
        {
            var item = (ToolStripMenuItem)s!;
            StartupManager.SetEnabled(item.Checked);
        })
        {
            Checked = StartupManager.IsEnabled(),
            CheckOnClick = true,
        };
        menu.Items.Add(startupItem);

        var themeMenu = new ToolStripMenuItem("Bar color");
        (string Key, string Label)[] themeOpts =
        {
            ("light", "Light"),
            ("darknavy", "Dark navy"),
            ("graphite", "Graphite"),
            ("indigoglass", "Indigo glass"),
            ("lavender", "Lavender"),
        };
        foreach (var (key, label) in themeOpts)
        {
            var item = new ToolStripMenuItem(label)
            {
                Tag = key,
                Checked = string.Equals(_settings.Theme, key, StringComparison.OrdinalIgnoreCase),
            };
            item.Click += (s, _) => SetTheme((string)((ToolStripMenuItem)s!).Tag!, themeMenu);
            themeMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(themeMenu);

        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem("Edit dictionary…", null, (_, _) => OpenPath(FlowDictionary.Path)));
        menu.Items.Add(new ToolStripMenuItem("Edit snippets…", null, (_, _) => OpenPath(Snippets.Path)));
        menu.Items.Add(new ToolStripMenuItem("Edit settings…", null, (_, _) => OpenPath(AppSettings.SettingsPath)));
        menu.Items.Add(new ToolStripMenuItem("Reload dictionary + snippets", null, (_, _) => ReloadData(true)));
        menu.Items.Add(new ToolStripMenuItem("Open data folder", null, (_, _) => OpenPath(AppSettings.DataDir)));

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit GatedVoice", null, (_, _) => Quit()));

        _tray.ContextMenuStrip = menu;
    }

    private void ToggleEnabled()
    {
        _settings.Enabled = _enabledItem.Checked;
        _settings.Save();
        _tray.Icon = IconFactory.Create(_settings.Enabled);
        SetStatus(_settings.Enabled ? (_modelReady ? "Ready" : "Loading…") : "Disabled");
    }

    private void SetTheme(string key, ToolStripMenuItem parent)
    {
        _settings.Theme = key;
        _settings.Save();
        _overlay.Theme = PillThemes.Get(key);
        foreach (ToolStripMenuItem it in parent.DropDownItems)
            it.Checked = string.Equals((string)it.Tag!, key, StringComparison.OrdinalIgnoreCase);
    }

    private void ShowInsights()
    {
        using var f = new InsightsForm(_insights);
        f.ShowDialog();
    }

    private void ReloadData(bool notify)
    {
        // Pull anything the UI may have changed on disk back into the running engine.
        var s = AppSettings.Load();
        _settings.Theme = s.Theme;
        _settings.RemoveFillers = s.RemoveFillers;
        _settings.TrailingSpace = s.TrailingSpace;
        _settings.Paste = s.Paste;
        _settings.AiProvider = s.AiProvider;
        _settings.AiModel = s.AiModel;
        _settings.AiApiKey = s.AiApiKey;
        _settings.OllamaUrl = s.OllamaUrl;

        _dict = FlowDictionary.Load();
        _snips = Snippets.Load();
        _ai = new AiTransformer(_settings);
        _overlay.Theme = PillThemes.Get(_settings.Theme);

        if (notify) _tray.ShowBalloonTip(2000, "GatedVoice", "Dictionary and snippets reloaded.", ToolTipIcon.Info);
    }

    private void OpenMain()
    {
        _main ??= new MainWindow(() => ReloadData(false));
        _main.ShowWindow();
    }

    private void SetStatus(string status)
    {
        _statusItem.Text = $"GatedVoice — {status}";
        _tray.Text = $"GatedVoice — {status}";
    }

    private static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    private void Quit()
    {
        _tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dictateKey?.Dispose();
            _polishKey?.Dispose();
            _scratchKey?.Dispose();
            _partialCts?.Cancel();
            _partialCts?.Dispose();
            _transcriber?.Dispose();
            _liveTranscriber?.Dispose();
            _overlay.Dispose();
            _main?.Dispose();
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
