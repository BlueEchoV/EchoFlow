using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EchoFlow;

/// User settings, stored in %APPDATA%\EchoFlow\settings.json. Most are changed from the main window.
sealed class Settings
{
    /// Keys to hold while talking, e.g. "Ctrl+Win", "Ctrl+Alt" or "F8".
    public string Hotkey { get; set; } = "Ctrl+Win";

    /// Optional shortcut that starts and stops hands-free dictation, e.g. "Ctrl+Win+Space". Double-tapping Hotkey always works too.
    public string HandsFreeHotkey { get; set; } = "";

    public bool DarkMode { get; set; } = true;

    /// Keep a small bar at the bottom of the screen even when you're not dictating.
    public bool ShowBarAlways { get; set; }

    /// Soft chimes when dictation starts and stops.
    public bool Sounds { get; set; } = true;

    /// While you dictate, pauses music and videos (and mutes anything that can't be paused), then resumes them.
    public bool QuietWhileDictating { get; set; } = true;

    /// When you fix a word right after dictating, add the fix to your words automatically.
    public bool AutoAddWords { get; set; } = true;

    /// Microphone name, or empty to follow the Windows default.
    public string Microphone { get; set; } = "";

    /// "auto" picks the best model for this PC. Or a whisper.cpp model name: "large-v3-turbo", "small", "base".
    public string Model { get; set; } = "auto";

    /// Spoken language code ("en", "es", ...), or "auto" to detect it each time.
    public string Language { get; set; } = "en";

    /// Folder holding words.txt. Point it at a OneDrive folder to share your words between PCs.
    public string WordListFolder { get; set; } = "";

    /// AI cleanup: filler removal, corrections, punctuation, lists. Needs an API key.
    public bool Cleanup { get; set; } = true;

    /// Claude model used for cleanup.
    public string CleanupModel { get; set; } = "claude-haiku-4-5";

    public static string AppDataDir { get; } = DataFolder(Environment.SpecialFolder.ApplicationData);

    /// Speech models and the log.
    public static string LocalDir { get; } = DataFolder(Environment.SpecialFolder.LocalApplicationData);

    /// The app used to be called Dictation; the first time, its folder (your key, history and models) moves over.
    static string DataFolder(Environment.SpecialFolder root)
    {
        string folder = Path.Combine(Environment.GetFolderPath(root), "EchoFlow");
        string old = Path.Combine(Environment.GetFolderPath(root), "Dictation");
        try
        {
            if (Directory.Exists(old) && !Directory.Exists(folder)) Directory.Move(old, folder);
        }
        catch (IOException) { } // in use: start fresh rather than fail
        return folder;
    }

    public static string FilePath => Path.Combine(AppDataDir, "settings.json");

    [JsonIgnore]
    public string WordListPath => Path.Combine(
        string.IsNullOrWhiteSpace(WordListFolder) ? AppDataDir : Environment.ExpandEnvironmentVariables(WordListFolder),
        "words.txt");

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public void Save() => File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));

    public static Settings Load()
    {
        Directory.CreateDirectory(AppDataDir);
        if (!File.Exists(FilePath))
        {
            var defaults = new Settings();
            defaults.Save();
            return defaults;
        }
        try
        {
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            settings.Save(); // adds any settings introduced since the file was written
            return settings;
        }
        catch (JsonException ex)
        {
            Log.Write($"settings.json is invalid, using defaults: {ex.Message}");
            return new Settings();
        }
    }
}
