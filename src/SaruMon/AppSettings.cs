using System.Text.Json;
using Microsoft.Win32;

namespace SaruMon;

internal sealed class AppSettings
{
    private static readonly string SettingsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SaruMon");
    public string? InterfaceId { get; set; }

    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(SettingsDirectory, "settings.json"))) ?? new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDirectory);
        var temporary = Path.Combine(SettingsDirectory, "settings-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, Path.Combine(SettingsDirectory, "settings.json"), overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue("SaruMon") is string value && value == StartupCommand;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (value) key.SetValue("SaruMon", StartupCommand);
            else if (key.GetValue("SaruMon") is string existing && existing == StartupCommand)
                key.DeleteValue("SaruMon", throwOnMissingValue: false);
        }
    }

    private static string StartupCommand => $"\"{Environment.ProcessPath}\" --tray";
}
