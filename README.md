# Surfio

Video and audio player for Windows, by Surfi. Built with .NET 10 (WPF) and the VLC engine
(LibVLCSharp), so it plays practically anything: MKV, MP4, AVI, WMV, MOV, WebM, FLV, TS, DVD rips,
FLAC, MP3, WAV, Opus, AC3/DTS audio, embedded and external subtitles, and network streams.

## Download

Get the installer from [Releases](https://github.com/surfitools/surfio/releases/latest)
(`Surfio-Setup-x.y.z.exe`, Windows 10/11 64-bit). Nothing else to install.

## Run from source

```
dotnet run
```

Requires the .NET 10 SDK. The first build downloads the VLC engine from NuGet (~40 MB).

## Build the installer

```
.\publish.ps1
```

This makes a self-contained build in `publish\` (no .NET needed on the target PC) and, if
[Inno Setup 6](https://jrsoftware.org/isinfo.php) is installed, `dist\Surfio-Setup-1.0.0.exe`
(~70 MB). The installer adds Start menu and desktop shortcuts, and registers Surfio for video and
music files under "Open with" and Settings → Default apps. It doesn't take over your defaults.

## Features

- Playlist panel: add files, folders (scanned recursively, natural sort) or `.m3u`/`.m3u8`/`.pls`
  playlists; drag and drop; reorder by name; save as `.m3u8`; titles, artists and durations read
  from tags
- Shuffle, repeat all / repeat one, next / previous
- Seek bar with hover time, click-to-seek and mouse-wheel seeking; click the total time to show time remaining
- Resumes long videos where you left off (per file)
- Speed 0.25×–3×, audio track and output device, subtitle track, external subtitle files (or drop
  one onto the video), subtitle delay, aspect ratio, frame step, snapshots to `Pictures\Surfio`
- Audio-only view with album art
- Fullscreen with auto-hiding controls; always on top; dark title bar
- One window: opening a file from Explorer while Surfio is running sends it to the open player
- Stops the screen from sleeping during video
- Remembers volume, window size, repeat/shuffle and recent files (`%AppData%\Surfio\settings.json`)

## Keyboard

| Key | Action |
| --- | --- |
| Space / K | Play / pause |
| ← → | Back / forward 5 s (Ctrl: 30 s) |
| ↑ ↓ | Volume |
| M | Mute |
| F / Enter / double-click | Fullscreen (Esc to leave) |
| N / P | Next / previous |
| 0–9 | Jump to 0–90 % |
| Home | Start over |
| [ ] / Backspace | Slower / faster / normal speed |
| V / B | Next subtitle / audio track |
| G / H | Subtitle delay −/+ 100 ms |
| . | Next frame |
| S | Snapshot |
| L | Show / hide playlist |
| Ctrl+O / Ctrl+Shift+O / Ctrl+U | Open files / folder / stream URL |
| F1 | Shortcut list |

## Project layout

```
App.xaml(.cs)            startup, single-instance hand-off
MainWindow.xaml(.cs)     the player
UrlDialog.xaml(.cs)      "Open stream" dialog
Models/PlaylistItem.cs   playlist entry
Services/                settings, file types & playlists, single instance, Windows APIs
Themes/Theme.xaml        colours and control styles
installer/               Inno Setup script
```

## Licence

Surfio's own code is MIT (see [LICENSE](LICENSE)). It uses the VLC engine (libVLC) and
LibVLCSharp, both LGPL 2.1+, loaded as separate DLLs. The installer ships them unmodified.
