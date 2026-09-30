using System.Text.Json;

namespace Surfio.Services;

public enum RepeatMode { Off, All, One }

/// <summary>User preferences, stored as JSON in %AppData%\Surfio.</summary>
public sealed class AppSettings
{
    public int Volume { get; set; } = 80;
    public bool Muted { get; set; }
    public RepeatMode Repeat { get; set; } = RepeatMode.Off;
    public bool Shuffle { get; set; }
    public bool PlaylistVisible { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    public bool ResumePlayback { get; set; } = true;
    public bool HardwareDecoding { get; set; } = true;
    public List<string> Recent { get; set; } = [];
    /// <summary>Where to resume each file, in milliseconds.</summary>
    public Dictionary<string, long> Positions { get; set; } = [];
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1120;
    public double WindowHeight { get; set; } = 680;
    public bool WindowMaximized { get; set; }

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Surfio");
    static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // Corrupt settings: start fresh rather than crash.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            // Keep the resume list bounded.
            if (Positions.Count > 300)
                Positions = Positions.Skip(Positions.Count - 300).ToDictionary(p => p.Key, p => p.Value);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Settings are a convenience; never block the player on them.
        }
    }

    public void AddRecent(string source)
    {
        Recent.RemoveAll(r => string.Equals(r, source, StringComparison.OrdinalIgnoreCase));
        Recent.Insert(0, source);
        if (Recent.Count > 12) Recent.RemoveRange(12, Recent.Count - 12);
    }
}
