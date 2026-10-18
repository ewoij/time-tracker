using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
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
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int AssocQueryString(int flags, int str, string assoc, string? extra, StringBuilder output, ref int length);

    public string DataFile { get; set; } = Path.Combine(DefaultFolder, "hours.xlsx");
    public string CurrentTaskFile { get; set; } = Path.Combine(DefaultFolder, "current_task.txt");
    public string LinePosition { get; set; } = "Top";
    public int LineThickness { get; set; } = 3;
    public bool LineClickable { get; set; } = true;
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
            WriteDefaultsIfMissing();
            return new Settings();
        }

        var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions) ?? new Settings();
        s.LineThickness = Math.Max(1, s.LineThickness);
        s.BackupCount = Math.Max(1, s.BackupCount);
        return s;
    }

    static void WriteDefaultsIfMissing()
    {
        if (File.Exists(FilePath)) return;
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(new Settings(), JsonOptions)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* read-only folder: run with defaults */ }
    }

    /// <summary>Opens settings.json in the default program for .json files, or in Notepad if there is none.</summary>
    public static void OpenInEditor()
    {
        WriteDefaultsIfMissing();
        // Without an association the shell would ask "How do you want to open this?": check first.
        if (HasAssociation(".json"))
        {
            try
            {
                Process.Start(new ProcessStartInfo(FilePath) { UseShellExecute = true, ErrorDialog = false });
                return;
            }
            catch (Win32Exception) { /* fall back to Notepad */ }
        }
        Process.Start("notepad.exe", $"\"{FilePath}\"");
    }

    /// <summary>True if a program is registered to open files with this extension.</summary>
    internal static bool HasAssociation(string extension)
    {
        const int ASSOCF_INIT_IGNOREUNKNOWN = 0x400, ASSOCSTR_EXECUTABLE = 2;
        var exe = new StringBuilder(1024);
        int length = exe.Capacity;
        return AssocQueryString(ASSOCF_INIT_IGNOREUNKNOWN, ASSOCSTR_EXECUTABLE, extension, null, exe, ref length) == 0
            && exe.Length > 0;
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
