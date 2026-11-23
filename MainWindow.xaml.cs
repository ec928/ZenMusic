using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
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
    private readonly DispatcherTimer _seekCoalesceTimer;

    private readonly List<TrackItem> _allTracks = new();
    private ObservableCollection<TrackItem> _visibleTracks = new();
    private readonly ObservableCollection<TrackItem> _queueTracks = new();
    private readonly List<TrackItem> _unshuffledQueue = new();
    private readonly ConcurrentDictionary<string, ImageSource?> _artThumbCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ImageSource> _artBitmapByHash = new(StringComparer.Ordinal);
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
    private readonly SemaphoreSlim _tagWriteGate = new(1, 1);
    private readonly object _pendingRatingsLock = new();
    private readonly Dictionary<string, (double? Rating, long Version)> _pendingRatingWrites = new(StringComparer.OrdinalIgnoreCase);
    private long _ratingWriteVersion;
    private int _flushingPendingRatings;
    private volatile bool _ratingFlushRequested;
    private int _releasingFileForTagWrite;
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
    private bool _isHoveringSeek;
    private bool _isHoveringVolume;
    private double _seekProgressRatio;
    private double _preMuteVolume = 70;
    private bool _selectedArtIsLight;
    private int _activeFocusLrcIndex = -1;
    private int _activeSelectedLrcIndex = -1;
    private string? _activePlayingPathForLyrics;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _sleepTimer;
    private DateTime? _sleepEndUtc;
    private bool _sleepAfterCurrentTrack;

    private static readonly SolidColorBrush TransparentRowBrush = new(Color.FromArgb(0, 0, 0, 0));
    private static readonly SolidColorBrush PlaybarGoldBrush = new(Color.FromArgb(255, 255, 200, 59));
    private static readonly SolidColorBrush PlaybarMutedIconBrush = new(Color.FromArgb(166, 255, 255, 255));
    private static readonly SolidColorBrush PlaybarLightAmberBrush = new(Color.FromArgb(255, 217, 119, 6));
    private static readonly SolidColorBrush PlaybarLightMutedIconBrush = new(Color.FromArgb(150, 26, 26, 26));
    private static readonly LinearGradientBrush InspectorOverlayBrush = new()
    {
        StartPoint = new Point(0.5, 0),
        EndPoint = new Point(0.5, 1),
        GradientStops =
        {
            // A dark shade that starts clear and only darkens under the text, so the cover stays
            // visible. Used on light covers too: a white wash over a photo reads as fog.
            new GradientStop { Color = Color.FromArgb(0, 0, 0, 0), Offset = 0.0 },
            new GradientStop { Color = Color.FromArgb(95, 0, 0, 0), Offset = 0.35 },
            new GradientStop { Color = Color.FromArgb(165, 0, 0, 0), Offset = 0.6 },
            new GradientStop { Color = Color.FromArgb(215, 0, 0, 0), Offset = 0.78 },
            new GradientStop { Color = Color.FromArgb(230, 0, 0, 0), Offset = 1.0 }
        }
    };
    // The white wash for light covers hid most of the cover; kept switchable for comparison.
    private const bool UseLightCoverOverlay = false;
    private static readonly LinearGradientBrush InspectorLightOverlayBrush = new()
    {
        StartPoint = new Point(0.5, 0),
        EndPoint = new Point(0.5, 1),
        GradientStops =
        {
            new GradientStop { Color = Color.FromArgb(0, 255, 255, 255), Offset = 0.0 },
            new GradientStop { Color = Color.FromArgb(175, 255, 255, 255), Offset = 0.32 },
            new GradientStop { Color = Color.FromArgb(208, 250, 250, 250), Offset = 0.62 },
            new GradientStop { Color = Color.FromArgb(242, 248, 248, 248), Offset = 1.0 }
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
    public static bool Not(bool value) => !value;
    public static Visibility BoolVis(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility NotNullVis(object? obj) => obj != null ? Visibility.Visible : Visibility.Collapsed;
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
        foreach (var t in _editingPlaylistTracks)
            t.IsNowPlaying = hasPath && string.Equals(t.Path, playingPath, StringComparison.OrdinalIgnoreCase);
    }

    private static double _titleStar = 2.2;
    private static double _artistStar = 1.15;
    private static double _albumStar = 1.35;

    private static bool IsColVisible(string name) => _columnVisible.TryGetValue(name, out bool v) && v;
    public static GridLength ColTitleWidth() => new(_titleStar, GridUnitType.Star);
    public static GridLength ColArtistWidth() => new(_artistStar, GridUnitType.Star);
    public static GridLength ColAlbumWidth() => new(_albumStar, GridUnitType.Star);
    public static GridLength ColGenreWidth() => IsColVisible("Genre") ? new GridLength(0.9, GridUnitType.Star) : new GridLength(0);
    public static GridLength ColYearWidth() => IsColVisible("Year") ? new GridLength(48) : new GridLength(0);
    public static GridLength ColRatingWidth() => IsColVisible("Rating") ? new GridLength(78) : new GridLength(0);
    public static GridLength ColLengthWidth() => IsColVisible("Length") ? new GridLength(56) : new GridLength(0);
    public static GridLength ColDateAddedWidth() => IsColVisible("DateAdded") ? new GridLength(64) : new GridLength(0);
    public static GridLength ColBitrateWidth() => IsColVisible("Bitrate") ? new GridLength(58) : new GridLength(0);
    public static Visibility ColRatingVis() => IsColVisible("Rating") ? Visibility.Visible : Visibility.Collapsed;

    private SavedPlaylist? _editingPlaylist;
    private readonly ObservableCollection<TrackItem> _editingPlaylistTracks = new();
    private bool _suppressRightComboChanged;
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

    private bool _hasLaunchFiles;

    public MainWindow(IReadOnlyList<string>? launchFiles = null)
    {
        _hasLaunchFiles = launchFiles is { Count: > 0 };
        InitializeComponent();

        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "ZenMusic.ico");
            if (File.Exists(iconPath))
                AppWindow.SetIcon(iconPath);
            string logoPath = Path.Combine(AppContext.BaseDirectory, "ZenMusic.png");
            if (File.Exists(logoPath))
                TitleBarLogoImage.Source = new BitmapImage(new Uri(logoPath));
        }
        catch { }

        try { SystemBackdrop = new MicaBackdrop(); } catch { }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);

        _settings = AppSettings.Load();
        TrackItem.ActiveDetailField = NormalizeDetailField(_settings.DetailColumnField);
        foreach (var kv in _settings.ColumnVisibility)
            _columnVisible[kv.Key] = kv.Value;

        _rightPaneOpen = _settings.RightPaneOpen;
        _inspectorArtMode = Math.Clamp(_settings.InspectorArtMode, 0, 2);
        SelectedInspectorScroll.MaxHeight = Math.Clamp(_settings.InspectorHeight, 130, 460);

        ApplyPaneWidths();
        ApplyColumnWidths();
        RestoreWindowBounds();
        WatchSystemColours();
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

        PlaybarVisualizerLeft.AttachAnalyzer(_engine.Fft);
        PlaybarVisualizer.AttachAnalyzer(_engine.Fft);
        WindowVisualizer.AttachAnalyzer(_engine.Fft);
        FocusVisualizer.AttachAnalyzer(_engine.Fft);
        ApplyVisualizerFromSettings();
        SyncVisualizerStageVisibility();

        VolumeSlider.Value = _settings.Volume;
        SeekRailCanvas.Invalidate();
        VolumeRailCanvas.Invalidate();
        _engine.SetVolume(_settings.Volume);
        UpdateShuffleButtonVisual();
        if (_settings.RepeatMode == 0 && !_settings.RepeatAll) _settings.RepeatMode = 0;
        UpdateRepeatButtonVisual();

        TracksListView.ItemsSource = _visibleTracks;
        AlbumsGridView.ItemsSource = _visibleTracks;
        QueueListView.ItemsSource = _queueTracks;
        SelectedSyncedLyricsList.ItemsSource = _selectedLrcLines;
        FocusSyncedLyricsList.ItemsSource = _focusLrcLines;

        foreach (var pl in _settings.Playlists) _playlists.Add(pl);
        SyncRightPanelList();
        UpdateSortHeaders();

        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _progressTimer.Tick += ProgressTimer_Tick;
        _seekCoalesceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _seekCoalesceTimer.Tick += (_, _) => FlushCoalescedSeek();

        SeekSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(SeekSlider_PointerPressed), true);
        SeekSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(SeekSlider_PointerReleased), true);
        SeekSlider.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(SeekSlider_PointerReleased), true);
        foreach (var starBtn in new[] { StarBtn1, StarBtn2, StarBtn3, StarBtn4, StarBtn5 })
            starBtn.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(StarBtn_PointerPressed), true);

        SearchHostGrid.SizeChanged += (_, _) => UpdateTitleBarPassthrough();
        SearchHostGrid.LayoutUpdated += (_, _) => UpdateTitleBarPassthrough();

        Closed += (_, _) =>
        {
            _progressTimer.Stop();
            _seekCoalesceTimer.Stop();
            SeekRailCanvas.RemoveFromVisualTree();
            VolumeRailCanvas.RemoveFromVisualTree();
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
            _cacheSaveTimer?.Stop();
            _engine.Dispose();
            FlushPendingRatingWritesSync();
            SaveLibraryCacheSnapshot();
        };

        if (!string.IsNullOrWhiteSpace(_settings.MusicFolder) && Directory.Exists(_settings.MusicFolder))
        {
            RecordRecentFolder(_settings.MusicFolder);
            StartFolderWatcher(_settings.MusicFolder);
            _ = OpenFolderFromCacheAsync(_settings.MusicFolder, _hasLaunchFiles ? launchFiles : null);
        }
        else if (_hasLaunchFiles && launchFiles != null)
        {
            _ = OpenLaunchFilesAsync(launchFiles);
        }
        _initialized = true;
    }

    private async Task OpenLaunchFilesAsync(IReadOnlyList<string> filePaths)
    {
        var byPath = new Dictionary<string, TrackItem>(_allTracks.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var t in _allTracks)
            byPath[t.Path] = t;

        var missingPaths = new List<string>();
        foreach (string p in filePaths)
        {
            if (!byPath.ContainsKey(p))
                missingPaths.Add(p);
        }

        if (missingPaths.Count > 0)
        {
            int startIndex = _allTracks.Count + 1;
            var addedTracks = await Task.Run(() =>
                missingPaths.Select((p, i) => MusicLibrary.ReadTrackMetadata(p, startIndex + i, false)).ToList());

            foreach (var t in addedTracks)
            {
                if (!byPath.ContainsKey(t.Path))
                {
                    _allTracks.Add(t);
                    byPath[t.Path] = t;
                }
            }
            RefreshVisibleTracks();
        }

        var tracksToPlay = new List<TrackItem>(filePaths.Count);
        foreach (string p in filePaths)
        {
            if (byPath.TryGetValue(p, out var item) && !tracksToPlay.Contains(item))
                tracksToPlay.Add(item);
        }

        if (tracksToPlay.Count == 0) return;

        EmptyStatePanel.Visibility = Visibility.Collapsed;
        PlayFromList(tracksToPlay, tracksToPlay[0]);
    }

    private async Task OpenFolderFromCacheAsync(string folder, IReadOnlyList<string>? launchFiles)
    {
        var cacheMap = await Task.Run(() => LibraryCache.Load(AppPaths.LibraryCachePath));
        var instant = LibraryCache.TracksInFolder(cacheMap, folder);
        if (instant.Count > 0)
        {
            _allTracks.Clear();
            _allTracks.AddRange(instant);
            RefreshVisibleTracks();
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            RestoreSessionQueue();
        }

        List<TrackItem>? launchedOutsideFolder = null;
        if (launchFiles != null)
        {
            await OpenLaunchFilesAsync(launchFiles);
            var wanted = new HashSet<string>(launchFiles, StringComparer.OrdinalIgnoreCase);
            launchedOutsideFolder = _allTracks.Where(t => wanted.Contains(t.Path)).ToList();
        }

        await ScanFolderAsync(folder, isStartupDelta: true, cacheMap);

        if (launchedOutsideFolder == null) return;
        var present = new HashSet<string>(_allTracks.Select(t => t.Path), StringComparer.OrdinalIgnoreCase);
        bool restored = false;
        foreach (var track in launchedOutsideFolder)
        {
            if (present.Add(track.Path))
            {
                _allTracks.Add(track);
                restored = true;
            }
        }
        if (restored) RefreshVisibleTracks();
    }

    private void RestoreSessionQueue()
    {
        if (_hasLaunchFiles || _settings.SavedQueuePaths.Count == 0 || _queueTracks.Count > 0) return;
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
        if (_editingPlaylist == null)
            QueueListView.SelectedIndex = idx;

        NowPlayingTitleText.Text = current.DisplayTitle;
        NowPlayingArtistText.Text = string.IsNullOrWhiteSpace(current.Artist) ? current.FileName : current.Artist;
        NowPlayingRatingCell.Value = current.Rating;
        FocusTitleText.Text = current.DisplayTitle;
        FocusArtistText.Text = NowPlayingArtistText.Text;
        FocusRatingCell.Value = current.Rating;
        _activePlayingPathForLyrics = current.Path;
        UpdateInspectorForSelection(GetPrimarySelectedTrack());

        var startPos = TimeSpan.FromSeconds(Math.Max(0, _settings.SavedPositionSeconds));
        _engine.LoadPausedFile(current.Path, startPos);

        double durSec = current.Duration.TotalSeconds;
        SyncPlaybackProgressUi(startPos, TimeSpan.FromSeconds(Math.Max(0, durSec)));

        _ = LoadPlayingArtAndSmtcAsync(current);
        _ = LoadFocusLyricsAsync(current.Path);
    }

    private void SaveSessionState()
    {
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

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _cacheSaveTimer;

    // A rating is written to the file with its timestamp kept, so the next start trusts the cache for it.
    // Saved only on close, a crash would bring the old rating back; this saves shortly after the change.
    private void SaveLibraryCacheSoon()
    {
        if (_cacheSaveTimer == null)
        {
            _cacheSaveTimer = DispatcherQueue.CreateTimer();
            _cacheSaveTimer.Interval = TimeSpan.FromSeconds(2);
            _cacheSaveTimer.IsRepeating = false;
            _cacheSaveTimer.Tick += (_, _) => SaveLibraryCacheSnapshot();
        }
        _cacheSaveTimer.Stop();
        _cacheSaveTimer.Start();
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

    private Windows.UI.ViewManagement.UISettings? _uiSettings;
    private Windows.UI.ViewManagement.AccessibilitySettings? _accessibility;
    private bool _highContrast;

    // Follows Windows for the "System" theme and for high contrast, both of which can change while the app is open.
    private void WatchSystemColours()
    {
        try
        {
            _uiSettings = new Windows.UI.ViewManagement.UISettings();
            _uiSettings.ColorValuesChanged += (_, _) => DispatcherQueue.TryEnqueue(OnSystemColoursChanged);
            _accessibility = new Windows.UI.ViewManagement.AccessibilitySettings();
            _highContrast = _accessibility.HighContrast;
            _accessibility.HighContrastChanged += (_, _) => DispatcherQueue.TryEnqueue(OnSystemColoursChanged);
        }
        catch { }
    }

    private void OnSystemColoursChanged()
    {
        bool highContrast = _highContrast;
        try { highContrast = _accessibility?.HighContrast ?? false; } catch { }
        bool wantDark = IsDarkTheme(_settings.Theme);
        bool isDark = RootGrid.RequestedTheme != ElementTheme.Light;
        if (highContrast == _highContrast && wantDark == isDark) return;
        _highContrast = highContrast;
        ApplyTheme(_settings.Theme);
    }

    private bool SystemPrefersDark()
    {
        try
        {
            var bg = (_uiSettings ??= new Windows.UI.ViewManagement.UISettings())
                .GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
            return bg.R + bg.G + bg.B < 384;
        }
        catch { return true; }
    }

    private bool IsDarkTheme(string theme) =>
        string.Equals(theme, "System", StringComparison.OrdinalIgnoreCase)
            ? SystemPrefersDark()
            : !string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase);

    /// <summary>Strength of the cover-colour wash: 0 = off, 1 = standard, 2 = vivid. Off in high contrast.</summary>
    private double TintScale => _highContrast ? 0 : Math.Clamp(_settings.CoverTint, 0, 200) / 100.0;

    private void ApplyTheme(string theme)
    {
        bool dark = IsDarkTheme(theme);
        RootGrid.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;

        foreach (var line in _selectedLrcLines)
            line.UseLightTheme = !dark;

        if (PlaybarVisualizerLeft != null) PlaybarVisualizerLeft.UseLightTheme = !dark;
        if (PlaybarVisualizer != null) PlaybarVisualizer.UseLightTheme = !dark;
        if (WindowVisualizer != null) WindowVisualizer.UseLightTheme = !dark;
        UpdateShuffleButtonVisual();
        UpdateRepeatButtonVisual();
        UpdateRightPaneButtonVisual();
        SeekRailCanvas?.Invalidate();
        VolumeRailCanvas?.Invalidate();
        // The wash is lighter in the light theme, so re-apply it at the new strength.
        if (_lastCoverTint.HasValue && PlaybarTintLayer != null) ApplyCoverTint(_lastCoverTint);
        else PlayingRowTint.Color = PlayingRowColor(null, !dark, 1, _highContrast);
        ApplyWorkspaceScrim();
        if (_inspectorTrack == null && SelectedTitleText != null)
        {
            _selectedArtIsLight = !dark;
            ApplySelectedInfoTheme();
        }

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
        bool wasOpen = RightSidebar.Visibility == Visibility.Visible && RightPaneCol.Width.Value > 0;
        RightPaneCol.Width = _rightPaneOpen ? new GridLength(316) : new GridLength(0);
        RightSidebar.Visibility = _rightPaneOpen ? Visibility.Visible : Visibility.Collapsed;
        UpdateRightPaneButtonVisual();
        ApplyColumnWidths();

        // Opening slides the pane in from the right; closing is immediate so the list reclaims the space at once.
        if (_rightPaneOpen && !wasOpen && _initialized)
        {
            var fade = RightSidebar.OpacityTransition;
            var slide = RightSidebar.TranslationTransition;
            RightSidebar.OpacityTransition = null;
            RightSidebar.TranslationTransition = null;
            RightSidebar.Opacity = 0;
            RightSidebar.Translation = new System.Numerics.Vector3(28, 0, 0);
            RightSidebar.OpacityTransition = fade;
            RightSidebar.TranslationTransition = slide;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                RightSidebar.Opacity = 1;
                RightSidebar.Translation = System.Numerics.Vector3.Zero;
            });
        }
    }

    // Above the table the header buttons sit over their columns. Above the tiles there are no columns,
    // so the same buttons close up into a "Sort by" strip at the left.
    private void ApplyColumnWidths()
    {
        bool tiles = IsTileMode;
        if (_rightPaneOpen)
        {
            _titleStar = 2.2;
            _artistStar = 1.15;
            _albumStar = 1.35;
        }
        else
        {
            // When the 316 px dock is hidden, spread the gained space toward Album and Artist
            // instead of letting Title absorb over half of it.
            _titleStar = 1.95;
            _artistStar = 1.2;
            _albumStar = 1.55;
        }

        GridLength Fit(GridLength table) => tiles && table.Value > 0 ? GridLength.Auto : table;
        HeaderColTitle.Width = tiles ? GridLength.Auto : ColTitleWidth();
        HeaderColArtist.Width = tiles ? GridLength.Auto : ColArtistWidth();
        HeaderColAlbum.Width = tiles ? GridLength.Auto : ColAlbumWidth();
        HeaderColGenre.Width = Fit(ColGenreWidth());
        HeaderColYear.Width = Fit(ColYearWidth());
        HeaderColRating.Width = Fit(ColRatingWidth());
        HeaderColLength.Width = Fit(ColLengthWidth());
        HeaderColDateAdded.Width = Fit(ColDateAddedWidth());
        HeaderColBitrate.Width = Fit(ColBitrateWidth());
        TableHeaderGrid.Padding = new Thickness(tiles ? 26 : 16, 0, 16, 0);
        TableHeaderGrid.BorderThickness = new Thickness(0, 0, 0, tiles ? 0 : 1);
        TileSortLabel.Visibility = tiles ? Visibility.Visible : Visibility.Collapsed;

        // Each button keeps the margin that lines it up with its column; in the strip they all get the same gap.
        foreach (var button in TableHeaderGrid.Children.OfType<Button>())
        {
            if (!_headerTableMargins.TryGetValue(button, out var tableMargin))
                _headerTableMargins[button] = tableMargin = button.Margin;
            button.Margin = !tiles ? tableMargin
                : ReferenceEquals(button, HeaderTitleBtn) ? new Thickness(50, 0, 8, 0)
                : new Thickness(0, 0, 8, 0);
        }

        if (!tiles && TracksListView.ItemsPanelRoot is Panel panel)
        {
            foreach (var child in panel.Children)
                ApplyRowTextColumnWidths(child as SelectorItem);
        }
    }

    private static void ApplyRowTextColumnWidths(SelectorItem? container)
    {
        if (container?.ContentTemplateRoot is not Grid row || row.ColumnDefinitions.Count < 3) return;
        if (Math.Abs(row.ColumnDefinitions[0].Width.Value - _titleStar) > 0.01)
            row.ColumnDefinitions[0].Width = ColTitleWidth();
        if (Math.Abs(row.ColumnDefinitions[1].Width.Value - _artistStar) > 0.01)
            row.ColumnDefinitions[1].Width = ColArtistWidth();
        if (Math.Abs(row.ColumnDefinitions[2].Width.Value - _albumStar) > 0.01)
            row.ColumnDefinitions[2].Width = ColAlbumWidth();
    }

    private readonly Dictionary<Button, Thickness> _headerTableMargins = new();

    // Tiles are sized so a whole number of them fills the row: the spare width goes into the covers.
    private double _tileWidth = 172;

    private void AlbumsGridView_SizeChanged(object sender, SizeChangedEventArgs e) => FitTilesToWidth();

    private void FitTilesToWidth()
    {
        double avail = AlbumsGridView.ActualWidth - AlbumsGridView.Padding.Left - AlbumsGridView.Padding.Right - 14;
        if (avail < 184) return;
        int columns = Math.Max(1, (int)(avail / 184));
        double tile = Math.Floor(avail / columns) - 12;
        var wrap = AlbumsGridView.ItemsPanelRoot as ItemsWrapGrid;
        bool cellSet = wrap == null || Math.Abs(wrap.ItemWidth - (tile + 12)) < 0.5;
        if (Math.Abs(tile - _tileWidth) < 0.5 && cellSet) return;
        _tileWidth = tile;
        if (wrap != null)
        {
            wrap.ItemWidth = tile + 12;
            wrap.ItemHeight = tile - 22 + 96 + 12;
        }
        if (AlbumsGridView.ItemsPanelRoot is not Panel panel) return;
        foreach (var child in panel.Children)
            ApplyTileSize(child as SelectorItem);
    }

    private void ApplyTileSize(SelectorItem? container)
    {
        if (container?.ContentTemplateRoot is not Grid tile) return;
        if (Math.Abs(tile.Width - _tileWidth) < 0.5) return;
        double art = _tileWidth - 22;
        tile.Width = _tileWidth;
        tile.Height = art + 96;
        if (tile.FindName("TileArt") is FrameworkElement cover)
        {
            cover.Width = art;
            cover.Height = art;
        }
    }

    private void ApplyViewModeVisuals()
    {
        bool isGrid = IsTileMode;
        TracksListView.Visibility = isGrid ? Visibility.Collapsed : Visibility.Visible;
        AlbumsGridView.Visibility = isGrid ? Visibility.Visible : Visibility.Collapsed;
        TableHeaderRowDef.Height = new GridLength(32);
        TableHeaderGrid.Visibility = Visibility.Visible;
        ApplyColumnWidths();
        UpdateSortHeaders();
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
        }, _settings.WasapiExclusive);
        _engine.SetEqualizerGains(_settings.EqualizerGains);
        int endpointCh = WasapiAudioEngine.GetEndpointChannelCount(_settings.OutputDeviceId);
        SpeakerBadgeText.Text = _settings.SpeakerLayout switch
        {
            SpeakerLayout.Stereo21 => endpointCh >= 6 ? "2.1 ch" : "2.1* ch",
            SpeakerLayout.Surround51Upmix => endpointCh >= 6 ? "5.1+ ch" : "5.1* ch",
            SpeakerLayout.Surround51Direct => endpointCh >= 6 ? "5.1 ch" : "5.1* ch",
            _ => "2.0 ch"
        };
        UpdateSpeakerBadgeTooltip();
    }

    /// <summary>The playbar's channel badge also says how much loudness leveling is doing to the playing track.</summary>
    private void UpdateSpeakerBadgeTooltip()
    {
        int endpointCh = WasapiAudioEngine.GetEndpointChannelCount(_settings.OutputDeviceId);
        string output = endpointCh >= 6 || _settings.SpeakerLayout == SpeakerLayout.Stereo20
            ? $"{endpointCh}-channel output"
            : $"{endpointCh}-channel output, layout folded down to stereo";

        string leveling;
        TrackItem? playing = _queueIndex >= 0 && _queueIndex < _queueTracks.Count ? _queueTracks[_queueIndex] : null;
        if (string.Equals(_settings.ReplayGainMode, "Off", StringComparison.OrdinalIgnoreCase))
            leveling = "Loudness leveling: off";
        else if (playing == null)
            leveling = "Loudness leveling: on";
        else if (!playing.ReplayGainTrackDb.HasValue)
            leveling = playing.IsCloudOnly ? "Loudness leveling: not measured (file is online only)" : "Loudness leveling: measuring this track...";
        else
        {
            double db = 20.0 * Math.Log10(Math.Max(1e-6, GetTrackReplayGainFactor(playing)));
            leveling = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "Loudness leveling: {0:+0.0;-0.0;0.0} dB on this track", db);
        }

        ToolTipService.SetToolTip(SpeakerBadgeBtn, $"{output}\n{leveling}\nClick to change device or speaker layout");
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

        PlaybarVisualizerLeft.Mode = mode;
        PlaybarVisualizer.Mode = mode;
        WindowVisualizer.Mode = mode;
        PlaybarVisualizerLeftHost.Visibility = Visibility.Visible;
        PlaybarVisualizerRightHost.Visibility = Visibility.Visible;
        FocusVisualizer.Mode = mode;
        PlaybarVisualizerLeft.IsAudioPlaying = _engine.IsPlaying;
        PlaybarVisualizer.IsAudioPlaying = _engine.IsPlaying;
        WindowVisualizer.IsAudioPlaying = _engine.IsPlaying;
        FocusVisualizer.IsAudioPlaying = _engine.IsPlaying;
        UpdateFocusVizLabel();
        SyncVisualizerStageVisibility();
    }

    private bool LargeVisualizerWanted()
    {
        if (_settings == null) return false;
        bool focusVisible = FocusVisualizerOverlay.Visibility == Visibility.Visible;
        bool library = !focusVisible && !_isMiniPlayer;
        return library && _settings.WindowVisualizerEnabled && _settings.VisualizerEnabled
            && !string.Equals(_settings.VisualizerMode, "Off", StringComparison.OrdinalIgnoreCase);
    }

    private void SyncVisualizerStageVisibility()
    {
        bool focusVisible = FocusVisualizerOverlay.Visibility == Visibility.Visible;
        bool barVisible = !focusVisible;
        bool largeOn = LargeVisualizerWanted();
        bool vizActive = _settings != null && _settings.VisualizerEnabled
            && !string.Equals(_settings.VisualizerMode, "Off", StringComparison.OrdinalIgnoreCase);
        PlaybarVisualizerLeft.IsStageVisible = barVisible;
        PlaybarVisualizer.IsStageVisible = barVisible;
        WindowVisualizer.IsStageVisible = largeOn;
        FocusVisualizer.IsStageVisible = focusVisible;
        UpdateFocusStageButtonVisual(focusVisible, largeOn, vizActive);
        ApplyWorkspaceScrim();
    }

    private void UpdateFocusStageButtonVisual(bool focusVisible, bool largeOn, bool vizActive)
    {
        if (FocusStageToggleIcon == null || FocusStageActiveDot == null) return;
        FocusStageActiveDot.Visibility = (vizActive && (focusVisible || largeOn)) ? Visibility.Visible : Visibility.Collapsed;
        FocusStageToggleIcon.Foreground = focusVisible
            ? (Brush)Application.Current.Resources["FavouriteStarBrush"]
            : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    }

    // The library backdrop visualizer sits behind the track list (Column 0).
    // While it is on, the center panel eases open slightly toward the bottom while keeping text legible.
    private void ApplyWorkspaceScrim()
    {
        bool light = RootGrid.RequestedTheme == ElementTheme.Light;
        if (!LargeVisualizerWanted() || _highContrast)
        {
            CenterWorkspaceGrid.Background = new SolidColorBrush(_highContrast
                ? Color.FromArgb(255, 0, 0, 0)
                : light ? Color.FromArgb(0xC0, 0xF7, 0xF7, 0xF7) : Color.FromArgb(0xB3, 0x12, 0x12, 0x12));
            RightSidebar.Background = new SolidColorBrush(_highContrast
                ? Color.FromArgb(255, 0, 0, 0)
                : light ? Color.FromArgb(0x99, 0xF3, 0xF3, 0xF3) : Color.FromArgb(0x99, 0x1C, 0x1C, 0x1C));
            return;
        }

        CenterWorkspaceGrid.Background = WorkspaceScrim(
            light ? Color.FromArgb(255, 247, 247, 247) : Color.FromArgb(255, 18, 18, 18),
            light ? (byte)0xF4 : (byte)0xF0,
            light ? (byte)0xD8 : (byte)0xCE,
            light ? (byte)0xA8 : (byte)0x9E);
        RightSidebar.Background = new SolidColorBrush(
            light ? Color.FromArgb(0x99, 0xF3, 0xF3, 0xF3) : Color.FromArgb(0x99, 0x1C, 0x1C, 0x1C));
    }

    private static LinearGradientBrush WorkspaceScrim(Color rgb, byte top, byte mid, byte low)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1)
        };
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(top, rgb.R, rgb.G, rgb.B), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(mid, rgb.R, rgb.G, rgb.B), Offset = 0.48 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(low, rgb.R, rgb.G, rgb.B), Offset = 1 });
        return brush;
    }

    private void UpdateFocusVizLabel()
    {
        FocusVizModeText.Text = FocusVisualizer.Mode switch
        {
            VisualizerMode.Waveform => "Silk Waveform",
            VisualizerMode.BarsAndWaveform => "Bars + Wave",
            VisualizerMode.Off => "Visualizer: Off",
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
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024
            };
            watcher.Created += OnWatchedFolderChanged;
            watcher.Deleted += OnWatchedFolderChanged;
            watcher.Changed += OnWatchedFolderChanged;
            watcher.Renamed += OnWatchedFolderRenamed;
            watcher.Error += OnWatchedFolderError;
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

    private DateTime _watcherRestartNotBeforeUtc;

    private void OnWatchedFolderError(object sender, ErrorEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(sender, _folderWatcher)) return;
            if (DateTime.UtcNow < _watcherRestartNotBeforeUtc) return;
            _watcherRestartNotBeforeUtc = DateTime.UtcNow.AddSeconds(2);
            string folder = _settings.MusicFolder ?? string.Empty;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
            try
            {
                _folderWatcher.EnableRaisingEvents = false;
                _folderWatcher.Dispose();
            }
            catch { }
            _folderWatcher = null;
            StartFolderWatcher(folder);
            _ = ScanFolderAsync(folder, isStartupDelta: true);
        });
    }

    private void QuietFolderWatcher(int seconds = 3)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        if (until > _suppressWatcherUntilUtc)
            _suppressWatcherUntilUtc = until;
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
                    if (next.IsCancellationRequested || DateTime.UtcNow < _suppressWatcherUntilUtc) return;
                    if (!string.IsNullOrWhiteSpace(_settings.MusicFolder) && Directory.Exists(_settings.MusicFolder))
                        _ = ScanFolderAsync(_settings.MusicFolder, isStartupDelta: true);
                });
            }
            catch (TaskCanceledException) { }
        });
    }

    private async Task ScanFolderAsync(string folder, bool isStartupDelta = false, IReadOnlyDictionary<string, CachedTrackEntry>? cacheMap = null)
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

        cacheMap ??= await Task.Run(() => LibraryCache.Load(AppPaths.LibraryCachePath));
        var tracks = await LibraryScanner.ScanFolderAsync(folder, cts.Token, batch =>
        {
            if (isStartupDelta && _allTracks.Count > 0) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (cts.IsCancellationRequested || myScan != _scanId) return;
                _allTracks.AddRange(batch);
                if (_activePlaylist == null && _activeFacet == "all" && string.IsNullOrWhiteSpace(SearchBox.Text))
                {
                    foreach (var t in batch)
                    {
                        t.IsEvenRow = (_visibleTracks.Count & 1) == 1;
                        _visibleTracks.Add(t);
                    }
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

        // Gains measured by the pre-0.7.15 meter are replaced quietly in the background.
        if (_rgScanCts == null &&
            !string.Equals(_settings.ReplayGainMode, "Off", StringComparison.OrdinalIgnoreCase) &&
            _allTracks.Any(t => !t.IsCloudOnly && t.ReplayGainIsStale))
        {
            _ = RunLoudnessAnalysisAsync(staleOnly: true);
        }
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

        string? playingPath = _activePlayingPathForLyrics ?? _engine.CurrentPath;
        bool hasPlaying = !string.IsNullOrEmpty(playingPath);
        for (int i = 0; i < filtered.Count; i++)
        {
            var t = filtered[i];
            t.IsNowPlaying = hasPlaying && string.Equals(t.Path, playingPath, StringComparison.OrdinalIgnoreCase);
            t.IsEvenRow = (i & 1) == 1;
        }

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
        if (!args.InRecycleQueue && ReferenceEquals(sender, AlbumsGridView))
        {
            FitTilesToWidth();
            ApplyTileSize(args.ItemContainer);
        }
        else if (!args.InRecycleQueue && ReferenceEquals(sender, TracksListView))
        {
            ApplyRowTextColumnWidths(args.ItemContainer);
        }
        if (args.InRecycleQueue || args.Item is not TrackItem track) return;
        if (track.CoverThumbnail != null || track.ArtLoadAttempted) return;
        if (!track.HasEmbeddedArt || track.IsCloudOnly)
        {
            track.ArtLoadAttempted = true;
            return;
        }

        string cacheKey = track.Path;
        if (_artThumbCache.TryGetValue(cacheKey, out var cachedImg))
        {
            track.ArtLoadAttempted = true;
            track.CoverThumbnail = cachedImg;
            return;
        }

        track.ArtLoadAttempted = true;

        // Rows and tiles that are only passing through during a fast scroll never reach the disk:
        // wait briefly, and give up if the container has already been handed to another track.
        await Task.Delay(120);
        if (!ReferenceEquals(args.ItemContainer?.Content, track))
        {
            track.ArtLoadAttempted = false;
            return;
        }

        string path = track.Path;
        var (bytes, hash) = await Task.Run(() =>
        {
            byte[]? b = MusicLibrary.ReadEmbeddedArtBytes(path);
            if (b == null || b.Length == 0) return ((byte[]?)null, string.Empty);
            string h = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b));
            return (b, h);
        });

        if (bytes == null || bytes.Length == 0)
        {
            _artThumbCache.TryAdd(cacheKey, null);
            return;
        }

        if (_artBitmapByHash.TryGetValue(hash, out var sharedBmp))
        {
            _artThumbCache[cacheKey] = sharedBmp;
            track.CoverThumbnail = sharedBmp;
            return;
        }

        var bmp = await CreateBitmapFromBytesAsync(bytes, decodePixelWidth: 180);
        if (bmp != null)
        {
            var finalBmp = _artBitmapByHash.GetOrAdd(hash, bmp);
            _artThumbCache[cacheKey] = finalBmp;
            track.CoverThumbnail = finalBmp;
        }
    }

    // Tiles stay quiet until pointed at: the card lifts slightly and its play button fades in.
    private void Tile_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement tile) return;
        tile.Translation = new System.Numerics.Vector3(0, -3, 0);
        if (FindDescendantButton(tile) is { } play) play.Opacity = 1;
    }

    private void Tile_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement tile) return;
        tile.Translation = System.Numerics.Vector3.Zero;
        if (FindDescendantButton(tile) is { } play) play.Opacity = 0;
    }

    private static Button? FindDescendantButton(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Button button) return button;
            if (FindDescendantButton(child) is { } found) return found;
        }
        return null;
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
        SyncRightPanelList();
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
            ("Album", true) => source.OrderBy(t => string.IsNullOrWhiteSpace(t.Album) ? 1 : 0).ThenBy(t => t.Album.Trim(), StringComparer.OrdinalIgnoreCase).ThenBy(t => t.DiscNumber > 0 ? t.DiscNumber : int.MaxValue).ThenBy(t => t.TrackNumber > 0 ? t.TrackNumber : int.MaxValue).ThenBy(t => t.Index),
            ("Album", false) => source.OrderByDescending(t => t.Album.Trim(), StringComparer.OrdinalIgnoreCase).ThenBy(t => t.DiscNumber > 0 ? t.DiscNumber : int.MaxValue).ThenBy(t => t.TrackNumber > 0 ? t.TrackNumber : int.MaxValue).ThenBy(t => t.Index),
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
            ("Track", true) => source.OrderBy(t => t.TrackNumber > 0 ? 0 : 1).ThenBy(t => t.DiscNumber > 0 ? t.DiscNumber : int.MaxValue).ThenBy(t => t.TrackNumber).ThenBy(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Index),
            ("Track", false) => source.OrderBy(t => t.TrackNumber > 0 ? 0 : 1).ThenByDescending(t => t.DiscNumber).ThenByDescending(t => t.TrackNumber).ThenBy(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Index),
            ("Codec", true) => source.OrderBy(t => string.IsNullOrWhiteSpace(t.Codec) ? 1 : 0).ThenBy(t => t.Codec, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Bitrate).ThenBy(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Index),
            ("Codec", false) => source.OrderByDescending(t => t.Codec, StringComparer.OrdinalIgnoreCase).ThenByDescending(t => t.Bitrate).ThenBy(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Index),
            ("SampleRate", true) => source.OrderBy(t => t.SampleRate > 0 ? 0 : 1).ThenBy(t => t.SampleRate).ThenBy(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Index),
            ("SampleRate", false) => source.OrderByDescending(t => t.SampleRate).ThenBy(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Index),
            ("Channels", true) => source.OrderBy(t => t.Channels).ThenBy(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Index),
            ("Channels", false) => source.OrderByDescending(t => t.Channels).ThenBy(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Index),
            ("ReplayGain", true) => source.OrderBy(t => t.ReplayGainTrackDb.HasValue ? 0 : 1).ThenBy(t => t.ReplayGainTrackDb ?? 0).ThenBy(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Index),
            ("ReplayGain", false) => source.OrderBy(t => t.ReplayGainTrackDb.HasValue ? 0 : 1).ThenByDescending(t => t.ReplayGainTrackDb ?? double.MinValue).ThenBy(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Index),
            ("Plays", true) => source.OrderBy(t => t.PlayCount).ThenBy(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Index),
            ("Plays", false) => source.OrderByDescending(t => t.PlayCount).ThenBy(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Index),
            ("LastPlayed", true) => source.OrderBy(t => t.LastPlayedUtc.HasValue ? 0 : 1).ThenBy(t => t.LastPlayedUtc ?? DateTime.MaxValue).ThenBy(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Index),
            ("LastPlayed", false) => source.OrderBy(t => t.LastPlayedUtc.HasValue ? 0 : 1).ThenByDescending(t => t.LastPlayedUtc ?? DateTime.MinValue).ThenBy(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Index),
            ("FileName", true) => source.OrderBy(t => t.FileName, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Index),
            ("FileName", false) => source.OrderByDescending(t => t.FileName, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Index),
            (_, false) => source.OrderByDescending(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Index),
            _ => source.OrderBy(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Index)
        };
    }

    private static readonly (string Key, string MenuLabel, string HeaderLabel)[] DetailDisplayFields =
    {
        ("Bitrate", "Bitrate", "Bitrate"),
        ("Track", "Track #", "Track"),
        ("Codec", "Codec", "Codec"),
        ("SampleRate", "Sample Rate", "Rate"),
        ("Channels", "Channels", "Ch"),
        ("ReplayGain", "ReplayGain", "Gain"),
        ("Plays", "Plays", "Plays"),
        ("LastPlayed", "Last Played", "Played")
    };

    private static readonly (string Key, string MenuLabel, string HeaderLabel)[] DetailSortFields =
    {
        ("Bitrate", "Bitrate", "Bitrate"),
        ("Track", "Track #", "Track"),
        ("Codec", "Codec", "Codec"),
        ("SampleRate", "Sample Rate", "Rate"),
        ("Channels", "Channels", "Ch"),
        ("ReplayGain", "ReplayGain", "Gain"),
        ("Plays", "Plays", "Plays"),
        ("LastPlayed", "Last Played", "Played")
    };

    private static string NormalizeDetailField(string? field)
    {
        foreach (var entry in DetailDisplayFields)
        {
            if (string.Equals(entry.Key, field, StringComparison.OrdinalIgnoreCase))
                return entry.Key;
        }
        return "Bitrate";
    }

    private static string GetDetailDisplayMenuLabel(string field)
    {
        foreach (var entry in DetailDisplayFields)
            if (string.Equals(entry.Key, field, StringComparison.Ordinal)) return entry.MenuLabel;
        return "Bitrate";
    }

    private static string GetDetailDisplayHeaderLabel(string field)
    {
        foreach (var entry in DetailDisplayFields)
            if (string.Equals(entry.Key, field, StringComparison.Ordinal)) return entry.HeaderLabel;
        return "Bitrate";
    }

    private static bool TryGetDetailSortMeta(string col, out string menuLabel, out string headerLabel)
    {
        foreach (var entry in DetailSortFields)
        {
            if (string.Equals(entry.Key, col, StringComparison.Ordinal))
            {
                menuLabel = entry.MenuLabel;
                headerLabel = entry.HeaderLabel;
                return true;
            }
        }
        menuLabel = string.Empty;
        headerLabel = string.Empty;
        return false;
    }

    private void TrimmedCell_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not TextBlock tb) return;
        ToolTipService.SetToolTip(tb, tb.IsTextTrimmed && !string.IsNullOrEmpty(tb.Text) ? tb.Text : null);
    }

    private void TrimmedCell_IsTextTrimmedChanged(TextBlock sender, IsTextTrimmedChangedEventArgs args)
    {
        ToolTipService.SetToolTip(sender, sender.IsTextTrimmed && !string.IsNullOrEmpty(sender.Text) ? sender.Text : null);
    }

    private void SortHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string col) return;
        ApplySortColumn(col);
    }

    private void HeaderDetailBtn_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();

        if (IsTileMode)
        {
            menu.Items.Add(new MenuFlyoutItem { Text = "Sort by", IsEnabled = false });
            foreach (var (key, menuLabel, _) in DetailSortFields)
            {
                bool active = string.Equals(_settings.SortColumn, key, StringComparison.Ordinal);
                string arrow = active ? (_settings.SortAscending ? " \u25B4" : " \u25BE") : string.Empty;
                var item = new MenuFlyoutItem { Text = $"{menuLabel}{arrow}" };
                if (active)
                    item.Icon = new FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Glyph = "\uE73E" };
                string captured = key;
                item.Click += (_, _) => ApplySortColumn(captured);
                menu.Items.Add(item);
            }
        }
        else
        {
            string activeField = TrackItem.ActiveDetailField;
            string activeLabel = GetDetailDisplayMenuLabel(activeField);
            bool sortedByActive = string.Equals(_settings.SortColumn, activeField, StringComparison.Ordinal);
            string arrow = sortedByActive ? (_settings.SortAscending ? " \u25B4" : " \u25BE") : string.Empty;
            var sortTrigger = new MenuFlyoutItem
            {
                Text = $"Sort by {activeLabel}{arrow}",
                Icon = new FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Glyph = "\uE8CB" }
            };
            sortTrigger.Click += (_, _) => ApplySortColumn(activeField);
            menu.Items.Add(sortTrigger);

            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(new MenuFlyoutItem { Text = "Show in column", IsEnabled = false });

            foreach (var (key, menuLabel, _) in DetailDisplayFields)
            {
                var radio = new RadioMenuFlyoutItem
                {
                    Text = menuLabel,
                    GroupName = "DetailDisplayField",
                    IsChecked = string.Equals(activeField, key, StringComparison.Ordinal)
                };
                string captured = key;
                radio.Click += (_, _) => SetDetailColumnField(captured);
                menu.Items.Add(radio);
            }
        }

        menu.ShowAt(HeaderDetailBtn, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
    }

    private void SetDetailColumnField(string field)
    {
        string normalized = NormalizeDetailField(field);
        if (string.Equals(TrackItem.ActiveDetailField, normalized, StringComparison.Ordinal) &&
            string.Equals(_settings.DetailColumnField, normalized, StringComparison.Ordinal))
            return;

        TrackItem.ActiveDetailField = normalized;
        _settings.DetailColumnField = normalized;
        RefreshDetailColumnDisplay();
        UpdateSortHeaders();
        _ = Task.Run(() => _settings.Save());
    }

    private void RefreshDetailColumnDisplay()
    {
        foreach (var t in _allTracks) t.NotifyDetailChanged();
        foreach (var t in _visibleTracks) t.NotifyDetailChanged();
        foreach (var t in _queueTracks) t.NotifyDetailChanged();
        foreach (var t in _editingPlaylistTracks) t.NotifyDetailChanged();
    }

    private void ApplySortColumn(string col)
    {
        if (string.Equals(_settings.SortColumn, col, StringComparison.Ordinal))
        {
            _settings.SortAscending = !_settings.SortAscending;
        }
        else
        {
            _settings.SortColumn = col;
            _settings.SortAscending = col is not ("Rating" or "Year" or "Bitrate" or "DateAdded"
                or "SampleRate" or "Channels" or "ReplayGain" or "Plays" or "LastPlayed");
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
            if (_editingPlaylist == null && _queueIndex >= 0)
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
            string label = col switch
            {
                "DateAdded" => "Date Added",
                "Bitrate" => TrackItem.ActiveDetailField == "Bitrate"
                    ? "Bitrate"
                    : $"Bitrate ({GetDetailDisplayMenuLabel(TrackItem.ActiveDetailField)})",
                _ => col
            };
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
            if (ReferenceEquals(child, HeaderDetailBtn))
            {
                string activeField = TrackItem.ActiveDetailField;
                string activeDisplay = GetDetailDisplayMenuLabel(activeField);
                string activeHeader = GetDetailDisplayHeaderLabel(activeField);

                if (IsTileMode)
                {
                    if (TryGetDetailSortMeta(_settings.SortColumn, out string sortMenuLabel, out string sortHeaderLabel))
                    {
                        string arrow = _settings.SortAscending ? "\u25B4" : "\u25BE";
                        HeaderDetailLabel.Text = $"{sortHeaderLabel} {arrow}";
                        ToolTipService.SetToolTip(child, $"Sorted by: {sortMenuLabel} ({(_settings.SortAscending ? "Ascending" : "Descending")})\nClick to choose sort");
                    }
                    else
                    {
                        HeaderDetailLabel.Text = activeHeader;
                        ToolTipService.SetToolTip(child, "Click to sort by Bitrate, Codec, Plays...");
                    }
                }
                else if (string.Equals(_settings.SortColumn, activeField, StringComparison.Ordinal))
                {
                    string arrow = _settings.SortAscending ? "\u25B4" : "\u25BE";
                    HeaderDetailLabel.Text = $"{activeHeader} {arrow}";
                    ToolTipService.SetToolTip(child, $"Showing & sorted by: {activeDisplay} ({(_settings.SortAscending ? "Ascending" : "Descending")})\nClick to change field or sort");
                }
                else if (TryGetDetailSortMeta(_settings.SortColumn, out string sortMenuLabel, out _))
                {
                    HeaderDetailLabel.Text = activeHeader;
                    ToolTipService.SetToolTip(child, $"Showing: {activeDisplay} · Sorted by: {sortMenuLabel}\nClick to change field or sort");
                }
                else
                {
                    HeaderDetailLabel.Text = activeHeader;
                    ToolTipService.SetToolTip(child, $"Showing: {activeDisplay}\nClick to change field or sort");
                }
                continue;
            }

            string label = tag switch
            {
                "Index" => "#",
                "Duration" => "Length",
                "DateAdded" => IsTileMode ? "Date Added" : "Added",
                _ => tag
            };
            child.Content = string.Equals(tag, _settings.SortColumn, StringComparison.Ordinal)
                ? $"{label} {(_settings.SortAscending ? "\u25B4" : "\u25BE")}"
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

    private void ScopeClearBtn_Click(object sender, RoutedEventArgs e)
    {
        _activeAlbumFilter = null;
        _activeArtistFilter = null;
        _activeHygiene = null;
        _activePlaylist = null;
        _activeCustomFilter = null;
        _activeFacet = "all";
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
        _activePlaylist = null;
        _activeCustomFilter = null;
        _activeArtistFilter = null;
        _activeAlbumFilter = null;
        _activeHygiene = key;
        RefreshVisibleTracks();
    }

    private void OpenRightPanel()
    {
        if (_isMiniPlayer) SetMiniPlayerMode(false);
        if (_rightPaneOpen) return;
        _rightPaneOpen = true;
        _settings.RightPaneOpen = true;
        ApplyPaneWidths();
        _settings.Save();
    }

    private void RefreshRightListModeCombo()
    {
        _suppressRightComboChanged = true;
        try
        {
            RightListModeCombo.Items.Clear();
            string qLabel = _queueTracks.Count > 0 ? $"Play Queue ({_queueTracks.Count:N0})" : "Play Queue";
            var qItem = new ComboBoxItem { Content = qLabel, Tag = "queue" };
            RightListModeCombo.Items.Add(qItem);

            ComboBoxItem selectedItem = qItem;
            foreach (var pl in _playlists)
            {
                var item = new ComboBoxItem { Content = $"\u266B  {pl.Name} ({pl.TrackCount})", Tag = pl };
                RightListModeCombo.Items.Add(item);
                if (pl == _editingPlaylist)
                    selectedItem = item;
            }
            RightListModeCombo.SelectedItem = selectedItem;
        }
        finally
        {
            _suppressRightComboChanged = false;
        }
    }

    private Dictionary<string, TrackItem> BuildTrackLookupByPath()
    {
        var byPath = new Dictionary<string, TrackItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _allTracks)
            byPath[t.Path] = t;
        return byPath;
    }

    private void SyncRightPanelList(bool refreshCombo = true)
    {
        string? playingPath = _activePlayingPathForLyrics ?? _engine.CurrentPath;
        bool hasPath = !string.IsNullOrEmpty(playingPath);

        bool prevSuppress = _suppressSelectionChanged;
        _suppressSelectionChanged = true;
        try
        {
            if (_editingPlaylist == null)
            {
                foreach (var t in _queueTracks)
                    t.IsNowPlaying = hasPath && string.Equals(t.Path, playingPath, StringComparison.OrdinalIgnoreCase);

                if (!ReferenceEquals(QueueListView.ItemsSource, _queueTracks))
                    QueueListView.ItemsSource = _queueTracks;
                RightListEmptyHint.Text = "Play queue is empty.\nDouble-click any song to play\nor drag tracks here.";
                RightListEmptyHint.Visibility = _queueTracks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                if (QueueListView.SelectedItem == null)
                {
                    var playingInQueue = (_queueIndex >= 0 && _queueIndex < _queueTracks.Count)
                        ? _queueTracks[_queueIndex]
                        : _queueTracks.FirstOrDefault(t => t.IsNowPlaying);
                    if (playingInQueue != null)
                    {
                        QueueListView.SelectedItem = playingInQueue;
                        QueueListView.ScrollIntoView(playingInQueue);
                    }
                }
            }
            else
            {
                var byPath = BuildTrackLookupByPath();
                var desiredTracks = new List<TrackItem>();
                foreach (string p in _editingPlaylist.TrackPaths)
                {
                    if (byPath.TryGetValue(p, out var item))
                    {
                        item.IsNowPlaying = hasPath && string.Equals(item.Path, playingPath, StringComparison.OrdinalIgnoreCase);
                        desiredTracks.Add(item);
                    }
                }

                if (!_editingPlaylistTracks.SequenceEqual(desiredTracks))
                {
                    _editingPlaylistTracks.Clear();
                    foreach (var item in desiredTracks)
                        _editingPlaylistTracks.Add(item);
                }

                if (!ReferenceEquals(QueueListView.ItemsSource, _editingPlaylistTracks))
                    QueueListView.ItemsSource = _editingPlaylistTracks;
                RightListEmptyHint.Text = $"\"{_editingPlaylist.Name}\" is empty.\nDrag songs here from your library\nor right-click songs \u2192 Add to playlist.";
                RightListEmptyHint.Visibility = _editingPlaylistTracks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                if (QueueListView.SelectedItem == null)
                {
                    var playingInPl = _editingPlaylistTracks.FirstOrDefault(t => t.IsNowPlaying);
                    if (playingInPl != null)
                    {
                        QueueListView.SelectedItem = playingInPl;
                        QueueListView.ScrollIntoView(playingInPl);
                    }
                }
            }
        }
        finally
        {
            _suppressSelectionChanged = prevSuppress;
        }

        if (refreshCombo)
            RefreshRightListModeCombo();
    }

    private void QueueListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged) return;
        if (QueueListView.SelectedItem is not TrackItem track) return;
        SyncSelectionAcrossViews(track);
        UpdateInspectorForSelection(track);
        ScrollActiveViewToTrack(track);
    }

    private void RightListModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressRightComboChanged) return;
        _editingPlaylist = (RightListModeCombo.SelectedItem as ComboBoxItem)?.Tag as SavedPlaylist;
        SyncRightPanelList(refreshCombo: false);
    }

    private void RightListPlayBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_editingPlaylist != null)
        {
            PlayPlaylistTracks(_editingPlaylist, QueueListView.SelectedItem as TrackItem);
        }
        else if (_queueTracks.Count > 0)
        {
            int idx = QueueListView.SelectedIndex >= 0 && QueueListView.SelectedIndex < _queueTracks.Count
                ? QueueListView.SelectedIndex
                : (_queueIndex >= 0 && _queueIndex < _queueTracks.Count ? _queueIndex : 0);
            PlayQueueIndex(idx);
        }
    }

    private void RightListMenuBtn_Click(object sender, RoutedEventArgs e)
        => ShowRightPanelManagementMenu(RightListMenuBtn, null, null);

    private void PlayPlaylistTracks(SavedPlaylist pl, TrackItem? startTrack = null)
    {
        var byPath = BuildTrackLookupByPath();
        var tracks = new List<TrackItem>();
        foreach (string p in pl.TrackPaths)
        {
            if (byPath.TryGetValue(p, out var item)) tracks.Add(item);
        }
        if (tracks.Count == 0)
        {
            ShowHudPill($"\"{pl.Name}\" has no tracks yet");
            return;
        }
        var first = startTrack != null && tracks.Contains(startTrack) ? startTrack : tracks[0];
        PlayFromList(tracks, first);
    }

    private async Task RenamePlaylistAsync(SavedPlaylist pl)
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
            else SyncRightPanelList();
        }
    }

    private void DuplicatePlaylist(SavedPlaylist source)
    {
        var copy = new SavedPlaylist
        {
            Name = $"{source.Name} (Copy)",
            TrackPaths = source.TrackPaths.ToList()
        };
        _playlists.Add(copy);
        _settings.Playlists.Add(copy);
        _settings.Save();
        _editingPlaylist = copy;
        SyncRightPanelList();
        ShowHudPill($"Duplicated as \"{copy.Name}\" ({copy.TrackCount} tracks)");
    }

    private void DeletePlaylistWithUndo(SavedPlaylist pl)
    {
        int oldIdx = _playlists.IndexOf(pl);
        _playlists.Remove(pl);
        _settings.Playlists.Remove(pl);
        _settings.Save();
        if (_editingPlaylist == pl)
            _editingPlaylist = null;
        if (_activePlaylist == pl)
        {
            _activePlaylist = null;
            RefreshVisibleTracks();
        }
        else
        {
            SyncRightPanelList();
        }
        PushUndo(() =>
        {
            int insertIdx = Math.Clamp(oldIdx, 0, _playlists.Count);
            _playlists.Insert(insertIdx, pl);
            _settings.Playlists.Insert(insertIdx, pl);
            _settings.Save();
            _editingPlaylist = pl;
            SyncRightPanelList();
            ShowHudPill($"Restored playlist \"{pl.Name}\"");
            return Task.CompletedTask;
        });
        ShowHudPill($"Deleted \"{pl.Name}\" (Ctrl+Z to undo)");
    }

    private void ClearPlaylistTracksWithUndo(SavedPlaylist pl)
    {
        if (pl.TrackPaths.Count == 0) return;
        var snapshot = pl.TrackPaths.ToList();
        pl.TrackPaths.Clear();
        pl.NotifyTracksChanged();
        _settings.Save();
        if (_activePlaylist == pl) RefreshVisibleTracks();
        else SyncRightPanelList();
        PushUndo(() =>
        {
            pl.TrackPaths.Clear();
            pl.TrackPaths.AddRange(snapshot);
            pl.NotifyTracksChanged();
            _settings.Save();
            if (_activePlaylist == pl) RefreshVisibleTracks();
            else SyncRightPanelList();
            ShowHudPill($"Undone: Restored tracks to \"{pl.Name}\"");
            return Task.CompletedTask;
        });
        ShowHudPill($"Cleared \"{pl.Name}\" (Ctrl+Z to undo)");
    }

    private void RightPaneToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        _rightPaneOpen = !_rightPaneOpen;
        _settings.RightPaneOpen = _rightPaneOpen;
        ApplyPaneWidths();
        _settings.Save();
    }

    private void UpdateRightPaneButtonVisual()
    {
        if (RightPaneToggleIcon == null || RightPaneToggleBtn == null) return;
        bool isLight = RootGrid.RequestedTheme == ElementTheme.Light;
        RightPaneToggleIcon.Foreground = _rightPaneOpen
            ? (isLight ? PlaybarLightAmberBrush : PlaybarGoldBrush)
            : (isLight ? PlaybarLightMutedIconBrush : PlaybarMutedIconBrush);
        ToolTipService.SetToolTip(RightPaneToggleBtn, _rightPaneOpen
            ? "Hide Queue & Track Panel (Ctrl+B)"
            : "Show Queue & Track Panel (Ctrl+B)");
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

    private void QueueListView_DragOver(object sender, DragEventArgs e)
    {
        if (_draggedTracks is not { Count: > 0 }) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = _editingPlaylist != null
            ? $"Add {_draggedTracks.Count} track(s) to \"{_editingPlaylist.Name}\""
            : $"Enqueue {_draggedTracks.Count} track(s)";
        e.DragUIOverride.IsCaptionVisible = true;
    }

    private void QueueListView_Drop(object sender, DragEventArgs e)
    {
        var tracks = _draggedTracks;
        _draggedTracks = null;
        if (tracks is not { Count: > 0 }) return;

        if (_editingPlaylist != null)
        {
            int added = AddTracksToPlaylist(_editingPlaylist, tracks);
            ShowHudPill(added > 0
                ? $"Added {added} track(s) to \"{_editingPlaylist.Name}\""
                : $"Already in \"{_editingPlaylist.Name}\"");
            return;
        }

        foreach (var t in tracks)
            _queueTracks.Add(t);
        UpdateQueueHeader();
        ShowHudPill($"Added {tracks.Count} track(s) to queue");
    }

    private void QueueListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (_editingPlaylist != null)
        {
            _editingPlaylist.TrackPaths.Clear();
            foreach (var t in _editingPlaylistTracks)
                _editingPlaylist.TrackPaths.Add(t.Path);
            _editingPlaylist.NotifyTracksChanged();
            _settings.Save();
            if (_activePlaylist == _editingPlaylist) RefreshVisibleTracks();
            else RefreshRightListModeCombo();
            return;
        }

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
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var previous = new List<(TrackItem Sample, string Path, double? OldRating)>();
        foreach (var t in targets)
        {
            if (seenPaths.Add(t.Path))
                previous.Add((t, t.Path, t.Rating));
            foreach (var peer in EnumerateMatchingTrackItems(t))
                peer.Rating = newRating;
        }

        if ((GetPrimarySelectedTrack() ?? _inspectorTrack) is TrackItem sel &&
            seenPaths.Contains(sel.Path))
            UpdateSelectedRatingVisuals(newRating);
        SyncPlayingTrackRating(targets, newRating);
        UpdateSidebarCounts();

        QuietFolderWatcher();
        if (pushUndo)
        {
            PushUndo(() =>
            {
                QuietFolderWatcher();
                foreach (var (sample, _, oldR) in previous)
                {
                    foreach (var peer in EnumerateMatchingTrackItems(sample))
                        peer.Rating = oldR;
                }
                if ((GetPrimarySelectedTrack() ?? _inspectorTrack) is TrackItem s)
                {
                    int idx = previous.FindIndex(x => string.Equals(x.Path, s.Path, StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0)
                        UpdateSelectedRatingVisuals(previous[idx].OldRating);
                }
                if (_queueIndex >= 0 && _queueIndex < _queueTracks.Count)
                {
                    var p = _queueTracks[_queueIndex];
                    NowPlayingRatingCell.Value = p.Rating;
                    FocusRatingCell.Value = p.Rating;
                }
                UpdateSidebarCounts();
                EnqueueRatingWrites(previous.Select(x => (x.Path, x.OldRating)));
                SaveLibraryCacheSoon();
                ShowHudPill("Undone: Star rating");
                return Task.CompletedTask;
            });
        }

        EnqueueRatingWrites(previous.Select(x => (x.Path, newRating)));
        SaveLibraryCacheSoon();
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

    private static bool IsInsideElement(DependencyObject? source, DependencyObject target)
    {
        while (source != null)
        {
            if (ReferenceEquals(source, target)) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
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
        var tappedTrack = (e.OriginalSource as FrameworkElement)?.DataContext as TrackItem ?? QueueListView.SelectedItem as TrackItem;
        if (_editingPlaylist != null)
        {
            if (tappedTrack != null && _editingPlaylistTracks.Contains(tappedTrack))
            {
                PlayPlaylistTracks(_editingPlaylist, tappedTrack);
                RevealTrackInMainList(tappedTrack);
            }
            return;
        }

        int qIdx = tappedTrack != null ? _queueTracks.IndexOf(tappedTrack) : QueueListView.SelectedIndex;
        if (qIdx >= 0 && qIdx < _queueTracks.Count)
        {
            var track = _queueTracks[qIdx];
            PlayQueueIndex(qIdx);
            RevealTrackInMainList(track);
        }
    }

    private void QueueListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var hit = (e.OriginalSource as FrameworkElement)?.DataContext as TrackItem;
        if (hit != null && !QueueListView.SelectedItems.Contains(hit))
            QueueListView.SelectedItem = hit;

        ShowRightPanelManagementMenu(QueueListView, e.GetPosition(QueueListView), hit);
    }

    private void RemoveSelectedFromRightQueue(IReadOnlyList<TrackItem> selected)
    {
        if (selected.Count == 0) return;
        string? playingPath = _queueIndex >= 0 && _queueIndex < _queueTracks.Count ? _queueTracks[_queueIndex].Path : null;
        var removeSet = new HashSet<TrackItem>(selected);
        for (int i = _queueTracks.Count - 1; i >= 0; i--)
        {
            if (removeSet.Contains(_queueTracks[i]))
                _queueTracks.RemoveAt(i);
        }
        _queueIndex = playingPath != null
            ? _queueTracks.Select((t, i) => (t, i)).FirstOrDefault(x => string.Equals(x.t.Path, playingPath, StringComparison.OrdinalIgnoreCase), (null!, -1)).i
            : -1;
        if (_queueTracks.Count == 0) _queueIndex = -1;
        else if (_queueIndex < 0) _queueIndex = Math.Min(0, _queueTracks.Count - 1);
        UpdateQueueHeader();
        ShowHudPill($"Removed {selected.Count} track(s) from queue");
    }

    private void ShowRightPanelManagementMenu(FrameworkElement target, Point? pos, TrackItem? hit = null)
    {
        var menu = new MenuFlyout();
        var rightSel = QueueListView.SelectedItems.OfType<TrackItem>().ToList();
        if (rightSel.Count == 0 && hit != null) rightSel.Add(hit);

        if (_editingPlaylist != null)
        {
            var pl = _editingPlaylist;
            if (hit != null)
            {
                var playHere = new MenuFlyoutItem { Text = "Play from here" };
                playHere.Click += (_, _) => PlayPlaylistTracks(pl, hit);
                menu.Items.Add(playHere);

                var showInLib = new MenuFlyoutItem { Text = "Show in library" };
                showInLib.Click += (_, _) => RevealTrackInMainList(hit);
                menu.Items.Add(showInLib);
            }
            else
            {
                var playPl = new MenuFlyoutItem { Text = $"Play \"{pl.Name}\"" };
                playPl.Click += (_, _) => PlayPlaylistTracks(pl);
                menu.Items.Add(playPl);
            }

            if (rightSel.Count > 0)
            {
                var removeFromPl = new MenuFlyoutItem { Text = $"Remove {rightSel.Count} selected track(s) from \"{pl.Name}\"" };
                removeFromPl.Click += (_, _) => RemoveTracksFromPlaylist(pl, rightSel);
                menu.Items.Add(removeFromPl);
            }

            var mainSel = GetSelectedTracks();
            if (mainSel.Count > 0)
            {
                var addLibSel = new MenuFlyoutItem { Text = $"Add {mainSel.Count} selected library track(s) to \"{pl.Name}\"" };
                addLibSel.Click += (_, _) =>
                {
                    int added = AddTracksToPlaylist(pl, mainSel);
                    ShowHudPill(added > 0 ? $"Added {added} track(s) to \"{pl.Name}\"" : $"Already in \"{pl.Name}\"");
                };
                menu.Items.Add(addLibSel);
            }

            var toggleMainScope = new MenuFlyoutItem
            {
                Text = _activePlaylist == pl ? "Show all music in main table" : $"Show only \"{pl.Name}\" in main table"
            };
            toggleMainScope.Click += (_, _) =>
            {
                if (_activePlaylist == pl)
                {
                    _activePlaylist = null;
                }
                else
                {
                    _activeCustomFilter = null;
                    _activeHygiene = null;
                    _activeArtistFilter = null;
                    _activeAlbumFilter = null;
                    _activePlaylist = pl;
                }
                RefreshVisibleTracks();
            };
            menu.Items.Add(toggleMainScope);

            menu.Items.Add(new MenuFlyoutSeparator());

            var newPl = new MenuFlyoutItem { Text = "New playlist..." };
            newPl.Click += async (_, _) => await CreatePlaylistAsync(GetSelectedTracks());
            menu.Items.Add(newPl);

            var importPl = new MenuFlyoutItem { Text = "Import .m3u / .m3u8..." };
            importPl.Click += ImportPlaylistBtn_Click;
            menu.Items.Add(importPl);

            var smartMix = new MenuFlyoutItem { Text = "Custom Smart Mix..." };
            smartMix.Click += async (_, _) => await OpenCustomSmartMixDialogAsync();
            menu.Items.Add(smartMix);

            var renamePl = new MenuFlyoutItem { Text = $"Rename \"{pl.Name}\"..." };
            renamePl.Click += async (_, _) => await RenamePlaylistAsync(pl);
            menu.Items.Add(renamePl);

            var dupPl = new MenuFlyoutItem { Text = $"Duplicate \"{pl.Name}\"" };
            dupPl.Click += (_, _) => DuplicatePlaylist(pl);
            menu.Items.Add(dupPl);

            var exportPl = new MenuFlyoutItem { Text = $"Export \"{pl.Name}\" (.m3u8)..." };
            exportPl.Click += async (_, _) => await ExportPlaylistM3u8Async(pl);
            menu.Items.Add(exportPl);

            menu.Items.Add(new MenuFlyoutSeparator());

            if (pl.TrackPaths.Count > 0)
            {
                var clearPl = new MenuFlyoutItem { Text = $"Clear all tracks from \"{pl.Name}\"" };
                clearPl.Click += (_, _) => ClearPlaylistTracksWithUndo(pl);
                menu.Items.Add(clearPl);
            }

            var delPl = new MenuFlyoutItem { Text = $"Delete \"{pl.Name}\"" };
            delPl.Click += (_, _) => DeletePlaylistWithUndo(pl);
            menu.Items.Add(delPl);
        }
        else
        {
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
            }

            if (rightSel.Count > 0)
            {
                var saveSelPl = new MenuFlyoutItem { Text = $"Save {rightSel.Count} selected track(s) as playlist..." };
                saveSelPl.Click += async (_, _) => await CreatePlaylistAsync(rightSel);
                menu.Items.Add(saveSelPl);

                if (_playlists.Count > 0)
                {
                    var addSub = new MenuFlyoutSubItem { Text = $"Add {rightSel.Count} selected to playlist" };
                    foreach (var pl in _playlists)
                    {
                        var item = new MenuFlyoutItem { Text = $"{pl.Name} ({pl.TrackCount})" };
                        item.Click += (_, _) =>
                        {
                            int added = AddTracksToPlaylist(pl, rightSel);
                            ShowHudPill(added > 0 ? $"Added {added} to \"{pl.Name}\"" : $"Already in \"{pl.Name}\"");
                        };
                        addSub.Items.Add(item);
                    }
                    menu.Items.Add(addSub);
                }

                var removeQ = new MenuFlyoutItem { Text = $"Remove {rightSel.Count} selected from queue" };
                removeQ.Click += (_, _) => RemoveSelectedFromRightQueue(rightSel);
                menu.Items.Add(removeQ);
            }

            if (menu.Items.Count > 0)
                menu.Items.Add(new MenuFlyoutSeparator());

            if (_queueTracks.Count > 0)
            {
                var curateQ = new MenuFlyoutItem { Text = "Curate playlist from queue..." };
                curateQ.Click += async (_, _) => await OpenQueueToPlaylistDialogAsync();
                menu.Items.Add(curateQ);
            }

            var newPl = new MenuFlyoutItem { Text = "New playlist..." };
            newPl.Click += async (_, _) => await CreatePlaylistAsync(GetSelectedTracks());
            menu.Items.Add(newPl);

            var importPl = new MenuFlyoutItem { Text = "Import .m3u / .m3u8..." };
            importPl.Click += ImportPlaylistBtn_Click;
            menu.Items.Add(importPl);

            var smartMix = new MenuFlyoutItem { Text = "Custom Smart Mix..." };
            smartMix.Click += async (_, _) => await OpenCustomSmartMixDialogAsync();
            menu.Items.Add(smartMix);

            if (_queueTracks.Count > 0)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
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
            }
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(BuildSleepTimerSubmenu());

        AddMenuIcons(menu);
        if (pos.HasValue)
            menu.ShowAt(target, pos.Value);
        else
            menu.ShowAt(target);
    }

    private void UpdateQueueHeader() => SyncRightPanelList();

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

    private readonly System.Threading.SemaphoreSlim _replayGainScanLock = new(1, 1);

    private float GetTrackReplayGainFactor(TrackItem? track)
    {
        if (track == null) return 1.0f;
        return ReplayGainAnalyzer.ComputeLinearFactor(
            track.ReplayGainTrackDb,
            track.ReplayGainTrackPeak,
            track.ReplayGainAlbumDb,
            track.ReplayGainAlbumPeak,
            _settings.ReplayGainMode,
            _settings.ReplayGainPreampDb);
    }

    private void ApplyLiveReplayGain()
    {
        if (_queueIndex >= 0 && _queueIndex < _queueTracks.Count)
        {
            var playing = _queueTracks[_queueIndex];
            _engine.SetReplayGainFactor(GetTrackReplayGainFactor(playing));
            if (NeedsLoudnessMeasure(playing))
                _ = EnsureTrackLoudnessMeasuredAsync(playing, applyLiveIfPlaying: true);
        }
        else
        {
            _engine.SetReplayGainFactor(1.0f);
        }
        UpdateSpeakerBadgeTooltip();
    }

    private static bool NeedsLoudnessMeasure(TrackItem t) =>
        !t.IsCloudOnly && (!t.ReplayGainTrackDb.HasValue || t.ReplayGainIsStale);

    /// <summary>Stores a fresh measurement on every item for that file and re-levels it if it is playing.</summary>
    private void ApplyLoudnessMeasurement(TrackItem track, ReplayGainAnalysis rg)
    {
        foreach (var peer in EnumerateMatchingTrackItems(track))
        {
            peer.ReplayGainTrackDb = rg.TrackGainDb;
            peer.ReplayGainTrackPeak = rg.TrackPeak;
            peer.ReplayGainFromTag = false;
            peer.ReplayGainMeterVersion = ReplayGainAnalyzer.MeterVersion;
            peer.NotifyDetailChanged();
        }
        if (string.Equals(_engine.CurrentPath, track.Path, StringComparison.OrdinalIgnoreCase))
        {
            _engine.SetReplayGainFactor(GetTrackReplayGainFactor(track));
            UpdateSpeakerBadgeTooltip();
        }
    }

    private async Task EnsureTrackLoudnessMeasuredAsync(TrackItem track, bool applyLiveIfPlaying)
    {
        if (!NeedsLoudnessMeasure(track)) return;
        if (string.Equals(_settings.ReplayGainMode, "Off", StringComparison.OrdinalIgnoreCase)) return;

        await _replayGainScanLock.WaitAsync();
        try
        {
            if (NeedsLoudnessMeasure(track) && File.Exists(track.Path))
            {
                var measured = await Task.Run(() => ReplayGainAnalyzer.AnalyzeFile(track.Path));
                if (measured is ReplayGainAnalysis rg)
                {
                    ApplyLoudnessMeasurement(track, rg);
                    SaveLibraryCacheSnapshot();
                }
            }
        }
        finally
        {
            _replayGainScanLock.Release();
        }

        if (applyLiveIfPlaying &&
            string.Equals(_engine.CurrentPath, track.Path, StringComparison.OrdinalIgnoreCase))
        {
            _engine.SetReplayGainFactor(GetTrackReplayGainFactor(track));
            UpdateSpeakerBadgeTooltip();
        }
    }

    private int _rgScanDone;
    private int _rgScanTotal;
    /// <summary>Set by the open Settings panel to refresh its Analyze row while a pass runs.</summary>
    private Action? _rgScanProgressChanged;

    /// <summary>
    /// Measures every local track that has no gain (or, with <paramref name="staleOnly"/>, only
    /// gains from an older meter). Files are only read. Cancel with <see cref="_rgScanCts"/>.
    /// </summary>
    private async Task RunLoudnessAnalysisAsync(bool staleOnly)
    {
        if (_rgScanCts != null) return;
        var pending = _allTracks
            .Where(t => staleOnly ? !t.IsCloudOnly && t.ReplayGainIsStale : NeedsLoudnessMeasure(t))
            .GroupBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        if (pending.Count == 0) return;

        var cts = new CancellationTokenSource();
        _rgScanCts = cts;
        _rgScanDone = 0;
        _rgScanTotal = pending.Count;
        _rgScanProgressChanged?.Invoke();
        try
        {
            await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cts.Token }, async (t, ct) =>
            {
                if (ct.IsCancellationRequested || !File.Exists(t.Path)) return;
                var res = await Task.Run(() => ReplayGainAnalyzer.AnalyzeFile(t.Path), ct);
                int curDone = Interlocked.Increment(ref _rgScanDone);
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (res is ReplayGainAnalysis rg) ApplyLoudnessMeasurement(t, rg);
                    _rgScanProgressChanged?.Invoke();
                    if (curDone % 25 == 0) SaveLibraryCacheSnapshot();
                });
            });
        }
        catch (OperationCanceledException) { }
        finally
        {
            _rgScanCts = null;
            cts.Dispose();
            SaveLibraryCacheSnapshot();
            _rgScanProgressChanged?.Invoke();
        }
    }

    private void PlayQueueIndex(int index)
    {
        if (index < 0 || index >= _queueTracks.Count) return;

        _queueIndex = index;
        var track = _queueTracks[index];
        _activePlayingPathForLyrics = track.Path;
        bool started = _engine.PlayFile(track.Path, GetTrackReplayGainFactor(track));
        _ = FlushPendingRatingWritesAsync();
        SetNowPlayingTrack(track.Path);
        UpdateSpeakerBadgeTooltip();
        if (started)
        {
            track.PlayCount++;
            track.LastPlayedUtc = DateTime.UtcNow;
        }

        if (!string.Equals(_settings.ReplayGainMode, "Off", StringComparison.OrdinalIgnoreCase))
        {
            _ = EnsureTrackLoudnessMeasuredAsync(track, applyLiveIfPlaying: true);
            if (index + 1 < _queueTracks.Count && NeedsLoudnessMeasure(_queueTracks[index + 1]))
                _ = EnsureTrackLoudnessMeasuredAsync(_queueTracks[index + 1], applyLiveIfPlaying: false);
        }

        UpdateQueueHeader();
        bool prevSuppress = _suppressSelectionChanged;
        _suppressSelectionChanged = true;
        try
        {
            if (_editingPlaylist == null)
            {
                QueueListView.SelectedIndex = index;
                QueueListView.ScrollIntoView(track);
            }
            else
            {
                int plIdx = _editingPlaylistTracks.IndexOf(track);
                if (plIdx >= 0)
                {
                    QueueListView.SelectedIndex = plIdx;
                    QueueListView.ScrollIntoView(_editingPlaylistTracks[plIdx]);
                }
                else
                {
                    QueueListView.SelectedItem = null;
                }
            }
        }
        finally
        {
            _suppressSelectionChanged = prevSuppress;
        }

        SyncSelectionAcrossViews(track);
        UpdateInspectorForSelection(track);
        ScrollActiveViewToTrack(track);

        NowPlayingTitleText.Text = track.DisplayTitle;
        NowPlayingArtistText.Text = string.IsNullOrWhiteSpace(track.Artist) ? track.FileName : track.Artist;
        NowPlayingRatingCell.Value = track.Rating;
        FocusTitleText.Text = track.DisplayTitle;
        FocusArtistText.Text = NowPlayingArtistText.Text;
        FocusRatingCell.Value = track.Rating;
        _seekProgressRatio = 0.0;
        SeekRailCanvas?.Invalidate();

        _ = LoadPlayingArtAndSmtcAsync(track);
        _ = LoadFocusLyricsAsync(track.Path);
    }

    private async Task LoadPlayingArtAndSmtcAsync(TrackItem track)
    {
        // A track known to have no art shows its generated cover at once, under the old cover as that fades out.
        // A track with art keeps whatever is underneath until its own cover has faded in over it.
        bool expectsArt = track.HasEmbeddedArt;
        if (!expectsArt)
        {
            ApplyArtFallback(track, NowPlayingArtFallback, NowPlayingArtInitial);
            ApplyArtFallback(track, FocusArtFallback, FocusArtInitial);
        }

        byte[]? bytes = await Task.Run(() => MusicLibrary.ReadEmbeddedArtBytes(track.Path));
        if (!string.Equals(_activePlayingPathForLyrics, track.Path, StringComparison.OrdinalIgnoreCase)) return;

        await _engine.UpdateSmtcMetadata(track.DisplayTitle, track.Artist, track.Album, bytes);
        if (!string.Equals(_activePlayingPathForLyrics, track.Path, StringComparison.OrdinalIgnoreCase)) return;
        var cover = await CreateBitmapFromBytesAsync(bytes);
        var backdrop = await CreateBitmapFromBytesAsync(bytes, decodePixelWidth: 40);
        if (!string.Equals(_activePlayingPathForLyrics, track.Path, StringComparison.OrdinalIgnoreCase)) return;

        bool hasArt = cover != null;
        if (!hasArt)
        {
            ApplyArtFallback(track, NowPlayingArtFallback, NowPlayingArtInitial);
            ApplyArtFallback(track, FocusArtFallback, FocusArtInitial);
        }
        CrossfadePlayingArt(cover, backdrop);
        if (hasArt) _ = HidePlayingFallbackAfterFadeAsync(track.Path);

        Color glowColor = bytes is { Length: > 0 }
            ? await ComputeDominantAccentColorAsync(bytes)
            : FallbackTintFor(track);
        FocusAmbientStop.Color = glowColor;
        ApplyCoverTint(glowColor);
    }

    // Each art slot has two stacked images. The incoming cover goes into the hidden one and fades in while the
    // visible one fades out, so the change is a true cross-fade however quickly the new cover decodes.
    private bool _playingArtOnB;

    private void CrossfadePlayingArt(ImageSource? cover, ImageSource? backdrop)
    {
        var (barIn, barOut) = _playingArtOnB ? (NowPlayingArtImage, NowPlayingArtImageB) : (NowPlayingArtImageB, NowPlayingArtImage);
        var (focusIn, focusOut) = _playingArtOnB ? (FocusArtImage, FocusArtImageB) : (FocusArtImageB, FocusArtImage);
        var (dropIn, dropOut) = _playingArtOnB ? (FocusBackdropImage, FocusBackdropImageB) : (FocusBackdropImageB, FocusBackdropImage);
        _playingArtOnB = !_playingArtOnB;

        barIn.Source = cover;
        focusIn.Source = cover;
        dropIn.Source = backdrop;
        barIn.Opacity = cover != null ? 1 : 0;
        focusIn.Opacity = cover != null ? 1 : 0;
        dropIn.Opacity = backdrop != null ? 0.6 : 0;
        barOut.Opacity = 0;
        focusOut.Opacity = 0;
        dropOut.Opacity = 0;
    }

    private async Task HidePlayingFallbackAfterFadeAsync(string path)
    {
        await Task.Delay(500);
        if (!string.Equals(_activePlayingPathForLyrics, path, StringComparison.OrdinalIgnoreCase)) return;
        ApplyArtFallback(null, NowPlayingArtFallback, NowPlayingArtInitial);
        ApplyArtFallback(null, FocusArtFallback, FocusArtInitial);
    }

    // Tracks without a cover still get a wash: a hue derived from the artist (else album, else title),
    // so the same artist always lands on the same colour.
    private static Color FallbackTintFor(TrackItem track)
        => HsvToColor(ArtHue(track.Artist, track.Album, track.DisplayTitle), 0.50, 0.85);

    private static int ArtHue(string? artist, string? album, string? title)
    {
        string key = !string.IsNullOrWhiteSpace(artist) ? artist
            : !string.IsNullOrWhiteSpace(album) ? album
            : title ?? string.Empty;
        uint hash = 2166136261;
        foreach (char ch in key.Trim().ToLowerInvariant())
            hash = (hash ^ ch) * 16777619;
        return (int)(hash % 360);
    }

    private static readonly Dictionary<int, Brush> _placeholderBrushes = new();

    /// <summary>Generated cover for tracks without art: a diagonal gradient in the artist's hue (same hue as the window tint).</summary>
    public static Brush PlaceholderBrush(string? artist, string? album, string? title)
    {
        int hue = ArtHue(artist, album, title);
        if (_placeholderBrushes.TryGetValue(hue, out var cached)) return cached;
        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1)
        };
        brush.GradientStops.Add(new GradientStop { Color = HsvToColor(hue, 0.52, 0.62), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = HsvToColor((hue + 28) % 360, 0.62, 0.34), Offset = 1 });
        _placeholderBrushes[hue] = brush;
        return brush;
    }

    private static void ApplyArtFallback(TrackItem? track, Border host, TextBlock initial)
    {
        if (track == null)
        {
            host.Visibility = Visibility.Collapsed;
            return;
        }
        host.Background = PlaceholderBrush(track.Artist, track.Album, track.DisplayTitle);
        initial.Text = track.ArtInitial;
        host.Visibility = Visibility.Visible;
    }

    private static Color HsvToColor(double hue, double s, double v)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs((hue / 60.0) % 2 - 1));
        double m = v - c;
        (double r, double g, double b) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return Color.FromArgb(255, (byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    // Washes the playbar and right pane with the playing cover's colour; fades between tracks, clears when there is no art.
    private async void ApplyCoverTint(Color? cover, bool fade = true)
    {
        _lastCoverTint = cover;
        double k = TintScale;
        byte Scaled(int alpha) => (byte)Math.Clamp(alpha * k, 0, 255);
        bool light = RootGrid.RequestedTheme == ElementTheme.Light;
        Color c = cover ?? Color.FromArgb(0, 0, 0, 0);
        if (cover.HasValue)
        {
            // Covers average out muddy: lift to a fixed brightness and widen the channel spread so the hue reads.
            double max = Math.Max(c.R, Math.Max(c.G, c.B));
            double scale = max > 0 ? 215.0 / max : 1.0;
            byte Lift(byte v) => (byte)Math.Clamp(215.0 - (215.0 - v * scale) * 1.5, 0, 255);
            c = Color.FromArgb(255, Lift(c.R), Lift(c.G), Lift(c.B));
        }
        byte strong = cover.HasValue ? Scaled(light ? 0x2E : 0x52) : (byte)0;
        var from = Color.FromArgb(strong, c.R, c.G, c.B);
        var to = Color.FromArgb(0, c.R, c.G, c.B);

        // Fade out, swap colour, fade in (the layers carry an OpacityTransition).
        int ticket = ++_coverTintTicket;
        // The list gets a lighter wash than the chrome so rows stay easy to read.
        byte canvasNear = cover.HasValue ? Scaled(light ? 0x1C : 0x34) : (byte)0;
        byte canvasFar = cover.HasValue ? Scaled(light ? 0x06 : 0x0C) : (byte)0;

        if (fade)
        {
            PlaybarTintLayer.Opacity = 0;
            PaneTintLayer.Opacity = 0;
            CanvasTintLayer.Opacity = 0;
            await Task.Delay(320);
            if (ticket != _coverTintTicket) return;
        }
        PlaybarTintStop.Color = from;
        PlaybarTintEndStop.Color = to;
        PaneTintStop.Color = from;
        PaneTintEndStop.Color = to;
        PlayingRowTint.Color = PlayingRowColor(cover.HasValue ? c : null, light, k, _highContrast);
        CanvasTintStop.Color = Color.FromArgb(canvasNear, c.R, c.G, c.B);
        CanvasTintEndStop.Color = Color.FromArgb(canvasFar, c.R, c.G, c.B);
        Color? vizAccent = cover.HasValue && k > 0 ? c : null;
        if (WindowVisualizer != null) WindowVisualizer.Accent = vizAccent;
        if (FocusVisualizer != null) FocusVisualizer.Accent = vizAccent;
        if (PlaybarVisualizerLeft != null) PlaybarVisualizerLeft.Accent = vizAccent;
        if (PlaybarVisualizer != null) PlaybarVisualizer.Accent = vizAccent;
        PlaybarTintLayer.Opacity = 1;
        PaneTintLayer.Opacity = 1;
        CanvasTintLayer.Opacity = 1;
    }

    private int _coverTintTicket;
    private Color? _lastCoverTint;

    /// <summary>
    /// Background of the playing row and playing tile. One shared brush, so recolouring it moves every
    /// realised row at once: it takes the cover colour, and is amber when there is no tint.
    /// </summary>
    public static SolidColorBrush PlayingRowTint { get; } = new(Color.FromArgb(0x1C, 0xFF, 0xC8, 0x3B));

    private static Color PlayingRowColor(Color? lifted, bool light, double strength, bool highContrast)
    {
        // High contrast keeps the gold it had as a theme brush; the cover colour is not used there.
        if (highContrast) return Color.FromArgb(0x33, 0xFF, 0xD7, 0x00);
        if (lifted is not { } c || strength <= 0)
            return light ? Color.FromArgb(0x22, 0xD9, 0x77, 0x06) : Color.FromArgb(0x1C, 0xFF, 0xC8, 0x3B);
        // Follows the wash strength, but never fades below the amber default's weight or swamps the row text.
        double alpha = Math.Clamp((light ? 0x38 : 0x3C) * strength, 0x20, 0x66);
        return Color.FromArgb((byte)alpha, c.R, c.G, c.B);
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
        SyncPlaybackProgressUi(line.Timestamp, _engine.Duration);
        if (!_engine.IsPlaying && _engine.CurrentPath != null)
            _engine.Resume();
    }

    private void Engine_PlaybackStateChanged(bool playing)
    {
        PlayPauseIcon.Glyph = playing ? "\uE769" : "\uE768";
        PlaybarVisualizerLeft.IsAudioPlaying = playing;
        PlaybarVisualizer.IsAudioPlaying = playing;
        WindowVisualizer.IsAudioPlaying = playing;
        FocusVisualizer.IsAudioPlaying = playing;

        if (playing) _progressTimer.Start();
        else
        {
            _progressTimer.Stop();
            if (Volatile.Read(ref _releasingFileForTagWrite) == 0)
                _ = FlushPendingRatingWritesAsync();
        }
    }

    private void Engine_TrackEnded()
    {
        if (_sleepAfterCurrentTrack)
        {
            ClearSleepTimer(showHud: false);
            ShowHudPill("Sleep timer paused playback");
            return;
        }
        PlayNext(fromAutoTrackEnd: true);
    }

    private MenuFlyoutSubItem BuildSleepTimerSubmenu()
    {
        string label = "Sleep timer";
        if (_sleepAfterCurrentTrack)
        {
            label = "Sleep timer (after track)";
        }
        else if (_sleepEndUtc is DateTime endUtc)
        {
            int mins = Math.Max(1, (int)Math.Ceiling((endUtc - DateTime.UtcNow).TotalMinutes));
            label = $"Sleep timer ({mins}m left)";
        }

        var sub = new MenuFlyoutSubItem { Text = label };
        var offItem = new MenuFlyoutItem { Text = "Off" };
        offItem.Click += (_, _) => ClearSleepTimer(showHud: true);
        sub.Items.Add(offItem);

        var afterTrack = new MenuFlyoutItem { Text = "After current track" };
        afterTrack.Click += (_, _) => SetSleepAfterTrack();
        sub.Items.Add(afterTrack);

        sub.Items.Add(new MenuFlyoutSeparator());

        foreach (int mins in new[] { 15, 30, 45, 60, 90 })
        {
            int capture = mins;
            var item = new MenuFlyoutItem { Text = $"{capture} minutes" };
            item.Click += (_, _) => SetSleepTimerMinutes(capture);
            sub.Items.Add(item);
        }
        return sub;
    }

    private void SetSleepAfterTrack()
    {
        _sleepTimer?.Stop();
        _sleepEndUtc = null;
        _sleepAfterCurrentTrack = true;
        ShowHudPill("Sleep timer: pause after current track");
    }

    private void SetSleepTimerMinutes(int minutes)
    {
        _sleepAfterCurrentTrack = false;
        _sleepEndUtc = DateTime.UtcNow.AddMinutes(minutes);
        if (_sleepTimer == null)
        {
            _sleepTimer = DispatcherQueue.CreateTimer();
            _sleepTimer.Interval = TimeSpan.FromSeconds(5);
            _sleepTimer.Tick += SleepTimer_Tick;
        }
        _sleepTimer.Start();
        ShowHudPill($"Sleep timer set for {minutes} min");
    }

    private void ClearSleepTimer(bool showHud)
    {
        bool wasActive = _sleepAfterCurrentTrack || _sleepEndUtc.HasValue;
        _sleepTimer?.Stop();
        _sleepEndUtc = null;
        _sleepAfterCurrentTrack = false;
        if (showHud && wasActive) ShowHudPill("Sleep timer off");
    }

    private void SleepTimer_Tick(object? sender, object e)
    {
        if (_sleepEndUtc is not DateTime endUtc)
        {
            _sleepTimer?.Stop();
            return;
        }
        if (DateTime.UtcNow < endUtc) return;

        ClearSleepTimer(showHud: false);
        if (_engine.IsPlaying) _engine.Pause();
        ShowHudPill("Sleep timer paused playback");
    }

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

    private void SyncPlaybackProgressUi(TimeSpan pos, TimeSpan dur)
    {
        ElapsedTimeText.Text = FormatTime(pos);
        TotalTimeText.Text = FormatTime(dur);

        if (dur.TotalSeconds > 0)
        {
            _isUpdatingSeekSlider = true;
            try
            {
                SeekSlider.Maximum = dur.TotalSeconds;
                SeekSlider.Value = Math.Clamp(pos.TotalSeconds, 0, dur.TotalSeconds);
                _seekProgressRatio = Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0.0, 1.0);
            }
            finally { _isUpdatingSeekSlider = false; }
        }
        SeekRailCanvas?.Invalidate();
    }

    private void ProgressTimer_Tick(object? sender, object e)
    {
        if (_isDraggingSeek) return;
        var pos = _engine.Position;
        var dur = _engine.Duration;
        SyncPlaybackProgressUi(pos, dur);

        if (FocusVisualizerOverlay.Visibility == Visibility.Visible)
        {
            FocusAmbientGlow.Opacity = 0.28 + _engine.Fft.BassEnergy * 0.42;
            UpdateActiveLyricHighlight(_focusLrcLines, FocusSyncedLyricsList, ref _activeFocusLrcIndex, pos);
            if (_engine.IsPlaying && _focusLastPointerMoveTick > 0 &&
                Environment.TickCount64 - _focusLastPointerMoveTick > 2800)
            {
                if (FocusTopBar.Opacity > 0.1) FocusTopBar.Opacity = 0.0;
            }
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
            SyncPlaybackProgressUi(TimeSpan.Zero, _engine.Duration);
            return;
        }
        int prev = _queueIndex - 1;
        if (prev < 0) prev = _settings.RepeatMode > 0 ? _queueTracks.Count - 1 : 0;
        PlayQueueIndex(prev);
    }

    private void SeekSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _seekCoalesceTimer.Stop();
        _isDraggingSeek = true;
        SeekSlider.CapturePointer(e.Pointer);
        SeekRailCanvas?.Invalidate();
    }

    private void SeekSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _seekCoalesceTimer.Stop();
        _isDraggingSeek = false;
        SeekSlider.ReleasePointerCapture(e.Pointer);
        if (_engine.Duration.TotalSeconds > 0)
        {
            var target = TimeSpan.FromSeconds(SeekSlider.Value);
            _engine.Seek(target);
            ElapsedTimeText.Text = FormatTime(target);
            _seekProgressRatio = Math.Clamp(target.TotalSeconds / _engine.Duration.TotalSeconds, 0.0, 1.0);
        }
        SeekRailCanvas?.Invalidate();
    }

    private void SeekSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (SeekSlider.Maximum > 0)
        {
            _seekProgressRatio = Math.Clamp(e.NewValue / SeekSlider.Maximum, 0.0, 1.0);
            SeekRailCanvas?.Invalidate();
        }

        if (_isUpdatingSeekSlider || _engine == null || _engine.Duration.TotalSeconds <= 0) return;
        if (_isDraggingSeek)
        {
            ElapsedTimeText.Text = FormatTime(TimeSpan.FromSeconds(e.NewValue));
            return;
        }
        ElapsedTimeText.Text = FormatTime(TimeSpan.FromSeconds(e.NewValue));
        // A volume wheel or a free-spinning scroll can land on this slider and fire once per notch.
        // Seeking on every notch repositions the decoder continuously, which is the stutter.
        _seekCoalesceTimer.Stop();
        _seekCoalesceTimer.Start();
    }

    private void FlushCoalescedSeek()
    {
        _seekCoalesceTimer.Stop();
        if (_isDraggingSeek || _isUpdatingSeekSlider || _engine.Duration.TotalSeconds <= 0) return;
        _engine.Seek(TimeSpan.FromSeconds(SeekSlider.Value));
    }

    private void SeekSlider_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        _seekCoalesceTimer.Stop();
        _engine.Seek(TimeSpan.FromSeconds(SeekSlider.Value));
    }

    private void SeekSlider_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isHoveringSeek = true;
        SeekRailCanvas?.Invalidate();
    }

    private void SeekSlider_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isHoveringSeek = false;
        SeekRailCanvas?.Invalidate();
    }

    private void SeekSlider_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_engine == null || _engine.Duration.TotalSeconds <= 0 || SeekSlider.ActualWidth <= 0) return;
        double ratio = Math.Clamp(e.GetCurrentPoint(SeekSlider).Position.X / SeekSlider.ActualWidth, 0.0, 1.0);
        var hoverTime = TimeSpan.FromSeconds(ratio * _engine.Duration.TotalSeconds);
        ToolTipService.SetToolTip(SeekSlider, $"{FormatTime(hoverTime)} / {FormatTime(_engine.Duration)}");
    }

    private void SeekRailCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        bool isLight = RootGrid.RequestedTheme == ElementTheme.Light;
        DrawSleekSliderRail(args.DrawingSession, (float)sender.ActualWidth, (float)sender.ActualHeight, _seekProgressRatio, _isHoveringSeek || _isDraggingSeek, isLight);
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_engine == null) return;
        _settings.Volume = e.NewValue;
        if (e.NewValue > 0) _isMuted = false;
        _engine.SetVolume(e.NewValue);
        UpdateVolumeIcon(e.NewValue);
        VolumeRailCanvas?.Invalidate();
    }

    private void VolumeSlider_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isHoveringVolume = true;
        VolumeRailCanvas?.Invalidate();
    }

    private void VolumeSlider_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isHoveringVolume = false;
        VolumeRailCanvas?.Invalidate();
    }

    private void VolumeRailCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        double ratio = VolumeSlider != null ? Math.Clamp(VolumeSlider.Value / 100.0, 0.0, 1.0) : 0.7;
        bool isLight = RootGrid.RequestedTheme == ElementTheme.Light;
        DrawSleekSliderRail(args.DrawingSession, (float)sender.ActualWidth, (float)sender.ActualHeight, ratio, _isHoveringVolume, isLight);
    }

    private static void DrawSleekSliderRail(CanvasDrawingSession ds, float w, float h, double ratio, bool hovering, bool isLight)
    {
        if (w <= 8f || h <= 4f) return;

        // WinUI 3 Slider leaves a ~10px thumb inset on each edge so the visual dot tracks the pointer hit-target.
        float padX = Math.Min(10f, w * 0.15f);
        float trackW = Math.Max(1f, w - padX * 2f);
        float midY = h * 0.5f;
        float clampedRatio = (float)Math.Clamp(ratio, 0.0, 1.0);
        float thumbX = padX + clampedRatio * trackW;

        Color trackBg = isLight ? Color.FromArgb(42, 0, 0, 0) : Color.FromArgb(56, 255, 255, 255);
        Color accentFill = isLight ? Color.FromArgb(255, 217, 119, 6) : Color.FromArgb(255, 255, 200, 59);
        Color hoverHalo = isLight ? Color.FromArgb(55, 217, 119, 6) : Color.FromArgb(65, 255, 200, 59);
        Color thumbRing = isLight ? Color.FromArgb(65, 0, 0, 0) : Color.FromArgb(95, 0, 0, 0);
        Color thumbCenter = isLight ? Color.FromArgb(255, 34, 34, 34) : Color.FromArgb(255, 255, 255, 255);

        const float railH = 2.5f;
        float railY = midY - railH * 0.5f;
        ds.FillRoundedRectangle(padX, railY, trackW, railH, 1.25f, 1.25f, trackBg);

        float activeW = Math.Max(0f, thumbX - padX);
        if (activeW > 0.5f)
        {
            ds.FillRoundedRectangle(padX, railY, activeW, railH, 1.25f, 1.25f, accentFill);
        }

        if (hovering)
        {
            ds.FillCircle(thumbX, midY, 7.2f, hoverHalo);
        }
        ds.FillCircle(thumbX, midY, hovering ? 5.6f : 5.4f, thumbRing);
        ds.FillCircle(thumbX, midY, hovering ? 5.0f : 4.8f, thumbCenter);
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
        _settings.Shuffle = !_settings.Shuffle;
        UpdateShuffleButtonVisual();
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
        if (_editingPlaylist == null && _queueIndex >= 0 && _queueIndex < _queueTracks.Count)
        {
            QueueListView.SelectedIndex = _queueIndex;
            QueueListView.ScrollIntoView(_queueTracks[_queueIndex]);
        }
    }

    private void UpdateShuffleButtonVisual()
    {
        bool active = _settings.Shuffle;
        bool isLight = RootGrid.RequestedTheme == ElementTheme.Light;
        ShuffleIcon.Foreground = active
            ? (isLight ? PlaybarLightAmberBrush : PlaybarGoldBrush)
            : (isLight ? PlaybarLightMutedIconBrush : PlaybarMutedIconBrush);
        ShuffleActiveDot.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(ShuffleToggle, active ? "Shuffle on" : "Shuffle off");
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
        bool active = _settings.RepeatMode != 0;
        bool isLight = RootGrid.RequestedTheme == ElementTheme.Light;
        RepeatIcon.Glyph = _settings.RepeatMode == 2 ? "\uE8ED" : "\uE8EE";
        RepeatIcon.Foreground = active
            ? (isLight ? PlaybarLightAmberBrush : PlaybarGoldBrush)
            : (isLight ? PlaybarLightMutedIconBrush : PlaybarMutedIconBrush);
        RepeatActiveDot.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(RepeatToggle, _settings.RepeatMode switch
        {
            0 => "Repeat off (Click to cycle)",
            2 => "Repeat one (Click to cycle)",
            _ => "Repeat all (Click to cycle)"
        });
    }

    private void SetVisualizerModeAndSave(VisualizerMode mode)
    {
        _settings.VisualizerEnabled = mode != VisualizerMode.Off;
        _settings.VisualizerMode = mode.ToString();
        _settings.Save();
        ApplyVisualizerFromSettings();
    }

    private void FocusVizModeBtn_Click(object sender, RoutedEventArgs e)
    {
        var next = FocusVisualizer.Mode switch
        {
            VisualizerMode.Spectrum => VisualizerMode.Waveform,
            VisualizerMode.Waveform => VisualizerMode.BarsAndWaveform,
            VisualizerMode.BarsAndWaveform => VisualizerMode.Off,
            _ => VisualizerMode.Spectrum
        };
        SetVisualizerModeAndSave(next);
    }

    private void VisualizerContextFlyout_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout flyout) return;
        string activeTag = !_settings.VisualizerEnabled ? "Off" : _settings.VisualizerMode;
        bool focusOpen = FocusVisualizerOverlay.Visibility == Visibility.Visible;
        foreach (var item in flyout.Items)
        {
            if (item is ToggleMenuFlyoutItem toggle && toggle.Tag is string tag)
            {
                if (tag == "WindowBackdrop")
                    toggle.IsChecked = _settings.WindowVisualizerEnabled;
                else
                    toggle.IsChecked = string.Equals(tag, activeTag, StringComparison.OrdinalIgnoreCase);
            }
            else if (item is MenuFlyoutItem action && action.Tag as string == "FocusStage")
            {
                action.Text = focusOpen ? "Exit Focus Stage (F11)" : "Focus Stage (F11)";
            }
        }
    }

    private void VisualizerContextMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string tag } && Enum.TryParse<VisualizerMode>(tag, out var mode))
        {
            SetVisualizerModeAndSave(mode);
        }
    }

    private void VisualizerContextBackdrop_Click(object sender, RoutedEventArgs e)
    {
        if (_settings == null) return;
        _settings.WindowVisualizerEnabled = !_settings.WindowVisualizerEnabled;
        _settings.Save();
        SyncVisualizerStageVisibility();
    }

    private void VisualizerContextFocusStage_Click(object sender, RoutedEventArgs e) => ToggleFocusVisualizer();

    private void PlaybarVisualizerHost_Tapped(object sender, TappedRoutedEventArgs e) => ToggleFocusVisualizer();

    private void CloseFocusBtn_Click(object sender, RoutedEventArgs e) => CloseFocusStage();

    private bool _isFocusFullScreen;
    private long _focusLastPointerMoveTick;

    private void FocusVisualizerOverlay_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        _focusLastPointerMoveTick = Environment.TickCount64;
        if (FocusTopBar.Opacity < 1.0)
            FocusTopBar.Opacity = 1.0;
    }

    private void FocusFullScreenBtn_Click(object sender, RoutedEventArgs e)
    {
        SetFocusFullScreen(!_isFocusFullScreen);
    }

    private void SetFocusFullScreen(bool enable)
    {
        if (_isFocusFullScreen == enable) return;
        _isFocusFullScreen = enable;
        try
        {
            if (enable)
            {
                AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                TopBarRowDef.Height = new GridLength(0);
                MainTitleBar.Visibility = Visibility.Collapsed;
                FocusFullScreenIcon.Glyph = "\uE73F";
                FocusFullScreenText.Text = "Windowed";
            }
            else
            {
                AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                if (!_isMiniPlayer)
                {
                    TopBarRowDef.Height = new GridLength(44);
                    MainTitleBar.Visibility = Visibility.Visible;
                    SetTitleBar(TitleBarDragRegion);
                    _lastSearchPassthroughRect = default;
                    DispatcherQueue.TryEnqueue(UpdateTitleBarPassthrough);
                }
                FocusFullScreenIcon.Glyph = "\uE740";
                FocusFullScreenText.Text = "Full screen";
            }
        }
        catch { }
    }

    private void CloseFocusStage()
    {
        if (_isFocusFullScreen)
            SetFocusFullScreen(false);
        FocusTopBar.Opacity = 1.0;
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
        if (IsInsideButton(src) || IsInsideType<Slider>(src) || IsInsideType<StarRatingCell>(src) || IsInsideType<VisualizerControl>(src)) return;

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
            int targetH = Math.Clamp((int)Math.Round(90 * scale), 86, work.Height);
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
                CloseFocusStage();

            SyncVisualizerStageVisibility();

            PlaybarRowDef.Height = new GridLength(1, GridUnitType.Star);
            PlaybarBorder.Height = double.NaN;
            PlaybarBorder.Padding = new Thickness(16, 4, 16, 6);
            PlaybarBorder.BorderThickness = new Thickness(0);
            SeekRailHost.Margin = new Thickness(-16, -4, -16, 0);
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

            PlaybarRowDef.Height = new GridLength(74);
            PlaybarBorder.Height = 74;
            PlaybarBorder.Padding = new Thickness(16, 2, 16, 3);
            ApplyVisualizerFromSettings();

            PlaybarBorder.BorderThickness = new Thickness(0);
            SeekRailHost.Margin = new Thickness(-16, -10, -16, 0);
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
        bool opening = FocusVisualizerOverlay.Visibility != Visibility.Visible;
        if (opening)
        {
            _focusLastPointerMoveTick = Environment.TickCount64;
            FocusTopBar.Opacity = 1.0;
            // Start transparent without animating, then fade in.
            var fade = FocusVisualizerOverlay.OpacityTransition;
            FocusVisualizerOverlay.OpacityTransition = null;
            FocusVisualizerOverlay.Opacity = 0;
            FocusVisualizerOverlay.OpacityTransition = fade;
            FocusVisualizerOverlay.Visibility = Visibility.Visible;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => FocusVisualizerOverlay.Opacity = 1);
            SyncVisualizerStageVisibility();
        }
        else
        {
            CloseFocusStage();
        }
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

    // The track the card is showing: the selection, or the playing track when nothing is selected.
    private TrackItem? _inspectorTrack;

    private void UpdateInspectorForSelection(TrackItem? track)
    {
        UpdateStatusFooter();
        if (track == null && _queueIndex >= 0 && _queueIndex < _queueTracks.Count)
            track = _queueTracks[_queueIndex];
        _inspectorTrack = track;
        SelectedEmptyBg.Visibility = track == null ? Visibility.Visible : Visibility.Collapsed;
        if (track == null)
        {
            // Empty card follows the app theme instead of sitting dark in a light window.
            _selectedArtIsLight = RootGrid.RequestedTheme == ElementTheme.Light;
            ApplySelectedInfoTheme();
            _selectedLyricsTrackPath = null;
            SelectedTitleText.Text = "No track selected";
            SelectedArtistText.Text = string.Empty;
            SelectedSpecsText.Text = string.Empty;
            SelectedArtImage.Source = null;
            ApplyArtFallback(null, SelectedArtFallback, SelectedArtInitial);
            SelectedDiscPlaceholder.Visibility = Visibility.Visible;
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
        ApplyArtFallback(track.HasEmbeddedArt ? null : track, SelectedArtFallback, SelectedArtInitial);
        SelectedDiscPlaceholder.Visibility = track.HasEmbeddedArt ? Visibility.Visible : Visibility.Collapsed;
        SelectedArtImage.Opacity = 0;
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
        // Generated-cover initial: small in the compact card, lifted clear of the text overlay in mode 2.
        SelectedArtInitial.FontSize = _inspectorArtMode == 0 ? 44 : _inspectorArtMode == 1 ? 80 : 60;
        SelectedArtInitial.Margin = new Thickness(0, 0, 0, _inspectorArtMode == 2 ? 100 : 0);
        SelectedDiscPlaceholder.Margin = SelectedArtInitial.Margin;

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
            // The top padding is where the fade under the text builds up, clear of the first line.
            SelectedInfoOverlay.Padding = new Thickness(8, 26, 8, 6);
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

        // Every cover gets the dark shade and light text (see InspectorOverlayBrush).
        if (UseLightCoverOverlay && _selectedArtIsLight)
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
        if (targets.Contains(SelectedArtImage) && !string.Equals(_selectedLyricsTrackPath, path, StringComparison.OrdinalIgnoreCase))
            return;
        await SetImageFromBytesAsync(bytes, targets);
        foreach (var img in targets) img.Opacity = 1;
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
        var pt = e.GetCurrentPoint(btn);
        if (!pt.Properties.IsLeftButtonPressed) return;
        double x = pt.Position.X;
        double width = btn.ActualWidth > 0 ? btn.ActualWidth : 20.0;
        double rating = x < width * 0.5 ? starIndex - 0.5 : starIndex;
        _lastStarPointerUtc = DateTime.UtcNow;
        var current = (GetPrimarySelectedTrack() ?? _inspectorTrack)?.Rating;
        ApplyRatingToCard(Nullable.Equals(current, rating) ? null : rating);
    }

    private void RateStarBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || !double.TryParse(btn.Tag?.ToString(), out double stars)) return;
        if (stars <= 0)
        {
            ApplyRatingToCard(null);
            return;
        }
        if ((DateTime.UtcNow - _lastStarPointerUtc).TotalMilliseconds > 300)
        {
            var current = (GetPrimarySelectedTrack() ?? _inspectorTrack)?.Rating;
            ApplyRatingToCard(Nullable.Equals(current, stars) ? null : stars);
        }
    }

    // The card's stars rate the selection, or the playing track it is showing when nothing is selected.
    private void ApplyRatingToCard(double? value)
    {
        if (GetSelectedTracks().Count > 0 || _inspectorTrack is not { } shown)
        {
            ApplyRatingToSelection(value);
            return;
        }
        var matching = EnumerateMatchingTrackItems(shown).ToList();
        ApplyRatingToTargets(matching, value, pushUndo: true);
        UpdateSelectedRatingVisuals(shown.Rating);
    }

    private void ApplyRatingToSelection(double? value)
    {
        var selected = GetSelectedTracks();
        ApplyRatingToTargets(selected, value, pushUndo: true);
    }

    private void UpdateSelectedRatingVisuals(double? rating)
    {
        var fills = new[] { StarFillHost1, StarFillHost2, StarFillHost3, StarFillHost4, StarFillHost5 };
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
                fills[i].Clip = new RectangleGeometry { Rect = new Rect(0, 0, 7, 16) };
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
        if (selected.Count == 0 && _inspectorTrack != null)
            selected.Add(_inspectorTrack);
        if (selected.Count > 0)
            await OpenPropertiesDialogAsync(selected);
    }

    private async Task<(bool Ok, string? Error)> RenameTrackPathAsync(TrackItem track, string oldPath, string newPath)
    {
        bool renamed;
        string? renameErr;
        await _tagWriteGate.WaitAsync();
        try
        {
            QuietFolderWatcher();
            Interlocked.Increment(ref _releasingFileForTagWrite);
            try
            {
                (renamed, renameErr) = await _engine.RenameFileAsync(oldPath, newPath);
            }
            finally
            {
                Interlocked.Decrement(ref _releasingFileForTagWrite);
                QuietFolderWatcher();
            }
        }
        finally
        {
            _tagWriteGate.Release();
        }
        if (!renamed) return (false, renameErr);
        RememberRenamedTrack(track, oldPath, newPath);
        return (true, null);
    }

    private void RememberRenamedTrack(TrackItem track, string oldPath, string newPath)
    {
        lock (_pendingRatingsLock)
        {
            if (_pendingRatingWrites.Remove(oldPath, out var pending))
                _pendingRatingWrites[newPath] = pending;
        }

        foreach (var peer in EnumerateTrackItemsByPath(oldPath).ToList())
            peer.Path = newPath;
        track.Path = newPath;
        if (_artThumbCache.TryRemove(oldPath, out var cachedThumb))
            _artThumbCache[newPath] = cachedThumb;
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
                    QuietFolderWatcher();
                    await Task.Run(() => Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                        t.Path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin));
                    QuietFolderWatcher();
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

    private IEnumerable<TrackItem> EnumerateTrackItemsByPath(string path)
    {
        var seen = new HashSet<TrackItem>();
        foreach (var t in _allTracks)
            if (string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase) && seen.Add(t))
                yield return t;
        foreach (var t in _visibleTracks)
            if (string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase) && seen.Add(t))
                yield return t;
        foreach (var t in _queueTracks)
            if (string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase) && seen.Add(t))
                yield return t;
        foreach (var t in _editingPlaylistTracks)
            if (string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase) && seen.Add(t))
                yield return t;
    }

    private IEnumerable<TrackItem> EnumerateMatchingTrackItems(TrackItem primary)
    {
        yield return primary;
        foreach (var t in EnumerateTrackItemsByPath(primary.Path))
        {
            if (!ReferenceEquals(t, primary))
                yield return t;
        }
    }

    private bool TryGetPendingRating(string path, out double? rating, out long version)
    {
        lock (_pendingRatingsLock)
        {
            if (_pendingRatingWrites.TryGetValue(path, out var entry))
            {
                rating = entry.Rating;
                version = entry.Version;
                return true;
            }
        }
        rating = null;
        version = 0;
        return false;
    }

    private double? ResolveTrackRating(TrackItem track, double? diskRating)
    {
        bool hasPending = TryGetPendingRating(track.Path, out double? pending, out _);
        return MusicLibrary.ResolveEffectiveRating(hasPending, pending, diskRating, track.Rating);
    }

    private async Task<(bool Ok, MusicLibrary.TrackDetails Details, string? Error)> ReadTrackDetailsSafelyAsync(string path)
    {
        await _tagWriteGate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                bool ok = MusicLibrary.TryReadFullDetails(path, out var d, out var err);
                return (ok, d, err);
            });
        }
        finally
        {
            _tagWriteGate.Release();
        }
    }

    private void ApplyTrackFileStats(string path, long fileSize, long fileWriteUtcTicks)
    {
        foreach (var t in EnumerateTrackItemsByPath(path))
        {
            t.FileSize = fileSize;
            t.FileWriteUtcTicks = fileWriteUtcTicks;
        }
    }

    private void UpdateTrackFileStats(string path)
    {
        long fileSize;
        long fileWriteUtcTicks;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return;
            fileSize = info.Length;
            fileWriteUtcTicks = info.LastWriteTimeUtc.Ticks;
        }
        catch
        {
            return;
        }

        if (DispatcherQueue.HasThreadAccess)
            ApplyTrackFileStats(path, fileSize, fileWriteUtcTicks);
        else
            DispatcherQueue.TryEnqueue(() => ApplyTrackFileStats(path, fileSize, fileWriteUtcTicks));
    }

    private void EnqueueRatingWrites(IEnumerable<(string Path, double? Rating)> writes)
    {
        var deduped = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, rating) in writes)
        {
            if (MusicLibrary.SupportsRatingTag(path))
                deduped[path] = rating;
        }
        if (deduped.Count == 0) return;

        lock (_pendingRatingsLock)
        {
            foreach (var kv in deduped)
            {
                long ver = ++_ratingWriteVersion;
                _pendingRatingWrites[kv.Key] = (kv.Value, ver);
            }
        }
        _ = FlushPendingRatingWritesAsync();
    }

    private async Task FlushPendingRatingWritesAsync()
    {
        _ratingFlushRequested = true;
        if (Interlocked.CompareExchange(ref _flushingPendingRatings, 1, 0) != 0)
            return;

        try
        {
            while (_ratingFlushRequested)
            {
                _ratingFlushRequested = false;
                List<(string Path, double? Rating, long Version)> snapshot;
                lock (_pendingRatingsLock)
                {
                    if (_pendingRatingWrites.Count == 0) break;
                    snapshot = _pendingRatingWrites
                        .Select(kv => (kv.Key, kv.Value.Rating, kv.Value.Version))
                        .ToList();
                }

                foreach (var (path, _, version) in snapshot)
                {
                    if (_engine.IsPlaying && string.Equals(_engine.CurrentPath, path, StringComparison.OrdinalIgnoreCase))
                        continue;

                    await _tagWriteGate.WaitAsync();
                    try
                    {
                        double? currentRating;
                        lock (_pendingRatingsLock)
                        {
                            if (!_pendingRatingWrites.TryGetValue(path, out var entry) || entry.Version != version)
                                continue;
                            currentRating = entry.Rating;
                        }

                        if (_engine.IsPlaying && string.Equals(_engine.CurrentPath, path, StringComparison.OrdinalIgnoreCase))
                            continue;

                        QuietFolderWatcher();
                        bool ok;
                        if (string.Equals(_engine.CurrentPath, path, StringComparison.OrdinalIgnoreCase))
                        {
                            Interlocked.Increment(ref _releasingFileForTagWrite);
                            try
                            {
                                ok = await _engine.WithFileReleasedAsync(path, () => Task.Run(() => MusicLibrary.TryWriteStars(path, currentRating)));
                            }
                            finally
                            {
                                Interlocked.Decrement(ref _releasingFileForTagWrite);
                            }
                        }
                        else
                        {
                            ok = await Task.Run(() => MusicLibrary.TryWriteStars(path, currentRating));
                        }
                        QuietFolderWatcher();

                        if (ok)
                        {
                            lock (_pendingRatingsLock)
                            {
                                if (_pendingRatingWrites.TryGetValue(path, out var entry) && entry.Version == version)
                                    _pendingRatingWrites.Remove(path);
                            }
                            UpdateTrackFileStats(path);
                            DispatcherQueue.TryEnqueue(SaveLibraryCacheSoon);
                        }
                    }
                    finally
                    {
                        _tagWriteGate.Release();
                    }
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _flushingPendingRatings, 0);
            if (_ratingFlushRequested)
                _ = FlushPendingRatingWritesAsync();
        }
    }

    private void FlushPendingRatingWritesSync()
    {
        bool locked = _tagWriteGate.Wait(TimeSpan.FromSeconds(3));
        try
        {
            List<(string Path, double? Rating, long Version)> snapshot;
            lock (_pendingRatingsLock)
            {
                if (_pendingRatingWrites.Count == 0) return;
                snapshot = _pendingRatingWrites
                    .Select(kv => (kv.Key, kv.Value.Rating, kv.Value.Version))
                    .ToList();
            }

            foreach (var (path, rating, version) in snapshot)
            {
                if (MusicLibrary.TryWriteStars(path, rating))
                {
                    lock (_pendingRatingsLock)
                    {
                        if (_pendingRatingWrites.TryGetValue(path, out var entry) && entry.Version == version)
                            _pendingRatingWrites.Remove(path);
                    }
                    UpdateTrackFileStats(path);
                }
            }
        }
        finally
        {
            if (locked) _tagWriteGate.Release();
        }
    }

    private async Task<ImageSource?> CacheAndBuildThumbnailAsync(string path, byte[]? artBytes)
    {
        _artThumbCache.TryRemove(path, out _);
        if (artBytes == null || artBytes.Length == 0)
        {
            _artThumbCache[path] = null;
            return null;
        }

        string hash = await Task.Run(() => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(artBytes)));
        if (_artBitmapByHash.TryGetValue(hash, out var sharedBmp))
        {
            _artThumbCache[path] = sharedBmp;
            return sharedBmp;
        }

        var bmp = await CreateBitmapFromBytesAsync(artBytes, decodePixelWidth: 180);
        if (bmp != null)
        {
            var finalBmp = _artBitmapByHash.GetOrAdd(hash, bmp);
            _artThumbCache[path] = finalBmp;
            return finalBmp;
        }

        return null;
    }

    private async Task<PropertiesFileCommitResult> SaveTrackDetailsAsync(
        TrackItem track,
        MusicLibrary.TrackDetails updated,
        bool pushUndo = true,
        DateTime? dateAddedUtc = null,
        string? renameTo = null)
    {
        MusicLibrary.TrackDetails? snapshotBefore = null;
        if (pushUndo)
        {
            var (snapOk, snap, snapErr) = await ReadTrackDetailsSafelyAsync(track.Path);
            if (!snapOk)
            {
                await ShowErrorDialogAsync(
                    "Could not save tags",
                    $"Failed to read current tags from \"{track.FileName}\":\n\n{snapErr ?? "The file may be read-only or in use by another program."}");
                return new PropertiesFileCommitResult(false, snapErr, false, null, false, null, track.Path);
            }
            snap.Rating = ResolveTrackRating(track, snap.Rating);
            snapshotBefore = snap;
        }

        bool hadPending = TryGetPendingRating(track.Path, out _, out long pendingVersionBeforeSave);
        string pathBeforeRename = track.Path;

        PropertiesFileCommitResult commit;
        await _tagWriteGate.WaitAsync();
        try
        {
            QuietFolderWatcher();
            Interlocked.Increment(ref _releasingFileForTagWrite);
            try
            {
                commit = await _engine.WithFileReleasedAsync(
                    track.Path,
                    () => Task.Run(() => PropertiesFileCommit.Apply(
                        track.Path,
                        () =>
                        {
                            bool ok = MusicLibrary.TryWriteFullDetails(track.Path, updated, out string? err);
                            return (ok, err);
                        },
                        dateAddedUtc,
                        renameTo)),
                    result => result.ResumePath);
            }
            finally
            {
                Interlocked.Decrement(ref _releasingFileForTagWrite);
                QuietFolderWatcher();
            }
        }
        finally
        {
            _tagWriteGate.Release();
        }

        if (!commit.TagsSaved)
        {
            await ShowErrorDialogAsync(
                "Could not save tags",
                $"Failed to write tags to \"{track.FileName}\":\n\n{commit.TagError ?? "The file may be read-only or in use by another program."}");
            return commit;
        }

        if (hadPending)
        {
            lock (_pendingRatingsLock)
            {
                if (_pendingRatingWrites.TryGetValue(track.Path, out var entry) && entry.Version == pendingVersionBeforeSave)
                    _pendingRatingWrites.Remove(track.Path);
            }
        }

        if (pushUndo && snapshotBefore != null)
        {
            snapshotBefore.ArtChanged = updated.ArtChanged;
            PushUndo(async () =>
            {
                await SaveTrackDetailsAsync(track, snapshotBefore, pushUndo: false);
                RefreshVisibleTracks();
                SaveLibraryCacheSnapshot();
                ShowHudPill($"Undone: Edit \"{track.DisplayTitle}\"");
            });
        }

        string writtenPath = commit.ResumePath;
        var (fileSize, fileWriteUtcTicks) = await Task.Run(() =>
        {
            try
            {
                var info = new FileInfo(writtenPath);
                return info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : (0L, 0L);
            }
            catch
            {
                return (0L, 0L);
            }
        });

        bool updateArt = updated.ArtChanged || (track.CoverThumbnail == null && updated.ArtBytes is { Length: > 0 });
        bool hasArt = updated.ArtBytes is { Length: > 0 };
        ImageSource? newThumb = null;
        if (updateArt)
        {
            newThumb = hasArt
                ? await CacheAndBuildThumbnailAsync(track.Path, updated.ArtBytes)
                : null;
            if (!hasArt)
                _artThumbCache[track.Path] = null;
        }

        foreach (var peer in EnumerateMatchingTrackItems(track))
        {
            peer.Title = updated.Title;
            peer.Artist = updated.Artist;
            peer.Album = updated.Album;
            peer.AlbumArtist = updated.AlbumArtist;
            peer.Genre = updated.Genre;
            peer.Year = updated.Year;
            peer.TrackNumber = updated.TrackNumber;
            peer.DiscNumber = updated.DiscNumber;
            peer.Rating = updated.Rating;
            if (commit.DateWritten && dateAddedUtc.HasValue)
                peer.DateAddedUtc = dateAddedUtc.Value;
            if (fileSize > 0)
            {
                peer.FileSize = fileSize;
                peer.FileWriteUtcTicks = fileWriteUtcTicks;
            }
            if (updateArt)
            {
                peer.HasEmbeddedArt = hasArt;
                peer.ArtLoadAttempted = true;
                peer.CoverThumbnail = newThumb;
            }
        }

        if (commit.Renamed)
            RememberRenamedTrack(track, pathBeforeRename, commit.ResumePath);

        if ((GetPrimarySelectedTrack() ?? _inspectorTrack) is { } shown &&
            string.Equals(shown.Path, track.Path, StringComparison.OrdinalIgnoreCase))
        {
            UpdateSelectedTrackLabels(shown);
            UpdateSelectedRatingVisuals(shown.Rating);
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
                await _engine.UpdateSmtcMetadata(track.DisplayTitle, track.Artist, track.Album);
        }

        UpdateSidebarCounts();
        return commit;
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
            int fullStars = (int)Math.Floor(captured);
            string starText = new string('★', fullStars) + (captured - fullStars >= 0.5 ? "½" : string.Empty);
            var rItem = new MenuFlyoutItem { Text = starText };
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
            removePlItem.Click += (_, _) => RemoveTracksFromPlaylist(activePl, selected);
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
            var single = selected[0];
            if (!string.IsNullOrWhiteSpace(single.Artist))
            {
                var byArtist = new MenuFlyoutItem { Text = $"Filter by artist: {single.Artist}" };
                byArtist.Click += (_, _) => ApplyArtistScope(single.Artist);
                menu.Items.Add(byArtist);
            }
            if (!string.IsNullOrWhiteSpace(single.Album))
            {
                var byAlbum = new MenuFlyoutItem { Text = $"Filter by album: {single.Album}" };
                byAlbum.Click += (_, _) => ApplyAlbumScope(single.Album);
                menu.Items.Add(byAlbum);
            }
            var explorerItem = new MenuFlyoutItem { Text = "Open in Explorer" };
            explorerItem.Click += (_, _) => OpenInExplorer(single.Path);
            menu.Items.Add(explorerItem);
        }

        var deleteItem = new MenuFlyoutItem { Text = selected.Count > 1 ? $"Delete {selected.Count} files to Recycle Bin..." : "Delete to Recycle Bin..." };
        deleteItem.Click += async (_, _) => await DeleteTracksToRecycleBinAsync(selected);
        menu.Items.Add(deleteItem);

        AddMenuIcons(menu);
        menu.ShowAt(view, e.GetPosition(view));
    }

    // Icons for the top-level actions of the track and playlist menus, matched on how each label starts
    // so the two menus stay consistent without tagging every item by hand. Order matters: longest prefix first.
    private static readonly (string Prefix, string Glyph)[] MenuIconMap =
    {
        ("Play next", ""),
        ("Play", ""),
        ("Show in library", ""),
        ("Add to playlist", ""),
        ("Add", ""),
        ("New playlist", "\uECC8"),
        ("Rate", ""),
        ("Remove", ""),
        ("Batch Properties", ""),
        ("Properties", ""),
        ("Auto-Tag", ""),
        ("Rename", ""),
        ("Open in Explorer", ""),
        ("Delete", ""),
        ("Import", ""),
        ("Export", ""),
        ("Duplicate", ""),
        ("Clear", ""),
        ("Save", ""),
        ("Curate", "\uE762"),
        ("Custom Smart Mix", "\uE945"),
        ("Sleep timer", "\uE708"),
    };

    private static void AddMenuIcons(MenuFlyout menu)
    {
        foreach (var entry in menu.Items)
        {
            string? text = entry switch
            {
                MenuFlyoutSubItem sub => sub.Text,
                MenuFlyoutItem item => item.Text,
                _ => null
            };
            if (string.IsNullOrEmpty(text)) continue;

            string? glyph = null;
            foreach (var (prefix, g) in MenuIconMap)
            {
                if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { glyph = g; break; }
            }
            if (glyph == null) continue;

            var icon = new FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Glyph = glyph };
            if (entry is MenuFlyoutSubItem s) s.Icon = icon;
            else if (entry is MenuFlyoutItem i) i.Icon = icon;
        }
    }

    private void RemoveTracksFromPlaylist(SavedPlaylist pl, IReadOnlyList<TrackItem> selected)
    {
        var snapshot = pl.TrackPaths.ToList();
        var toRemove = new HashSet<string>(selected.Select(s => s.Path), StringComparer.OrdinalIgnoreCase);
        int removed = pl.TrackPaths.RemoveAll(p => toRemove.Contains(p));
        if (removed <= 0) return;
        pl.NotifyTracksChanged();
        _settings.Save();
        if (_activePlaylist == pl) RefreshVisibleTracks();
        else SyncRightPanelList();
        PushUndo(() =>
        {
            pl.TrackPaths.Clear();
            pl.TrackPaths.AddRange(snapshot);
            pl.NotifyTracksChanged();
            _settings.Save();
            if (_activePlaylist == pl) RefreshVisibleTracks();
            else SyncRightPanelList();
            ShowHudPill($"Undone: Restored to \"{pl.Name}\"");
            return Task.CompletedTask;
        });
        ShowHudPill($"Removed {removed} track(s) from \"{pl.Name}\" (Ctrl+Z to undo)");
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
            if (_activePlaylist == pl) RefreshVisibleTracks();
            else SyncRightPanelList();
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
        _editingPlaylist = pl;
        SyncRightPanelList();
        OpenRightPanel();
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

    private async Task OpenQueueToPlaylistDialogAsync()
    {
        if (_queueTracks.Count == 0)
        {
            ShowHudPill("Play queue is empty");
            return;
        }

        var allQueue = _queueTracks.ToList();
        var checkedSet = new HashSet<TrackItem>(QueueListView.SelectedItems.OfType<TrackItem>());

        var nameBox = new TextBox
        {
            Header = "Playlist name",
            PlaceholderText = "Playlist name",
            Text = $"Playlist {_playlists.Count + 1}"
        };
        var filterBox = new TextBox
        {
            PlaceholderText = "Filter queue songs by title, artist, or album..."
        };
        var countText = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.75,
            VerticalAlignment = VerticalAlignment.Center
        };
        var selectVisBtn = new Button { Content = "Select visible", Padding = new Thickness(10, 4, 10, 4) };
        var clearSelBtn = new Button { Content = "Clear", Padding = new Thickness(10, 4, 10, 4) };

        var pickerList = new ListView
        {
            SelectionMode = ListViewSelectionMode.Multiple,
            Height = 320
        };

        var displayed = new List<TrackItem>();
        bool syncing = false;

        void UpdateSelectedCount()
        {
            countText.Text = checkedSet.Count == 0
                ? "Select the songs you want to include"
                : $"{checkedSet.Count:N0} track(s) selected";
        }

        void RebuildPickerItems()
        {
            syncing = true;
            try
            {
                string q = filterBox.Text.Trim();
                displayed = string.IsNullOrEmpty(q)
                    ? allQueue
                    : allQueue.Where(t =>
                        t.DisplayTitle.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        t.Artist.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        t.Album.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

                pickerList.Items.Clear();
                foreach (var t in displayed)
                {
                    var row = new Grid { Padding = new Thickness(0, 2, 0, 2) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var txt = new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(t.Artist) ? t.DisplayTitle : $"{t.DisplayTitle} — {t.Artist}",
                        FontSize = 12.5,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Tag = t
                    };
                    var dur = new TextBlock
                    {
                        Text = t.DurationText,
                        FontSize = 11.5,
                        Opacity = 0.65,
                        Margin = new Thickness(8, 0, 0, 0)
                    };
                    Grid.SetColumn(txt, 0);
                    Grid.SetColumn(dur, 1);
                    row.Children.Add(txt);
                    row.Children.Add(dur);
                    row.Tag = t;
                    pickerList.Items.Add(row);
                    if (checkedSet.Contains(t))
                        pickerList.SelectedItems.Add(row);
                }
                UpdateSelectedCount();
            }
            finally { syncing = false; }
        }

        pickerList.SelectionChanged += (_, args) =>
        {
            if (syncing) return;
            foreach (var added in args.AddedItems.OfType<FrameworkElement>())
                if (added.Tag is TrackItem t) checkedSet.Add(t);
            foreach (var removed in args.RemovedItems.OfType<FrameworkElement>())
                if (removed.Tag is TrackItem t) checkedSet.Remove(t);
            UpdateSelectedCount();
        };

        filterBox.TextChanged += (_, _) => RebuildPickerItems();
        selectVisBtn.Click += (_, _) =>
        {
            syncing = true;
            try
            {
                foreach (var row in pickerList.Items.OfType<FrameworkElement>())
                {
                    if (row.Tag is TrackItem t) checkedSet.Add(t);
                    if (!pickerList.SelectedItems.Contains(row)) pickerList.SelectedItems.Add(row);
                }
                UpdateSelectedCount();
            }
            finally { syncing = false; }
        };
        clearSelBtn.Click += (_, _) =>
        {
            syncing = true;
            try
            {
                checkedSet.Clear();
                pickerList.SelectedItems.Clear();
                UpdateSelectedCount();
            }
            finally { syncing = false; }
        };

        var actionRow = new Grid { ColumnSpacing = 8 };
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(countText, 0);
        Grid.SetColumn(selectVisBtn, 1);
        Grid.SetColumn(clearSelBtn, 2);
        actionRow.Children.Add(countText);
        actionRow.Children.Add(selectVisBtn);
        actionRow.Children.Add(clearSelBtn);

        var rootPanel = new StackPanel { Spacing = 10, MinWidth = 480 };
        rootPanel.Children.Add(nameBox);
        rootPanel.Children.Add(filterBox);
        rootPanel.Children.Add(actionRow);
        rootPanel.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush,
            Child = pickerList
        });

        RebuildPickerItems();

        var dlg = new ContentDialog
        {
            Title = "Curate Playlist from Queue",
            Content = rootPanel,
            PrimaryButtonText = "Create Playlist",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.RequestedTheme
        };
        dlg.Resources["ContentDialogMaxWidth"] = 560.0;
        DialogFooter.MainButtonOnRight(dlg);
        if (await dlg.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(nameBox.Text)) return;

        var chosen = allQueue.Where(t => checkedSet.Contains(t)).ToList();
        var pl = new SavedPlaylist { Name = nameBox.Text.Trim() };
        foreach (var path in chosen.Select(t => t.Path).Distinct(StringComparer.OrdinalIgnoreCase))
            pl.TrackPaths.Add(path);

        _playlists.Add(pl);
        _settings.Playlists.Add(pl);
        _settings.Save();
        _editingPlaylist = pl;
        SyncRightPanelList();
        OpenRightPanel();
        ShowHudPill(pl.TrackCount > 0
            ? $"Created \"{pl.Name}\" ({pl.TrackCount} tracks)"
            : $"Created \"{pl.Name}\" — drag songs here to add");
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
        _editingPlaylist = pl;
        SyncRightPanelList();
        OpenRightPanel();

        ShowHudPill(pl.TrackCount > 0
            ? $"Created \"{pl.Name}\" ({pl.TrackCount} tracks)"
            : $"Created \"{pl.Name}\" — drag songs here from your library");
    }

    private async void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Never intercept keys while a ContentDialog or Flyout popup is open
        if (RootGrid.XamlRoot != null && VisualTreeHelper.GetOpenPopupsForXamlRoot(RootGrid.XamlRoot).Count > 0)
        {
            if (e.Key == VirtualKey.Escape && FocusVisualizerOverlay.Visibility == Visibility.Visible)
            {
                CloseFocusStage();
                e.Handled = true;
            }
            return;
        }

        // While the settings panel is open, Esc closes it and every other key belongs to its controls.
        if (SettingsOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == VirtualKey.Escape)
            {
                CloseSettingsPanel();
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
            CloseFocusStage();
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
            RightPaneToggleBtn_Click(sender, e);
        }
        else if (e.Key == VirtualKey.Delete && !inTextBox)
        {
            if (IsInsideElement(focused as DependencyObject, QueueListView))
            {
                var rightSel = QueueListView.SelectedItems.OfType<TrackItem>().ToList();
                if (rightSel.Count > 0)
                {
                    e.Handled = true;
                    if (_editingPlaylist != null)
                        RemoveTracksFromPlaylist(_editingPlaylist, rightSel);
                    else
                        RemoveSelectedFromRightQueue(rightSel);
                }
                return;
            }
            var selected = GetSelectedTracks();
            if (selected.Count > 0)
            {
                e.Handled = true;
                if (_activePlaylist != null)
                    RemoveTracksFromPlaylist(_activePlaylist, selected);
                else
                    await DeleteTracksToRecycleBinAsync(selected);
            }
        }
        else if ((e.Key == VirtualKey.Left || e.Key == VirtualKey.Right) &&
                 !inTextBox &&
                 focused is not Slider &&
                 _engine.Duration.TotalSeconds > 0 &&
                 (ctrl || !(IsTileMode && FocusVisualizerOverlay.Visibility != Visibility.Visible && IsInsideElement(focused as DependencyObject, AlbumsGridView))))
        {
            e.Handled = true;
            double delta = e.Key == VirtualKey.Right ? 5.0 : -5.0;
            var target = _engine.Position + TimeSpan.FromSeconds(delta);
            _engine.Seek(target);
            SyncPlaybackProgressUi(_engine.Position, _engine.Duration);
        }
        else if ((e.Key == VirtualKey.Home || e.Key == VirtualKey.End) && !inTextBox && !ctrl && !alt && focused is not Slider)
        {
            if (IsInsideElement(focused as DependencyObject, QueueListView))
            {
                var rightList = _editingPlaylist != null ? _editingPlaylistTracks : _queueTracks;
                if (rightList.Count > 0)
                {
                    var target = e.Key == VirtualKey.Home ? rightList[0] : rightList[rightList.Count - 1];
                    QueueListView.SelectedItem = target;
                    QueueListView.UpdateLayout();
                    QueueListView.ScrollIntoView(target, e.Key == VirtualKey.Home ? ScrollIntoViewAlignment.Leading : ScrollIntoViewAlignment.Default);
                    e.Handled = true;
                }
            }
            else if (_visibleTracks.Count > 0)
            {
                var target = e.Key == VirtualKey.Home ? _visibleTracks[0] : _visibleTracks[_visibleTracks.Count - 1];
                SyncSelectionAcrossViews(target);
                UpdateInspectorForSelection(target);
                ScrollActiveViewToTrack(target, e.Key == VirtualKey.Home ? ScrollIntoViewAlignment.Leading : ScrollIntoViewAlignment.Default);
                (IsTileMode ? (Control)AlbumsGridView : TracksListView).Focus(FocusState.Programmatic);
                e.Handled = true;
            }
        }
        else if (e.Key == VirtualKey.Enter && !inTextBox && !ctrl && !alt &&
                 focused is not Button && focused is not ComboBox && focused is not Slider)
        {
            if (IsInsideElement(focused as DependencyObject, QueueListView))
            {
                var qTrack = QueueListView.SelectedItem as TrackItem;
                if (_editingPlaylist != null)
                {
                    if (qTrack != null && _editingPlaylistTracks.Contains(qTrack))
                    {
                        PlayPlaylistTracks(_editingPlaylist, qTrack);
                        RevealTrackInMainList(qTrack);
                        e.Handled = true;
                    }
                }
                else
                {
                    int qIdx = qTrack != null ? _queueTracks.IndexOf(qTrack) : QueueListView.SelectedIndex;
                    if (qIdx >= 0 && qIdx < _queueTracks.Count)
                    {
                        var track = _queueTracks[qIdx];
                        PlayQueueIndex(qIdx);
                        RevealTrackInMainList(track);
                        e.Handled = true;
                    }
                }
            }
            else if ((IsInsideElement(focused as DependencyObject, TracksListView) ||
                      IsInsideElement(focused as DependencyObject, AlbumsGridView)) &&
                     GetPrimarySelectedTrack() is TrackItem track)
            {
                PlayFromList(_visibleTracks.ToList(), track);
                e.Handled = true;
            }
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

    private void ApplyArtistScope(string artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return;
        _activeFacet = "all";
        _activePlaylist = null;
        _activeCustomFilter = null;
        _activeHygiene = null;
        _activeAlbumFilter = null;
        _activeArtistFilter = artist.Trim();
        RefreshVisibleTracks(scrollToSelection: true);
    }

    private void ApplyAlbumScope(string album)
    {
        if (string.IsNullOrWhiteSpace(album)) return;
        _activeFacet = "all";
        _activePlaylist = null;
        _activeCustomFilter = null;
        _activeHygiene = null;
        _activeArtistFilter = null;
        _activeAlbumFilter = album.Trim();
        RefreshVisibleTracks(scrollToSelection: true);
    }

    private async void BrowseAlbums_Click(object sender, RoutedEventArgs e)
        => await OpenBrowseAlbumsDialogAsync();

    private async void BrowseArtists_Click(object sender, RoutedEventArgs e)
        => await OpenBrowseArtistsDialogAsync();

    private void QuickMixBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_allTracks.Count == 0)
        {
            ShowHudPill("Open a music folder first");
            return;
        }

        TrackItem? contextTrack = (_queueIndex >= 0 && _queueIndex < _queueTracks.Count)
            ? _queueTracks[_queueIndex]
            : GetPrimarySelectedTrack();

        var menu = new MenuFlyout();
        foreach (var preset in SmartPlaylistGenerator.GetQuickPresets(contextTrack))
        {
            var captured = preset;
            var item = new MenuFlyoutItem { Text = captured.Name };
            ToolTipService.SetToolTip(item, captured.Subtitle);
            item.Click += (_, _) =>
            {
                var tracks = SmartPlaylistGenerator.Generate(_allTracks, captured);
                if (tracks.Count == 0)
                {
                    ShowHudPill($"No matching tracks for \"{captured.Name}\"");
                    return;
                }
                LoadSmartMixIntoQueue(captured.Name, tracks);
            };
            menu.Items.Add(item);
        }

        menu.Items.Add(new MenuFlyoutSeparator());

        var custom = new MenuFlyoutItem { Text = "Custom Smart Mix..." };
        custom.Click += async (_, _) => await OpenCustomSmartMixDialogAsync();
        menu.Items.Add(custom);

        if (_queueTracks.Count > 0)
        {
            var saveQ = new MenuFlyoutItem { Text = $"Save queue as playlist ({_queueTracks.Count})..." };
            saveQ.Click += async (_, _) => await OpenQueueToPlaylistDialogAsync();
            menu.Items.Add(saveQ);
        }

        AddMenuIcons(menu);
        menu.ShowAt(QuickMixBtn);
    }

    private void LoadSmartMixIntoQueue(string mixName, List<TrackItem> mixTracks, bool autoPlay = true)
    {
        if (mixTracks.Count == 0)
        {
            ShowHudPill($"No matching tracks for \"{mixName}\"");
            return;
        }

        _editingPlaylist = null;
        _unshuffledQueue.Clear();
        _unshuffledQueue.AddRange(mixTracks);

        _queueTracks.Clear();
        foreach (var t in mixTracks)
            _queueTracks.Add(t);

        OpenRightPanel();
        if (autoPlay)
        {
            PlayQueueIndex(0);
        }
        else
        {
            _queueIndex = -1;
            UpdateQueueHeader();
        }

        ShowHudPill($"{mixName} ({mixTracks.Count}) — reorder/edit here or save via ···");
    }
}
