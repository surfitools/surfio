using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using Microsoft.Win32;
using Surfio.Models;
using Surfio.Services;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using VlcMedia = LibVLCSharp.Shared.Media;

namespace Surfio;

public partial class MainWindow : Window
{
    // Segoe Fluent Icons / MDL2 glyphs
    const string IconPlay = "", IconPause = "", IconVolume = "", IconMuted = "";
    const string IconFullscreen = "", IconExitFullscreen = "", IconRepeatAll = "", IconRepeatOne = "";
    static readonly float[] Speeds = [0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f, 3f];

    readonly LibVLC _vlc;
    readonly MediaPlayer _player;
    readonly AppSettings _settings = AppSettings.Load();
    readonly ObservableCollection<PlaylistItem> _items = [];
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer _osdTimer = new() { Interval = TimeSpan.FromSeconds(1.6) };
    readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    readonly DispatcherTimer _clickTimer = new() { Interval = TimeSpan.FromMilliseconds(240) };
    readonly SemaphoreSlim _parseGate = new(2);
    readonly Random _random = new();
    readonly string[] _startupArgs;
    readonly bool _ready;

    VlcMedia? _media;
    PlaylistItem? _current;
    long _pendingResumeMs;
    bool _seeking, _fullscreen, _showRemaining, _audioOnly;
    float _rate = 1f;
    WindowState _restoreState;
    Point _lastMouse;

    public MainWindow(string[] args)
    {
        InitializeComponent();
        _startupArgs = args;

        _vlc = new LibVLC(false, "--no-video-title-show");
        _player = new MediaPlayer(_vlc)
        {
            EnableHardwareDecoding = _settings.HardwareDecoding,
            EnableKeyInput = false,
            EnableMouseInput = false,
        };

        // LibVLC raises events on its own threads; never call back into it from there.
        _player.Playing += (_, _) => Ui(OnPlaying);
        _player.Paused += (_, _) => Ui(() => SetPlayingUi(false));
        _player.Stopped += (_, _) => Ui(() => SetPlayingUi(false));
        _player.EndReached += (_, _) => Ui(OnEnded);
        _player.EncounteredError += (_, _) => Ui(OnError);
        _player.LengthChanged += (_, e) => Ui(() => OnLength(e.Length));

        PlaylistBox.ItemsSource = _items;
        _items.CollectionChanged += (_, _) => UpdatePlaylistInfo();

        _tick.Tick += (_, _) => UpdatePosition();
        _osdTimer.Tick += (_, _) => { _osdTimer.Stop(); Osd.Visibility = Visibility.Collapsed; };
        _idleTimer.Tick += (_, _) => HideFullscreenControls();
        _clickTimer.Tick += (_, _) => { _clickTimer.Stop(); TogglePlay(); };

        ApplySettings();

        Loaded += OnLoaded;
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(this);
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        Drop += OnDrop;
        DragOver += OnDragOver;

        _ready = true;
    }

    void Ui(Action action) => Dispatcher.BeginInvoke(action);

