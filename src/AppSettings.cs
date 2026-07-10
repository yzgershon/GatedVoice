using System;
using System.IO;
using System.Text.Json;

namespace Flow;

/// <summary>User-editable settings, persisted as JSON in %APPDATA%\Flow\settings.json.</summary>
public sealed class AppSettings
{
    // Hotkey: hold Modifier + Key to dictate. Modifier is Ctrl/Alt/Shift/Win/None.
    public string Modifier { get; set; } = "Ctrl";
    public string Key { get; set; } = "Space";

    public string Language { get; set; } = "en";
    public string ModelPath { get; set; } = "";
    // Small, fast model used only for the live word-by-word preview while you speak.
    public string LiveModelPath { get; set; } = "";

    // Pill body colour: light | darknavy | graphite | indigoglass | lavender
    public string Theme { get; set; } = "light";
    public bool Paste { get; set; } = true;   // clipboard-paste injection (vs. simulated typing)

    // Style (formatting) preferences shown on the Style page
    public bool RemoveFillers { get; set; } = false;   // strip "um", "uh"
    public bool TrailingSpace { get; set; } = true;     // add a space after each dictation
    public bool PlaySounds { get; set; } = false;
    public bool Enabled { get; set; } = true;

    // AI Transforms (filled in later): none | ollama | anthropic | openai
    public string AiProvider { get; set; } = "none";
    public string AiModel { get; set; } = "gemma4:latest";
    public string AiApiKey { get; set; } = "";
    public string OllamaUrl { get; set; } = "http://localhost:11434";
    // Hotkey to Polish the last dictation, e.g. hold Ctrl+Shift while it's on the clipboard.
    public string TransformModifier { get; set; } = "Alt";
    public string TransformKey { get; set; } = "Space";

    // Hotkey to capture a quick thought straight into the Scratchpad.
    public string ScratchModifier { get; set; } = "Alt";
    public string ScratchKey { get; set; } = "N";

    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShyVoice");

    public static string SettingsPath => Path.Combine(DataDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            if (File.Exists(SettingsPath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                if (s != null) { s.Save(); return s; } // rewrite to add any new fields
            }
        }
        catch { /* fall through to defaults */ }

        var def = new AppSettings();
        def.Save();
        return def;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* ignore */ }
    }

    /// <summary>Finds a usable ggml model, checking the configured path then common locations.</summary>
    public static string ResolveModelPath(string configured)
    {
        string[] candidates =
        {
            configured,
            Path.Combine(DataDir, "models", "ggml-base.en.bin"),
            Path.Combine(AppContext.BaseDirectory, "models", "ggml-base.en.bin"),
        };
        foreach (var c in candidates)
            if (!string.IsNullOrWhiteSpace(c) && File.Exists(c)) return c;

        return Path.Combine(DataDir, "models", "ggml-base.en.bin");
    }

    /// <summary>Finds the small/fast model used for live partials (falls back to none).</summary>
    public static string ResolveLiveModelPath(string configured)
    {
        string[] candidates =
        {
            configured,
            Path.Combine(DataDir, "models", "ggml-tiny.en.bin"),
            Path.Combine(AppContext.BaseDirectory, "models", "ggml-tiny.en.bin"),
        };
        foreach (var c in candidates)
            if (!string.IsNullOrWhiteSpace(c) && File.Exists(c)) return c;
        return "";
    }
}
