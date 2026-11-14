using System;
using System.IO;
using System.Text.Json;

namespace DialWin;

internal static class Log
{
    static readonly object Gate = new();
    public static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dial");
    public static readonly string FilePath = Path.Combine(Dir, "dial.log");
    public static bool Verbose = true;

    public static void Init()
    {
        Directory.CreateDirectory(Dir);
        try
        {
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000) File.Delete(FilePath);
        }
        catch { }
    }

    public static void Write(string message, bool verboseOnly = false)
    {
        if (verboseOnly && !Verbose) return;
        lock (Gate)
        {
            try { File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}"); }
            catch { }
        }
    }
}

internal sealed class Settings
{
    /// <summary>Only react to volume changes on an output device whose name contains this. Empty = any device.</summary>
    public string DeviceNameContains { get; set; } = "CMF";

    /// <summary>Where volume is parked in every mode except Volume (0 to 1).</summary>
    public double ParkLevel { get; set; } = 0.5;

    /// <summary>What a press does in Volume mode: playpause, mute or none.</summary>
    public string VolumePress { get; set; } = "playpause";

    /// <summary>Log every key, volume change and device switch to dial.log.</summary>
    public bool VerboseLog { get; set; } = true;

    /// <summary>Launch Dial when you sign in to Windows.</summary>
    public bool StartWithWindows { get; set; } = true;

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { Log.Write("Couldn't save settings: " + ex.Message); }
    }

    static string FilePath => Path.Combine(Log.Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex) { Log.Write("settings.json unreadable, using defaults: " + ex.Message); }

        var fresh = new Settings();
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(fresh, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
        return fresh;
    }
}

internal static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Adds or removes Dial from the current user's startup list (no admin needed).</summary>
    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            if (enabled && Environment.ProcessPath is { } exe) key.SetValue("Dial", $"\"{exe}\"");
            else key.DeleteValue("Dial", throwOnMissingValue: false);
        }
        catch (Exception ex) { Log.Write("Startup registration failed: " + ex.Message); }
    }
}