    static void After(int ms, Action action)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); action(); };
        t.Start();
    }

    // ------------------------------------------------------------------ setup

    void ApplySettings()
    {
        VolumeSlider.Value = _settings.Volume;
        VolumeText.Text = $"{_settings.Volume}%";
        UpdateMuteIcon();
        UpdateRepeatShuffleUi();
        SetPlaylistVisible(_settings.PlaylistVisible);
        Topmost = _settings.AlwaysOnTop;

        var s = _settings;
        bool onScreen = !double.IsNaN(s.WindowLeft) &&
                        s.WindowLeft >= SystemParameters.VirtualScreenLeft - 50 &&
                        s.WindowTop >= SystemParameters.VirtualScreenTop - 50 &&
                        s.WindowLeft + 200 <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
                        s.WindowTop + 100 <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        Width = Math.Max(MinWidth, s.WindowWidth);
        Height = Math.Max(MinHeight, s.WindowHeight);
        if (onScreen)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = s.WindowLeft;
            Top = s.WindowTop;
        }
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        Video.MediaPlayer = _player;
        _player.Volume = _settings.Volume;
        _player.Mute = _settings.Muted;
        UpdateEmptyHint();
        UpdatePlaylistInfo();
        if (_startupArgs.Length > 0) Open(_startupArgs, play: true);
    }

    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveResumePosition();
        if (_fullscreen) ExitFullscreen();
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _settings.WindowLeft = bounds.Left;
        _settings.WindowTop = bounds.Top;
        _settings.WindowWidth = bounds.Width;
        _settings.WindowHeight = bounds.Height;
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        _settings.Save();
        Native.KeepAwake(false);
        _tick.Stop();
        _player.Stop();
        _player.Dispose();
        _media?.Dispose();
        _vlc.Dispose();
    }

    /// <summary>Files opened from Explorer while Surfio was already running.</summary>
    public void OpenFromOtherInstance(string[] args)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (args.Length == 1 && args[0] == "--activate") return;
        Open(args, play: true);
    }

    // ------------------------------------------------------------------ opening

    void Open(IEnumerable<string> inputs, bool play)
    {
        var all = inputs.ToList();

        // A subtitle file dropped onto a playing video attaches to it.
        var subs = all.Where(p => File.Exists(p) && MediaFiles.IsSubtitle(p)).ToList();
        if (subs.Count > 0 && _current != null)
        {
            foreach (var s in subs) LoadSubtitle(s);
            all = all.Except(subs).ToList();
            if (all.Count == 0) return;
        }

        var sources = MediaFiles.Expand(all);
        if (sources.Count == 0)
        {
            ShowOsd("Nothing playable there");
            return;
        }

        int first = _items.Count;
        foreach (var src in sources)
        {
            var item = new PlaylistItem(src);
            _items.Add(item);
            QueueParse(item);
        }
        if (play || _current == null) PlayAt(first);
        else ShowOsd(sources.Count == 1 ? "Added to playlist" : $"Added {sources.Count} items");
    }

    void OpenFilesDialog(bool play)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Filter = MediaFiles.OpenFilter, Title = "Open media" };
        if (dlg.ShowDialog(this) == true) Open(dlg.FileNames, play);
    }

    void OpenFolderDialog()
    {
        var dlg = new OpenFolderDialog { Title = "Open folder", Multiselect = true };
        if (dlg.ShowDialog(this) == true) Open(dlg.FolderNames, play: true);
    }

    void OpenUrlDialog()
    {
        var dlg = new UrlDialog { Owner = this };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Url)) Open([dlg.Url.Trim()], play: true);
    }

    async void QueueParse(PlaylistItem item)
    {
        if (item.IsUrl) return;
        await _parseGate.WaitAsync();
        try
        {
            using var media = new VlcMedia(_vlc, item.Source, FromType.FromPath);
            await media.Parse(MediaParseOptions.ParseLocal, 4000);
            var title = media.Meta(MetadataType.Title);
            var artist = media.Meta(MetadataType.Artist);
            if (media.Duration > 0) item.DurationMs = media.Duration;
            // VLC falls back to the file name when there is no title tag; keep ours (no extension) then.
            if (!string.IsNullOrWhiteSpace(title) && title != Path.GetFileName(item.Source)) item.Title = title;
            if (!string.IsNullOrWhiteSpace(artist)) item.Artist = artist;
            UpdatePlaylistInfo();
            if (item == _current) UpdateNowPlaying();
        }
        catch
        {
            // Unreadable tags are not an error; the file may still play.
        }
        finally
        {
            _parseGate.Release();
        }
    }

    // ------------------------------------------------------------------ playback

    int CurrentIndex => _current == null ? -1 : _items.IndexOf(_current);

    void PlayAt(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        SaveResumePosition();

        if (_current != null) _current.IsCurrent = false;
        var item = _items[index];
        _current = item;
        item.IsCurrent = true;
        item.Failed = false;
        PlaylistBox.ScrollIntoView(item);

        var old = _media;
        _media = item.IsUrl ? new VlcMedia(_vlc, new Uri(item.Source)) : new VlcMedia(_vlc, item.Source, FromType.FromPath);
        _pendingResumeMs = !item.IsUrl && _settings.ResumePlayback && _settings.Positions.TryGetValue(item.Source, out var pos) ? pos : 0;
        _audioOnly = false;
        AudioPanel.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Collapsed;
        SeekSlider.IsEnabled = true;
        SeekSlider.Value = 0;
        TimeText.Text = "0:00";

        _player.Play(_media);
        old?.Dispose();

        _settings.AddRecent(item.Source);
        UpdateNowPlaying();
    }

    void OnPlaying()
    {
        SetPlayingUi(true);
        _tick.Start();
        if (Math.Abs(_rate - 1f) > 0.01f) _player.SetRate(_rate);

        if (_pendingResumeMs > 0)
        {
            var ms = _pendingResumeMs;
            _pendingResumeMs = 0;
            _player.Time = ms;
            ShowOsd($"Resumed at {TimeFormat.Format(ms)} · Home to start over", 3000);
        }
        // Tracks are known shortly after playback starts.
        After(600, DetectAudioOnly);
    }

    void DetectAudioOnly()
    {
        if (_current == null || _media == null) return;
        _audioOnly = _player.VideoTrackCount <= 0;
        if (!_audioOnly)
        {
            AudioPanel.Visibility = Visibility.Collapsed;
            return;
        }
        AudioTitle.Text = _current.Title;
        AudioArtist.Text = _media.Meta(MetadataType.Artist) ?? _current.Artist;
        ArtImage.Source = null;
        ArtPlaceholder.Visibility = Visibility.Visible;
        var art = _media.Meta(MetadataType.ArtworkURL);
        if (!string.IsNullOrEmpty(art) && Uri.TryCreate(art, UriKind.Absolute, out var artUri) && artUri.IsFile && File.Exists(artUri.LocalPath))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = artUri;
                bmp.DecodePixelWidth = 480;
                bmp.EndInit();
                ArtImage.Source = bmp;
                ArtPlaceholder.Visibility = Visibility.Collapsed;
            }
            catch
            {
                // Broken artwork: keep the placeholder.
            }
        }
        AudioPanel.Visibility = Visibility.Visible;
        SetPlayingUi(_player.IsPlaying);
    }

    void OnEnded()
    {
        if (_current != null && !_current.IsUrl) _settings.Positions.Remove(_current.Source);
        if (_settings.Repeat == RepeatMode.One && _current != null && CurrentIndex >= 0) PlayAt(CurrentIndex);
        else Next(auto: true);
    }

    void OnError()
    {
        if (_current != null) _current.Failed = true;
        ShowOsd("Can't play this file", 2500);
        bool anyLeft = _items.Any(i => !i.Failed);
        if (anyLeft && _items.Count > 1) After(700, () => Next(auto: true));
        else StopPlayback();
    }

    void OnLength(long length)
    {
        SeekSlider.Maximum = Math.Max(1, length);
        if (_current != null && length > 0 && _current.DurationMs == 0) _current.DurationMs = length;
        UpdateTimeTexts(_player.Time, length);
    }

    void Next(bool auto = false)
    {
        if (_items.Count == 0) return;
        int idx = CurrentIndex;
        if (_settings.Shuffle && _items.Count > 1)
        {
            int pick;
            do pick = _random.Next(_items.Count); while (pick == idx);
            PlayAt(pick);
            return;
        }
        idx++;
        if (idx >= _items.Count)
        {
            if (_settings.Repeat == RepeatMode.All) idx = 0;
            else
            {
                if (auto) StopPlayback();
                return;
            }
        }
        PlayAt(idx);
    }

    void Previous()
    {
        if (_player.Time > 3000 && _player.IsSeekable)
        {
            _player.Time = 0;
            return;
        }
        int idx = CurrentIndex - 1;
        if (idx < 0) idx = _settings.Repeat == RepeatMode.All ? _items.Count - 1 : 0;
        PlayAt(idx);
    }

    void TogglePlay()
    {
        if (_current == null)
        {
            if (_items.Count > 0) PlayAt(0);
            else if (_settings.Recent.Count > 0 && File.Exists(_settings.Recent[0])) Open([_settings.Recent[0]], play: true);
            else OpenFilesDialog(play: true);
            return;
        }
        var state = _player.State;
        if (state is VLCState.Ended or VLCState.Stopped or VLCState.NothingSpecial or VLCState.Error)
        {
            int idx = CurrentIndex;
            PlayAt(idx >= 0 ? idx : 0);
            return;
        }
        bool wasPlaying = _player.IsPlaying;
        _player.SetPause(wasPlaying);
        ShowOsd(wasPlaying ? "Paused" : "Playing");
    }

    void StopPlayback()
    {
        SaveResumePosition();
        _player.Stop();
        _tick.Stop();
        SeekSlider.Value = 0;
        SeekSlider.IsEnabled = false;
        TimeText.Text = "0:00";
        AudioPanel.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
        SetPlayingUi(false);
        UpdateEmptyHint();
    }

    void SetPlayingUi(bool playing)
    {
        PlayPauseButton.Content = playing ? IconPause : IconPlay;
        // Keep the screen on for video, not for background music.
        Native.KeepAwake(playing && !_audioOnly);
    }

    void UpdateNowPlaying()
    {
        if (_current == null)
        {
            NowPlayingText.Text = "";
            Title = "Surfio";
            return;
        }
        var text = string.IsNullOrEmpty(_current.Artist) ? _current.Title : $"{_current.Artist} — {_current.Title}";
        NowPlayingText.Text = text;
        Title = $"{text} — Surfio";
        if (_audioOnly)
        {
            AudioTitle.Text = _current.Title;
            AudioArtist.Text = _current.Artist;
        }
    }

    void SaveResumePosition()
    {
        if (_current == null || _current.IsUrl || !_settings.ResumePlayback) return;
        long len = _player.Length, time = _player.Time;
        // Only remember long media, and only if we're somewhere in the middle.
        if (len > 5 * 60_000 && time > 30_000 && time < len - 30_000) _settings.Positions[_current.Source] = time;
        else if (len > 0) _settings.Positions.Remove(_current.Source);
    }

    // ------------------------------------------------------------------ position & seeking

    void UpdatePosition()
    {
        long len = _player.Length;
        if (len <= 0) return;
        long time = _player.Time;
        if (!_seeking)
        {
            SeekSlider.Maximum = len;
            SeekSlider.Value = Math.Clamp(time, 0, len);
        }
        UpdateTimeTexts(_seeking ? (long)SeekSlider.Value : time, len);
    }

    void UpdateTimeTexts(long time, long len)
    {
        TimeText.Text = TimeFormat.Format(time);
        LengthText.Text = len <= 0 ? "--:--" : _showRemaining ? "-" + TimeFormat.Format(len - time) : TimeFormat.Format(len);
    }

    void SeekBy(long deltaMs)
    {
        if (_current == null || !_player.IsSeekable) return;
        long len = _player.Length;
        long t = Math.Clamp(_player.Time + deltaMs, 0, Math.Max(0, len - 500));
        _player.Time = t;
        UpdateTimeTexts(t, len);
        ShowOsd($"{(deltaMs >= 0 ? "»" : "«")}  {TimeFormat.Format(t)} / {TimeFormat.Format(len)}");
    }

    void SeekToFraction(double fraction)
    {
        if (_current == null || !_player.IsSeekable || _player.Length <= 0) return;
        var t = (long)(_player.Length * Math.Clamp(fraction, 0, 1));
        _player.Time = t;
        ShowOsd($"{TimeFormat.Format(t)} / {TimeFormat.Format(_player.Length)}");
    }

    void SeekSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e) => _seeking = true;

    void SeekSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_seeking) return;
        _seeking = false;
        if (_player.IsSeekable) _player.Time = (long)SeekSlider.Value;
    }

    void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_seeking && _ready) UpdateTimeTexts((long)e.NewValue, _player.Length);
    }

    void SeekSlider_MouseMove(object sender, MouseEventArgs e)
    {
        if (SeekSlider.Maximum <= 1 || _current == null) return;
        var x = e.GetPosition(SeekSlider).X;
        var ms = (long)(Math.Clamp(x / SeekSlider.ActualWidth, 0, 1) * SeekSlider.Maximum);
        SeekHoverText.Text = TimeFormat.Format(ms);
        SeekHover.Visibility = Visibility.Visible;
        SeekHover.UpdateLayout();
        Canvas.SetLeft(SeekHover, Math.Clamp(x - SeekHover.ActualWidth / 2, 0, Math.Max(0, SeekSlider.ActualWidth - SeekHover.ActualWidth)));
    }

    void SeekSlider_MouseLeave(object sender, MouseEventArgs e) => SeekHover.Visibility = Visibility.Collapsed;

    void SeekSlider_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        SeekBy(e.Delta > 0 ? 5000 : -5000);
        e.Handled = true;
    }

    void LengthText_Click(object sender, MouseButtonEventArgs e)
    {
        _showRemaining = !_showRemaining;
        UpdateTimeTexts(_player.Time, _player.Length);
    }

    // ------------------------------------------------------------------ volume & speed

    void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        int v = (int)Math.Round(e.NewValue);
        _settings.Volume = v;
        _player.Volume = v;
        VolumeText.Text = $"{v}%";
        if (_settings.Muted && v > 0) SetMuted(false);
        UpdateMuteIcon();
    }

    void ChangeVolume(int delta)
    {
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta, 0, VolumeSlider.Maximum);
        ShowOsd($"Volume {(int)VolumeSlider.Value}%");
    }

    void VolumeSlider_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ChangeVolume(e.Delta > 0 ? 5 : -5);
        e.Handled = true;
    }

    void SetMuted(bool muted)
    {
        _settings.Muted = muted;
        _player.Mute = muted;
        UpdateMuteIcon();
    }

    System.Windows.Media.Brush Res(string key) => (System.Windows.Media.Brush)FindResource(key);

    void UpdateMuteIcon()
    {
        bool silent = _settings.Muted || VolumeSlider.Value <= 0;
        MuteButton.Content = silent ? IconMuted : IconVolume;
        MuteButton.Foreground = Res(silent ? "Muted" : "Text");
    }

    void SetSpeed(float rate)
    {
        _rate = Math.Clamp(rate, 0.25f, 4f);
        _player.SetRate(_rate);
        SpeedText.Text = $"{_rate:0.##}×";
        SpeedText.Foreground = Res(Math.Abs(_rate - 1f) < 0.01f ? "Text" : "AccentHover");
        ShowOsd($"Speed {_rate:0.##}×");
    }

    void StepSpeed(int dir)
    {
        int i = Array.FindIndex(Speeds, s => s >= _rate - 0.001f);
        if (i < 0) i = Speeds.Length - 1;
        i = Math.Clamp(i + dir, 0, Speeds.Length - 1);
        SetSpeed(Speeds[i]);
    }

    // ------------------------------------------------------------------ tracks, subtitles, snapshot

    void LoadSubtitle(string path)
    {
        if (_current == null) return;
        _player.AddSlave(MediaSlaveType.Subtitle, new Uri(path).AbsoluteUri, true);
        ShowOsd($"Subtitles: {Path.GetFileName(path)}");
    }

    void CycleSubtitle()
    {
        var tracks = _player.SpuDescription;
        if (tracks.Length == 0) { ShowOsd("No subtitles"); return; }
        int i = Array.FindIndex(tracks, t => t.Id == _player.Spu);
        var next = tracks[(i + 1) % tracks.Length];
        _player.SetSpu(next.Id);
        ShowOsd(next.Id < 0 ? "Subtitles off" : $"Subtitles: {next.Name}");
    }

    void CycleAudioTrack()
    {
        var tracks = _player.AudioTrackDescription.Where(t => t.Id >= 0).ToArray();
        if (tracks.Length < 2) { ShowOsd(tracks.Length == 1 ? "Only one audio track" : "No audio tracks"); return; }
        int i = Array.FindIndex(tracks, t => t.Id == _player.AudioTrack);
        var next = tracks[(i + 1) % tracks.Length];
        _player.SetAudioTrack(next.Id);
        ShowOsd($"Audio: {next.Name}");
    }

    void ShiftSubtitleDelay(long deltaMs)
    {
        if (_current == null) return;
        _player.SetSpuDelay(_player.SpuDelay + deltaMs * 1000);
        ShowOsd($"Subtitle delay {_player.SpuDelay / 1000} ms");
    }

    void TakeSnapshot()
    {
        if (_current == null || _audioOnly || _player.VideoTrackCount <= 0) { ShowOsd("Nothing to capture"); return; }
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Surfio");
        Directory.CreateDirectory(dir);
        var safe = string.Concat(_current.Title.Split(Path.GetInvalidFileNameChars()));
        var stamp = TimeFormat.Format(_player.Time).Replace(':', '-');
        var file = Path.Combine(dir, $"{safe} {stamp}.png");
        ShowOsd(_player.TakeSnapshot(0, file, 0, 0) ? "Snapshot saved to Pictures\\Surfio" : "Snapshot failed");
    }

    // ------------------------------------------------------------------ fullscreen

    void ToggleFullscreen()
    {
        if (_fullscreen) ExitFullscreen();
        else EnterFullscreen();
    }

    void EnterFullscreen()
    {
        _fullscreen = true;
        _restoreState = WindowState;
        TopBar.Visibility = Visibility.Collapsed;
        PlaylistPanel.Visibility = Visibility.Collapsed;
        PlaylistColumn.Width = new GridLength(0);

        ControlsHost.Child = null;
        ControlsHost.Visibility = Visibility.Collapsed;
        OverlayControlsHost.Child = ControlBar;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal; // forces a re-layout over the taskbar
        WindowState = WindowState.Maximized;
        FullscreenButton.Content = IconExitFullscreen;
        ShowFullscreenControls();
    }

    void ExitFullscreen()
    {
        _fullscreen = false;
        _idleTimer.Stop();
        OverlayControlsHost.Child = null;
        OverlayControlsHost.Visibility = Visibility.Visible;
        ControlsHost.Child = ControlBar;
        ControlsHost.Visibility = Visibility.Visible;
        Overlay.Cursor = null;

        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        WindowState = _restoreState;
        TopBar.Visibility = Visibility.Visible;
        SetPlaylistVisible(_settings.PlaylistVisible);
        FullscreenButton.Content = IconFullscreen;
    }

    void ShowFullscreenControls()
    {
        if (!_fullscreen) return;
        OverlayControlsHost.Visibility = Visibility.Visible;
        Overlay.Cursor = null;
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    void HideFullscreenControls()
    {
        if (!_fullscreen) return;
        if (OverlayControlsHost.IsMouseOver || !_player.IsPlaying)
        {
            _idleTimer.Stop();
            _idleTimer.Start();
            return;
        }
        _idleTimer.Stop();
        OverlayControlsHost.Visibility = Visibility.Collapsed;
        Overlay.Cursor = Cursors.None;
    }

    // ------------------------------------------------------------------ playlist panel

    void SetPlaylistVisible(bool visible)
    {
        _settings.PlaylistVisible = visible;
        if (_fullscreen) return;
        PlaylistPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PlaylistColumn.Width = visible ? new GridLength(310) : new GridLength(0);
        PlaylistToggle.Foreground = Res(visible ? "AccentHover" : "Text");
    }

    void UpdatePlaylistInfo()
    {
        int n = _items.Count;
        PlaylistCount.Text = n == 0 ? "" : n.ToString();
        PlaylistEmpty.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (n == 0)
        {
            PlaylistTotal.Text = "Drag files here to add them";
            return;
        }
        long total = _items.Sum(i => i.DurationMs);
        PlaylistTotal.Text = $"{n} {(n == 1 ? "item" : "items")}" + (total > 0 ? $" · {TimeFormat.Format(total)}" : "");
    }

    void UpdateEmptyHint()
    {
        var last = _settings.Recent.FirstOrDefault(File.Exists);
        EmptyRecentHint.Text = last == null ? "" : $"Press Space to continue “{Path.GetFileNameWithoutExtension(last)}”";
    }

    void RemoveSelected()
    {
        var selected = PlaylistBox.SelectedItems.Cast<PlaylistItem>().ToList();
        foreach (var item in selected) _items.Remove(item);
    }

    void PlaylistBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PlaylistBox.SelectedItem is PlaylistItem item) PlayAt(_items.IndexOf(item));
    }

    void PlaylistBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { RemoveSelected(); e.Handled = true; }
        else if (e.Key == Key.Enter && PlaylistBox.SelectedItem is PlaylistItem item) { PlayAt(_items.IndexOf(item)); e.Handled = true; }
    }

    void PlaylistBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = PlaylistBox.ContextMenu!;
        menu.Items.Clear();
        if (PlaylistBox.SelectedItem is PlaylistItem item)
        {
            menu.Items.Add(MenuEntry("Play", () => PlayAt(_items.IndexOf(item)), "Enter"));
            menu.Items.Add(MenuEntry("Remove", RemoveSelected, "Del"));
            if (!item.IsUrl) menu.Items.Add(MenuEntry("Show in folder", () => Process.Start("explorer.exe", $"/select,\"{item.Source}\"")));
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(MenuEntry("Add files…", () => OpenFilesDialog(play: false)));
        menu.Items.Add(MenuEntry("Sort by name", SortPlaylist, enabled: _items.Count > 1));
        menu.Items.Add(MenuEntry("Save playlist…", SavePlaylist, enabled: _items.Count > 0));
        menu.Items.Add(MenuEntry("Clear playlist", ClearPlaylist, enabled: _items.Count > 0));
    }

    void SortPlaylist()
    {
        var sorted = _items.OrderBy(i => i.Title, NaturalComparer.Instance).ToList();
        _items.Clear();
        foreach (var i in sorted) _items.Add(i);
    }

    void SavePlaylist()
    {
        var dlg = new SaveFileDialog { Filter = "M3U playlist|*.m3u8", FileName = "Surfio playlist.m3u8" };
        if (dlg.ShowDialog(this) != true) return;
        MediaFiles.WritePlaylist(dlg.FileName, _items.Select(i => (i.Source, i.Title, i.DurationMs)));
        ShowOsd("Playlist saved");
    }

    void ClearPlaylist()
    {
        _items.Clear();
        UpdatePlaylistInfo();
    }

    void AddFiles_Click(object sender, RoutedEventArgs e) => OpenFilesDialog(play: false);
    void SavePlaylist_Click(object sender, RoutedEventArgs e) { if (_items.Count > 0) SavePlaylist(); }
    void ClearPlaylist_Click(object sender, RoutedEventArgs e) => ClearPlaylist();
    void PlaylistToggle_Click(object sender, RoutedEventArgs e) => SetPlaylistVisible(!_settings.PlaylistVisible);

    // ------------------------------------------------------------------ menus

    static MenuItem MenuEntry(string header, Action onClick, string? gesture = null, bool isChecked = false, bool enabled = true)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsChecked = isChecked, IsEnabled = enabled };
        item.Click += (_, _) => onClick();
        return item;
    }

    static MenuItem MenuLabel(string text) => new() { Header = text, IsEnabled = false, FontSize = 11.5 };

    static void ShowMenu(FrameworkElement target, IEnumerable<Control> items)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };
        foreach (var i in items) menu.Items.Add(i);
        menu.IsOpen = true;
    }

    void OpenMenu_Click(object sender, RoutedEventArgs e)
    {
        var items = new List<Control>
        {
            MenuEntry("Open files…", () => OpenFilesDialog(play: true), "Ctrl+O"),
            MenuEntry("Open folder…", OpenFolderDialog, "Ctrl+Shift+O"),
            MenuEntry("Open stream URL…", OpenUrlDialog, "Ctrl+U"),
        };
        var recent = _settings.Recent.Take(10).ToList();
        if (recent.Count > 0)
        {
            items.Add(new Separator());
            items.Add(MenuLabel("Recent"));
            foreach (var r in recent)
            {
                bool isUrl = Uri.TryCreate(r, UriKind.Absolute, out var u) && !u.IsFile;
                items.Add(MenuEntry(isUrl ? r : Path.GetFileName(r), () => Open([r], play: true), enabled: isUrl || File.Exists(r)));
            }
            items.Add(MenuEntry("Clear recent", () => { _settings.Recent.Clear(); UpdateEmptyHint(); }));
        }
        ShowMenu(OpenMenuButton, items);
    }

    void AudioMenu_Click(object sender, RoutedEventArgs e)
    {
        var items = new List<Control> { MenuLabel("Audio track") };
        TrackDescription[] tracks = _current == null ? [] : _player.AudioTrackDescription;
        if (tracks.Length == 0) items.Add(MenuLabel("  None — play something first"));
        foreach (var t in tracks)
        {
            var id = t.Id;
            items.Add(MenuEntry(id < 0 ? "Off" : t.Name, () => _player.SetAudioTrack(id), isChecked: _player.AudioTrack == id));
        }
        items.Add(new Separator());
        items.Add(MenuLabel("Output device"));
        var current = _player.OutputDevice;
        foreach (var d in _player.AudioOutputDeviceEnum)
        {
            var id = d.DeviceIdentifier;
            items.Add(MenuEntry(string.IsNullOrEmpty(d.Description) ? "Default" : d.Description, () => _player.SetOutputDevice(id), isChecked: id == current));
        }
        items.Add(new Separator());
        items.Add(MenuEntry("Next audio track", CycleAudioTrack, "B", enabled: _current != null));
        ShowMenu(AudioMenuButton, items);
    }

    void SubtitleMenu_Click(object sender, RoutedEventArgs e)
    {
        var items = new List<Control>();
        TrackDescription[] tracks = _current == null ? [] : _player.SpuDescription;
        if (tracks.Length == 0) items.Add(MenuLabel("No subtitle tracks"));
        foreach (var t in tracks)
        {
            var id = t.Id;
            items.Add(MenuEntry(id < 0 ? "Off" : t.Name, () => _player.SetSpu(id), isChecked: _player.Spu == id));
        }
        items.Add(new Separator());
        items.Add(MenuEntry("Load subtitle file…", () =>
        {
            var dlg = new OpenFileDialog { Filter = "Subtitles|" + string.Join(";", MediaFiles.Subtitles.Select(s => "*" + s)) };
            if (dlg.ShowDialog(this) == true) LoadSubtitle(dlg.FileName);
        }, enabled: _current != null));
        items.Add(MenuEntry("Next subtitle", CycleSubtitle, "V", enabled: _current != null));
        items.Add(new Separator());
        items.Add(MenuEntry($"Delay −100 ms   (now {_player.SpuDelay / 1000} ms)", () => ShiftSubtitleDelay(-100), "G", enabled: _current != null));
        items.Add(MenuEntry("Delay +100 ms", () => ShiftSubtitleDelay(100), "H", enabled: _current != null));
        items.Add(MenuEntry("Reset delay", () => { _player.SetSpuDelay(0); ShowOsd("Subtitle delay 0 ms"); }, enabled: _current != null));
        ShowMenu(SubtitleMenuButton, items);
    }

    void VideoMenu_Click(object sender, RoutedEventArgs e)
    {
        var items = new List<Control> { MenuLabel("Aspect ratio") };
        var current = _player.AspectRatio;
        foreach (var (label, value) in new[] { ("Default", (string?)null), ("16:9", "16:9"), ("4:3", "4:3"), ("21:9", "21:9"), ("1:1", "1:1"), ("2.35:1", "235:100") })
        {
            items.Add(MenuEntry(label, () => { _player.AspectRatio = value; ShowOsd($"Aspect {label}"); }, isChecked: current == value));
        }
        items.Add(new Separator());
        items.Add(MenuEntry("Take snapshot", TakeSnapshot, "S", enabled: _current != null));
        items.Add(MenuEntry("Next frame", () => _player.NextFrame(), ".", enabled: _current != null));
        items.Add(MenuEntry("Fullscreen", ToggleFullscreen, "F"));
        items.Add(new Separator());
        items.Add(MenuEntry("Always on top", ToggleAlwaysOnTop, isChecked: _settings.AlwaysOnTop));
        items.Add(MenuEntry("Resume where I left off", () => _settings.ResumePlayback = !_settings.ResumePlayback, isChecked: _settings.ResumePlayback));
        items.Add(MenuEntry("Hardware decoding", () =>
        {
            _settings.HardwareDecoding = !_settings.HardwareDecoding;
            _player.EnableHardwareDecoding = _settings.HardwareDecoding;
            ShowOsd("Applies from the next file");
        }, isChecked: _settings.HardwareDecoding));
        items.Add(new Separator());
        items.Add(MenuEntry("Keyboard shortcuts", ShowShortcuts, "F1"));
        ShowMenu(VideoMenuButton, items);
    }

    void SpeedMenu_Click(object sender, RoutedEventArgs e)
    {
        var items = new List<Control>();
        foreach (var s in Speeds)
        {
            var speed = s;
            items.Add(MenuEntry(Math.Abs(s - 1f) < 0.01f ? "1× (normal)" : $"{s:0.##}×", () => SetSpeed(speed), isChecked: Math.Abs(_rate - s) < 0.01f));
        }
        items.Add(new Separator());
        items.Add(MenuEntry("Slower", () => StepSpeed(-1), "["));
        items.Add(MenuEntry("Faster", () => StepSpeed(1), "]"));
        items.Add(MenuEntry("Normal", () => SetSpeed(1f), "Backspace"));
        ShowMenu(SpeedMenuButton, items);
    }

    void ToggleAlwaysOnTop()
    {
        _settings.AlwaysOnTop = !_settings.AlwaysOnTop;
        Topmost = _settings.AlwaysOnTop;
        ShowOsd(_settings.AlwaysOnTop ? "Always on top" : "Normal window");
    }

    void ShowShortcuts()
    {
        MessageBox.Show(this,
            "Space / K\tPlay / pause\n" +
            "← →\t\tBack / forward 5 s (Ctrl: 30 s)\n" +
            "↑ ↓\t\tVolume\n" +
            "M\t\tMute\n" +
            "F, Enter\tFullscreen (Esc to leave)\n" +
            "N / P\t\tNext / previous\n" +
            "0–9\t\tJump to 0–90 %\n" +
            "Home\t\tStart over\n" +
            "[  ]  Backspace\tSlower / faster / normal speed\n" +
            "V / B\t\tNext subtitle / audio track\n" +
            "G / H\t\tSubtitle delay −/+ 100 ms\n" +
            ".\t\tNext frame\n" +
            "S\t\tSnapshot\n" +
            "L\t\tShow / hide playlist\n" +
            "Ctrl+O\t\tOpen files (Ctrl+Shift+O folder, Ctrl+U URL)\n\n" +
            "Mouse: click to pause, double-click for fullscreen, wheel for volume, right-click for the menu.",
            "Surfio shortcuts");
    }

    // ------------------------------------------------------------------ transport buttons

    void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlay();
    void Stop_Click(object sender, RoutedEventArgs e) => StopPlayback();
    void Next_Click(object sender, RoutedEventArgs e) => Next();
    void Previous_Click(object sender, RoutedEventArgs e) => Previous();
    void Snapshot_Click(object sender, RoutedEventArgs e) => TakeSnapshot();
    void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
    void Mute_Click(object sender, RoutedEventArgs e) => SetMuted(!_settings.Muted);

    void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        _settings.Shuffle = !_settings.Shuffle;
        UpdateRepeatShuffleUi();
        ShowOsd(_settings.Shuffle ? "Shuffle on" : "Shuffle off");
    }

    void Repeat_Click(object sender, RoutedEventArgs e)
    {
        _settings.Repeat = _settings.Repeat switch { RepeatMode.Off => RepeatMode.All, RepeatMode.All => RepeatMode.One, _ => RepeatMode.Off };
        UpdateRepeatShuffleUi();
        ShowOsd(_settings.Repeat switch { RepeatMode.All => "Repeat all", RepeatMode.One => "Repeat one", _ => "Repeat off" });
    }

    void UpdateRepeatShuffleUi()
    {
        ShuffleButton.Foreground = Res(_settings.Shuffle ? "AccentHover" : "Muted");
        RepeatButton.Foreground = Res(_settings.Repeat == RepeatMode.Off ? "Muted" : "AccentHover");
        RepeatButton.Content = _settings.Repeat == RepeatMode.One ? IconRepeatOne : IconRepeatAll;
        RepeatButton.ToolTip = _settings.Repeat switch { RepeatMode.All => "Repeat: all", RepeatMode.One => "Repeat: one", _ => "Repeat: off" };
    }

    // ------------------------------------------------------------------ overlay (mouse on the video)

    void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (OverlayControlsHost.Child != null && OverlayControlsHost.IsMouseOver) return;
        Overlay.Focus();
        if (e.ClickCount >= 2)
        {
            _clickTimer.Stop();
            ToggleFullscreen();
        }
        else if (_current != null)
        {
            _clickTimer.Stop();
            _clickTimer.Start(); // single click pauses, unless a second click follows
        }
    }

    void Overlay_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var items = new List<Control>
        {
            MenuEntry(_player.IsPlaying ? "Pause" : "Play", TogglePlay, "Space"),
            MenuEntry("Stop", StopPlayback, enabled: _current != null),
            MenuEntry("Next", () => Next(), "N", enabled: _items.Count > 1),
            MenuEntry("Previous", Previous, "P", enabled: _items.Count > 0),
            new Separator(),
            MenuEntry("Open files…", () => OpenFilesDialog(play: true), "Ctrl+O"),
            MenuEntry("Open folder…", OpenFolderDialog, "Ctrl+Shift+O"),
            new Separator(),
            MenuEntry("Next subtitle", CycleSubtitle, "V", enabled: _current != null),
            MenuEntry("Next audio track", CycleAudioTrack, "B", enabled: _current != null),
            MenuEntry("Take snapshot", TakeSnapshot, "S", enabled: _current != null),
            new Separator(),
            MenuEntry(_fullscreen ? "Exit fullscreen" : "Fullscreen", ToggleFullscreen, "F"),
            MenuEntry("Playlist", () => SetPlaylistVisible(!_settings.PlaylistVisible), "L", isChecked: _settings.PlaylistVisible),
            MenuEntry("Always on top", ToggleAlwaysOnTop, isChecked: _settings.AlwaysOnTop),
        };
        var menu = new ContextMenu { PlacementTarget = Overlay, Placement = PlacementMode.MousePoint };
        foreach (var i in items) menu.Items.Add(i);
        menu.IsOpen = true;
        e.Handled = true;
    }

    void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(Overlay);
        if ((p - _lastMouse).Length < 2) return;
        _lastMouse = p;
        ShowFullscreenControls();
    }

    void Overlay_MouseWheel(object sender, MouseWheelEventArgs e) => ChangeVolume(e.Delta > 0 ? 5 : -5);

    // ------------------------------------------------------------------ drag and drop

    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        e.Handled = true;
        Activate();
        // Dropping onto the playlist queues; dropping onto the video plays.
        bool ontoPlaylist = PlaylistPanel.IsVisible && PlaylistPanel.IsMouseOver;
        Open(files, play: !ontoPlaylist);
    }

    // ------------------------------------------------------------------ keyboard

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        // Let the playlist keep its own navigation keys.
        if (PlaylistBox.IsKeyboardFocusWithin && key is Key.Up or Key.Down or Key.Enter or Key.Delete or Key.PageUp or Key.PageDown or Key.Home or Key.End) return;

        bool handled = true;
        switch (key)
        {
            case Key.Space or Key.K or Key.MediaPlayPause: TogglePlay(); break;
            case Key.Left: SeekBy(ctrl ? -30_000 : -5_000); break;
            case Key.Right: SeekBy(ctrl ? 30_000 : 5_000); break;
            case Key.Up: ChangeVolume(5); break;
            case Key.Down: ChangeVolume(-5); break;
            case Key.M or Key.VolumeMute: SetMuted(!_settings.Muted); ShowOsd(_settings.Muted ? "Muted" : "Sound on"); break;
            case Key.F or Key.Enter: ToggleFullscreen(); break;
            case Key.Escape when _fullscreen: ExitFullscreen(); break;
            case Key.N or Key.MediaNextTrack: Next(); break;
            case Key.P or Key.MediaPreviousTrack: Previous(); break;
            case Key.MediaStop: StopPlayback(); break;
            case Key.Home: if (_current != null && _player.IsSeekable) { _player.Time = 0; ShowOsd("Start"); } break;
            case Key.S: TakeSnapshot(); break;
            case Key.L: SetPlaylistVisible(!_settings.PlaylistVisible); break;
            case Key.V: CycleSubtitle(); break;
            case Key.B: CycleAudioTrack(); break;
            case Key.G: ShiftSubtitleDelay(-100); break;
            case Key.H: ShiftSubtitleDelay(100); break;
            case Key.OemOpenBrackets: StepSpeed(-1); break;
            case Key.OemCloseBrackets: StepSpeed(1); break;
            case Key.Back: SetSpeed(1f); break;
            case Key.OemPeriod: if (_current != null) _player.NextFrame(); break;
            case Key.O when ctrl && shift: OpenFolderDialog(); break;
            case Key.O when ctrl: OpenFilesDialog(play: true); break;
            case Key.U when ctrl: OpenUrlDialog(); break;
            case Key.F1: ShowShortcuts(); break;
            case >= Key.D0 and <= Key.D9 when !ctrl: SeekToFraction((key - Key.D0) / 10.0); break;
            case >= Key.NumPad0 and <= Key.NumPad9: SeekToFraction((key - Key.NumPad0) / 10.0); break;
            default: handled = false; break;
        }
        if (handled)
        {
            e.Handled = true;
            if (_fullscreen && key is not (Key.F or Key.Enter or Key.Escape)) ShowFullscreenControls();
        }
    }

    // ------------------------------------------------------------------ OSD

    void ShowOsd(string text, int ms = 1600)
    {
        OsdText.Text = text;
        Osd.Visibility = Visibility.Visible;
        _osdTimer.Stop();
        _osdTimer.Interval = TimeSpan.FromMilliseconds(ms);
        _osdTimer.Start();
    }
}
