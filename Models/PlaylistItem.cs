using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Surfio.Models;

public sealed class PlaylistItem : INotifyPropertyChanged
{
    public PlaylistItem(string source)
    {
        Source = source;
        IsUrl = Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile;
        _title = IsUrl ? source : Path.GetFileNameWithoutExtension(source);
    }

    /// <summary>File path or URL.</summary>
    public string Source { get; }
    public bool IsUrl { get; }

    string _title;
    public string Title { get => _title; set => Set(ref _title, value); }

    string _artist = "";
    public string Artist { get => _artist; set { Set(ref _artist, value); OnChanged(nameof(Subtitle)); } }

    long _durationMs;
    public long DurationMs { get => _durationMs; set { Set(ref _durationMs, value); OnChanged(nameof(DurationText)); } }

    bool _isCurrent;
    public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }

    bool _failed;
    public bool Failed { get => _failed; set => Set(ref _failed, value); }

    public string DurationText => DurationMs > 0 ? TimeFormat.Format(DurationMs) : "";
    public string Subtitle => !string.IsNullOrEmpty(Artist) ? Artist : IsUrl ? "Stream" : Path.GetExtension(Source).TrimStart('.').ToUpperInvariant();

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnChanged(name!);
    }

    void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class TimeFormat
{
    public static string Format(long ms)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}
