using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using WinRT.Interop;
using ZenMusic.Audio;
using ZenMusic.Library;
using ZenMusic.Tagging;
using ZenMusic.UI;

namespace ZenMusic;

public sealed partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly WasapiAudioEngine _engine;
    private readonly DispatcherTimer _progressTimer;

    private readonly List<TrackItem> _allTracks = new();
    private ObservableCollection<TrackItem> _visibleTracks = new();
    private readonly ObservableCollection<TrackItem> _queueTracks = new();
    private readonly List<TrackItem> _unshuffledQueue = new();
    private readonly ConcurrentDictionary<string, ImageSource?> _artThumbCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<SavedPlaylist> _playlists = new();
    private readonly ObservableCollection<LrcDisplayLine> _selectedLrcLines = new();
    private readonly ObservableCollection<LrcDisplayLine> _focusLrcLines = new();
    private readonly Stack<Func<Task>> _undoStack = new();
    private bool _suppressSelectionChanged;

    private static List<TrackItem>? _draggedTracks;
    private static readonly Dictionary<string, bool> _columnVisible = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Genre"] = false,
        ["Year"] = true,
        ["Rating"] = true,
        ["Length"] = true,
        ["DateAdded"] = true,
        ["Bitrate"] = true
    };

    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _searchDebounceCts;
    private FileSystemWatcher? _folderWatcher;
    private CancellationTokenSource? _watcherDebounceCts;
    private DateTime _suppressWatcherUntilUtc = DateTime.MinValue;
    private int _scanId;
    private int _queueIndex = -1;
    private bool _isDraggingSeek;
    private bool _isUpdatingSeekSlider;
    private DateTime _lastStarPointerUtc = DateTime.MinValue;
    private RectInt32 _lastSearchPassthroughRect;
    private string? _selectedLyricsTrackPath;
    private int _inspectorArtMode = 2;
    private bool _isDraggingSplitter;
    private double _splitterStartY;
    private double _splitterStartHeight;
    private bool _isMuted;
    private double _preMuteVolume = 70;
    private bool _selectedArtIsLight;
    private int _activeFocusLrcIndex = -1;
    private int _activeSelectedLrcIndex = -1;
    private string? _activePlayingPathForLyrics;

    private static readonly SolidColorBrush TransparentRowBrush = new(Color.FromArgb(0, 0, 0, 0));
    private static readonly LinearGradientBrush InspectorOverlayBrush = new()
    {
        StartPoint = new Point(0.5, 0),
        EndPoint = new Point(0.5, 1),
        GradientStops =
        {
            new GradientStop { Color = Color.FromArgb(0, 0, 0, 0), Offset = 0.0 },
            new GradientStop { Color = Color.FromArgb(75, 10, 10, 10), Offset = 0.45 },
            new GradientStop { Color = Color.FromArgb(148, 8, 8, 8), Offset = 1.0 }
        }
    };
    private static readonly LinearGradientBrush InspectorLightOverlayBrush = new()
    {
        StartPoint = new Point(0.5, 0),
        EndPoint = new Point(0.5, 1),
        GradientStops =
        {
            new GradientStop { Color = Color.FromArgb(0, 255, 255, 255), Offset = 0.0 },
            new GradientStop { Color = Color.FromArgb(120, 255, 255, 255), Offset = 0.42 },
            new GradientStop { Color = Color.FromArgb(204, 248, 248, 248), Offset = 1.0 }
        }
    };
    private static readonly SolidColorBrush DarkOverlayPrimaryBrush = new(Color.FromArgb(255, 245, 245, 245));
    private static readonly SolidColorBrush DarkOverlaySecondaryBrush = new(Color.FromArgb(255, 212, 212, 212));
    private static readonly SolidColorBrush DarkOverlayAccentBrush = new(Color.FromArgb(255, 255, 204, 72));
    private static readonly SolidColorBrush DarkOverlayStarBgBrush = new(Color.FromArgb(95, 255, 255, 255));
    private static readonly SolidColorBrush LightOverlayPrimaryBrush = new(Color.FromArgb(255, 20, 20, 20));
    private static readonly SolidColorBrush LightOverlaySecondaryBrush = new(Color.FromArgb(255, 58, 58, 58));
    private static readonly SolidColorBrush LightOverlayAccentBrush = new(Color.FromArgb(255, 148, 88, 0));
    private static readonly SolidColorBrush LightOverlayStarBgBrush = new(Color.FromArgb(85, 0, 0, 0));

    public static Visibility NowPlayingVis(bool isNowPlaying) => isNowPlaying ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility NotNowPlayingVis(bool isNowPlaying) => isNowPlaying ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility EvenRowVis(bool isNowPlaying, int index) => !isNowPlaying && index % 2 == 0 ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility BoolVis(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static ImageSource? ToImageSource(object? obj) => obj as ImageSource;

    private bool IsTileMode => string.Equals(_settings.ViewMode, "Grid", StringComparison.OrdinalIgnoreCase);

    private List<TrackItem> GetSelectedTracks()
    {
        var active = IsTileMode ? (ListViewBase)AlbumsGridView : TracksListView;
        return active.SelectedItems.OfType<TrackItem>().ToList();
    }

    private TrackItem? GetPrimarySelectedTrack()
    {
        var active = IsTileMode ? (ListViewBase)AlbumsGridView : TracksListView;
        return active.SelectedItem as TrackItem
            ?? TracksListView.SelectedItem as TrackItem
            ?? AlbumsGridView.SelectedItem as TrackItem;
    }

    private void SyncSelectionAcrossViews(TrackItem? track)
    {
        _suppressSelectionChanged = true;
        try
        {
            TracksListView.SelectedItem = track;
            AlbumsGridView.SelectedItem = track;
        }
        finally
        {
            _suppressSelectionChanged = false;
        }
    }

    private void ScrollActiveViewToTrack(TrackItem? track, ScrollIntoViewAlignment alignment = ScrollIntoViewAlignment.Default)
    {
        if (track == null) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_visibleTracks.Contains(track)) return;
            if (IsTileMode)
            {
                AlbumsGridView.UpdateLayout();
                AlbumsGridView.ScrollIntoView(track, alignment);
            }
            else
            {
                TracksListView.UpdateLayout();
                TracksListView.ScrollIntoView(track, alignment);
            }
        });
    }

    private void SetNowPlayingTrack(string? playingPath)
    {
        bool hasPath = !string.IsNullOrEmpty(playingPath);
        foreach (var t in _allTracks)
            t.IsNowPlaying = hasPath && string.Equals(t.Path, playingPath, StringComparison.OrdinalIgnoreCase);
        foreach (var t in _visibleTracks)
            t.IsNowPlaying = hasPath && string.Equals(t.Path, playingPath, StringComparison.OrdinalIgnoreCase);
        foreach (var t in _queueTracks)
            t.IsNowPlaying = hasPath && string.Equals(t.Path, playingPath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsColVisible(string name) => _columnVisible.TryGetValue(name, out bool v) && v;
    public static GridLength ColGenreWidth() => IsColVisible("Genre") ? new GridLength(0.9, GridUnitType.Star) : new GridLength(0);
    public static GridLength ColYearWidth() => IsColVisible("Year") ? new GridLength(48) : new GridLength(0);
    public static GridLength ColRatingWidth() => IsColVisible("Rating") ? new GridLength(78) : new GridLength(0);
    public static GridLength ColLengthWidth() => IsColVisible("Length") ? new GridLength(56) : new GridLength(0);
    public static GridLength ColDateAddedWidth() => IsColVisible("DateAdded") ? new GridLength(124) : new GridLength(0);
    public static GridLength ColBitrateWidth() => IsColVisible("Bitrate") ? new GridLength(68) : new GridLength(0);
    public static Visibility ColRatingVis() => IsColVisible("Rating") ? Visibility.Visible : Visibility.Collapsed;

    private bool _leftPaneOpen = true;
    private bool _rightPaneOpen = true;
    private bool _isMiniPlayer;
    private RectInt32 _preMiniBounds;
    private bool _wasMaximizedBeforeMini;
    private bool _initialized;
    private string _activeFacet = "all";
    private SavedPlaylist? _activePlaylist;
    private CustomFilterCriteria? _activeCustomFilter;
    private string? _activeHygiene;
    private string? _activeArtistFilter;
    private string? _activeAlbumFilter;
    private string _typeJumpBuffer = string.Empty;
    private DateTime _lastTypeJumpUtc = DateTime.MinValue;
    private CancellationTokenSource? _jumpPillCts;

    public MainWindow()
    {
        InitializeComponent();

        try { SystemBackdrop = new MicaBackdrop(); } catch { }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);

        _settings = AppSettings.Load();
        foreach (var kv in _settings.ColumnVisibility)
            _columnVisible[kv.Key] = kv.Value;

        _leftPaneOpen = _settings.LeftPaneOpen;
        _rightPaneOpen = _settings.RightPaneOpen;
        _inspectorArtMode = Math.Clamp(_settings.InspectorArtMode, 0, 2);
        SelectedInspectorScroll.MaxHeight = Math.Clamp(_settings.InspectorHeight, 130, 460);

        ApplyPaneWidths();
        ApplyColumnWidths();
        RestoreWindowBounds();
        ApplyTheme(_settings.Theme);
        ApplyInspectorArtMode();
        ApplyViewModeVisuals();

        _engine = new WasapiAudioEngine(DispatcherQueue);
        _engine.TrackEnded += Engine_TrackEnded;
        _engine.PlaybackStateChanged += Engine_PlaybackStateChanged;
        _engine.PlaybackError += Engine_PlaybackError;
        _engine.SmtcNextRequested += () => PlayNext(fromAutoTrackEnd: false);
        _engine.SmtcPreviousRequested += () => PlayPrevious();

        ApplyAudioConfigFromSettings();

        PlaybarVisualizer.AttachAnalyzer(_engine.Fft);
        FocusVisualizer.AttachAnalyzer(_engine.Fft);
        ApplyVisualizerFromSettings();
        SyncVisualizerStageVisibility();

        VolumeSlider.Value = _settings.Volume;
        _engine.SetVolume(_settings.Volume);
        ShuffleToggle.IsChecked = _settings.Shuffle;
        if (_settings.RepeatMode == 0 && !_settings.RepeatAll) _settings.RepeatMode = 0;
        UpdateRepeatButtonVisual();

        TracksListView.ItemsSource = _visibleTracks;
        AlbumsGridView.ItemsSource = _visibleTracks;
        QueueListView.ItemsSource = _queueTracks;
        SelectedSyncedLyricsList.ItemsSource = _selectedLrcLines;
        FocusSyncedLyricsList.ItemsSource = _focusLrcLines;

        foreach (var pl in _settings.Playlists) _playlists.Add(pl);
        PlaylistsList.ItemsSource = _playlists;
        PlaylistsEmptyHint.Visibility = _playlists.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSortHeaders();

        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _progressTimer.Tick += ProgressTimer_Tick;

        SeekSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(SeekSlider_PointerPressed), true);
        SeekSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(SeekSlider_PointerReleased), true);
        foreach (var starBtn in new[] { StarBtn1, StarBtn2, StarBtn3, StarBtn4, StarBtn5 })
            starBtn.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(StarBtn_PointerPressed), true);

        SearchHostGrid.SizeChanged += (_, _) => UpdateTitleBarPassthrough();
        SearchHostGrid.LayoutUpdated += (_, _) => UpdateTitleBarPassthrough();

        Closed += (_, _) =>
        {
            _progressTimer.Stop();
            _scanCts?.Cancel();
            _watcherDebounceCts?.Cancel();
            if (_folderWatcher != null)
            {
                _folderWatcher.EnableRaisingEvents = false;
                _folderWatcher.Dispose();
                _folderWatcher = null;
            }
            SaveWindowBounds();
            SaveSessionState();
            _settings.Save();
            SaveLibraryCacheSnapshot();
            _engine.Dispose();
        };

        if (!string.IsNullOrWhiteSpace(_settings.MusicFolder) && Directory.Exists(_settings.MusicFolder))
        {
            RecordRecentFolder(_settings.MusicFolder);
            LoadCachedLibraryAndRestoreSession(_settings.MusicFolder);
            StartFolderWatcher(_settings.MusicFolder);
            _ = ScanFolderAsync(_settings.MusicFolder, isStartupDelta: true);
        }
        _initialized = true;
    }

    private void LoadCachedLibraryAndRestoreSession(string folder)
    {
        var instant = LibraryCache.LoadInstantTracks(AppPaths.LibraryCachePath, folder);
        if (instant.Count == 0) return;

        _allTracks.Clear();
        _allTracks.AddRange(instant);
        RefreshVisibleTracks();
        EmptyStatePanel.Visibility = Visibility.Collapsed;
        RestoreSessionQueue();
    }

    private void RestoreSessionQueue()
    {
        if (_settings.SavedQueuePaths.Count == 0 || _queueTracks.Count > 0) return;
        var byPath = _allTracks.ToDictionary(t => t.Path, StringComparer.OrdinalIgnoreCase);
        _queueTracks.Clear();
        _unshuffledQueue.Clear();
        foreach (string p in _settings.SavedQueuePaths)
        {
            if (byPath.TryGetValue(p, out var t))
            {
                _queueTracks.Add(t);
                _unshuffledQueue.Add(t);
            }
        }
        if (_queueTracks.Count == 0) return;

        int idx = Math.Clamp(_settings.SavedQueueIndex, 0, _queueTracks.Count - 1);
        _queueIndex = idx;
        var current = _queueTracks[idx];
        SetNowPlayingTrack(current.Path);
        UpdateQueueHeader();
        QueueListView.SelectedIndex = idx;

        NowPlayingTitleText.Text = current.DisplayTitle;
        NowPlayingArtistText.Text = string.IsNullOrWhiteSpace(current.Artist) ? current.FileName : current.Artist;
        NowPlayingRatingCell.Value = current.Rating;
        FocusTitleText.Text = current.DisplayTitle;
        FocusArtistText.Text = NowPlayingArtistText.Text;
        FocusRatingCell.Value = current.Rating;
        _activePlayingPathForLyrics = current.Path;

        var startPos = TimeSpan.FromSeconds(Math.Max(0, _settings.SavedPositionSeconds));
        _engine.LoadPausedFile(current.Path, startPos);

        double durSec = current.Duration.TotalSeconds;
        ElapsedTimeText.Text = FormatTime(startPos);
        TotalTimeText.Text = FormatTime(current.Duration);
        if (durSec > 0)
        {
            _isUpdatingSeekSlider = true;
            try
            {
                SeekSlider.Maximum = durSec;
                SeekSlider.Value = Math.Clamp(startPos.TotalSeconds, 0, durSec);
            }
            finally { _isUpdatingSeekSlider = false; }
        }

        _ = LoadPlayingArtAndSmtcAsync(current);
        _ = LoadFocusLyricsAsync(current.Path);
    }

    private void SaveSessionState()
    {
        _settings.LeftPaneOpen = _leftPaneOpen;
        _settings.RightPaneOpen = _rightPaneOpen;
        _settings.InspectorHeight = SelectedInspectorScroll.MaxHeight;
        _settings.InspectorArtMode = _inspectorArtMode;
        _settings.SavedQueuePaths = _queueTracks.Select(t => t.Path).ToList();
        _settings.SavedQueueIndex = _queueIndex;
        _settings.SavedPositionSeconds = _engine.Position.TotalSeconds;
    }

    private void SaveLibraryCacheSnapshot()
    {
        if (_allTracks.Count > 0)
            LibraryCache.Save(AppPaths.LibraryCachePath, _allTracks);
    }

    private void RestoreWindowBounds()
    {
        try
        {
            var appWin = this.AppWindow;
            var display = DisplayArea.GetFromWindowId(appWin.Id, DisplayAreaFallback.Primary);
            var work = display.WorkArea;
            int w = Math.Clamp(_settings.WindowWidth, Math.Min(1080, work.Width), work.Width);
            int h = Math.Clamp(_settings.WindowHeight, Math.Min(640, work.Height), work.Height);
            if (_settings.WindowX is int x && _settings.WindowY is int y)
            {
                appWin.MoveAndResize(new RectInt32(x, y, w, h));
            }
            else
            {
                int cx = work.X + Math.Max(0, (work.Width - w) / 2);
                int cy = work.Y + Math.Max(0, (work.Height - h) / 2);
                appWin.MoveAndResize(new RectInt32(cx, cy, w, h));
            }

            if (_settings.IsMaximized && appWin.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }
        }
        catch { }
    }

    private void SaveWindowBounds()
    {
        try
        {
            if (_isMiniPlayer)
            {
                _settings.IsMaximized = _wasMaximizedBeforeMini;
                if (!_wasMaximizedBeforeMini && _preMiniBounds.Width >= 400 && _preMiniBounds.Height >= 300)
                {
                    _settings.WindowWidth = _preMiniBounds.Width;
                    _settings.WindowHeight = _preMiniBounds.Height;
                    _settings.WindowX = _preMiniBounds.X;
                    _settings.WindowY = _preMiniBounds.Y;
                }
                return;
            }

            var appWin = this.AppWindow;
            bool isMax = appWin.Presenter is OverlappedPresenter p &&
                         p.State == OverlappedPresenterState.Maximized;
            _settings.IsMaximized = isMax;
            if (!isMax && appWin.Size.Width >= 400 && appWin.Size.Height >= 300)
            {
                _settings.WindowWidth = appWin.Size.Width;
                _settings.WindowHeight = appWin.Size.Height;
                _settings.WindowX = appWin.Position.X;
                _settings.WindowY = appWin.Position.Y;
            }
        }
        catch { }
    }

    private void ApplyTheme(string theme)
    {
        bool dark = !string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase);
        RootGrid.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;

        foreach (var line in _selectedLrcLines)
            line.UseLightTheme = !dark;

        try
        {
            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                var tb = AppWindow.TitleBar;
                tb.ButtonBackgroundColor = Colors.Transparent;
                tb.ButtonInactiveBackgroundColor = Colors.Transparent;
                tb.ButtonForegroundColor = dark ? Colors.White : Color.FromArgb(255, 26, 26, 26);
                tb.ButtonInactiveForegroundColor = dark ? Color.FromArgb(160, 255, 255, 255) : Color.FromArgb(140, 26, 26, 26);
                tb.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
                tb.ButtonHoverBackgroundColor = dark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0);
                tb.ButtonPressedBackgroundColor = dark ? Color.FromArgb(38, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0);
            }
        }
        catch { }
    }

    private void ApplyPaneWidths()
    {
        LeftPaneCol.Width = _leftPaneOpen ? new GridLength(210) : new GridLength(0);
        LeftSidebar.Visibility = _leftPaneOpen ? Visibility.Visible : Visibility.Collapsed;
        RightPaneCol.Width = _rightPaneOpen ? new GridLength(276) : new GridLength(0);
        RightSidebar.Visibility = _rightPaneOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyColumnWidths()
    {
        HeaderColGenre.Width = ColGenreWidth();
        HeaderColYear.Width = ColYearWidth();
        HeaderColRating.Width = ColRatingWidth();
        HeaderColLength.Width = ColLengthWidth();
        HeaderColDateAdded.Width = ColDateAddedWidth();
        HeaderColBitrate.Width = ColBitrateWidth();
    }

    private void ApplyViewModeVisuals()
    {
        bool isGrid = IsTileMode;
        TracksListView.Visibility = isGrid ? Visibility.Collapsed : Visibility.Visible;
        AlbumsGridView.Visibility = isGrid ? Visibility.Visible : Visibility.Collapsed;
        TableHeaderRowDef.Height = new GridLength(32);
        TableHeaderGrid.Visibility = Visibility.Visible;
        ViewToggleIcon.Glyph = isGrid ? "\uE8FD" : "\uE80A";
        ToolTipService.SetToolTip(ViewToggleBtn, isGrid ? "Switch to Track Table" : "Switch to Tile View");
    }

    private void ApplyAudioConfigFromSettings()
    {
        _engine.ConfigureAudio(_settings.OutputDeviceId, new SpeakerRoutingOptions
        {
            Layout = _settings.SpeakerLayout,
            CenterLevel = _settings.CenterLevel,
            LfeGain = _settings.LfeGain,
            RearLevel = _settings.RearLevel
        });
        int endpointCh = WasapiAudioEngine.GetEndpointChannelCount(_settings.OutputDeviceId);
        SpeakerBadgeText.Text = _settings.SpeakerLayout switch
        {
            SpeakerLayout.Stereo21 => endpointCh >= 6 ? "2.1" : "2.1*",
            SpeakerLayout.Surround51Upmix => endpointCh >= 6 ? "5.1+" : "5.1*",
            SpeakerLayout.Surround51Direct => endpointCh >= 6 ? "5.1" : "5.1*",
            _ => "2.0"
        };
        ToolTipService.SetToolTip(SpeakerBadgeBtn, endpointCh >= 6 || _settings.SpeakerLayout == SpeakerLayout.Stereo20
            ? $"Output: {endpointCh}ch WASAPI (Click to switch)"
            : $"Windows audio device reports {endpointCh}ch; using spatial stereo fold-down (Click to switch)");
    }

    private void ApplyVisualizerFromSettings()
    {
        var mode = !_settings.VisualizerEnabled ? VisualizerMode.Off : _settings.VisualizerMode switch
        {
            "Waveform" => VisualizerMode.Waveform,
            "BarsAndWaveform" => VisualizerMode.BarsAndWaveform,
            "Off" => VisualizerMode.Off,
            _ => VisualizerMode.Spectrum
        };

        PlaybarVisualizer.Mode = mode;
        FocusVisualizer.Mode = mode == VisualizerMode.Off ? VisualizerMode.Spectrum : mode;
        PlaybarVisualizerHost.Visibility = mode == VisualizerMode.Off ? Visibility.Collapsed : Visibility.Visible;
        PlaybarVisualizer.IsAudioPlaying = _engine.IsPlaying;
        FocusVisualizer.IsAudioPlaying = _engine.IsPlaying;
        UpdateFocusVizLabel();
        SyncVisualizerStageVisibility();
    }

    private void SyncVisualizerStageVisibility()
    {
        bool focusVisible = FocusVisualizerOverlay.Visibility == Visibility.Visible;
        PlaybarVisualizer.IsStageVisible = !_isMiniPlayer && !focusVisible;
        FocusVisualizer.IsStageVisible = focusVisible;
    }

    private void UpdateFocusVizLabel()
    {
        FocusVizModeText.Text = FocusVisualizer.Mode switch
        {
            VisualizerMode.Waveform => "Silk Waveform",
            VisualizerMode.BarsAndWaveform => "Bars + Wave",
            _ => "Spectrum"
        };
    }

    private void StartFolderWatcher(string folder)
    {
        try
        {
            if (_folderWatcher != null)
            {
                if (string.Equals(_folderWatcher.Path.TrimEnd(Path.DirectorySeparatorChar), folder.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    return;
                _folderWatcher.EnableRaisingEvents = false;
                _folderWatcher.Dispose();
                _folderWatcher = null;
            }
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
            };
            watcher.Created += OnWatchedFolderChanged;
            watcher.Deleted += OnWatchedFolderChanged;
            watcher.Renamed += OnWatchedFolderRenamed;
            watcher.EnableRaisingEvents = true;
            _folderWatcher = watcher;
        }
        catch { }
    }

    private void OnWatchedFolderChanged(object sender, FileSystemEventArgs e)
    {
        if (DateTime.UtcNow < _suppressWatcherUntilUtc) return;
        if (!MusicLibrary.IsSupportedExtension(e.FullPath) && e.ChangeType != WatcherChangeTypes.Deleted)
        {
            try
            {
                if (!Directory.Exists(e.FullPath)) return;
            }
            catch { return; }
        }
        ScheduleWatcherRescan();
    }

    private void OnWatchedFolderRenamed(object sender, RenamedEventArgs e)
    {
        if (DateTime.UtcNow < _suppressWatcherUntilUtc) return;
        if (MusicLibrary.IsSupportedExtension(e.FullPath) || MusicLibrary.IsSupportedExtension(e.OldFullPath) || Directory.Exists(e.FullPath))
            ScheduleWatcherRescan();
    }

    private void ScheduleWatcherRescan()
    {
        var prev = _watcherDebounceCts;
        var next = new CancellationTokenSource();
        _watcherDebounceCts = next;
        prev?.Cancel();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1200, next.Token);
                if (next.IsCancellationRequested) return;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!next.IsCancellationRequested && !string.IsNullOrWhiteSpace(_settings.MusicFolder) && Directory.Exists(_settings.MusicFolder))
                        _ = ScanFolderAsync(_settings.MusicFolder, isStartupDelta: true);
                });
            }
            catch (TaskCanceledException) { }
        });
    }

    private async Task ScanFolderAsync(string folder, bool isStartupDelta = false)
    {
        StartFolderWatcher(folder);
        _scanCts?.Cancel();
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        int myScan = Interlocked.Increment(ref _scanId);

        if (!isStartupDelta || _allTracks.Count == 0)
        {
            _allTracks.Clear();
            _visibleTracks.Clear();
            EmptyStatePanel.Visibility = Visibility.Collapsed;
        }

        var cacheMap = await Task.Run(() => LibraryCache.Load(AppPaths.LibraryCachePath));
        var tracks = await LibraryScanner.ScanFolderAsync(folder, cts.Token, batch =>
        {
            if (isStartupDelta && _allTracks.Count > 0) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (cts.IsCancellationRequested || myScan != _scanId) return;
                _allTracks.AddRange(batch);
                if (_activePlaylist == null && _activeFacet == "all" && string.IsNullOrWhiteSpace(SearchBox.Text))
                {
                    foreach (var t in batch) _visibleTracks.Add(t);
                }
            });
        }, cacheMap);

        if (cts.IsCancellationRequested || myScan != _scanId) return;

        // Reconcile existing TrackItem instances by Path so selection, queue, and IsNowPlaying identity stay unified
        var existingByPath = new Dictionary<string, TrackItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var prev in _allTracks)
            existingByPath[prev.Path] = prev;

        bool sameOrder = isStartupDelta && existingByPath.Count == tracks.Count && _allTracks.Count == tracks.Count;
        var reconciled = new List<TrackItem>(tracks.Count);
        for (int i = 0; i < tracks.Count; i++)
        {
            var scanned = tracks[i];
            if (existingByPath.TryGetValue(scanned.Path, out var existing))
            {
                existing.CopyMetadataFrom(scanned);
                reconciled.Add(existing);
                if (sameOrder && !ReferenceEquals(_allTracks[i], existing))
                    sameOrder = false;
            }
            else
            {
                reconciled.Add(scanned);
                sameOrder = false;
            }
        }

        _allTracks.Clear();
        _allTracks.AddRange(reconciled);
        SetNowPlayingTrack(_engine.CurrentPath);

        if (sameOrder && _visibleTracks.Count > 0)
        {
            UpdateSidebarCounts();
            UpdateStatusFooter();
        }
        else
        {
            RefreshVisibleTracks();
        }

        RestoreSessionQueue();
        UpdateEmptyState();
        _ = Task.Run(() => LibraryCache.Save(AppPaths.LibraryCachePath, reconciled));
    }

    private string CurrentScopeLabel() => _activeAlbumFilter != null ? $"Album: {_activeAlbumFilter}"
        : _activeArtistFilter != null ? $"Artist: {_activeArtistFilter}"
        : _activeHygiene != null ? HygieneLabel(_activeHygiene)
        : _activePlaylist?.Name
        ?? _activeCustomFilter?.Name
        ?? _activeFacet switch
        {
            "recent" => "Recently added",
            "toprated" => "Top rated",
            "5star" => "5★ Favorites",
            "unrated" => "Unrated",
            _ => "All music"
        };

    private void RefreshVisibleTracks(bool scrollToSelection = false)
    {
        string query = SearchBox.Text?.Trim() ?? string.Empty;
        IEnumerable<TrackItem> source = _allTracks;

        if (_activePlaylist != null)
        {
            var byPath = _allTracks.ToDictionary(t => t.Path, StringComparer.OrdinalIgnoreCase);
            var plTracks = new List<TrackItem>();
            foreach (string p in _activePlaylist.TrackPaths)
            {
                if (byPath.TryGetValue(p, out var item)) plTracks.Add(item);
            }
            source = plTracks;
        }
        else if (_activeHygiene != null)
        {
            source = _activeHygiene switch
            {
                "missing_art" => source.Where(t => !t.HasEmbeddedArt && !t.IsCloudOnly),
                "missing_tags" => source.Where(t => string.IsNullOrWhiteSpace(t.Artist) || string.IsNullOrWhiteSpace(t.Album)),
                "dirty_names" => source.Where(t => t.HasDirtyFilename),
                "low_bitrate" => source.Where(t => t.Bitrate > 0 && t.Bitrate < 128),
                "cloud_only" => source.Where(t => t.IsCloudOnly),
                "duplicates" => FindDuplicateTracks(source),
                _ => source
            };
        }
        else if (_activeCustomFilter != null)
        {
            if (_activeCustomFilter.MinRating is double minR && minR > 0)
                source = source.Where(t => t.Rating is double r && r >= minR);
            if (!string.IsNullOrWhiteSpace(_activeCustomFilter.TextContains))
            {
                string txt = _activeCustomFilter.TextContains.Trim();
                source = source.Where(t =>
                    t.DisplayTitle.Contains(txt, StringComparison.OrdinalIgnoreCase) ||
                    t.Artist.Contains(txt, StringComparison.OrdinalIgnoreCase) ||
                    t.Album.Contains(txt, StringComparison.OrdinalIgnoreCase) ||
                    t.Genre.Contains(txt, StringComparison.OrdinalIgnoreCase));
            }
        }
        else if (_activeFacet == "toprated")
        {
            source = source.Where(t => t.Rating is double r && r >= 4.0);
        }
        else if (_activeFacet == "5star")
        {
            source = source.Where(t => t.Rating is double r && r >= 4.9);
        }
        else if (_activeFacet == "unrated")
        {
            source = source.Where(t => t.Rating is null or <= 0);
        }
        else if (_activeFacet == "recent")
        {
            source = source.OrderByDescending(t => t.DateAddedUtc).Take(200);
        }

        if (!string.IsNullOrEmpty(_activeArtistFilter))
        {
            source = source.Where(t => string.Equals(t.Artist, _activeArtistFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(_activeAlbumFilter))
        {
            source = source.Where(t => string.Equals(t.Album, _activeAlbumFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(query))
        {
            source = source.Where(t =>
                t.DisplayTitle.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                t.Artist.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                t.Album.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                t.Genre.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                t.Path.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        if (_activeFacet != "recent")
        {
            source = ApplySort(source, _settings.SortColumn, _settings.SortAscending);
        }

        var previousSelection = GetPrimarySelectedTrack();
        var filtered = source.ToList();

        string? playingPath = _engine.CurrentPath;
        bool hasPlaying = !string.IsNullOrEmpty(playingPath);
        foreach (var t in filtered)
            t.IsNowPlaying = hasPlaying && string.Equals(t.Path, playingPath, StringComparison.OrdinalIgnoreCase);

        if (!_visibleTracks.SequenceEqual(filtered))
        {
            _suppressSelectionChanged = true;
            try
            {
                _visibleTracks = new ObservableCollection<TrackItem>(filtered);
                TracksListView.ItemsSource = _visibleTracks;
                AlbumsGridView.ItemsSource = _visibleTracks;
                if (previousSelection != null && _visibleTracks.Contains(previousSelection))
                {
                    TracksListView.SelectedItem = previousSelection;
                    AlbumsGridView.SelectedItem = previousSelection;
                }
            }
            finally
            {
                _suppressSelectionChanged = false;
            }
        }
        else if (previousSelection != null && _visibleTracks.Contains(previousSelection))
        {
            SyncSelectionAcrossViews(previousSelection);
        }

        var scrollTarget = (previousSelection != null && _visibleTracks.Contains(previousSelection))
            ? previousSelection
            : _visibleTracks.FirstOrDefault(t => t.IsNowPlaying);
        if (scrollToSelection && scrollTarget != null)
        {
            ScrollActiveViewToTrack(scrollTarget);
        }

        bool hasScopeFilter = _activeAlbumFilter != null || _activeArtistFilter != null || _activeHygiene != null ||
                              _activePlaylist != null || _activeCustomFilter != null || _activeFacet != "all";

        if (hasScopeFilter)
            ScopeClearText.Text = CurrentScopeLabel();
        ScopeClearBtn.Visibility = hasScopeFilter ? Visibility.Visible : Visibility.Collapsed;
        UpdateSidebarCounts();
        UpdateStatusFooter();
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        if (_allTracks.Count == 0)
        {
            EmptyStateTitle.Text = "Choose your music folder to begin";
            EmptyStateSubtitle.Text = "Supports MP3, FLAC, M4A, WAV, WMA, OGG with OneDrive cloud-safe scanning.";
            EmptyStateActionBtn.Content = "Open music folder";
            EmptyStatePanel.Visibility = Visibility.Visible;
        }
        else if (_visibleTracks.Count == 0)
        {
            if (_activePlaylist != null)
            {
                EmptyStateTitle.Text = $"Playlist \"{_activePlaylist.Name}\" is empty";
                EmptyStateSubtitle.Text = "Right-click tracks in All music → Add to playlist, or drag tracks onto this playlist.";
            }
            else
            {
                EmptyStateTitle.Text = "No matching tracks";
                EmptyStateSubtitle.Text = "Clear your search or active filter to view all tracks.";
            }
            EmptyStateActionBtn.Content = "Back to All music";
            EmptyStatePanel.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyStatePanel.Visibility = Visibility.Collapsed;
        }
    }

    private void EmptyStateActionBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_allTracks.Count == 0)
            OpenFolderBtn_Click(sender, e);
        else
            ScopeClearBtn_Click(sender, e);
    }

    private static IEnumerable<TrackItem> FindDuplicateTracks(IEnumerable<TrackItem> tracks)
    {
        return tracks
            .GroupBy(t => $"{t.Artist.Trim().ToLowerInvariant()}|{t.DisplayTitle.Trim().ToLowerInvariant()}")
            .Where(g => g.Count() > 1)
            .SelectMany(g => g);
    }

    private static string HygieneLabel(string key) => key switch
    {
        "missing_art" => "Missing cover art",
        "missing_tags" => "Missing artist/album",
        "dirty_names" => "Dirty filenames",
        "duplicates" => "Potential duplicates",
        "low_bitrate" => "Low bitrate",
        "cloud_only" => "Cloud-only files",
        _ => "Library hygiene"
    };

    private async void AlbumsGridView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not TrackItem track) return;
        if (track.CoverThumbnail != null || track.ArtLoadAttempted) return;
        track.ArtLoadAttempted = true;
        if (!track.HasEmbeddedArt || track.IsCloudOnly) return;

        string cacheKey = !string.IsNullOrWhiteSpace(track.Album)
            ? $"{track.Album.Trim()}|{Path.GetDirectoryName(track.Path)}"
            : track.Path;

        if (_artThumbCache.TryGetValue(cacheKey, out var cachedImg))
        {
            track.CoverThumbnail = cachedImg;
            return;
        }

        string path = track.Path;
        byte[]? bytes = await Task.Run(() => MusicLibrary.ReadEmbeddedArtBytes(path));
        if (bytes == null || bytes.Length == 0)
        {
            _artThumbCache.TryAdd(cacheKey, null);
            return;
        }

        var bmp = await CreateBitmapFromBytesAsync(bytes, decodePixelWidth: 180);
        if (bmp != null)
        {
            _artThumbCache[cacheKey] = bmp;
            track.CoverThumbnail = bmp;
        }
    }

    private void TilePlayBtn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TrackItem track)
        {
            SyncSelectionAcrossViews(track);
            UpdateInspectorForSelection(track);
            PlayFromList(_visibleTracks.ToList(), track);
        }
    }

    private void UpdateSidebarCounts()
    {
        int total = _allTracks.Count;
        FacetAllText.Text = total > 0 ? $"All music ({total:N0})" : "All music";
        PlaylistsEmptyHint.Visibility = _playlists.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateStatusFooter() { }

    private static string FormatLongDuration(TimeSpan t)
    {
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}h {t.Minutes:D2}m";
        return $"{t.Minutes}m {t.Seconds:D2}s";
    }

    private static IEnumerable<TrackItem> ApplySort(IEnumerable<TrackItem> source, string col, bool asc)
    {
        return (col, asc) switch
        {
            ("Title", true) => source.OrderBy(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Index),
            ("Title", false) => source.OrderByDescending(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Index),
            ("Artist", true) => source.OrderBy(t => string.IsNullOrWhiteSpace(t.Artist) ? 1 : 0).ThenBy(t => t.Artist.Trim(), StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Album.Trim(), StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Index),
            ("Artist", false) => source.OrderByDescending(t => t.Artist.Trim(), StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Album.Trim(), StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Index),
            ("Album", true) => source.OrderBy(t => string.IsNullOrWhiteSpace(t.Album) ? 1 : 0).ThenBy(t => t.Album.Trim(), StringComparer.OrdinalIgnoreCase).ThenBy(t => t.TrackNumber > 0 ? t.TrackNumber : int.MaxValue).ThenBy(t => t.Index),
            ("Album", false) => source.OrderByDescending(t => t.Album.Trim(), StringComparer.OrdinalIgnoreCase).ThenBy(t => t.TrackNumber > 0 ? t.TrackNumber : int.MaxValue).ThenBy(t => t.Index),
            ("Genre", true) => source.OrderBy(t => string.IsNullOrWhiteSpace(t.Genre) ? 1 : 0).ThenBy(t => t.Genre.Trim(), StringComparer.OrdinalIgnoreCase).ThenBy(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Index),
            ("Genre", false) => source.OrderByDescending(t => t.Genre.Trim(), StringComparer.OrdinalIgnoreCase).ThenBy(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Index),
            ("Year", true) => source.OrderBy(t => t.Year > 0 ? 0 : 1).ThenBy(t => t.Year).ThenBy(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Index),
            ("Year", false) => source.OrderByDescending(t => t.Year).ThenBy(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Index),
            ("Rating", true) => source.OrderBy(t => t.Rating ?? 0).ThenBy(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Index),
            ("Rating", false) => source.OrderByDescending(t => t.Rating ?? 0).ThenBy(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Index),
            ("Duration", true) => source.OrderBy(t => t.Duration).ThenBy(t => t.Index),
            ("Duration", false) => source.OrderByDescending(t => t.Duration).ThenBy(t => t.Index),
            ("DateAdded", true) => source.OrderBy(t => t.DateAddedUtc).ThenBy(t => t.Index),
            ("DateAdded", false) => source.OrderByDescending(t => t.DateAddedUtc).ThenBy(t => t.Index),
            ("Bitrate", true) => source.OrderBy(t => t.Bitrate).ThenBy(t => t.Index),
            ("Bitrate", false) => source.OrderByDescending(t => t.Bitrate).ThenBy(t => t.Index),
            (_, false) => source.OrderByDescending(t => t.Index),
            _ => source.OrderBy(t => t.Index)
        };
    }

    private void SortHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string col) return;
        if (string.Equals(_settings.SortColumn, col, StringComparison.Ordinal))
        {
            _settings.SortAscending = !_settings.SortAscending;
        }
        else
        {
            _settings.SortColumn = col;
            _settings.SortAscending = col is not ("Rating" or "Year" or "Bitrate" or "DateAdded");
        }
        UpdateSortHeaders();
        RefreshVisibleTracks(scrollToSelection: true);

        if (_queueTracks.Count > 1 && !_settings.Shuffle &&
            _queueIndex >= 0 && _queueIndex < _queueTracks.Count &&
            _visibleTracks.Contains(_queueTracks[_queueIndex]))
        {
            var playing = _queueTracks[_queueIndex];
            _unshuffledQueue.Clear();
            _unshuffledQueue.AddRange(_visibleTracks);
            _queueTracks.Clear();
            foreach (var t in _visibleTracks) _queueTracks.Add(t);
            _queueIndex = _queueTracks.IndexOf(playing);
            UpdateQueueHeader();
            if (_queueIndex >= 0)
            {
                QueueListView.SelectedIndex = _queueIndex;
                QueueListView.ScrollIntoView(playing);
            }
        }

        _ = Task.Run(() => _settings.Save());
    }

    private void TableHeaderGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        foreach (string col in new[] { "Genre", "Year", "Rating", "Length", "DateAdded", "Bitrate" })
        {
            string label = col == "DateAdded" ? "Date Added" : col;
            var toggle = new ToggleMenuFlyoutItem
            {
                Text = label,
                IsChecked = IsColVisible(col)
            };
            toggle.Click += (_, _) =>
            {
                _columnVisible[col] = toggle.IsChecked;
                _settings.ColumnVisibility[col] = toggle.IsChecked;
                _settings.Save();
                ApplyColumnWidths();
                TracksListView.ItemsSource = null;
                TracksListView.ItemsSource = _visibleTracks;
            };
            menu.Items.Add(toggle);
        }
        menu.ShowAt(TableHeaderGrid, e.GetPosition(TableHeaderGrid));
    }

    private void UpdateSortHeaders()
    {
        foreach (var child in TableHeaderGrid.Children.OfType<Button>())
        {
            string tag = child.Tag?.ToString() ?? string.Empty;
            string label = tag switch
            {
                "Index" => "#",
                "Duration" => "Length",
                "DateAdded" => "Date Added",
                _ => tag
            };
            child.Content = string.Equals(tag, _settings.SortColumn, StringComparison.Ordinal)
                ? $"{label} {(_settings.SortAscending ? "\u25B2" : "\u25BC")}"
                : label;
        }
    }

    private async void OpenFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        await SwitchToFolderAsync(folder.Path);
    }

    private async void RescanBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_settings.MusicFolder))
            await ScanFolderAsync(_settings.MusicFolder);
    }

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initialized) return;
        ClearSearchBtn.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Collapsed : Visibility.Visible;

        _searchDebounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchDebounceCts = cts;
        try
        {
            await Task.Delay(100, cts.Token);
            if (!cts.IsCancellationRequested) RefreshVisibleTracks();
        }
        catch (TaskCanceledException) { }
    }

    private void ClearSearchBtn_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        RefreshVisibleTracks();
    }

    private void AllMusicBtn_Click(object sender, RoutedEventArgs e) => ScopeClearBtn_Click(sender, e);

    private void ScopeClearBtn_Click(object sender, RoutedEventArgs e)
    {
        _activeAlbumFilter = null;
        _activeArtistFilter = null;
        _activeHygiene = null;
        _activePlaylist = null;
        _activeCustomFilter = null;
        _activeFacet = "all";
        PlaylistsList.SelectedIndex = -1;
        if (!string.IsNullOrEmpty(SearchBox.Text))
            SearchBox.Text = string.Empty;
        RefreshVisibleTracks();
    }

    private void ViewToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetPrimarySelectedTrack();
        bool currentlyGrid = IsTileMode;
        _settings.ViewMode = currentlyGrid ? "Table" : "Grid";
        ApplyViewModeVisuals();
        if (selected != null && _visibleTracks.Contains(selected))
        {
            SyncSelectionAcrossViews(selected);
            ScrollActiveViewToTrack(selected);
        }
        _ = Task.Run(() => _settings.Save());
    }

    private void HygienePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not string key) return;
        PlaylistsList.SelectedIndex = -1;
        _activePlaylist = null;
        _activeCustomFilter = null;
        _activeArtistFilter = null;
        _activeAlbumFilter = null;
        _activeHygiene = key;
        RefreshVisibleTracks();
    }

    private void PlaylistsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || PlaylistsList.SelectedItem is not SavedPlaylist pl) return;
        _activeCustomFilter = null;
        _activeHygiene = null;
        _activeArtistFilter = null;
        _activeAlbumFilter = null;
        _activePlaylist = pl;
        RefreshVisibleTracks();
    }

    private void PlaylistsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
        if (PlaylistsList.SelectedItem is SavedPlaylist pl)
            PlayPlaylistTracks(pl);
    }

    private void PlaylistRowPlayBtn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SavedPlaylist pl)
            PlayPlaylistTracks(pl);
    }

    private void PlaylistRowMenuBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is SavedPlaylist pl)
            ShowPlaylistContextMenu(pl, fe, null);
    }

    private void PlayPlaylistTracks(SavedPlaylist pl)
    {
        var byPath = _allTracks.ToDictionary(t => t.Path, StringComparer.OrdinalIgnoreCase);
        var tracks = new List<TrackItem>();
        foreach (string p in pl.TrackPaths)
        {
            if (byPath.TryGetValue(p, out var item)) tracks.Add(item);
        }
        if (tracks.Count == 0)
        {
            PlaylistsList.SelectedItem = pl;
            ShowHudPill($"\"{pl.Name}\" has no tracks yet");
            return;
        }
        PlaylistsList.SelectedItem = pl;
        PlayFromList(tracks, tracks[0]);
    }

    private void PlaylistsList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        var lvi = FindAncestor<ListViewItem>(src);
        var pl = (src as FrameworkElement)?.DataContext as SavedPlaylist
            ?? (src as FrameworkElement)?.Tag as SavedPlaylist
            ?? lvi?.Content as SavedPlaylist
            ?? (lvi != null ? PlaylistsList.ItemFromContainer(lvi) as SavedPlaylist : null);
        if (pl == null) return;
        PlaylistsList.SelectedItem = pl;
        ShowPlaylistContextMenu(pl, PlaylistsList, e.GetPosition(PlaylistsList));
    }

    private void ShowPlaylistContextMenu(SavedPlaylist pl, FrameworkElement target, Point? pos)
    {
        var menu = new MenuFlyout();

        var playPlItem = new MenuFlyoutItem { Text = "Play playlist" };
        playPlItem.Click += (_, _) => PlayPlaylistTracks(pl);
        menu.Items.Add(playPlItem);

        var selected = GetSelectedTracks();
        if (selected.Count > 0 && _activePlaylist != pl)
        {
            var addSelItem = new MenuFlyoutItem { Text = $"Add {selected.Count} selected track(s)" };
            addSelItem.Click += (_, _) =>
            {
                int added = AddTracksToPlaylist(pl, selected);
                ShowHudPill(added > 0 ? $"Added {added} track(s) to \"{pl.Name}\"" : $"Already in \"{pl.Name}\"");
            };
            menu.Items.Add(addSelItem);
        }

        menu.Items.Add(new MenuFlyoutSeparator());

        var renameItem = new MenuFlyoutItem { Text = "Rename playlist..." };
        renameItem.Click += async (_, _) =>
        {
            var box = new TextBox { Text = pl.Name, PlaceholderText = "Playlist name" };
            var dlg = new ContentDialog
            {
                Title = "Rename playlist",
                Content = box,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = RootGrid.RequestedTheme
            };
            DialogFooter.MainButtonOnRight(dlg);
            if (await dlg.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text))
            {
                pl.Name = box.Text.Trim();
                _settings.Save();
                if (_activePlaylist == pl) RefreshVisibleTracks();
            }
        };
        menu.Items.Add(renameItem);

        var exportItem = new MenuFlyoutItem { Text = "Export M3U8..." };
        exportItem.Click += async (_, _) => await ExportPlaylistM3u8Async(pl);
        menu.Items.Add(exportItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var deleteItem = new MenuFlyoutItem { Text = $"Delete \"{pl.Name}\"" };
        deleteItem.Click += (_, _) =>
        {
            int oldIdx = _playlists.IndexOf(pl);
            _playlists.Remove(pl);
            _settings.Playlists.Remove(pl);
            _settings.Save();
            PlaylistsEmptyHint.Visibility = _playlists.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_activePlaylist == pl)
            {
                _activePlaylist = null;
                PlaylistsList.SelectedIndex = -1;
                RefreshVisibleTracks();
            }
            PushUndo(() =>
            {
                int insertIdx = Math.Clamp(oldIdx, 0, _playlists.Count);
                _playlists.Insert(insertIdx, pl);
                _settings.Playlists.Insert(insertIdx, pl);
                _settings.Save();
                PlaylistsEmptyHint.Visibility = Visibility.Collapsed;
                ShowHudPill($"Restored playlist \"{pl.Name}\"");
                return Task.CompletedTask;
            });
            ShowHudPill($"Deleted \"{pl.Name}\" (Ctrl+Z to undo)");
        };
        menu.Items.Add(deleteItem);

        if (pos.HasValue)
            menu.ShowAt(target, pos.Value);
        else
            menu.ShowAt(target);
    }

    private void LeftPaneToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        _leftPaneOpen = !_leftPaneOpen;
        _settings.LeftPaneOpen = _leftPaneOpen;
        ApplyPaneWidths();
        _settings.Save();
    }

    private void RightPaneToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        _rightPaneOpen = !_rightPaneOpen;
        _settings.RightPaneOpen = _rightPaneOpen;
        ApplyPaneWidths();
        _settings.Save();
    }

    private void PlayAllBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_visibleTracks.Count == 0) return;
        var startTrack = GetPrimarySelectedTrack() ?? _visibleTracks[0];
        PlayFromList(_visibleTracks.ToList(), startTrack);
    }

    private void TracksListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (IsInsideType<StarRatingCell>(e.OriginalSource as DependencyObject) ||
            IsInsideButton(e.OriginalSource as DependencyObject))
            return;
        if (GetPrimarySelectedTrack() is TrackItem track)
            PlayFromList(_visibleTracks.ToList(), track);
    }

    private void TracksListView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var dragged = e.Items.OfType<TrackItem>().ToList();
        if (dragged.Count == 0)
            dragged = GetSelectedTracks();
        _draggedTracks = dragged;
        e.Data.SetText("ZenMusicTracks");
        e.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    private SavedPlaylist? HitTestPlaylistRow(DragEventArgs e)
    {
        for (int i = 0; i < _playlists.Count; i++)
        {
            if (PlaylistsList.ContainerFromIndex(i) is not UIElement container) continue;
            try
            {
                var pt = e.GetPosition(container);
                if (pt.X >= 0 && pt.Y >= 0 && pt.X <= container.ActualSize.X && pt.Y <= container.ActualSize.Y)
                    return _playlists[i];
            }
            catch { }
        }
        return null;
    }

    private void PlaylistsList_DragOver(object sender, DragEventArgs e)
    {
        if (_draggedTracks is not { Count: > 0 }) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        var target = HitTestPlaylistRow(e);
        e.DragUIOverride.Caption = target != null
            ? $"Add {_draggedTracks.Count} track(s) to \"{target.Name}\""
            : $"Create playlist from {_draggedTracks.Count} track(s)";
        e.DragUIOverride.IsCaptionVisible = true;
    }

    private async void PlaylistsList_Drop(object sender, DragEventArgs e)
    {
        var tracks = _draggedTracks;
        _draggedTracks = null;
        if (tracks is not { Count: > 0 }) return;

        var targetPl = HitTestPlaylistRow(e);
        if (targetPl != null)
        {
            int added = AddTracksToPlaylist(targetPl, tracks);
            ShowHudPill(added > 0 ? $"Added {added} track(s) to \"{targetPl.Name}\"" : $"Already in \"{targetPl.Name}\"");
            return;
        }
        await CreatePlaylistAsync(tracks);
    }

    private void QueueListView_DragOver(object sender, DragEventArgs e)
    {
        if (_draggedTracks is { Count: > 0 })
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = $"Enqueue {_draggedTracks.Count} track(s)";
        }
    }

    private void QueueListView_Drop(object sender, DragEventArgs e)
    {
        if (_draggedTracks is not { Count: > 0 }) return;
        foreach (var t in _draggedTracks)
            _queueTracks.Add(t);
        UpdateQueueHeader();
        ShowHudPill($"Added {_draggedTracks.Count} track(s) to queue");
        _draggedTracks = null;
    }

    private void QueueListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        for (int i = 0; i < _queueTracks.Count; i++)
        {
            if (_queueTracks[i].IsNowPlaying)
            {
                _queueIndex = i;
                break;
            }
        }
        UpdateQueueHeader();
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Open folder or play audio files";
        }
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        if (items.Count == 0) return;

        var firstFolder = items.OfType<StorageFolder>().FirstOrDefault();
        if (firstFolder != null)
        {
            await SwitchToFolderAsync(firstFolder.Path);
            return;
        }

        var audioFiles = items.OfType<StorageFile>()
            .Select(f => f.Path)
            .Where(MusicLibrary.IsSupportedExtension)
            .ToList();
        if (audioFiles.Count == 0) return;

        var droppedTracks = await Task.Run(() =>
            audioFiles.Select((p, i) => MusicLibrary.ReadTrackMetadata(p, _allTracks.Count + i + 1, false)).ToList());
        var existingPaths = new HashSet<string>(_allTracks.Select(t => t.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var t in droppedTracks)
        {
            if (existingPaths.Add(t.Path))
                _allTracks.Add(t);
        }
        RefreshVisibleTracks();
        EmptyStatePanel.Visibility = Visibility.Collapsed;
        PlayFromList(droppedTracks, droppedTracks[0]);
    }

    private void RowRatingCell_RatingSelected(object? sender, double? newRating)
    {
        if (sender is not FrameworkElement el || el.DataContext is not TrackItem track) return;
        var targets = TracksListView.SelectedItems.Contains(track) && TracksListView.SelectedItems.Count > 1
            ? TracksListView.SelectedItems.OfType<TrackItem>().ToList()
            : new List<TrackItem> { track };

        if (!TracksListView.SelectedItems.Contains(track))
            TracksListView.SelectedItem = track;

        ApplyRatingToTargets(targets, newRating, pushUndo: true);
    }

    private void ApplyRatingToTargets(IReadOnlyList<TrackItem> targets, double? newRating, bool pushUndo)
    {
        if (targets.Count == 0) return;
        var previous = targets.Select(t => (Track: t, OldRating: t.Rating)).ToList();

        foreach (var t in targets) t.Rating = newRating;
        if (GetPrimarySelectedTrack() is TrackItem sel && targets.Contains(sel))
            UpdateSelectedRatingVisuals(newRating);
        SyncPlayingTrackRating(targets, newRating);
        UpdateSidebarCounts();

        if (pushUndo)
        {
            PushUndo(() =>
            {
                foreach (var (tr, oldR) in previous) tr.Rating = oldR;
                if (GetPrimarySelectedTrack() is TrackItem s && previous.Any(x => x.Track == s))
                    UpdateSelectedRatingVisuals(s.Rating);
                if (_queueIndex >= 0 && _queueIndex < _queueTracks.Count)
                {
                    var p = _queueTracks[_queueIndex];
                    NowPlayingRatingCell.Value = p.Rating;
                    FocusRatingCell.Value = p.Rating;
                }
                UpdateSidebarCounts();
                _ = Task.Run(() =>
                {
                    foreach (var (tr, oldR) in previous) MusicLibrary.TryWriteStars(tr.Path, oldR);
                });
                ShowHudPill("Undone: Star rating");
                return Task.CompletedTask;
            });
        }

        _ = Task.Run(() =>
        {
            foreach (var t in targets) MusicLibrary.TryWriteStars(t.Path, newRating);
        });
    }

    private void SyncPlayingTrackRating(IEnumerable<TrackItem> updatedTracks, double? newRating)
    {
        if (_queueIndex < 0 || _queueIndex >= _queueTracks.Count) return;
        var playing = _queueTracks[_queueIndex];
        if (updatedTracks.Any(t => string.Equals(t.Path, playing.Path, StringComparison.OrdinalIgnoreCase)))
        {
            playing.Rating = newRating;
            NowPlayingRatingCell.Value = newRating;
            FocusRatingCell.Value = newRating;
        }
    }

    private static bool IsInsideType<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source != null)
        {
            if (source is T) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source != null)
        {
            if (source is T match) return match;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private void QueueListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (QueueListView.SelectedIndex >= 0 && QueueListView.SelectedIndex < _queueTracks.Count)
        {
            var track = _queueTracks[QueueListView.SelectedIndex];
            PlayQueueIndex(QueueListView.SelectedIndex);
            RevealTrackInMainList(track);
        }
    }

    private void QueueListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var hit = (e.OriginalSource as FrameworkElement)?.DataContext as TrackItem;
        if (hit != null) QueueListView.SelectedItem = hit;

        if (_queueTracks.Count == 0) return;
        var menu = new MenuFlyout();

        if (hit != null)
        {
            int idx = _queueTracks.IndexOf(hit);
            var playHere = new MenuFlyoutItem { Text = "Play from here" };
            playHere.Click += (_, _) =>
            {
                if (idx >= 0)
                {
                    PlayQueueIndex(idx);
                    RevealTrackInMainList(hit);
                }
            };
            menu.Items.Add(playHere);

            var showInLib = new MenuFlyoutItem { Text = "Show in library" };
            showInLib.Click += (_, _) => RevealTrackInMainList(hit);
            menu.Items.Add(showInLib);

            var removeOne = new MenuFlyoutItem { Text = "Remove from queue" };
            removeOne.Click += (_, _) =>
            {
                if (idx < 0) return;
                _queueTracks.RemoveAt(idx);
                if (idx < _queueIndex) _queueIndex--;
                else if (idx == _queueIndex) _queueIndex = Math.Min(_queueIndex, _queueTracks.Count - 1);
                UpdateQueueHeader();
            };
            menu.Items.Add(removeOne);
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        var clearQueue = new MenuFlyoutItem { Text = "Clear queue" };
        clearQueue.Click += (_, _) =>
        {
            _queueTracks.Clear();
            _unshuffledQueue.Clear();
            _queueIndex = -1;
            SetNowPlayingTrack(null);
            UpdateQueueHeader();
        };
        menu.Items.Add(clearQueue);

        menu.ShowAt(QueueListView, e.GetPosition(QueueListView));
    }

    private void UpdateQueueHeader()
    {
        if (_queueTracks.Count == 0)
        {
            QueueHeaderText.Text = "Now playing (0)";
            return;
        }
        var total = TimeSpan.FromSeconds(_queueTracks.Sum(t => t.Duration.TotalSeconds));
        int pos = _queueIndex >= 0 && _queueIndex < _queueTracks.Count ? _queueIndex + 1 : 0;
        QueueHeaderText.Text = $"Now playing ({pos} / {_queueTracks.Count:N0} · {FormatLongDuration(total)})";
    }

    private void RightPaneSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isDraggingSplitter = true;
        _splitterStartY = e.GetCurrentPoint(RightSidebar).Position.Y;
        _splitterStartHeight = SelectedInspectorScroll.MaxHeight;
        RightPaneSplitter.CapturePointer(e.Pointer);
    }

    private void RightPaneSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingSplitter) return;
        double currentY = e.GetCurrentPoint(RightSidebar).Position.Y;
        double delta = _splitterStartY - currentY;
        SelectedInspectorScroll.MaxHeight = Math.Clamp(_splitterStartHeight + delta, 130, 460);
    }

    private void RightPaneSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingSplitter) return;
        _isDraggingSplitter = false;
        RightPaneSplitter.ReleasePointerCapture(e.Pointer);
        _settings.InspectorHeight = SelectedInspectorScroll.MaxHeight;
        _settings.Save();
    }

    private void PlayFromList(IReadOnlyList<TrackItem> list, TrackItem startTrack)
    {
        if (list.Count == 0) return;
        _unshuffledQueue.Clear();
        _unshuffledQueue.AddRange(list);

        _queueTracks.Clear();
        if (_settings.Shuffle && list.Count > 1)
        {
            _queueTracks.Add(startTrack);
            var rest = list.Where(t => !ReferenceEquals(t, startTrack)).ToList();
            ShuffleInPlace(rest);
            foreach (var t in rest) _queueTracks.Add(t);
            PlayQueueIndex(0);
        }
        else
        {
            foreach (var t in list) _queueTracks.Add(t);
            int idx = _queueTracks.IndexOf(startTrack);
            PlayQueueIndex(idx >= 0 ? idx : 0);
        }
    }

    private static void ShuffleInPlace<T>(IList<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private void PlayQueueIndex(int index)
    {
        if (index < 0 || index >= _queueTracks.Count) return;

        _queueIndex = index;
        var track = _queueTracks[index];
        SetNowPlayingTrack(track.Path);
        track.PlayCount++;
        track.LastPlayedUtc = DateTime.UtcNow;
        _activePlayingPathForLyrics = track.Path;

        UpdateQueueHeader();
        QueueListView.SelectedIndex = index;
        QueueListView.ScrollIntoView(track);

        NowPlayingTitleText.Text = track.DisplayTitle;
        NowPlayingArtistText.Text = string.IsNullOrWhiteSpace(track.Artist) ? track.FileName : track.Artist;
        NowPlayingRatingCell.Value = track.Rating;
        FocusTitleText.Text = track.DisplayTitle;
        FocusArtistText.Text = NowPlayingArtistText.Text;
        FocusRatingCell.Value = track.Rating;

        _ = LoadPlayingArtAndSmtcAsync(track);
        _ = LoadFocusLyricsAsync(track.Path);
        _engine.PlayFile(track.Path);
    }

    private async Task LoadPlayingArtAndSmtcAsync(TrackItem track)
    {
        byte[]? bytes = await Task.Run(() => MusicLibrary.ReadEmbeddedArtBytes(track.Path));
        if (!string.Equals(_activePlayingPathForLyrics, track.Path, StringComparison.OrdinalIgnoreCase)) return;

        _engine.UpdateSmtcMetadata(track.DisplayTitle, track.Artist, track.Album, bytes);
        await SetImageFromBytesAsync(bytes, NowPlayingArtImage, FocusArtImage);

        Color glowColor = await ComputeDominantAccentColorAsync(bytes);
        FocusAmbientStop.Color = glowColor;
    }

    private async Task LoadFocusLyricsAsync(string path)
    {
        string lyrics = await Task.Run(() => MusicLibrary.ReadLyrics(path));
        if (!string.Equals(_activePlayingPathForLyrics, path, StringComparison.OrdinalIgnoreCase)) return;
        ApplyLyricsToView(lyrics, FocusLyricsHost, FocusLyricsText, FocusPlainLyricsScroll, FocusSyncedLyricsList, _focusLrcLines, forceDark: true);
        _activeFocusLrcIndex = -1;
    }

    private void ApplyLyricsToView(
        string? rawLyrics,
        FrameworkElement host,
        TextBlock plainBlock,
        FrameworkElement plainContainer,
        ListView syncedList,
        ObservableCollection<LrcDisplayLine> targetLines,
        bool forceDark)
    {
        string trimmed = rawLyrics?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed))
        {
            host.Visibility = Visibility.Collapsed;
            targetLines.Clear();
            return;
        }

        host.Visibility = Visibility.Visible;
        var parsed = LrcParser.Parse(trimmed);
        bool useLight = !forceDark && RootGrid.RequestedTheme == ElementTheme.Light;
        targetLines.Clear();
        if (parsed.Count > 0)
        {
            foreach (var p in parsed)
            {
                if (!string.IsNullOrWhiteSpace(p.Text))
                    targetLines.Add(new LrcDisplayLine { Timestamp = p.Timestamp, Text = p.Text, UseLightTheme = useLight });
            }
            plainContainer.Visibility = Visibility.Collapsed;
            syncedList.Visibility = Visibility.Visible;
        }
        else
        {
            plainBlock.Text = trimmed;
            syncedList.Visibility = Visibility.Collapsed;
            plainContainer.Visibility = Visibility.Visible;
        }
    }

    private void LyricsLine_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not LrcDisplayLine line) return;
        if (ReferenceEquals(sender, SelectedSyncedLyricsList) &&
            !string.Equals(_selectedLyricsTrackPath, _engine.CurrentPath, StringComparison.OrdinalIgnoreCase))
            return;
        _engine.Seek(line.Timestamp);
        if (!_engine.IsPlaying && _engine.CurrentPath != null)
            _engine.Resume();
    }

    private void Engine_PlaybackStateChanged(bool playing)
    {
        PlayPauseIcon.Glyph = playing ? "\uE769" : "\uE768";
        PlaybarVisualizer.IsAudioPlaying = playing;
        FocusVisualizer.IsAudioPlaying = playing;

        if (playing) _progressTimer.Start();
        else _progressTimer.Stop();
    }

    private void Engine_TrackEnded() => PlayNext(fromAutoTrackEnd: true);

    private async void Engine_PlaybackError(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "Playback error",
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.RequestedTheme
        };
        try { await dialog.ShowAsync(); } catch { }
    }

    private void ProgressTimer_Tick(object? sender, object e)
    {
        if (_isDraggingSeek) return;
        var pos = _engine.Position;
        var dur = _engine.Duration;

        ElapsedTimeText.Text = FormatTime(pos);
        TotalTimeText.Text = FormatTime(dur);

        if (dur.TotalSeconds > 0)
        {
            _isUpdatingSeekSlider = true;
            try
            {
                SeekSlider.Maximum = dur.TotalSeconds;
                SeekSlider.Value = Math.Clamp(pos.TotalSeconds, 0, dur.TotalSeconds);
            }
            finally { _isUpdatingSeekSlider = false; }
        }

        if (FocusVisualizerOverlay.Visibility == Visibility.Visible)
        {
            FocusAmbientGlow.Opacity = 0.28 + _engine.Fft.BassEnergy * 0.42;
            UpdateActiveLyricHighlight(_focusLrcLines, FocusSyncedLyricsList, ref _activeFocusLrcIndex, pos);
        }

        if (_selectedLrcLines.Count > 0 &&
            string.Equals(_selectedLyricsTrackPath, _engine.CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            UpdateActiveLyricHighlight(_selectedLrcLines, SelectedSyncedLyricsList, ref _activeSelectedLrcIndex, pos);
        }
    }

    private static void UpdateActiveLyricHighlight(
        ObservableCollection<LrcDisplayLine> lines,
        ListView listView,
        ref int lastIndex,
        TimeSpan pos)
    {
        if (lines.Count == 0) return;
        int idx = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Timestamp <= pos) idx = i;
            else break;
        }
        if (idx == lastIndex) return;
        if (lastIndex >= 0 && lastIndex < lines.Count) lines[lastIndex].IsActive = false;
        lastIndex = idx;
        if (idx >= 0 && idx < lines.Count)
        {
            lines[idx].IsActive = true;
            listView.ScrollIntoView(lines[idx], ScrollIntoViewAlignment.Default);
        }
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes:D2}:{t.Seconds:D2}";

    private void PlayPauseBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.CurrentPath == null)
        {
            PlayAllBtn_Click(sender, e);
            return;
        }
        if (_engine.IsPlaying) _engine.Pause();
        else _engine.Resume();
    }

    private void NextBtn_Click(object sender, RoutedEventArgs e) => PlayNext(fromAutoTrackEnd: false);
    private void PrevBtn_Click(object sender, RoutedEventArgs e) => PlayPrevious();

    private void PlayNext(bool fromAutoTrackEnd = false)
    {
        if (_queueTracks.Count == 0) return;
        if (fromAutoTrackEnd && _settings.RepeatMode == 2 && _queueIndex >= 0 && _queueIndex < _queueTracks.Count)
        {
            PlayQueueIndex(_queueIndex);
            return;
        }

        int next = _queueIndex + 1;
        if (next >= _queueTracks.Count)
        {
            if (_settings.RepeatMode == 0)
            {
                _engine.Stop();
                return;
            }
            if (_settings.Shuffle && _queueTracks.Count > 1)
            {
                var reshuffled = _queueTracks.ToList();
                ShuffleInPlace(reshuffled);
                _queueTracks.Clear();
                foreach (var t in reshuffled) _queueTracks.Add(t);
            }
            next = 0;
        }
        PlayQueueIndex(next);
    }

    private void PlayPrevious()
    {
        if (_queueTracks.Count == 0) return;
        if (_engine.Position.TotalSeconds > 3)
        {
            _engine.Seek(TimeSpan.Zero);
            return;
        }
        int prev = _queueIndex - 1;
        if (prev < 0) prev = _settings.RepeatMode > 0 ? _queueTracks.Count - 1 : 0;
        PlayQueueIndex(prev);
    }

    private void SeekSlider_PointerPressed(object sender, PointerRoutedEventArgs e) => _isDraggingSeek = true;

    private void SeekSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isDraggingSeek = false;
        if (_engine.Duration.TotalSeconds > 0)
        {
            var target = TimeSpan.FromSeconds(SeekSlider.Value);
            _engine.Seek(target);
            ElapsedTimeText.Text = FormatTime(target);
        }
    }

    private void SeekSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingSeekSlider || _engine == null || _engine.Duration.TotalSeconds <= 0) return;
        if (_isDraggingSeek)
        {
            ElapsedTimeText.Text = FormatTime(TimeSpan.FromSeconds(e.NewValue));
            return;
        }
        _engine.Seek(TimeSpan.FromSeconds(e.NewValue));
        ElapsedTimeText.Text = FormatTime(TimeSpan.FromSeconds(e.NewValue));
    }

    private void SeekSlider_KeyUp(object sender, KeyRoutedEventArgs e) => _engine.Seek(TimeSpan.FromSeconds(SeekSlider.Value));

    private void SeekSlider_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_engine == null || _engine.Duration.TotalSeconds <= 0 || SeekSlider.ActualWidth <= 0) return;
        double ratio = Math.Clamp(e.GetCurrentPoint(SeekSlider).Position.X / SeekSlider.ActualWidth, 0.0, 1.0);
        var hoverTime = TimeSpan.FromSeconds(ratio * _engine.Duration.TotalSeconds);
        ToolTipService.SetToolTip(SeekSlider, $"{FormatTime(hoverTime)} / {FormatTime(_engine.Duration)}");
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_engine == null) return;
        _settings.Volume = e.NewValue;
        if (e.NewValue > 0) _isMuted = false;
        _engine.SetVolume(e.NewValue);
        UpdateVolumeIcon(e.NewValue);
    }

    private void MuteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isMuted || VolumeSlider.Value <= 0.5)
        {
            _isMuted = false;
            VolumeSlider.Value = _preMuteVolume > 1 ? _preMuteVolume : 70;
        }
        else
        {
            _preMuteVolume = VolumeSlider.Value;
            _isMuted = true;
            VolumeSlider.Value = 0;
        }
    }

    private void UpdateVolumeIcon(double volume)
    {
        if (VolumeIcon == null) return;
        VolumeIcon.Glyph = (_isMuted || volume <= 0.5) ? "\uE74F"
            : volume < 35 ? "\uE993"
            : volume < 70 ? "\uE994"
            : "\uE767";
    }

    private void ShuffleToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.Shuffle = ShuffleToggle.IsChecked == true;
        _settings.Save();

        if (_queueTracks.Count <= 1) return;
        TrackItem? playing = _queueIndex >= 0 && _queueIndex < _queueTracks.Count ? _queueTracks[_queueIndex] : null;

        if (_settings.Shuffle)
        {
            if (_unshuffledQueue.Count == 0) _unshuffledQueue.AddRange(_queueTracks);
            var rest = _queueTracks.Where(t => !ReferenceEquals(t, playing)).ToList();
            ShuffleInPlace(rest);
            _queueTracks.Clear();
            if (playing != null)
            {
                _queueTracks.Add(playing);
                _queueIndex = 0;
            }
            foreach (var t in rest) _queueTracks.Add(t);
        }
        else if (_unshuffledQueue.Count > 0)
        {
            _queueTracks.Clear();
            foreach (var t in _unshuffledQueue) _queueTracks.Add(t);
            _queueIndex = playing != null ? _queueTracks.IndexOf(playing) : (_queueTracks.Count > 0 ? 0 : -1);
        }
        UpdateQueueHeader();
        if (_queueIndex >= 0 && _queueIndex < _queueTracks.Count)
        {
            QueueListView.SelectedIndex = _queueIndex;
            QueueListView.ScrollIntoView(_queueTracks[_queueIndex]);
        }
    }

    private void RepeatToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.RepeatMode = (_settings.RepeatMode + 1) % 3;
        _settings.RepeatAll = _settings.RepeatMode != 0;
        UpdateRepeatButtonVisual();
        _settings.Save();
    }

    private void UpdateRepeatButtonVisual()
    {
        RepeatToggle.IsChecked = _settings.RepeatMode != 0;
        RepeatIcon.Glyph = _settings.RepeatMode == 2 ? "\uE8ED" : "\uE8EE";
        ToolTipService.SetToolTip(RepeatToggle, _settings.RepeatMode switch
        {
            0 => "Repeat off (Click to cycle)",
            2 => "Repeat one (Click to cycle)",
            _ => "Repeat all (Click to cycle)"
        });
    }

    private void VisualizerToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        _settings.VisualizerEnabled = !_settings.VisualizerEnabled;
        _settings.Save();
        ApplyVisualizerFromSettings();
    }

    private void FocusVizModeBtn_Click(object sender, RoutedEventArgs e)
    {
        var next = FocusVisualizer.Mode switch
        {
            VisualizerMode.Spectrum => VisualizerMode.Waveform,
            VisualizerMode.Waveform => VisualizerMode.BarsAndWaveform,
            _ => VisualizerMode.Spectrum
        };
        FocusVisualizer.Mode = next;
        _settings.VisualizerMode = next.ToString();
        _settings.VisualizerEnabled = true;
        _settings.Save();
        PlaybarVisualizer.Mode = next;
        PlaybarVisualizerHost.Visibility = Visibility.Visible;
        UpdateFocusVizLabel();
    }

    private void PlaybarVisualizerHost_Tapped(object sender, TappedRoutedEventArgs e) => ToggleFocusVisualizer();

    private void CloseFocusBtn_Click(object sender, RoutedEventArgs e)
    {
        FocusVisualizerOverlay.Visibility = Visibility.Collapsed;
        SyncVisualizerStageVisibility();
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    private void PlaybarBorder_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_isMiniPlayer) return;
        if (!e.GetCurrentPoint(PlaybarBorder).Properties.IsLeftButtonPressed) return;
        var src = e.OriginalSource as DependencyObject;
        if (IsInsideButton(src) || IsInsideType<Slider>(src) || IsInsideType<StarRatingCell>(src)) return;

        ReleaseCapture();
        SendMessage(WindowNative.GetWindowHandle(this), 0x00A1 /* WM_NCLBUTTONDOWN */, 2 /* HTCAPTION */, 0);
    }

    private void MiniPlayerToggleBtn_Click(object sender, RoutedEventArgs e) => SetMiniPlayerMode(!_isMiniPlayer);

    private void SetMiniPlayerMode(bool enable)
    {
        if (_isMiniPlayer == enable) return;
        _isMiniPlayer = enable;

        var appWin = this.AppWindow;
        var presenter = appWin.Presenter as OverlappedPresenter;

        if (enable)
        {
            // Capture current visual window bounds BEFORE Restore() so the mini-player stays anchored where the playbar is
            var currentPos = appWin.Position;
            var currentSize = appWin.Size;
            var display = DisplayArea.GetFromWindowId(appWin.Id, DisplayAreaFallback.Nearest);
            var work = display.WorkArea;

            double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
            int targetW = Math.Clamp((int)Math.Round(900 * scale), Math.Min(700, work.Width), work.Width);
            int targetH = Math.Clamp((int)Math.Round(72 * scale), 68, work.Height);
            int currentCenterX = currentPos.X + (currentSize.Width / 2);
            int currentBottomY = currentPos.Y + currentSize.Height;
            int bottomPad = (int)Math.Round(16 * scale);
            int targetX = Math.Clamp(currentCenterX - (targetW / 2), work.X, Math.Max(work.X, work.X + work.Width - targetW));
            int targetY = Math.Clamp(currentBottomY - targetH - bottomPad, work.Y, Math.Max(work.Y, work.Y + work.Height - targetH));

            _wasMaximizedBeforeMini = presenter != null && presenter.State == OverlappedPresenterState.Maximized;
            if (_wasMaximizedBeforeMini)
                presenter!.Restore();

            _preMiniBounds = new RectInt32(appWin.Position.X, appWin.Position.Y, appWin.Size.Width, appWin.Size.Height);

            TopBarRowDef.Height = new GridLength(0);
            MainTitleBar.Visibility = Visibility.Collapsed;
            WorkspaceRowDef.Height = new GridLength(0);
            MainWorkspaceGrid.Visibility = Visibility.Collapsed;

            if (FocusVisualizerOverlay.Visibility == Visibility.Visible)
                FocusVisualizerOverlay.Visibility = Visibility.Collapsed;

            // Hide embedded visualizer in mini mode so the scrubber keeps full width
            PlaybarVisualizerHost.Visibility = Visibility.Collapsed;
            PlaybarVisualizerCol.Width = new GridLength(0);
            VisualizerToggleBtn.Visibility = Visibility.Collapsed;
            SyncVisualizerStageVisibility();

            PlaybarBorder.BorderThickness = new Thickness(0);
            MiniPlayerIcon.Glyph = "\uE740";
            ToolTipService.SetToolTip(MiniPlayerToggleBtn, "Expand to Full Window (Ctrl+M)");

            if (presenter != null)
            {
                presenter.SetBorderAndTitleBar(true, false);
                presenter.IsMaximizable = false;
                presenter.IsAlwaysOnTop = true;
            }
            appWin.MoveAndResize(new RectInt32(targetX, targetY, targetW, targetH));
            UpdateTitleBarPassthrough();
        }
        else
        {
            if (presenter != null)
            {
                presenter.IsAlwaysOnTop = false;
                presenter.IsMaximizable = true;
                presenter.SetBorderAndTitleBar(true, true);
            }

            TopBarRowDef.Height = new GridLength(44);
            MainTitleBar.Visibility = Visibility.Visible;
            SetTitleBar(TitleBarDragRegion);
            WorkspaceRowDef.Height = new GridLength(1, GridUnitType.Star);
            MainWorkspaceGrid.Visibility = Visibility.Visible;
            ApplyPaneWidths();

            PlaybarVisualizerCol.Width = new GridLength(200);
            VisualizerToggleBtn.Visibility = Visibility.Visible;
            ApplyVisualizerFromSettings();

            PlaybarBorder.BorderThickness = new Thickness(0, 1, 0, 0);
            MiniPlayerIcon.Glyph = "\uE73F";
            ToolTipService.SetToolTip(MiniPlayerToggleBtn, "Collapse to Mini Player (Ctrl+M)");

            if (_preMiniBounds.Width >= 400 && _preMiniBounds.Height >= 300)
                appWin.MoveAndResize(_preMiniBounds);
            if (_wasMaximizedBeforeMini && presenter != null)
                presenter.Maximize();

            _lastSearchPassthroughRect = default;
            DispatcherQueue.TryEnqueue(UpdateTitleBarPassthrough);
        }
    }

    private void ToggleFocusVisualizer()
    {
        if (_isMiniPlayer)
            SetMiniPlayerMode(false);
        FocusVisualizerOverlay.Visibility = FocusVisualizerOverlay.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        SyncVisualizerStageVisibility();
    }

    private void TracksListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged) return;
        _suppressSelectionChanged = true;
        try { AlbumsGridView.SelectedItem = TracksListView.SelectedItem; }
        finally { _suppressSelectionChanged = false; }
        UpdateInspectorForSelection(TracksListView.SelectedItem as TrackItem);
    }

    private void AlbumsGridView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged) return;
        _suppressSelectionChanged = true;
        try { TracksListView.SelectedItem = AlbumsGridView.SelectedItem; }
        finally { _suppressSelectionChanged = false; }
        UpdateInspectorForSelection(AlbumsGridView.SelectedItem as TrackItem);
    }

    private void UpdateInspectorForSelection(TrackItem? track)
    {
        UpdateStatusFooter();
        if (track == null)
        {
            _selectedLyricsTrackPath = null;
            SelectedTitleText.Text = "No track selected";
            SelectedArtistText.Text = string.Empty;
            SelectedSpecsText.Text = string.Empty;
            SelectedArtImage.Source = null;
            SelectedPropertiesBtn.IsEnabled = false;
            UpdateSelectedRatingVisuals(null);
            UpdateSelectedLyricsPreview(string.Empty);
            return;
        }
        _selectedLyricsTrackPath = track.Path;
        SelectedInspectorScroll.ChangeView(null, 0, null, true);
        UpdateSelectedTrackLabels(track);
        SelectedPropertiesBtn.IsEnabled = true;
        UpdateSelectedRatingVisuals(track.Rating);
        _ = LoadArtAsync(track.Path, SelectedArtImage);
        _ = LoadSelectedLyricsAsync(track.Path);
    }

    private void UpdateSelectedTrackLabels(TrackItem track)
    {
        SelectedTitleText.Text = track.DisplayTitle;
        SelectedArtistText.Text = track.Artist;
        string codecInfo = $"{track.Codec} {track.BitrateText}".Trim();
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(track.DurationText)) parts.Add(track.DurationText);
        if (!string.IsNullOrWhiteSpace(codecInfo)) parts.Add(codecInfo);
        if (!string.IsNullOrWhiteSpace(track.Album)) parts.Add(track.Album);
        SelectedSpecsText.Text = parts.Count > 0 ? string.Join(" · ", parts) : track.ChannelsText;
    }

    private void SelectedArtistText_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SelectedArtistText.Text)) return;
        e.Handled = true;
        FilterByArtist(SelectedArtistText.Text);
    }

    private void NowPlayingArtistText_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_queueIndex < 0 || _queueIndex >= _queueTracks.Count) return;
        string artist = _queueTracks[_queueIndex].Artist;
        if (string.IsNullOrWhiteSpace(artist)) return;
        e.Handled = true;
        FilterByArtist(artist);
    }

    private void FilterByArtist(string artist)
    {
        if (_isMiniPlayer)
            SetMiniPlayerMode(false);
        _activeArtistFilter = artist;
        _activeAlbumFilter = null;
        if (string.Equals(_settings.ViewMode, "Grid", StringComparison.OrdinalIgnoreCase))
        {
            _settings.ViewMode = "Table";
            _settings.Save();
            ApplyViewModeVisuals();
        }
        RefreshVisibleTracks();
    }

    private void SelectedArtBorder_Tapped(object sender, TappedRoutedEventArgs e)
    {
        _inspectorArtMode = (_inspectorArtMode + 1) % 3;
        _settings.InspectorArtMode = _inspectorArtMode;
        ApplyInspectorArtMode();
    }

    private void SelectedInfoOverlay_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_inspectorArtMode != 2) return;
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
        _inspectorArtMode = 0;
        _settings.InspectorArtMode = _inspectorArtMode;
        ApplyInspectorArtMode();
        e.Handled = true;
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is ButtonBase) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void ApplyInspectorArtMode()
    {
        if (_inspectorArtMode == 0)
        {
            SelectedArtCol.Width = new GridLength(112);
            SelectedHeroGrid.ColumnSpacing = 12;
            Grid.SetColumnSpan(SelectedArtBorder, 1);
            SelectedArtBorder.Width = 112;
            SelectedArtBorder.Height = 112;
            SelectedArtBorder.HorizontalAlignment = HorizontalAlignment.Left;

            SelectedInfoOverlay.Visibility = Visibility.Visible;
            Grid.SetColumn(SelectedInfoOverlay, 1);
            Grid.SetColumnSpan(SelectedInfoOverlay, 1);
            SelectedInfoOverlay.VerticalAlignment = VerticalAlignment.Center;
            SelectedInfoOverlay.Background = TransparentRowBrush;
            SelectedInfoOverlay.Padding = new Thickness(0);
            SelectedInfoOverlay.CornerRadius = new CornerRadius(0);
            ApplySelectedInfoTheme();
        }
        else if (_inspectorArtMode == 1)
        {
            SelectedArtCol.Width = new GridLength(1, GridUnitType.Star);
            SelectedHeroGrid.ColumnSpacing = 0;
            Grid.SetColumnSpan(SelectedArtBorder, 2);
            SelectedArtBorder.Width = double.NaN;
            SelectedArtBorder.Height = 208;
            SelectedArtBorder.HorizontalAlignment = HorizontalAlignment.Stretch;

            SelectedInfoOverlay.Visibility = Visibility.Collapsed;
        }
        else
        {
            SelectedArtCol.Width = new GridLength(1, GridUnitType.Star);
            SelectedHeroGrid.ColumnSpacing = 0;
            Grid.SetColumnSpan(SelectedArtBorder, 2);
            SelectedArtBorder.Width = double.NaN;
            SelectedArtBorder.Height = 208;
            SelectedArtBorder.HorizontalAlignment = HorizontalAlignment.Stretch;

            SelectedInfoOverlay.Visibility = Visibility.Visible;
            Grid.SetColumn(SelectedInfoOverlay, 0);
            Grid.SetColumnSpan(SelectedInfoOverlay, 2);
            SelectedInfoOverlay.VerticalAlignment = VerticalAlignment.Bottom;
            SelectedInfoOverlay.Padding = new Thickness(8, 6, 8, 6);
            SelectedInfoOverlay.CornerRadius = new CornerRadius(0, 0, 8, 8);
            ApplySelectedInfoTheme();
        }
    }

    private void ApplySelectedInfoTheme()
    {
        if (_inspectorArtMode != 2)
        {
            SelectedInfoOverlay.RequestedTheme = ElementTheme.Default;
            SelectedTitleText.ClearValue(TextBlock.ForegroundProperty);
            SelectedArtistText.ClearValue(TextBlock.ForegroundProperty);
            SelectedSpecsText.ClearValue(TextBlock.ForegroundProperty);
            SelectedPropertiesIcon.ClearValue(FontIcon.ForegroundProperty);
            SelectedClearRatingIcon.ClearValue(FontIcon.ForegroundProperty);
            foreach (var bg in new[] { StarBg1, StarBg2, StarBg3, StarBg4, StarBg5 })
                bg.ClearValue(TextBlock.ForegroundProperty);
            return;
        }

        if (_selectedArtIsLight)
        {
            SelectedInfoOverlay.RequestedTheme = ElementTheme.Light;
            SelectedInfoOverlay.Background = InspectorLightOverlayBrush;
            SelectedTitleText.Foreground = LightOverlayPrimaryBrush;
            SelectedArtistText.Foreground = LightOverlayAccentBrush;
            SelectedSpecsText.Foreground = LightOverlaySecondaryBrush;
            SelectedPropertiesIcon.Foreground = LightOverlayPrimaryBrush;
            SelectedClearRatingIcon.Foreground = LightOverlaySecondaryBrush;
            foreach (var bg in new[] { StarBg1, StarBg2, StarBg3, StarBg4, StarBg5 })
                bg.Foreground = LightOverlayStarBgBrush;
        }
        else
        {
            SelectedInfoOverlay.RequestedTheme = ElementTheme.Dark;
            SelectedInfoOverlay.Background = InspectorOverlayBrush;
            SelectedTitleText.Foreground = DarkOverlayPrimaryBrush;
            SelectedArtistText.Foreground = DarkOverlayAccentBrush;
            SelectedSpecsText.Foreground = DarkOverlaySecondaryBrush;
            SelectedPropertiesIcon.Foreground = DarkOverlayPrimaryBrush;
            SelectedClearRatingIcon.Foreground = DarkOverlaySecondaryBrush;
            foreach (var bg in new[] { StarBg1, StarBg2, StarBg3, StarBg4, StarBg5 })
                bg.Foreground = DarkOverlayStarBgBrush;
        }
    }

    private async Task LoadSelectedLyricsAsync(string path)
    {
        string lyrics = await Task.Run(() => MusicLibrary.ReadLyrics(path));
        if (!string.Equals(_selectedLyricsTrackPath, path, StringComparison.OrdinalIgnoreCase)) return;
        UpdateSelectedLyricsPreview(lyrics);
    }

    private void UpdateSelectedLyricsPreview(string lyrics)
    {
        ApplyLyricsToView(lyrics, SelectedLyricsContainer, SelectedLyricsText, SelectedLyricsText, SelectedSyncedLyricsList, _selectedLrcLines, forceDark: false);
        _activeSelectedLrcIndex = -1;
    }

    private void UpdateTitleBarPassthrough()
    {
        try
        {
            if (_isMiniPlayer)
            {
                _lastSearchPassthroughRect = default;
                InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
                    .SetRegionRects(NonClientRegionKind.Passthrough, Array.Empty<RectInt32>());
                return;
            }
            if (RootGrid.XamlRoot == null || SearchHostGrid.ActualWidth <= 0) return;
            double scale = RootGrid.XamlRoot.RasterizationScale;
            var pt = SearchHostGrid.TransformToVisual(null).TransformPoint(new Point(0, 0));
            var rect = new RectInt32(
                (int)Math.Floor(pt.X * scale),
                (int)Math.Floor(pt.Y * scale),
                (int)Math.Ceiling(SearchHostGrid.ActualWidth * scale),
                (int)Math.Ceiling(SearchHostGrid.ActualHeight * scale));
            if (rect.X == _lastSearchPassthroughRect.X && rect.Y == _lastSearchPassthroughRect.Y &&
                rect.Width == _lastSearchPassthroughRect.Width && rect.Height == _lastSearchPassthroughRect.Height)
                return;

            _lastSearchPassthroughRect = rect;
            var nonClient = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            nonClient.SetRegionRects(NonClientRegionKind.Passthrough, new[] { rect });
        }
        catch { }
    }

    private void NowPlayingInfo_PointerEntered(object sender, PointerRoutedEventArgs e) => NowPlayingLocateBadge.Opacity = 1.0;

    private void NowPlayingInfo_PointerExited(object sender, PointerRoutedEventArgs e) => NowPlayingLocateBadge.Opacity = 0.82;

    private void NowPlayingInfo_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (IsInsideType<StarRatingCell>(e.OriginalSource as DependencyObject) ||
            IsInsideButton(e.OriginalSource as DependencyObject))
            return;
        LocateNowPlayingTrack();
    }

    private void LocateNowPlayingTrack()
    {
        TrackItem? playing = (_queueIndex >= 0 && _queueIndex < _queueTracks.Count)
            ? _queueTracks[_queueIndex]
            : (!string.IsNullOrEmpty(_engine.CurrentPath)
                ? _allTracks.FirstOrDefault(t => string.Equals(t.Path, _engine.CurrentPath, StringComparison.OrdinalIgnoreCase))
                : null);
        if (playing == null) return;
        if (_isMiniPlayer)
            SetMiniPlayerMode(false);
        RevealTrackInMainList(playing);
    }

    private void RevealTrackInMainList(TrackItem track)
    {
        var match = _visibleTracks.FirstOrDefault(t => ReferenceEquals(t, track) || string.Equals(t.Path, track.Path, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            if (!string.IsNullOrEmpty(SearchBox.Text))
                SearchBox.Text = string.Empty;
            _activeFacet = "all";
            _activePlaylist = null;
            _activeCustomFilter = null;
            _activeHygiene = null;
            _activeArtistFilter = null;
            _activeAlbumFilter = null;
            RefreshVisibleTracks();
            match = _visibleTracks.FirstOrDefault(t => ReferenceEquals(t, track) || string.Equals(t.Path, track.Path, StringComparison.OrdinalIgnoreCase));
            if (match == null) return;
        }

        SyncSelectionAcrossViews(match);
        UpdateInspectorForSelection(match);
        ScrollActiveViewToTrack(match);
    }

    private void NowPlayingRatingCell_RatingSelected(object? sender, double? newRating)
    {
        if (_queueIndex < 0 || _queueIndex >= _queueTracks.Count) return;
        var playing = _queueTracks[_queueIndex];
        var matching = _allTracks.Where(x => string.Equals(x.Path, playing.Path, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!matching.Contains(playing)) matching.Add(playing);
        ApplyRatingToTargets(matching, newRating, pushUndo: true);
    }

    private async Task LoadArtAsync(string path, params Image[] targets)
    {
        byte[]? bytes = await Task.Run(() => MusicLibrary.ReadEmbeddedArtBytes(path));
        await SetImageFromBytesAsync(bytes, targets);
        if (targets.Contains(SelectedArtImage))
        {
            _selectedArtIsLight = await ComputeBottomLuminanceIsLightAsync(bytes);
            ApplySelectedInfoTheme();
        }
    }

    private static async Task<Color> ComputeDominantAccentColorAsync(byte[]? bytes)
    {
        Color fallback = Color.FromArgb(255, 0, 120, 212);
        if (bytes == null || bytes.Length == 0) return fallback;
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform { ScaledWidth = 16, ScaledHeight = 16, InterpolationMode = BitmapInterpolationMode.Linear },
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            byte[] pixels = pixelData.DetachPixelData();

            double rSum = 0, gSum = 0, bSum = 0, weightSum = 0;
            for (int i = 0; i + 3 < pixels.Length; i += 4)
            {
                double b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                double max = Math.Max(r, Math.Max(g, b));
                double min = Math.Min(r, Math.Min(g, b));
                double sat = max > 0 ? (max - min) / max : 0;
                double w = 0.2 + sat * 2.5;
                rSum += r * w;
                gSum += g * w;
                bSum += b * w;
                weightSum += w;
            }
            if (weightSum <= 0) return fallback;
            return Color.FromArgb(255,
                (byte)Math.Clamp(rSum / weightSum, 30, 240),
                (byte)Math.Clamp(gSum / weightSum, 30, 240),
                (byte)Math.Clamp(bSum / weightSum, 30, 240));
        }
        catch { return fallback; }
    }

    private static async Task<bool> ComputeBottomLuminanceIsLightAsync(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return false;
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            const uint sampleW = 24;
            const uint sampleH = 24;
            var transform = new BitmapTransform { ScaledWidth = sampleW, ScaledHeight = sampleH, InterpolationMode = BitmapInterpolationMode.Linear };
            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            byte[] pixels = pixelData.DetachPixelData();

            double lumSum = 0;
            int count = 0;
            for (int y = 11; y < sampleH; y++)
            {
                for (int x = 0; x < sampleW; x++)
                {
                    int idx = (y * (int)sampleW + x) * 4;
                    double b = pixels[idx] / 255.0;
                    double g = pixels[idx + 1] / 255.0;
                    double r = pixels[idx + 2] / 255.0;
                    lumSum += 0.2126 * r + 0.7152 * g + 0.0722 * b;
                    count++;
                }
            }
            return count > 0 && (lumSum / count) > 0.50;
        }
        catch { return false; }
    }

    private static async Task<BitmapImage?> CreateBitmapFromBytesAsync(byte[]? bytes, int decodePixelWidth = 0)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var bmp = new BitmapImage();
            if (decodePixelWidth > 0) bmp.DecodePixelWidth = decodePixelWidth;
            await bmp.SetSourceAsync(stream);
            return bmp;
        }
        catch { return null; }
    }

    private static async Task SetImageFromBytesAsync(byte[]? bytes, params Image[] targets)
    {
        var bmp = await CreateBitmapFromBytesAsync(bytes);
        foreach (var img in targets) img.Source = bmp;
    }

    private void StarBtn_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Button btn || !int.TryParse(btn.Tag?.ToString(), out int starIndex)) return;
        double x = e.GetCurrentPoint(btn).Position.X;
        double width = btn.ActualWidth > 0 ? btn.ActualWidth : 20.0;
        double rating = x < width * 0.5 ? starIndex - 0.5 : starIndex;
        _lastStarPointerUtc = DateTime.UtcNow;
        var current = GetPrimarySelectedTrack()?.Rating;
        ApplyRatingToSelection(Nullable.Equals(current, rating) ? null : rating);
    }

    private void RateStarBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || !double.TryParse(btn.Tag?.ToString(), out double stars)) return;
        if (stars <= 0)
        {
            ApplyRatingToSelection(null);
            return;
        }
        if ((DateTime.UtcNow - _lastStarPointerUtc).TotalMilliseconds > 300)
        {
            var current = GetPrimarySelectedTrack()?.Rating;
            ApplyRatingToSelection(Nullable.Equals(current, stars) ? null : stars);
        }
    }

    private void ApplyRatingToSelection(double? value)
    {
        var selected = GetSelectedTracks();
        ApplyRatingToTargets(selected, value, pushUndo: true);
    }

    private void UpdateSelectedRatingVisuals(double? rating)
    {
        var fills = new[] { StarFill1, StarFill2, StarFill3, StarFill4, StarFill5 };
        double r = rating ?? 0;
        for (int i = 0; i < fills.Length; i++)
        {
            double threshold = i + 1;
            if (r >= threshold)
            {
                fills[i].Visibility = Visibility.Visible;
                fills[i].Clip = null;
            }
            else if (r >= threshold - 0.5)
            {
                fills[i].Visibility = Visibility.Visible;
                fills[i].Clip = new RectangleGeometry { Rect = new Rect(0, 0, 8, 20) };
            }
            else
            {
                fills[i].Visibility = Visibility.Collapsed;
                fills[i].Clip = null;
            }
        }
    }

    private async void SelectedPropertiesBtn_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedTracks();
        if (selected.Count > 0)
            await OpenPropertiesDialogAsync(selected);
    }

    private async Task<(bool Ok, string? Error)> RenameTrackPathAsync(TrackItem track, string oldPath, string newPath)
    {
        _suppressWatcherUntilUtc = DateTime.UtcNow.AddSeconds(2);
        var (renamed, renameErr) = await _engine.RenameFileAsync(oldPath, newPath);
        if (!renamed) return (false, renameErr);

        track.Path = newPath;
        if (string.Equals(_selectedLyricsTrackPath, oldPath, StringComparison.OrdinalIgnoreCase))
            _selectedLyricsTrackPath = newPath;
        if (string.Equals(_activePlayingPathForLyrics, oldPath, StringComparison.OrdinalIgnoreCase))
            _activePlayingPathForLyrics = newPath;

        foreach (var pl in _playlists)
        {
            for (int i = 0; i < pl.TrackPaths.Count; i++)
            {
                if (string.Equals(pl.TrackPaths[i], oldPath, StringComparison.OrdinalIgnoreCase))
                    pl.TrackPaths[i] = newPath;
            }
        }
        _settings.Save();
        if (GetPrimarySelectedTrack() == track)
            UpdateSelectedTrackLabels(track);
        return (true, null);
    }

    private async Task ShowErrorDialogAsync(string title, string message)
    {
        var errDlg = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.RequestedTheme
        };
        await errDlg.ShowAsync();
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                {
                    UseShellExecute = true
                });
            }
            else
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"")
                    {
                        UseShellExecute = true
                    });
                }
            }
        }
        catch { }
    }

    private async Task DeleteTracksToRecycleBinAsync(IReadOnlyList<TrackItem> tracks)
    {
        if (tracks.Count == 0) return;
        string prompt = tracks.Count == 1
            ? $"Move \"{tracks[0].FileName}\" to the Recycle Bin?"
            : $"Move {tracks.Count} selected files to the Recycle Bin?";

        var confirmDlg = new ContentDialog
        {
            Title = "Delete to Recycle Bin",
            Content = prompt,
            PrimaryButtonText = "Move to Recycle Bin",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.RequestedTheme
        };
        DialogFooter.MainButtonOnRight(confirmDlg);
        if (await confirmDlg.ShowAsync() != ContentDialogResult.Primary) return;

        var deletedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        foreach (var t in tracks)
        {
            if (string.Equals(_engine.CurrentPath, t.Path, StringComparison.OrdinalIgnoreCase))
                _engine.Stop();

            try
            {
                if (File.Exists(t.Path))
                {
                    _suppressWatcherUntilUtc = DateTime.UtcNow.AddSeconds(2);
                    await Task.Run(() => Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                        t.Path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin));
                }
                deletedPaths.Add(t.Path);
            }
            catch (Exception ex) { errors.Add($"{t.FileName}: {ex.Message}"); }
        }

        if (deletedPaths.Count > 0)
        {
            _allTracks.RemoveAll(t => deletedPaths.Contains(t.Path));
            for (int i = _queueTracks.Count - 1; i >= 0; i--)
            {
                if (deletedPaths.Contains(_queueTracks[i].Path))
                    _queueTracks.RemoveAt(i);
            }
            if (_queueIndex >= _queueTracks.Count)
                _queueIndex = _queueTracks.Count - 1;
            UpdateQueueHeader();

            foreach (var pl in _playlists)
            {
                if (pl.TrackPaths.RemoveAll(p => deletedPaths.Contains(p)) > 0)
                    pl.NotifyTracksChanged();
            }
            _settings.Save();
            SaveLibraryCacheSnapshot();
            RefreshVisibleTracks();
            UpdateStatusFooter();
        }

        if (errors.Count > 0)
            await ShowErrorDialogAsync("Some files could not be deleted", string.Join("\n", errors));
    }

    private async Task<bool> SaveTrackDetailsAsync(TrackItem track, MusicLibrary.TrackDetails updated, bool pushUndo = true)
    {
        MusicLibrary.TrackDetails? snapshotBefore = pushUndo ? await Task.Run(() => MusicLibrary.ReadFullDetails(track.Path)) : null;

        var (saved, error) = await _engine.WithFileReleasedAsync(
            track.Path,
            () => Task.Run(() =>
            {
                bool ok = MusicLibrary.TryWriteFullDetails(track.Path, updated, out string? err);
                return (ok, err);
            }));

        if (!saved)
        {
            var errDlg = new ContentDialog
            {
                Title = "Could not save tags",
                Content = $"Failed to write tags to \"{track.FileName}\":\n\n{error ?? "The file may be read-only or in use by another program."}",
                CloseButtonText = "OK",
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = RootGrid.RequestedTheme
            };
            await errDlg.ShowAsync();
            return false;
        }

        if (pushUndo && snapshotBefore != null)
        {
            snapshotBefore.ArtChanged = updated.ArtChanged;
            PushUndo(async () =>
            {
                await SaveTrackDetailsAsync(track, snapshotBefore, pushUndo: false);
                ShowHudPill($"Undone: Edit \"{track.DisplayTitle}\"");
            });
        }

        track.Title = updated.Title;
        track.Artist = updated.Artist;
        track.Album = updated.Album;
        track.Genre = updated.Genre;
        track.Year = updated.Year;
        track.TrackNumber = updated.TrackNumber;
        track.Rating = updated.Rating;
        if (updated.ArtChanged)
        {
            track.HasEmbeddedArt = updated.ArtBytes is { Length: > 0 };
            track.ArtLoadAttempted = false;
            track.CoverThumbnail = null;
            string cacheKey = !string.IsNullOrWhiteSpace(track.Album)
                ? $"{track.Album.Trim()}|{Path.GetDirectoryName(track.Path)}"
                : track.Path;
            _artThumbCache.TryRemove(cacheKey, out _);
        }

        if (GetPrimarySelectedTrack() == track)
        {
            UpdateSelectedTrackLabels(track);
            UpdateSelectedRatingVisuals(track.Rating);
            UpdateSelectedLyricsPreview(updated.Lyrics);
            if (updated.ArtChanged)
                await LoadArtAsync(track.Path, SelectedArtImage);
        }

        if (string.Equals(_engine.CurrentPath, track.Path, StringComparison.OrdinalIgnoreCase))
        {
            NowPlayingTitleText.Text = track.DisplayTitle;
            NowPlayingArtistText.Text = string.IsNullOrWhiteSpace(track.Artist) ? track.FileName : track.Artist;
            NowPlayingRatingCell.Value = track.Rating;
            FocusTitleText.Text = track.DisplayTitle;
            FocusArtistText.Text = NowPlayingArtistText.Text;
            FocusRatingCell.Value = track.Rating;
            await LoadFocusLyricsAsync(track.Path);
            if (updated.ArtChanged)
                await LoadPlayingArtAndSmtcAsync(track);
            else
                _engine.UpdateSmtcMetadata(track.DisplayTitle, track.Artist, track.Album);
        }

        return true;
    }

    private void PushUndo(Func<Task> undoAction)
    {
        _undoStack.Push(undoAction);
        if (_undoStack.Count > 30)
        {
            var kept = _undoStack.Take(30).Reverse().ToList();
            _undoStack.Clear();
            foreach (var item in kept) _undoStack.Push(item);
        }
    }

    private async Task PerformUndoAsync()
    {
        if (_undoStack.Count == 0)
        {
            ShowHudPill("Nothing to undo");
            return;
        }
        var action = _undoStack.Pop();
        await action();
    }

    private void TracksListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        => ShowTrackContextMenu(TracksListView, e);

    private void AlbumsGridView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        => ShowTrackContextMenu(AlbumsGridView, e);

    private void ShowTrackContextMenu(ListViewBase view, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is TrackItem hit && !view.SelectedItems.Contains(hit))
            view.SelectedItem = hit;

        var selected = view.SelectedItems.OfType<TrackItem>().ToList();
        if (selected.Count == 0) return;

        var menu = new MenuFlyout();

        var playItem = new MenuFlyoutItem { Text = "Play now" };
        playItem.Click += (_, _) => PlayFromList(_visibleTracks.ToList(), selected[0]);
        menu.Items.Add(playItem);

        var playNextItem = new MenuFlyoutItem { Text = "Play next" };
        playNextItem.Click += (_, _) =>
        {
            int insertAt = _queueIndex >= 0 && _queueIndex < _queueTracks.Count ? _queueIndex + 1 : 0;
            for (int i = 0; i < selected.Count; i++)
                _queueTracks.Insert(insertAt + i, selected[i]);
            UpdateQueueHeader();
        };
        menu.Items.Add(playNextItem);

        var queueEndItem = new MenuFlyoutItem { Text = "Add to end of queue" };
        queueEndItem.Click += (_, _) =>
        {
            foreach (var t in selected) _queueTracks.Add(t);
            UpdateQueueHeader();
        };
        menu.Items.Add(queueEndItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var rateSub = new MenuFlyoutSubItem { Text = "Rate" };
        var clearRateItem = new MenuFlyoutItem { Text = "Unrated" };
        clearRateItem.Click += (_, _) => ApplyRatingToSelection(null);
        rateSub.Items.Add(clearRateItem);
        for (double r = 0.5; r <= 5.0; r += 0.5)
        {
            double captured = r;
            var rItem = new MenuFlyoutItem { Text = $"{captured:0.#} ★" };
            rItem.Click += (_, _) => ApplyRatingToSelection(captured);
            rateSub.Items.Add(rItem);
        }
        menu.Items.Add(rateSub);

        var addPlSub = new MenuFlyoutSubItem { Text = "Add to playlist" };
        foreach (var pl in _playlists)
        {
            var plItem = new MenuFlyoutItem { Text = pl.Name };
            plItem.Click += (_, _) =>
            {
                int added = AddTracksToPlaylist(pl, selected);
                ShowHudPill(added > 0 ? $"Added {added} to \"{pl.Name}\"" : $"Already in \"{pl.Name}\"");
            };
            addPlSub.Items.Add(plItem);
        }
        var newPlItem = new MenuFlyoutItem { Text = "+ New playlist from selection..." };
        newPlItem.Click += async (_, _) => await CreatePlaylistAsync(selected);
        addPlSub.Items.Add(newPlItem);
        menu.Items.Add(addPlSub);

        if (_activePlaylist != null)
        {
            var activePl = _activePlaylist;
            var removePlItem = new MenuFlyoutItem { Text = $"Remove from \"{activePl.Name}\"" };
            removePlItem.Click += (_, _) => RemoveSelectionFromActivePlaylist(activePl, selected);
            menu.Items.Add(removePlItem);
        }

        menu.Items.Add(new MenuFlyoutSeparator());

        var propsItem = new MenuFlyoutItem { Text = selected.Count > 1 ? $"Batch Properties ({selected.Count} tracks)..." : "Properties & Lyrics..." };
        propsItem.Click += async (_, _) => await OpenPropertiesDialogAsync(selected);
        menu.Items.Add(propsItem);

        var autoTagItem = new MenuFlyoutItem { Text = selected.Count > 1 ? $"Auto-Tag ({selected.Count} tracks)..." : "Auto-Tag from Web & Filename..." };
        autoTagItem.Click += async (_, _) => await OpenAutoTagDialogAsync(selected);
        menu.Items.Add(autoTagItem);

        var renameFromTagsItem = new MenuFlyoutItem { Text = "Rename files from tags..." };
        renameFromTagsItem.Click += async (_, _) => await OpenBatchRenameDialogAsync(selected);
        menu.Items.Add(renameFromTagsItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        if (selected.Count == 1)
        {
            var explorerItem = new MenuFlyoutItem { Text = "Open in Explorer" };
            explorerItem.Click += (_, _) => OpenInExplorer(selected[0].Path);
            menu.Items.Add(explorerItem);
        }

        var deleteItem = new MenuFlyoutItem { Text = selected.Count > 1 ? $"Delete {selected.Count} files to Recycle Bin..." : "Delete to Recycle Bin..." };
        deleteItem.Click += async (_, _) => await DeleteTracksToRecycleBinAsync(selected);
        menu.Items.Add(deleteItem);

        menu.ShowAt(view, e.GetPosition(view));
    }

    private void RemoveSelectionFromActivePlaylist(SavedPlaylist activePl, IReadOnlyList<TrackItem> selected)
    {
        var snapshot = activePl.TrackPaths.ToList();
        var toRemove = new HashSet<string>(selected.Select(s => s.Path), StringComparer.OrdinalIgnoreCase);
        int removed = activePl.TrackPaths.RemoveAll(p => toRemove.Contains(p));
        if (removed <= 0) return;
        activePl.NotifyTracksChanged();
        _settings.Save();
        RefreshVisibleTracks();
        PushUndo(() =>
        {
            activePl.TrackPaths.Clear();
            activePl.TrackPaths.AddRange(snapshot);
            activePl.NotifyTracksChanged();
            _settings.Save();
            RefreshVisibleTracks();
            ShowHudPill($"Undone: Restored to \"{activePl.Name}\"");
            return Task.CompletedTask;
        });
    }

    private int AddTracksToPlaylist(SavedPlaylist pl, IReadOnlyList<TrackItem> tracks)
    {
        var existing = new HashSet<string>(pl.TrackPaths, StringComparer.OrdinalIgnoreCase);
        int added = 0;
        foreach (var t in tracks)
        {
            if (existing.Add(t.Path))
            {
                pl.TrackPaths.Add(t.Path);
                added++;
            }
        }
        if (added > 0)
        {
            pl.NotifyTracksChanged();
            _settings.Save();
        }
        return added;
    }

    private async void NewPlaylistBtn_Click(object sender, RoutedEventArgs e) => await CreatePlaylistAsync(GetSelectedTracks());

    private async void ImportPlaylistBtn_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".m3u");
        picker.FileTypeFilter.Add(".m3u8");
        var picked = await picker.PickSingleFileAsync();
        if (picked == null) return;

        string dir = Path.GetDirectoryName(picked.Path) ?? string.Empty;
        string[] lines = await File.ReadAllLinesAsync(picked.Path);
        var importedPaths = new List<string>();
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;
            string full = Path.IsPathRooted(line) ? line : Path.GetFullPath(Path.Combine(dir, line));
            if (File.Exists(full)) importedPaths.Add(full);
        }

        var pl = new SavedPlaylist
        {
            Name = Path.GetFileNameWithoutExtension(picked.Path),
            TrackPaths = importedPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
        _playlists.Add(pl);
        _settings.Playlists.Add(pl);
        _settings.Save();
        PlaylistsEmptyHint.Visibility = Visibility.Collapsed;
        PlaylistsList.SelectedItem = pl;
        ShowHudPill($"Imported \"{pl.Name}\" ({pl.TrackCount} tracks)");
    }

    private async Task ExportPlaylistM3u8Async(SavedPlaylist pl)
    {
        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.SuggestedFileName = pl.Name;
        picker.FileTypeChoices.Add("M3U8 Playlist", new[] { ".m3u8" });
        picker.FileTypeChoices.Add("M3U Playlist", new[] { ".m3u" });
        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        var byPath = _allTracks.ToDictionary(t => t.Path, StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        foreach (string path in pl.TrackPaths)
        {
            if (byPath.TryGetValue(path, out var t))
                sb.AppendLine($"#EXTINF:{(int)Math.Round(t.Duration.TotalSeconds)},{t.Artist} - {t.DisplayTitle}");
            sb.AppendLine(path);
        }
        await File.WriteAllTextAsync(file.Path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        ShowHudPill($"Exported \"{Path.GetFileName(file.Path)}\"");
    }

    private async void SaveQueueAsPlaylistBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_queueTracks.Count > 0)
            await CreatePlaylistAsync(_queueTracks.ToList());
    }

    private async Task CreatePlaylistAsync(IReadOnlyList<TrackItem> initialTracks)
    {
        var input = new TextBox { PlaceholderText = "Playlist name", Text = $"Playlist {_playlists.Count + 1}" };
        CheckBox? includeCheck = initialTracks.Count > 0
            ? new CheckBox
            {
                Content = $"Include {initialTracks.Count} selected track(s)",
                IsChecked = true
            }
            : null;
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(input);
        if (includeCheck != null) panel.Children.Add(includeCheck);

        var dlg = new ContentDialog
        {
            Title = "New playlist",
            Content = panel,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.RequestedTheme
        };
        DialogFooter.MainButtonOnRight(dlg);
        if (await dlg.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(input.Text)) return;

        var pl = new SavedPlaylist { Name = input.Text.Trim() };
        if (initialTracks.Count > 0 && includeCheck?.IsChecked != false)
        {
            foreach (var path in initialTracks.Select(t => t.Path).Distinct(StringComparer.OrdinalIgnoreCase))
                pl.TrackPaths.Add(path);
        }
        _playlists.Add(pl);
        _settings.Playlists.Add(pl);
        _settings.Save();
        PlaylistsEmptyHint.Visibility = Visibility.Collapsed;

        if (pl.TrackPaths.Count > 0)
        {
            PlaylistsList.SelectedItem = pl;
            ShowHudPill($"Created \"{pl.Name}\" ({pl.TrackCount} tracks)");
        }
        else
        {
            ShowHudPill($"Created \"{pl.Name}\" — drag or right-click songs to add");
        }
    }

    private async void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Never intercept keys while a ContentDialog or Flyout popup is open
        if (RootGrid.XamlRoot != null && VisualTreeHelper.GetOpenPopupsForXamlRoot(RootGrid.XamlRoot).Count > 0)
        {
            if (e.Key == VirtualKey.Escape && FocusVisualizerOverlay.Visibility == Visibility.Visible)
            {
                FocusVisualizerOverlay.Visibility = Visibility.Collapsed;
                SyncVisualizerStageVisibility();
                e.Handled = true;
            }
            return;
        }

        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool alt = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var focused = FocusManager.GetFocusedElement(RootGrid.XamlRoot);
        bool inTextBox = focused is TextBox;

        if (e.Key == VirtualKey.F11)
        {
            ToggleFocusVisualizer();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape && FocusVisualizerOverlay.Visibility == Visibility.Visible)
        {
            FocusVisualizerOverlay.Visibility = Visibility.Collapsed;
            SyncVisualizerStageVisibility();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.F1)
        {
            e.Handled = true;
            await ShowShortcutsHelpAsync();
        }
        else if (e.Key == VirtualKey.F2 && !inTextBox)
        {
            var selected = GetSelectedTracks();
            if (selected.Count > 0)
            {
                e.Handled = true;
                await OpenPropertiesDialogAsync(selected);
            }
        }
        else if (e.Key == VirtualKey.F5)
        {
            e.Handled = true;
            RescanBtn_Click(sender, e);
        }
        else if (ctrl && e.Key == VirtualKey.Z && !inTextBox)
        {
            e.Handled = true;
            await PerformUndoAsync();
        }
        else if ((ctrl && e.Key == VirtualKey.B) || (e.Key == VirtualKey.Tab && !inTextBox && !ctrl && !alt))
        {
            e.Handled = true;
            LeftPaneToggleBtn_Click(sender, e);
        }
        else if (e.Key == VirtualKey.Delete && !inTextBox)
        {
            var selected = GetSelectedTracks();
            if (selected.Count > 0)
            {
                e.Handled = true;
                if (_activePlaylist != null)
                    RemoveSelectionFromActivePlaylist(_activePlaylist, selected);
                else
                    await DeleteTracksToRecycleBinAsync(selected);
            }
        }
        else if ((e.Key == VirtualKey.Left || e.Key == VirtualKey.Right) && !inTextBox && focused is not Slider && _engine.Duration.TotalSeconds > 0)
        {
            e.Handled = true;
            double delta = e.Key == VirtualKey.Right ? 5.0 : -5.0;
            _engine.Seek(_engine.Position + TimeSpan.FromSeconds(delta));
        }
        else if (e.Key == VirtualKey.Space && !inTextBox)
        {
            PlayPauseBtn_Click(sender, e);
            e.Handled = true;
        }
        else if (ctrl && e.Key == VirtualKey.F)
        {
            if (_isMiniPlayer)
                SetMiniPlayerMode(false);
            SearchBox.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
        else if (ctrl && e.Key == VirtualKey.L)
        {
            LocateNowPlayingTrack();
            e.Handled = true;
        }
        else if (ctrl && e.Key == VirtualKey.M)
        {
            SetMiniPlayerMode(!_isMiniPlayer);
            e.Handled = true;
        }
        else if (ctrl && e.Key == VirtualKey.O)
        {
            OpenFolderBtn_Click(sender, e);
            e.Handled = true;
        }
        else if (!inTextBox && !ctrl && !alt && TryGetTypeJumpChar(e.Key, out char ch))
        {
            HandleTypeToJump(ch);
            e.Handled = true;
        }
    }

    private static bool TryGetTypeJumpChar(VirtualKey key, out char ch)
    {
        if (key >= VirtualKey.A && key <= VirtualKey.Z)
        {
            ch = (char)('A' + (key - VirtualKey.A));
            return true;
        }
        if (key >= VirtualKey.Number0 && key <= VirtualKey.Number9)
        {
            ch = (char)('0' + (key - VirtualKey.Number0));
            return true;
        }
        if (key >= VirtualKey.NumberPad0 && key <= VirtualKey.NumberPad9)
        {
            ch = (char)('0' + (key - VirtualKey.NumberPad0));
            return true;
        }
        ch = '\0';
        return false;
    }

    private void HandleTypeToJump(char ch)
    {
        if (_visibleTracks.Count == 0) return;

        var now = DateTime.UtcNow;
        if ((now - _lastTypeJumpUtc).TotalMilliseconds > 1200)
            _typeJumpBuffer = string.Empty;
        _lastTypeJumpUtc = now;
        _typeJumpBuffer += ch;

        string fieldLabel = _settings.SortColumn switch
        {
            "Artist" => "Artist",
            "Album" => "Album",
            "Year" => "Year",
            _ => "Title"
        };

        TrackItem? match = _visibleTracks.FirstOrDefault(t =>
        {
            string val = _settings.SortColumn switch
            {
                "Artist" => t.Artist,
                "Album" => t.Album,
                "Year" => t.YearText,
                _ => t.DisplayTitle
            };
            return val.StartsWith(_typeJumpBuffer, StringComparison.OrdinalIgnoreCase);
        });

        if (match == null && _typeJumpBuffer.Length > 1)
        {
            _typeJumpBuffer = ch.ToString();
            match = _visibleTracks.FirstOrDefault(t =>
            {
                string val = _settings.SortColumn switch
                {
                    "Artist" => t.Artist,
                    "Album" => t.Album,
                    "Year" => t.YearText,
                    _ => t.DisplayTitle
                };
                return val.StartsWith(_typeJumpBuffer, StringComparison.OrdinalIgnoreCase);
            });
        }

        ShowHudPill(match != null
            ? $"{fieldLabel}: \"{_typeJumpBuffer}\""
            : $"{fieldLabel}: \"{_typeJumpBuffer}\" (no match)");

        if (match != null)
        {
            SyncSelectionAcrossViews(match);
            UpdateInspectorForSelection(match);
            ScrollActiveViewToTrack(match, ScrollIntoViewAlignment.Leading);
        }
    }

    private async void ShowHudPill(string message)
    {
        _jumpPillCts?.Cancel();
        var cts = new CancellationTokenSource();
        _jumpPillCts = cts;
        TypeJumpText.Text = message;
        TypeJumpPill.Visibility = Visibility.Visible;
        try
        {
            await Task.Delay(1600, cts.Token);
            if (!cts.IsCancellationRequested)
                TypeJumpPill.Visibility = Visibility.Collapsed;
        }
        catch (TaskCanceledException) { }
    }
}
