using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace TimeTracker;

/// <summary>
/// Settings from settings.json next to the exe, created with the defaults if missing.
/// Paths may contain environment variables (%USERPROFILE%). Changes apply on restart.
/// </summary>
sealed class Settings
{
    public string DataFile { get; set; } = Path.Combine(DefaultFolder, "hours.xlsx");
    public string CurrentTaskFile { get; set; } = Path.Combine(DefaultFolder, "current_task.txt");
    public string LinePosition { get; set; } = "Top";
    public int LineThickness { get; set; } = 3;
    public int UnlockQuestionMinutes { get; set; } = 5;
    public bool LaunchAtStartup { get; set; }
    public int BackupCount { get; set; } = 30;

    [JsonIgnore] public bool LineAtBottom => LinePosition.Equals("Bottom", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public string DataFilePath => Environment.ExpandEnvironmentVariables(DataFile);
    [JsonIgnore] public string CurrentTaskFilePath => Environment.ExpandEnvironmentVariables(CurrentTaskFile);

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TimeTracker");

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Throws if the file exists but cannot be parsed: better to say so than to silently use defaults.</summary>
    public static Settings Load()
    {
        if (!File.Exists(FilePath))
        {
            var defaults = new Settings();
            try { File.WriteAllText(FilePath, JsonSerializer.Serialize(defaults, JsonOptions)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* read-only folder: run with defaults */ }
            return defaults;
        }

        var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions) ?? new Settings();
        s.LineThickness = Math.Max(1, s.LineThickness);
        s.BackupCount = Math.Max(1, s.BackupCount);
        return s;
    }

    /// <summary>Adds or removes the app in HKCU\...\Run, so it matches the setting.</summary>
    public void ApplyLaunchAtStartup()
    {
        const string name = "TimeTracker";
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (LaunchAtStartup) key.SetValue(name, $"\"{Environment.ProcessPath}\"");
        else if (key.GetValue(name) != null) key.DeleteValue(name);
    }
}
