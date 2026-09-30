namespace Surfio.Services;

public static class MediaFiles
{
    public static readonly string[] Video =
    [
        ".mp4", ".m4v", ".mkv", ".webm", ".avi", ".mov", ".wmv", ".flv", ".mpg", ".mpeg", ".m2ts", ".mts",
        ".ts", ".3gp", ".3g2", ".ogv", ".vob", ".divx", ".xvid", ".asf", ".rm", ".rmvb", ".f4v", ".mxf",
    ];

    public static readonly string[] Audio =
    [
        ".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg", ".oga", ".opus", ".wma", ".alac", ".aiff", ".aif",
        ".ape", ".wv", ".mka", ".ac3", ".dts", ".amr", ".mid", ".midi",
    ];

    public static readonly string[] Playlists = [".m3u", ".m3u8", ".pls"];

    public static readonly string[] Subtitles = [".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".smi"];

    static readonly HashSet<string> Playable = new(Video.Concat(Audio), StringComparer.OrdinalIgnoreCase);

    public static bool IsPlayable(string path) => Playable.Contains(Path.GetExtension(path));
    public static bool IsPlaylist(string path) => Playlists.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    public static bool IsSubtitle(string path) => Subtitles.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static string OpenFilter
    {
        get
        {
            static string Mask(IEnumerable<string> ext) => string.Join(";", ext.Select(e => "*" + e));
            return $"All media|{Mask(Video.Concat(Audio).Concat(Playlists))}" +
                   $"|Video|{Mask(Video)}|Audio|{Mask(Audio)}|Playlists|{Mask(Playlists)}|All files|*.*";
        }
    }

    /// <summary>Expands folders and playlist files into a flat, naturally sorted list of media sources.</summary>
    public static List<string> Expand(IEnumerable<string> inputs)
    {
        var result = new List<string>();
        foreach (var input in inputs)
        {
            if (Directory.Exists(input))
            {
                var files = Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories)
                    .Where(IsPlayable)
                    .OrderBy(f => f, NaturalComparer.Instance);
                result.AddRange(files);
            }
            else if (File.Exists(input) && IsPlaylist(input))
            {
                result.AddRange(ReadPlaylist(input));
            }
            else if (File.Exists(input) || Uri.TryCreate(input, UriKind.Absolute, out _))
            {
                result.Add(input);
            }
        }
        return result;
    }

    static IEnumerable<string> ReadPlaylist(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        var isPls = Path.GetExtension(path).Equals(".pls", StringComparison.OrdinalIgnoreCase);
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (isPls)
            {
                // .pls entries look like "File1=C:\music\song.mp3"
                if (!line.StartsWith("File", StringComparison.OrdinalIgnoreCase) || !line.Contains('=')) continue;
                line = line[(line.IndexOf('=') + 1)..].Trim();
            }
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (Uri.TryCreate(line, UriKind.Absolute, out var uri) && !uri.IsFile) yield return line;
            else
            {
                var full = Path.IsPathRooted(line) ? line : Path.GetFullPath(Path.Combine(dir, line));
                if (File.Exists(full)) yield return full;
            }
        }
    }

    public static void WritePlaylist(string path, IEnumerable<(string source, string title, long ms)> items)
    {
        using var w = new StreamWriter(path, false, new System.Text.UTF8Encoding(false));
        w.WriteLine("#EXTM3U");
        foreach (var (source, title, ms) in items)
        {
            w.WriteLine($"#EXTINF:{(ms > 0 ? ms / 1000 : -1)},{title}");
            w.WriteLine(source);
        }
    }
}

/// <summary>Sorts "Episode 2" before "Episode 10".</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int StrCmpLogicalW(string a, string b);

    public int Compare(string? x, string? y) => StrCmpLogicalW(x ?? "", y ?? "");
}
