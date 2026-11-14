using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.UI;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
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
    private readonly ObservableCollection<SavedPlaylist> _playlists = new();
    private readonly ObservableCollection<CustomFilterCriteria> _customFilters = new();

    private CancellationTokenSource? _scanCts;
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

    private static readonly SolidColorBrush PlayingRowBrush = new(Color.FromArgb(28, 255, 200, 59));
    private static readonly SolidColorBrush EvenRowBrush = new(Color.FromArgb(7, 255, 255, 255));
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
    public static Brush NowPlayingRowBg(bool isNowPlaying) => isNowPlaying ? PlayingRowBrush : TransparentRowBrush;
    public static Brush RowBackground(bool isNowPlaying, int index)
        => isNowPlaying ? PlayingRowBrush : (index % 2 == 0 ? EvenRowBrush : TransparentRowBrush);
    private bool _leftPaneOpen;
    private bool _rightPaneOpen = true;
    private bool _initialized;
    private string _activeFacet = "all";
    private SavedPlaylist? _activePlaylist;
    private CustomFilterCriteria? _activeCustomFilter;
    private string _typeJumpBuffer = string.Empty;
    private DateTime _lastTypeJumpUtc = DateTime.MinValue;
    private CancellationTokenSource? _jumpPillCts;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);

        _settings = AppSettings.Load();
        RestoreWindowBounds();
        ApplyTheme(_settings.Theme);
        ApplyInspectorArtMode();

        _engine = new WasapiAudioEngine(DispatcherQueue);
        _engine.TrackEnded += Engine_TrackEnded;
        _engine.PlaybackStateChanged += Engine_PlaybackStateChanged;
        _engine.PlaybackError += Engine_PlaybackError;
        _engine.SmtcNextRequested += () => PlayNext();
        _engine.SmtcPreviousRequested += () => PlayPrevious();

        ApplyAudioConfigFromSettings();

        PlaybarVisualizer.AttachAnalyzer(_engine.Fft);
        FocusVisualizer.AttachAnalyzer(_engine.Fft);
        ApplyVisualizerFromSettings();

        VolumeSlider.Value = _settings.Volume;
        _engine.SetVolume(_settings.Volume);
        ShuffleToggle.IsChecked = _settings.Shuffle;
        RepeatToggle.IsChecked = _settings.RepeatAll;

        TracksListView.ItemsSource = _visibleTracks;
        QueueListView.ItemsSource = _queueTracks;
        foreach (var pl in _settings.Playlists) _playlists.Add(pl);
        PlaylistsList.ItemsSource = _playlists;
        foreach (var cf in _settings.CustomFilters) _customFilters.Add(cf);
        CustomFiltersList.ItemsSource = _customFilters;
        UpdateSortHeaders();

        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _progressTimer.Tick += ProgressTimer_Tick;

        SeekSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(SeekSlider_PointerPressed), true);
        SeekSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(SeekSlider_PointerReleased), true);
        foreach (var starBtn in new[] { StarBtn1, StarBtn2, StarBtn3, StarBtn4, StarBtn5 })
            starBtn.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(StarBtn_PointerPressed), true);

        SearchBox.SizeChanged += (_, _) => UpdateTitleBarPassthrough();
        SearchBox.LayoutUpdated += (_, _) => UpdateTitleBarPassthrough();

        Closed += (_, _) =>
        {
            _progressTimer.Stop();
            _scanCts?.Cancel();
            SaveWindowBounds();
            _settings.Save();
            _engine.Dispose();
        };

        if (!string.IsNullOrWhiteSpace(_settings.MusicFolder) && Directory.Exists(_settings.MusicFolder))
        {
            _ = ScanFolderAsync(_settings.MusicFolder);
        }
        _initialized = true;
    }

    private void RestoreWindowBounds()
    {
        try
        {
            var appWin = this.AppWindow;
            var display = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(appWin.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            var work = display.WorkArea;
            int w = Math.Clamp(_settings.WindowWidth, Math.Min(1080, work.Width), work.Width);
            int h = Math.Clamp(_settings.WindowHeight, Math.Min(640, work.Height), work.Height);
            if (_settings.WindowX is int x && _settings.WindowY is int y)
            {
                appWin.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h));
            }
            else
            {
                int cx = work.X + Math.Max(0, (work.Width - w) / 2);
                int cy = work.Y + Math.Max(0, (work.Height - h) / 2);
                appWin.MoveAndResize(new Windows.Graphics.RectInt32(cx, cy, w, h));
            }

            if (_settings.IsMaximized && appWin.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
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
            var appWin = this.AppWindow;
            bool isMax = appWin.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p &&
                         p.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized;
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
        ThemeIcon.Glyph = dark ? "\uE706" : "\uE708";
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
        ToolTipService.SetToolTip(SpeakerBadgeText, endpointCh >= 6 || _settings.SpeakerLayout == SpeakerLayout.Stereo20
            ? $"Output: {endpointCh}ch WASAPI"
            : $"Windows audio device reports {endpointCh}ch; using spatial stereo fold-down");
    }

    private void ApplyVisualizerFromSettings()
    {
        var mode = !_settings.VisualizerEnabled ? VisualizerMode.Off : _settings.VisualizerMode switch
        {
            "Waveform" => VisualizerMode.Waveform,
            "Off" => VisualizerMode.Off,
            _ => VisualizerMode.Spectrum
        };

        PlaybarVisualizer.Mode = mode;
        FocusVisualizer.Mode = mode == VisualizerMode.Off ? VisualizerMode.Spectrum : mode;
        PlaybarVisualizerHost.Visibility = mode == VisualizerMode.Off ? Visibility.Collapsed : Visibility.Visible;
        PlaybarVisualizer.IsAudioPlaying = _engine.IsPlaying;
        FocusVisualizer.IsAudioPlaying = _engine.IsPlaying;
    }

    private async Task ScanFolderAsync(string folder)
    {
        _scanCts?.Cancel();
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        int myScan = Interlocked.Increment(ref _scanId);

        _allTracks.Clear();
        _visibleTracks.Clear();
        EmptyStatePanel.Visibility = Visibility.Collapsed;
        ScopePillText.Text = "Scanning...";

        var tracks = await LibraryScanner.ScanFolderAsync(folder, cts.Token, batch =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (cts.IsCancellationRequested || myScan != _scanId) return;
                _allTracks.AddRange(batch);
                if (_activePlaylist == null && _activeFacet == "all" && string.IsNullOrWhiteSpace(SearchBox.Text))
                {
                    foreach (var t in batch) _visibleTracks.Add(t);
                }
                ScopePillText.Text = $"Scanning... {_allTracks.Count:N0} tracks";
            });
        });

        if (cts.IsCancellationRequested || myScan != _scanId) return;

        _allTracks.Clear();
        _allTracks.AddRange(tracks);
        RefreshVisibleTracks();
        EmptyStatePanel.Visibility = _allTracks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshVisibleTracks()
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

        if (!string.IsNullOrEmpty(query))
        {
            source = source.Where(t =>
                t.DisplayTitle.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                t.Artist.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                t.Album.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                t.Path.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        if (_activeFacet != "recent")
        {
            source = ApplySort(source, _settings.SortColumn, _settings.SortAscending);
        }

        var previousSelection = TracksListView.SelectedItem as TrackItem;
        var filtered = source.ToList();
        _visibleTracks = new ObservableCollection<TrackItem>(filtered);
        TracksListView.ItemsSource = _visibleTracks;

        if (previousSelection != null && _visibleTracks.Contains(previousSelection))
        {
            TracksListView.SelectedItem = previousSelection;
            int idx = _visibleTracks.IndexOf(previousSelection);
            int anchorIdx = Math.Max(0, idx - 4);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (TracksListView.SelectedItem == previousSelection && anchorIdx < _visibleTracks.Count)
                    TracksListView.ScrollIntoView(_visibleTracks[anchorIdx], ScrollIntoViewAlignment.Leading);
            });
        }

        string scopeName = _activePlaylist?.Name ?? _activeCustomFilter?.Name ?? _activeFacet switch
        {
            "recent" => "Recently added",
            "toprated" => "Top rated",
            "5star" => "5★ Favorites",
            "unrated" => "Unrated",
            _ => "All music"
        };
        ScopePillText.Text = $"{scopeName} · {_visibleTracks.Count:N0} tracks";
        UpdateSidebarCounts();
        UpdateStatusFooter();
    }

    private void UpdateSidebarCounts()
    {
        int total = _allTracks.Count;
        int topRated = _allTracks.Count(t => t.Rating is double r && r >= 4.0);
        int fiveStar = _allTracks.Count(t => t.Rating is double r && r >= 4.9);
        int unrated = _allTracks.Count(t => t.Rating is null or <= 0);

        FacetAllItem.Content = total > 0 ? $"All music ({total:N0})" : "All music";
        FacetRecentItem.Content = total > 0 ? $"Recently added ({Math.Min(200, total)})" : "Recently added";
        FacetTopRatedItem.Content = total > 0 ? $"Top rated 4★+ ({topRated:N0})" : "Top rated (4★+)";
        FacetFiveStarItem.Content = total > 0 ? $"5★ Favorites ({fiveStar:N0})" : "5★ Favorites";
        FacetUnratedItem.Content = total > 0 ? $"Unrated ({unrated:N0})" : "Unrated";
    }

    private void UpdateStatusFooter()
    {
        int count = _visibleTracks.Count;
        var totalDur = TimeSpan.FromSeconds(_visibleTracks.Sum(t => t.Duration.TotalSeconds));
        string totalText = $"{count:N0} {(count == 1 ? "track" : "tracks")} · {FormatLongDuration(totalDur)} total";

        var sel = TracksListView.SelectedItems.OfType<TrackItem>().ToList();
        if (sel.Count > 1)
        {
            var selDur = TimeSpan.FromSeconds(sel.Sum(t => t.Duration.TotalSeconds));
            StatusFooterText.Text = $"{totalText}   |   {sel.Count:N0} tracks selected ({FormatLongDuration(selDur)})";
        }
        else
        {
            StatusFooterText.Text = totalText;
        }
    }

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
            ("Title", true) => source.OrderBy(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase),
            ("Title", false) => source.OrderByDescending(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase),
            ("Artist", true) => source.OrderBy(t => t.Artist, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Album, StringComparer.OrdinalIgnoreCase),
            ("Artist", false) => source.OrderByDescending(t => t.Artist, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Album, StringComparer.OrdinalIgnoreCase),
            ("Album", true) => source.OrderBy(t => t.Album, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Index),
            ("Album", false) => source.OrderByDescending(t => t.Album, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Index),
            ("Year", true) => source.OrderBy(t => t.Year).ThenBy(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase),
            ("Year", false) => source.OrderByDescending(t => t.Year).ThenBy(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase),
            ("Rating", true) => source.OrderBy(t => t.Rating ?? 0).ThenBy(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase),
            ("Rating", false) => source.OrderByDescending(t => t.Rating ?? 0).ThenBy(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase),
            ("Duration", true) => source.OrderBy(t => t.Duration),
            ("Duration", false) => source.OrderByDescending(t => t.Duration),
            ("DateAdded", true) => source.OrderBy(t => t.DateAddedUtc),
            ("DateAdded", false) => source.OrderByDescending(t => t.DateAddedUtc),
            ("Bitrate", true) => source.OrderBy(t => t.Bitrate),
            ("Bitrate", false) => source.OrderByDescending(t => t.Bitrate),
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
        _settings.Save();
        UpdateSortHeaders();
        RefreshVisibleTracks();
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

        _settings.MusicFolder = folder.Path;
        _settings.Save();
        await ScanFolderAsync(folder.Path);
    }

    private async void RescanBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_settings.MusicFolder))
            await ScanFolderAsync(_settings.MusicFolder);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initialized) return;
        RefreshVisibleTracks();
    }

    private void FacetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || FacetList.SelectedItem is not ListViewItem item) return;
        PlaylistsList.SelectedIndex = -1;
        CustomFiltersList.SelectedIndex = -1;
        _activePlaylist = null;
        _activeCustomFilter = null;
        _activeFacet = item.Tag?.ToString() ?? "all";
        RefreshVisibleTracks();
    }

    private void CustomFiltersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || CustomFiltersList.SelectedItem is not CustomFilterCriteria cf) return;
        FacetList.SelectedIndex = -1;
        PlaylistsList.SelectedIndex = -1;
        _activePlaylist = null;
        _activeCustomFilter = cf;
        RefreshVisibleTracks();
    }

    private void CustomFiltersList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not CustomFilterCriteria cf) return;
        var menu = new MenuFlyout();
        var delItem = new MenuFlyoutItem { Text = $"Remove \"{cf.Name}\"" };
        delItem.Click += (_, _) =>
        {
            _customFilters.Remove(cf);
            _settings.CustomFilters.Remove(cf);
            _settings.Save();
            if (_activeCustomFilter == cf)
            {
                _activeCustomFilter = null;
                FacetList.SelectedIndex = 0;
            }
        };
        menu.Items.Add(delItem);
        menu.ShowAt(CustomFiltersList, e.GetPosition(CustomFiltersList));
    }

    private async void NewFilterBtn_Click(object sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox { PlaceholderText = "e.g. 3★+ Favorites", Header = "Filter name" };
        var ratingBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Header = "Minimum star rating" };
        ratingBox.Items.Add(new ComboBoxItem { Content = "Any rating", Tag = 0.0 });
        ratingBox.Items.Add(new ComboBoxItem { Content = "1★ and up", Tag = 1.0 });
        ratingBox.Items.Add(new ComboBoxItem { Content = "2★ and up", Tag = 2.0 });
        ratingBox.Items.Add(new ComboBoxItem { Content = "3★ and up", Tag = 3.0 });
        ratingBox.Items.Add(new ComboBoxItem { Content = "3.5★ and up", Tag = 3.5 });
        ratingBox.Items.Add(new ComboBoxItem { Content = "4★ and up", Tag = 4.0 });
        ratingBox.Items.Add(new ComboBoxItem { Content = "4.5★ and up", Tag = 4.5 });
        ratingBox.Items.Add(new ComboBoxItem { Content = "5★ only", Tag = 5.0 });
        ratingBox.SelectedIndex = 3; // Default 3★+

        var textBox = new TextBox { PlaceholderText = "Optional artist, genre, album, or keyword", Header = "Contains text (optional)" };

        var panel = new StackPanel { Spacing = 10, MinWidth = 320 };
        panel.Children.Add(nameBox);
        panel.Children.Add(ratingBox);
        panel.Children.Add(textBox);

        var dlg = new ContentDialog
        {
            Title = "New library filter",
            Content = panel,
            PrimaryButtonText = "Add filter",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = ElementTheme.Dark
        };

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        double minRating = (double)((ratingBox.SelectedItem as ComboBoxItem)?.Tag ?? 0.0);
        string text = textBox.Text?.Trim() ?? string.Empty;
        string name = nameBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(name))
        {
            name = minRating > 0 && !string.IsNullOrEmpty(text) ? $"{minRating:0.#}★+ · {text}"
                 : minRating > 0 ? $"{minRating:0.#}★+ Rated"
                 : !string.IsNullOrEmpty(text) ? text
                 : $"Filter {_customFilters.Count + 1}";
        }

        var cf = new CustomFilterCriteria
        {
            Name = name,
            MinRating = minRating > 0 ? minRating : null,
            TextContains = string.IsNullOrEmpty(text) ? null : text
        };
        _customFilters.Add(cf);
        _settings.CustomFilters.Add(cf);
        _settings.Save();
        CustomFiltersList.SelectedItem = cf;
    }

    private void PlaylistsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || PlaylistsList.SelectedItem is not SavedPlaylist pl) return;
        FacetList.SelectedIndex = -1;
        CustomFiltersList.SelectedIndex = -1;
        _activeCustomFilter = null;
        _activePlaylist = pl;
        RefreshVisibleTracks();
    }

    private void PlaylistsList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not SavedPlaylist pl) return;
        var menu = new MenuFlyout();

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
                RequestedTheme = ElementTheme.Dark
            };
            if (await dlg.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text))
            {
                pl.Name = box.Text.Trim();
                int idx = _playlists.IndexOf(pl);
                if (idx >= 0) _playlists[idx] = pl;
                _settings.Save();
                if (_activePlaylist == pl) RefreshVisibleTracks();
            }
        };
        menu.Items.Add(renameItem);

        var deleteItem = new MenuFlyoutItem { Text = $"Delete \"{pl.Name}\"" };
        deleteItem.Click += (_, _) =>
        {
            _playlists.Remove(pl);
            _settings.Playlists.Remove(pl);
            _settings.Save();
            if (_activePlaylist == pl)
            {
                _activePlaylist = null;
                FacetList.SelectedIndex = 0;
            }
        };
        menu.Items.Add(deleteItem);

        menu.ShowAt(PlaylistsList, e.GetPosition(PlaylistsList));
    }

    private void LeftPaneToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        _leftPaneOpen = !_leftPaneOpen;
        LeftPaneCol.Width = _leftPaneOpen ? new GridLength(200) : new GridLength(0);
        LeftSidebar.Visibility = _leftPaneOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RightPaneToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        _rightPaneOpen = !_rightPaneOpen;
        RightPaneCol.Width = _rightPaneOpen ? new GridLength(276) : new GridLength(0);
        RightSidebar.Visibility = _rightPaneOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ThemeToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        _settings.Theme = string.Equals(_settings.Theme, "Light", StringComparison.OrdinalIgnoreCase) ? "Dark" : "Light";
        _settings.Save();
        ApplyTheme(_settings.Theme);
    }

    private void PlayAllBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_visibleTracks.Count == 0) return;
        var startTrack = TracksListView.SelectedItem as TrackItem ?? _visibleTracks[0];
        PlayFromList(_visibleTracks.ToList(), startTrack);
    }

    private void TracksListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (IsInsideType<StarRatingCell>(e.OriginalSource as DependencyObject)) return;
        if (TracksListView.SelectedItem is TrackItem track)
            PlayFromList(_visibleTracks.ToList(), track);
    }

    private void RowRatingCell_RatingSelected(object? sender, double? newRating)
    {
        if (sender is not FrameworkElement el || el.DataContext is not TrackItem track) return;
        var targets = TracksListView.SelectedItems.Contains(track) && TracksListView.SelectedItems.Count > 1
            ? TracksListView.SelectedItems.OfType<TrackItem>().ToList()
            : new List<TrackItem> { track };

        if (!TracksListView.SelectedItems.Contains(track))
            TracksListView.SelectedItem = track;

        foreach (var t in targets) t.Rating = newRating;
        if (TracksListView.SelectedItem == track)
            UpdateSelectedRatingVisuals(newRating);
        SyncPlayingTrackRating(targets, newRating);
        UpdateSidebarCounts();

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
            foreach (var t in _queueTracks) t.IsNowPlaying = false;
            _queueTracks.Clear();
            _queueIndex = -1;
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
    }

    private void PlayFromList(IReadOnlyList<TrackItem> list, TrackItem startTrack)
    {
        _queueTracks.Clear();
        foreach (var t in list) _queueTracks.Add(t);
        int idx = _queueTracks.IndexOf(startTrack);
        PlayQueueIndex(idx >= 0 ? idx : 0);
    }

    private void PlayQueueIndex(int index)
    {
        if (index < 0 || index >= _queueTracks.Count) return;

        if (_queueIndex >= 0 && _queueIndex < _queueTracks.Count)
            _queueTracks[_queueIndex].IsNowPlaying = false;

        _queueIndex = index;
        var track = _queueTracks[index];
        track.IsNowPlaying = true;

        UpdateQueueHeader();
        QueueListView.SelectedIndex = index;
        QueueListView.ScrollIntoView(track);

        NowPlayingTitleText.Text = track.DisplayTitle;
        NowPlayingArtistText.Text = string.IsNullOrWhiteSpace(track.Artist) ? track.FileName : track.Artist;
        NowPlayingRatingCell.Value = track.Rating;
        FocusTitleText.Text = track.DisplayTitle;
        FocusArtistText.Text = NowPlayingArtistText.Text;
        FocusRatingCell.Value = track.Rating;

        _engine.UpdateSmtcMetadata(track.DisplayTitle, track.Artist, track.Album);
        _ = LoadArtAsync(track.Path, NowPlayingArtImage, FocusArtImage);
        _ = LoadFocusLyricsAsync(track.Path);
        _engine.PlayFile(track.Path);
    }

    private async Task LoadFocusLyricsAsync(string path)
    {
        string lyrics = await Task.Run(() => MusicLibrary.ReadLyrics(path));
        if (!string.Equals(_engine.CurrentPath, path, StringComparison.OrdinalIgnoreCase)) return;
        string trimmed = lyrics?.Trim() ?? string.Empty;
        FocusLyricsText.Text = trimmed;
        FocusLyricsHost.Visibility = string.IsNullOrEmpty(trimmed) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Engine_PlaybackStateChanged(bool playing)
    {
        PlayPauseIcon.Glyph = playing ? "\uE769" : "\uE768";
        PlaybarVisualizer.IsAudioPlaying = playing;
        FocusVisualizer.IsAudioPlaying = playing;

        if (playing) _progressTimer.Start();
        else _progressTimer.Stop();
    }

    private void Engine_TrackEnded() => PlayNext();

    private async void Engine_PlaybackError(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "Playback error",
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = ElementTheme.Dark
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
            finally
            {
                _isUpdatingSeekSlider = false;
            }
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

    private void NextBtn_Click(object sender, RoutedEventArgs e) => PlayNext();
    private void PrevBtn_Click(object sender, RoutedEventArgs e) => PlayPrevious();

    private void PlayNext()
    {
        if (_queueTracks.Count == 0) return;
        int next;
        if (_settings.Shuffle && _queueTracks.Count > 1)
        {
            do { next = Random.Shared.Next(_queueTracks.Count); }
            while (next == _queueIndex);
        }
        else
        {
            next = _queueIndex + 1;
            if (next >= _queueTracks.Count)
            {
                if (!_settings.RepeatAll) { _engine.Stop(); return; }
                next = 0;
            }
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
        if (prev < 0) prev = _settings.RepeatAll ? _queueTracks.Count - 1 : 0;
        PlayQueueIndex(prev);
    }

    private void SeekSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isDraggingSeek = true;
    }

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

    private void SeekSlider_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        _engine.Seek(TimeSpan.FromSeconds(SeekSlider.Value));
    }

    private void SeekSlider_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_engine == null || _engine.Duration.TotalSeconds <= 0 || SeekSlider.ActualWidth <= 0) return;
        double ratio = Math.Clamp(e.GetCurrentPoint(SeekSlider).Position.X / SeekSlider.ActualWidth, 0.0, 1.0);
        var hoverTime = TimeSpan.FromSeconds(ratio * _engine.Duration.TotalSeconds);
        ToolTipService.SetToolTip(SeekSlider, $"{FormatTime(hoverTime)} / {FormatTime(_engine.Duration)}");
    }

    private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
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
    }

    private void RepeatToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.RepeatAll = RepeatToggle.IsChecked == true;
        _settings.Save();
    }

    private void VisualizerToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        _settings.VisualizerEnabled = !_settings.VisualizerEnabled;
        _settings.Save();
        ApplyVisualizerFromSettings();
    }

    private void PlaybarVisualizerHost_Tapped(object sender, TappedRoutedEventArgs e) => ToggleFocusVisualizer();
    private void CloseFocusBtn_Click(object sender, RoutedEventArgs e) => FocusVisualizerOverlay.Visibility = Visibility.Collapsed;

    private void ToggleFocusVisualizer()
    {
        FocusVisualizerOverlay.Visibility = FocusVisualizerOverlay.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void TracksListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateStatusFooter();
        if (TracksListView.SelectedItem is not TrackItem track)
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

    private void SelectedArtBorder_Tapped(object sender, TappedRoutedEventArgs e)
    {
        _inspectorArtMode = (_inspectorArtMode + 1) % 3;
        ApplyInspectorArtMode();
    }

    private void SelectedInfoOverlay_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_inspectorArtMode != 2) return;
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
        _inspectorArtMode = 0;
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
        string trimmed = lyrics?.Trim() ?? string.Empty;
        SelectedLyricsText.Text = trimmed;
        SelectedLyricsContainer.Visibility = string.IsNullOrEmpty(trimmed) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateTitleBarPassthrough()
    {
        try
        {
            if (RootGrid.XamlRoot == null || SearchBox.ActualWidth <= 0) return;
            double scale = RootGrid.XamlRoot.RasterizationScale;
            var pt = SearchBox.TransformToVisual(null).TransformPoint(new Point(0, 0));
            var rect = new RectInt32(
                (int)Math.Floor(pt.X * scale),
                (int)Math.Floor(pt.Y * scale),
                (int)Math.Ceiling(SearchBox.ActualWidth * scale),
                (int)Math.Ceiling(SearchBox.ActualHeight * scale));
            if (rect.X == _lastSearchPassthroughRect.X && rect.Y == _lastSearchPassthroughRect.Y &&
                rect.Width == _lastSearchPassthroughRect.Width && rect.Height == _lastSearchPassthroughRect.Height)
                return;

            _lastSearchPassthroughRect = rect;
            var nonClient = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            nonClient.SetRegionRects(NonClientRegionKind.Passthrough, new[] { rect });
        }
        catch { }
    }

    private void NowPlayingInfo_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (IsInsideType<StarRatingCell>(e.OriginalSource as DependencyObject)) return;
        if (_queueIndex < 0 || _queueIndex >= _queueTracks.Count) return;
        RevealTrackInMainList(_queueTracks[_queueIndex]);
    }

    private void RevealTrackInMainList(TrackItem track)
    {
        int idx = _visibleTracks.IndexOf(track);
        if (idx < 0)
        {
            // Track is hidden by current search/facet/playlist; switch to All Music to reveal it.
            if (!string.IsNullOrEmpty(SearchBox.Text))
                SearchBox.Text = string.Empty;
            _activeFacet = "all";
            _activePlaylist = null;
            _activeCustomFilter = null;
            RefreshVisibleTracks();
            idx = _visibleTracks.IndexOf(track);
            if (idx < 0) return;
        }

        TracksListView.SelectedItem = track;
        TracksListView.ScrollIntoView(track, ScrollIntoViewAlignment.Default);
    }

    private void NowPlayingRatingCell_RatingSelected(object? sender, double? newRating)
    {
        if (_queueIndex < 0 || _queueIndex >= _queueTracks.Count) return;
        var playing = _queueTracks[_queueIndex];
        playing.Rating = newRating;
        NowPlayingRatingCell.Value = newRating;
        FocusRatingCell.Value = newRating;

        foreach (var t in _allTracks.Where(x => string.Equals(x.Path, playing.Path, StringComparison.OrdinalIgnoreCase)))
            t.Rating = newRating;

        if (TracksListView.SelectedItem is TrackItem sel && string.Equals(sel.Path, playing.Path, StringComparison.OrdinalIgnoreCase))
            UpdateSelectedRatingVisuals(newRating);

        UpdateSidebarCounts();
        string path = playing.Path;
        _ = Task.Run(() => MusicLibrary.TryWriteStars(path, newRating));
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
            // Sample the bottom 55% of the artwork where the overlay sits.
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
        catch
        {
            return false;
        }
    }

    private static async Task SetImageFromBytesAsync(byte[]? bytes, params Image[] targets)
    {
        if (bytes == null || bytes.Length == 0)
        {
            foreach (var img in targets) img.Source = null;
            return;
        }

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
            await bmp.SetSourceAsync(stream);
            foreach (var img in targets) img.Source = bmp;
        }
        catch
        {
            foreach (var img in targets) img.Source = null;
        }
    }

    private void StarBtn_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Button btn || !int.TryParse(btn.Tag?.ToString(), out int starIndex)) return;
        double x = e.GetCurrentPoint(btn).Position.X;
        double width = btn.ActualWidth > 0 ? btn.ActualWidth : 20.0;
        double rating = x < width * 0.5 ? starIndex - 0.5 : starIndex;
        _lastStarPointerUtc = DateTime.UtcNow;
        var current = (TracksListView.SelectedItem as TrackItem)?.Rating;
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
            var current = (TracksListView.SelectedItem as TrackItem)?.Rating;
            ApplyRatingToSelection(Nullable.Equals(current, stars) ? null : stars);
        }
    }

    private void ApplyRatingToSelection(double? value)
    {
        var selected = TracksListView.SelectedItems.OfType<TrackItem>().ToList();
        if (selected.Count == 0) return;

        foreach (var t in selected) t.Rating = value;
        UpdateSelectedRatingVisuals(value);
        SyncPlayingTrackRating(selected, value);
        UpdateSidebarCounts();

        _ = Task.Run(() =>
        {
            foreach (var t in selected) MusicLibrary.TryWriteStars(t.Path, value);
        });
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
        if (TracksListView.SelectedItem is TrackItem track)
            await OpenPropertiesDialogAsync(track);
    }

    private async Task OpenPropertiesDialogAsync(TrackItem track)
    {
        var details = await Task.Run(() => MusicLibrary.ReadFullDetails(track.Path));

        var artPreview = new Image { Width = 88, Height = 88, Stretch = Stretch.UniformToFill };
        var artPlaceholder = new FontIcon
        {
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            Glyph = "\uE93C",
            FontSize = 28,
            Opacity = 0.45
        };
        var artBox = new Border
        {
            Width = 88,
            Height = 88,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            Child = new Grid { Children = { artPlaceholder, artPreview } }
        };

        byte[]? currentArtBytes = details.ArtBytes;
        string currentArtMime = details.ArtMimeType;
        bool artChanged = false;
        await SetImageFromBytesAsync(currentArtBytes, artPreview);

        var changeArtBtn = new Button { Content = "Change Art..." };
        var removeArtBtn = new Button { Content = "Remove Art", IsEnabled = currentArtBytes is { Length: > 0 } };
        changeArtBtn.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".bmp");
            picker.FileTypeFilter.Add(".webp");
            var picked = await picker.PickSingleFileAsync();
            if (picked == null) return;
            currentArtBytes = await File.ReadAllBytesAsync(picked.Path);
            string ext = Path.GetExtension(picked.Path).ToLowerInvariant();
            currentArtMime = ext == ".png" ? "image/png" : ext == ".webp" ? "image/webp" : ext == ".bmp" ? "image/bmp" : "image/jpeg";
            artChanged = true;
            removeArtBtn.IsEnabled = true;
            await SetImageFromBytesAsync(currentArtBytes, artPreview);
        };
        removeArtBtn.Click += (_, _) =>
        {
            currentArtBytes = null;
            artChanged = true;
            removeArtBtn.IsEnabled = false;
            artPreview.Source = null;
        };

        var artRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        artRow.Children.Add(artBox);
        var artBtns = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        artBtns.Children.Add(new TextBlock { Text = "Album Art", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var artBtnStrip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        artBtnStrip.Children.Add(changeArtBtn);
        artBtnStrip.Children.Add(removeArtBtn);
        artBtns.Children.Add(artBtnStrip);
        artRow.Children.Add(artBtns);

        var titleBox = new TextBox { Header = "Title", Text = details.Title };
        var artistBox = new TextBox { Header = "Artist", Text = details.Artist };
        var albumBox = new TextBox { Header = "Album", Text = details.Album };
        var albumArtistBox = new TextBox { Header = "Album Artist", Text = details.AlbumArtist };
        var genreBox = new TextBox { Header = "Genre", Text = details.Genre };
        var yearBox = new NumberBox { Header = "Year", Value = details.Year > 0 ? details.Year : double.NaN, Minimum = 0, Maximum = 2100, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var trackNumBox = new NumberBox { Header = "Track #", Value = details.TrackNumber > 0 ? details.TrackNumber : double.NaN, Minimum = 0, Maximum = 999, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };

        var ratingCombo = new ComboBox { Header = "Rating", HorizontalAlignment = HorizontalAlignment.Stretch };
        ratingCombo.Items.Add(new ComboBoxItem { Content = "Unrated", Tag = -1.0 });
        for (double r = 0.5; r <= 5.0; r += 0.5)
            ratingCombo.Items.Add(new ComboBoxItem { Content = $"{r:0.#} ★", Tag = r });
        ratingCombo.SelectedIndex = details.Rating.HasValue
            ? Math.Clamp((int)Math.Round(details.Rating.Value * 2), 1, 10)
            : 0;

        // AcceptsReturn and TextWrapping MUST be set before Text in WinUI 3 so newlines aren't stripped.
        var lyricsBox = new TextBox
        {
            Header = "Lyrics",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Height = 415,
            VerticalAlignment = VerticalAlignment.Stretch,
            Text = details.Lyrics.Replace("\r\n", "\r").Replace("\n", "\r")
        };
        ScrollViewer.SetVerticalScrollBarVisibility(lyricsBox, ScrollBarVisibility.Auto);

        var rowGrid = new Grid { ColumnSpacing = 8 };
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(yearBox, 0);
        Grid.SetColumn(trackNumBox, 1);
        Grid.SetColumn(ratingCombo, 2);
        rowGrid.Children.Add(yearBox);
        rowGrid.Children.Add(trackNumBox);
        rowGrid.Children.Add(ratingCombo);

        var leftPanel = new StackPanel { Spacing = 8 };
        leftPanel.Children.Add(artRow);
        leftPanel.Children.Add(titleBox);
        leftPanel.Children.Add(artistBox);
        leftPanel.Children.Add(albumBox);
        leftPanel.Children.Add(albumArtistBox);
        leftPanel.Children.Add(genreBox);
        leftPanel.Children.Add(rowGrid);

        // Full-width File, Date Added & Actions bar below the 2-column tag/lyrics grid
        string origDir = Path.GetDirectoryName(track.Path) ?? string.Empty;
        string origExt = Path.GetExtension(track.Path);
        string origNameNoExt = Path.GetFileNameWithoutExtension(track.Path);
        DateTime origLocalDate = track.DateAddedUtc.ToLocalTime();

        var fileNameBox = new TextBox
        {
            Header = "File Name",
            Text = origNameNoExt,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var extLabel = new TextBlock
        {
            Text = origExt,
            FontSize = 12,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 4, 8)
        };
        var explorerBtn = new Button
        {
            Content = "Open in Explorer",
            VerticalAlignment = VerticalAlignment.Bottom
        };
        explorerBtn.Click += (_, _) => OpenInExplorer(track.Path);

        bool deletedFromDialog = false;
        ContentDialog? dlg = null;

        var deleteBtn = new Button
        {
            Content = "Delete to Recycle Bin...",
            Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 118, 118)),
            VerticalAlignment = VerticalAlignment.Bottom
        };

        var fileNameRow = new Grid { ColumnSpacing = 8 };
        fileNameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fileNameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fileNameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fileNameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(fileNameBox, 0);
        Grid.SetColumn(extLabel, 1);
        Grid.SetColumn(explorerBtn, 2);
        Grid.SetColumn(deleteBtn, 3);
        fileNameRow.Children.Add(fileNameBox);
        fileNameRow.Children.Add(extLabel);
        fileNameRow.Children.Add(explorerBtn);
        fileNameRow.Children.Add(deleteBtn);

        var datePicker = new CalendarDatePicker
        {
            Header = "Date Added",
            Date = new DateTimeOffset(origLocalDate.Date),
            DateFormat = "{day.integer(2)}/{month.integer(2)}/{year.full}",
            Width = 150
        };
        var timePicker = new TimePicker
        {
            Header = "Time",
            Time = new TimeSpan(origLocalDate.Hour, origLocalDate.Minute, 0),
            ClockIdentifier = "24HourClock"
        };
        var nowBtn = new Button
        {
            Content = "Now",
            VerticalAlignment = VerticalAlignment.Bottom
        };
        nowBtn.Click += (_, _) =>
        {
            var now = DateTime.Now;
            datePicker.Date = new DateTimeOffset(now.Date);
            timePicker.Time = new TimeSpan(now.Hour, now.Minute, 0);
        };

        var folderSpecsText = new TextBlock
        {
            Text = $"{origDir}\n{track.Codec} · {track.BitrateText} · {track.ChannelsText} · {track.DurationText}",
            FontSize = 11,
            Opacity = 0.72,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(8, 0, 0, 2)
        };

        var dateAndSpecsRow = new Grid { ColumnSpacing = 10 };
        dateAndSpecsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dateAndSpecsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dateAndSpecsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dateAndSpecsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(datePicker, 0);
        Grid.SetColumn(timePicker, 1);
        Grid.SetColumn(nowBtn, 2);
        Grid.SetColumn(folderSpecsText, 3);
        dateAndSpecsRow.Children.Add(datePicker);
        dateAndSpecsRow.Children.Add(timePicker);
        dateAndSpecsRow.Children.Add(nowBtn);
        dateAndSpecsRow.Children.Add(folderSpecsText);

        var filePanel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10),
            Child = new StackPanel
            {
                Spacing = 10,
                Children = { fileNameRow, dateAndSpecsRow }
            }
        };

        var layoutGrid = new Grid { ColumnSpacing = 18, RowSpacing = 12, Width = 840 };
        layoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(350) });
        layoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(leftPanel, 0);
        Grid.SetColumn(leftPanel, 0);
        Grid.SetRow(lyricsBox, 0);
        Grid.SetColumn(lyricsBox, 1);
        Grid.SetRow(filePanel, 1);
        Grid.SetColumn(filePanel, 0);
        Grid.SetColumnSpan(filePanel, 2);
        layoutGrid.Children.Add(leftPanel);
        layoutGrid.Children.Add(lyricsBox);
        layoutGrid.Children.Add(filePanel);

        var scroll = new ScrollViewer { Content = layoutGrid, MaxHeight = 650 };
        dlg = new ContentDialog
        {
            Title = "Song Properties & Lyrics",
            Content = scroll,
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Auto-Tag...",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = ElementTheme.Dark
        };
        dlg.Resources["ContentDialogMaxWidth"] = 960.0;
        dlg.Resources["ContentDialogMaxHeight"] = 820.0;

        deleteBtn.Click += (_, _) =>
        {
            deletedFromDialog = true;
            dlg?.Hide();
        };

        var dlgResult = await dlg.ShowAsync();
        if (dlgResult == ContentDialogResult.Secondary)
        {
            await OpenAutoTagDialogAsync(new[] { track });
            return;
        }
        if (deletedFromDialog)
        {
            await DeleteTracksToRecycleBinAsync(new[] { track });
            return;
        }
        if (dlgResult != ContentDialogResult.Primary) return;

        double? newRating = (ratingCombo.SelectedItem as ComboBoxItem)?.Tag is double tagVal && tagVal > 0 ? tagVal : null;
        var updated = new MusicLibrary.TrackDetails
        {
            Title = titleBox.Text.Trim(),
            Artist = artistBox.Text.Trim(),
            Album = albumBox.Text.Trim(),
            AlbumArtist = albumArtistBox.Text.Trim(),
            Genre = genreBox.Text.Trim(),
            Year = double.IsNaN(yearBox.Value) ? 0 : (int)yearBox.Value,
            TrackNumber = double.IsNaN(trackNumBox.Value) ? 0 : (int)trackNumBox.Value,
            Rating = newRating,
            Lyrics = lyricsBox.Text.Replace("\r", Environment.NewLine),
            ArtBytes = currentArtBytes,
            ArtMimeType = currentArtMime,
            ArtChanged = artChanged
        };

        bool tagsSaved = await SaveTrackDetailsAsync(track, updated);
        if (!tagsSaved) return;

        // Apply Date Added change if edited
        if (datePicker.Date.HasValue)
        {
            var d = datePicker.Date.Value.Date;
            var t = timePicker.Time;
            var newLocal = new DateTime(d.Year, d.Month, d.Day, t.Hours, t.Minutes, 0, DateTimeKind.Local);
            var origMinuteLocal = new DateTime(origLocalDate.Year, origLocalDate.Month, origLocalDate.Day, origLocalDate.Hour, origLocalDate.Minute, 0, DateTimeKind.Local);
            if (newLocal != origMinuteLocal)
            {
                DateTime newUtc = newLocal.ToUniversalTime();
                var (dateOk, dateErr) = await _engine.WithFileReleasedAsync(track.Path, () => Task.Run(() =>
                {
                    try
                    {
                        File.SetLastWriteTimeUtc(track.Path, newUtc);
                        return (true, (string?)null);
                    }
                    catch (Exception ex)
                    {
                        return (false, ex.Message);
                    }
                }));
                if (dateOk)
                {
                    track.DateAddedUtc = newUtc;
                    RefreshVisibleTracks();
                }
                else
                {
                    await ShowErrorDialogAsync("Could not update Date Added", dateErr ?? "Unknown error.");
                }
            }
        }

        // Apply File Name change if edited
        string desiredName = fileNameBox.Text.Trim();
        if (!string.IsNullOrEmpty(desiredName) && !string.Equals(desiredName, origNameNoExt, StringComparison.Ordinal))
        {
            if (desiredName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                await ShowErrorDialogAsync("Invalid file name", "The file name contains invalid characters (e.g. \\ / : * ? \" < > |).");
                return;
            }

            string newPath = Path.Combine(origDir, desiredName + origExt);
            if (File.Exists(newPath))
            {
                await ShowErrorDialogAsync("File already exists", $"A file named \"{desiredName + origExt}\" already exists in this folder.");
                return;
            }

            string oldPath = track.Path;
            var (renamed, renameErr) = await _engine.RenameFileAsync(oldPath, newPath);
            if (!renamed)
            {
                await ShowErrorDialogAsync("Could not rename file", renameErr ?? "Unknown error.");
                return;
            }

            track.Path = newPath;
            if (string.Equals(_selectedLyricsTrackPath, oldPath, StringComparison.OrdinalIgnoreCase))
                _selectedLyricsTrackPath = newPath;

            foreach (var pl in _playlists)
            {
                for (int i = 0; i < pl.TrackPaths.Count; i++)
                {
                    if (string.Equals(pl.TrackPaths[i], oldPath, StringComparison.OrdinalIgnoreCase))
                        pl.TrackPaths[i] = newPath;
                }
            }
            _settings.Save();
            if (TracksListView.SelectedItem == track)
                UpdateSelectedTrackLabels(track);
        }
    }

    private async Task ShowErrorDialogAsync(string title, string message)
    {
        var errDlg = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = ElementTheme.Dark
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
            RequestedTheme = ElementTheme.Dark
        };
        if (await confirmDlg.ShowAsync() != ContentDialogResult.Primary) return;

        var deletedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        foreach (var t in tracks)
        {
            if (string.Equals(_engine.CurrentPath, t.Path, StringComparison.OrdinalIgnoreCase))
            {
                _engine.Stop();
            }

            try
            {
                if (File.Exists(t.Path))
                {
                    await Task.Run(() => Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                        t.Path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin));
                }
                deletedPaths.Add(t.Path);
            }
            catch (Exception ex)
            {
                errors.Add($"{t.FileName}: {ex.Message}");
            }
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
                pl.TrackPaths.RemoveAll(p => deletedPaths.Contains(p));
            _settings.Save();

            RefreshVisibleTracks();
            UpdateStatusFooter();
        }

        if (errors.Count > 0)
        {
            await ShowErrorDialogAsync("Some files could not be deleted", string.Join("\n", errors));
        }
    }

    private async Task<bool> SaveTrackDetailsAsync(TrackItem track, MusicLibrary.TrackDetails updated)
    {
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
                RequestedTheme = ElementTheme.Dark
            };
            await errDlg.ShowAsync();
            return false;
        }

        track.Title = updated.Title;
        track.Artist = updated.Artist;
        track.Album = updated.Album;
        track.Genre = updated.Genre;
        track.Year = updated.Year;
        track.Rating = updated.Rating;

        if (TracksListView.SelectedItem == track)
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
            _engine.UpdateSmtcMetadata(track.DisplayTitle, track.Artist, track.Album);
            await LoadFocusLyricsAsync(track.Path);
            if (updated.ArtChanged)
                await LoadArtAsync(track.Path, NowPlayingArtImage, FocusArtImage);
        }

        return true;
    }

    private void TracksListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is TrackItem hit && !TracksListView.SelectedItems.Contains(hit))
        {
            TracksListView.SelectedItem = hit;
        }

        var selected = TracksListView.SelectedItems.OfType<TrackItem>().ToList();
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
            plItem.Click += (_, _) => AddTracksToPlaylist(pl, selected);
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
            removePlItem.Click += (_, _) =>
            {
                var toRemove = new HashSet<string>(selected.Select(s => s.Path), StringComparer.OrdinalIgnoreCase);
                activePl.TrackPaths.RemoveAll(p => toRemove.Contains(p));
                _settings.Save();
                RefreshVisibleTracks();
            };
            menu.Items.Add(removePlItem);
        }

        menu.Items.Add(new MenuFlyoutSeparator());

        var propsItem = new MenuFlyoutItem { Text = "Properties & Lyrics..." };
        propsItem.Click += async (_, _) => await OpenPropertiesDialogAsync(selected[0]);
        menu.Items.Add(propsItem);

        var autoTagItem = new MenuFlyoutItem { Text = selected.Count > 1 ? $"Auto-Tag ({selected.Count} tracks)..." : "Auto-Tag from Web & Filename..." };
        autoTagItem.Click += async (_, _) => await OpenAutoTagDialogAsync(selected);
        menu.Items.Add(autoTagItem);

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

        menu.ShowAt(TracksListView, e.GetPosition(TracksListView));
    }

    private async Task OpenAutoTagDialogAsync(IReadOnlyList<TrackItem> tracks)
    {
        if (tracks.Count == 0) return;

        for (int trackIdx = 0; trackIdx < tracks.Count; trackIdx++)
        {
            var track = tracks[trackIdx];
            var current = await Task.Run(() => MusicLibrary.ReadFullDetails(track.Path));
            var parsed = AutoTagger.ParseFromFilename(track.Path);

            var queryTitleBox = new TextBox
            {
                Header = "Search Title",
                Text = string.IsNullOrWhiteSpace(current.Title) ? parsed.Title : current.Title,
                Width = 240
            };
            var queryArtistBox = new TextBox
            {
                Header = "Search Artist",
                Text = string.IsNullOrWhiteSpace(current.Artist) ? parsed.Artist : current.Artist,
                Width = 220
            };
            var searchBtn = new Button { Content = "Search", VerticalAlignment = VerticalAlignment.Bottom };
            var loadingRing = new ProgressRing { Width = 20, Height = 20, IsActive = true, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6, 0, 0, 6) };
            var statusLabel = new TextBlock { FontSize = 11, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6, 0, 0, 8), Text = "Searching MusicBrainz, iTunes & LRCLIB..." };

            var searchRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            searchRow.Children.Add(queryTitleBox);
            searchRow.Children.Add(queryArtistBox);
            searchRow.Children.Add(searchBtn);
            searchRow.Children.Add(loadingRing);
            searchRow.Children.Add(statusLabel);

            var matchCombo = new ComboBox { Header = "Matched Release / Source", HorizontalAlignment = HorizontalAlignment.Stretch };

            var selectAllBtn = new Button { Content = "Select All (Recommended)", Padding = new Thickness(10, 4, 10, 4), FontSize = 11 };
            var emptyOnlyBtn = new Button { Content = "Only Empty Fields", Padding = new Thickness(10, 4, 10, 4), FontSize = 11 };
            var uncheckAllBtn = new Button { Content = "Uncheck All", Padding = new Thickness(10, 4, 10, 4), FontSize = 11 };
            var presetRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            presetRow.Children.Add(selectAllBtn);
            presetRow.Children.Add(emptyOnlyBtn);
            presetRow.Children.Add(uncheckAllBtn);

            CheckBox MakeCheck(string label) => new() { Content = label, VerticalAlignment = VerticalAlignment.Center, MinWidth = 110 };
            TextBox MakeProposedBox() => new() { HorizontalAlignment = HorizontalAlignment.Stretch };
            TextBlock MakeCurrentHint(string cur) => new()
            {
                Text = string.IsNullOrWhiteSpace(cur) ? "(empty)" : cur,
                FontSize = 11,
                Opacity = 0.65,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };

            var chkTitle = MakeCheck("Title");
            var chkArtist = MakeCheck("Artist");
            var chkAlbum = MakeCheck("Album");
            var chkAlbumArtist = MakeCheck("Album Artist");
            var chkGenre = MakeCheck("Genre");
            var chkYear = MakeCheck("Year");
            var chkTrack = MakeCheck("Track #");
            var chkLyrics = MakeCheck("Lyrics");
            var chkArt = MakeCheck("Album Art");

            var boxTitle = MakeProposedBox();
            var boxArtist = MakeProposedBox();
            var boxAlbum = MakeProposedBox();
            var boxAlbumArtist = MakeProposedBox();
            var boxGenre = MakeProposedBox();
            var boxYear = MakeProposedBox();
            var boxTrack = MakeProposedBox();
            var boxLyrics = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 140,
                FontSize = 12
            };
            ScrollViewer.SetVerticalScrollBarVisibility(boxLyrics, ScrollBarVisibility.Auto);

            Grid MakeDiffRow(CheckBox chk, string currentVal, Control proposedCtrl)
            {
                var g = new Grid { ColumnSpacing = 8 };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var curTb = MakeCurrentHint(currentVal);
                Grid.SetColumn(chk, 0);
                Grid.SetColumn(curTb, 1);
                Grid.SetColumn(proposedCtrl, 2);
                g.Children.Add(chk);
                g.Children.Add(curTb);
                g.Children.Add(proposedCtrl);
                return g;
            }

            var diffHeader = new Grid { ColumnSpacing = 8 };
            diffHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
            diffHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            diffHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var hField = new TextBlock { Text = "Apply Field", FontSize = 11, Opacity = 0.7 };
            var hCur = new TextBlock { Text = "Current Tag", FontSize = 11, Opacity = 0.7 };
            var hNew = new TextBlock { Text = "Proposed Tag (Editable)", FontSize = 11, Opacity = 0.7 };
            Grid.SetColumn(hField, 0); Grid.SetColumn(hCur, 1); Grid.SetColumn(hNew, 2);
            diffHeader.Children.Add(hField); diffHeader.Children.Add(hCur); diffHeader.Children.Add(hNew);

            var leftCol = new StackPanel { Spacing = 6 };
            leftCol.Children.Add(diffHeader);
            leftCol.Children.Add(MakeDiffRow(chkTitle, current.Title, boxTitle));
            leftCol.Children.Add(MakeDiffRow(chkArtist, current.Artist, boxArtist));
            leftCol.Children.Add(MakeDiffRow(chkAlbum, current.Album, boxAlbum));
            leftCol.Children.Add(MakeDiffRow(chkAlbumArtist, current.AlbumArtist, boxAlbumArtist));
            leftCol.Children.Add(MakeDiffRow(chkGenre, current.Genre, boxGenre));
            leftCol.Children.Add(MakeDiffRow(chkYear, current.Year > 0 ? current.Year.ToString() : "", boxYear));
            leftCol.Children.Add(MakeDiffRow(chkTrack, current.TrackNumber > 0 ? current.TrackNumber.ToString() : "", boxTrack));
            leftCol.Children.Add(MakeDiffRow(chkLyrics, string.IsNullOrWhiteSpace(current.Lyrics) ? "" : "(has lyrics)", boxLyrics));

            // Right column: Current Art -> Selected Art + Multiple Art Options Picker
            var curArtImg = new Image { Width = 64, Height = 64, Stretch = Stretch.UniformToFill };
            await SetImageFromBytesAsync(current.ArtBytes, curArtImg);
            var curArtBorder = new Border
            {
                Width = 64, Height = 64, CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                Child = curArtImg
            };

            var selArtImg = new Image { Width = 120, Height = 120, Stretch = Stretch.UniformToFill };
            var selArtBorder = new Border
            {
                Width = 120, Height = 120, CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(180, 255, 200, 59)),
                BorderThickness = new Thickness(1.5),
                Child = selArtImg
            };

            var artThumbsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var artThumbsScroll = new ScrollViewer
            {
                Content = artThumbsPanel,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            var artHintText = new TextBlock { Text = "No cover options yet", FontSize = 11, Opacity = 0.65 };

            var rightCol = new StackPanel { Spacing = 8 };
            rightCol.Children.Add(chkArt);
            var artCompare = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            artCompare.Children.Add(new StackPanel { Spacing = 2, Children = { new TextBlock { Text = "Current", FontSize = 10.5, Opacity = 0.7 }, curArtBorder } });
            artCompare.Children.Add(new StackPanel { Spacing = 2, Children = { new TextBlock { Text = "Selected New", FontSize = 10.5, Opacity = 0.7 }, selArtBorder } });
            rightCol.Children.Add(artCompare);
            rightCol.Children.Add(new TextBlock { Text = "Cover Art Options (click to choose):", FontSize = 11, Opacity = 0.8 });
            rightCol.Children.Add(artHintText);
            rightCol.Children.Add(artThumbsScroll);

            byte[]? chosenArtBytes = null;
            AutoTagSearchResult? latestResult = null;
            var artThumbBorders = new List<Border>();

            async Task SelectArtOptionAsync(byte[]? bytes, int thumbIndex)
            {
                chosenArtBytes = bytes;
                await SetImageFromBytesAsync(bytes, selArtImg);
                for (int i = 0; i < artThumbBorders.Count; i++)
                {
                    artThumbBorders[i].BorderBrush = i == thumbIndex
                        ? new SolidColorBrush(Color.FromArgb(255, 255, 200, 59))
                        : new SolidColorBrush(Color.FromArgb(50, 255, 255, 255));
                    artThumbBorders[i].BorderThickness = new Thickness(i == thumbIndex ? 2 : 1);
                }
            }

            void ApplyRecommendedChecks(bool onlyEmpty)
            {
                static string Norm(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
                bool ShouldCheck(string proposed, string existing) =>
                    !string.IsNullOrWhiteSpace(proposed) &&
                    !string.Equals(Norm(proposed), Norm(existing), StringComparison.Ordinal) &&
                    (!onlyEmpty || string.IsNullOrWhiteSpace(existing));

                chkTitle.IsChecked = ShouldCheck(boxTitle.Text, current.Title);
                chkArtist.IsChecked = ShouldCheck(boxArtist.Text, current.Artist);
                chkAlbum.IsChecked = ShouldCheck(boxAlbum.Text, current.Album);
                chkAlbumArtist.IsChecked = ShouldCheck(boxAlbumArtist.Text, current.AlbumArtist);
                chkGenre.IsChecked = ShouldCheck(boxGenre.Text, current.Genre);
                chkYear.IsChecked = ShouldCheck(boxYear.Text, current.Year > 0 ? current.Year.ToString() : "");
                chkTrack.IsChecked = ShouldCheck(boxTrack.Text, current.TrackNumber > 0 ? current.TrackNumber.ToString() : "");
                chkLyrics.IsChecked = ShouldCheck(boxLyrics.Text, current.Lyrics);
                bool sameArt = chosenArtBytes is { Length: > 0 } &&
                               current.ArtBytes is { Length: > 0 } &&
                               chosenArtBytes.AsSpan().SequenceEqual(current.ArtBytes);
                chkArt.IsChecked = chosenArtBytes is { Length: > 0 } && !sameArt && (!onlyEmpty || current.ArtBytes is not { Length: > 0 });
            }

            void PopulateCandidate(AutoTagCandidate c)
            {
                boxTitle.Text = c.Title;
                boxArtist.Text = c.Artist;
                boxAlbum.Text = c.Album;
                boxAlbumArtist.Text = c.AlbumArtist;
                boxGenre.Text = c.Genre;
                boxYear.Text = c.Year > 0 ? c.Year.ToString() : string.Empty;
                boxTrack.Text = c.TrackNumber > 0 ? c.TrackNumber.ToString() : string.Empty;
                ApplyRecommendedChecks(onlyEmpty: false);
            }

            selectAllBtn.Click += (_, _) => ApplyRecommendedChecks(onlyEmpty: false);
            emptyOnlyBtn.Click += (_, _) => ApplyRecommendedChecks(onlyEmpty: true);
            uncheckAllBtn.Click += (_, _) =>
            {
                foreach (var cb in new[] { chkTitle, chkArtist, chkAlbum, chkAlbumArtist, chkGenre, chkYear, chkTrack, chkLyrics, chkArt })
                    cb.IsChecked = false;
            };

            matchCombo.SelectionChanged += async (_, _) =>
            {
                if (latestResult == null || matchCombo.SelectedIndex < 0 || matchCombo.SelectedIndex >= latestResult.Candidates.Count) return;
                var c = latestResult.Candidates[matchCombo.SelectedIndex];
                PopulateCandidate(c);
                if (!string.IsNullOrEmpty(c.ArtUrl))
                {
                    int artIdx = latestResult.ArtOptions.FindIndex(a => string.Equals(a.Url, c.ArtUrl, StringComparison.OrdinalIgnoreCase));
                    if (artIdx >= 0)
                        await SelectArtOptionAsync(latestResult.ArtOptions[artIdx].Bytes, artIdx);
                }
            };

            CancellationTokenSource? searchCts = null;
            async Task RunSearchAsync()
            {
                searchCts?.Cancel();
                searchCts = new CancellationTokenSource();
                var token = searchCts.Token;

                loadingRing.IsActive = true;
                loadingRing.Visibility = Visibility.Visible;
                statusLabel.Text = "Searching MusicBrainz, iTunes & LRCLIB...";
                searchBtn.IsEnabled = false;

                var res = await AutoTagger.SearchAsync(track.Path, queryTitleBox.Text, queryArtistBox.Text, track.Duration, token);
                if (token.IsCancellationRequested) return;

                latestResult = res;
                loadingRing.IsActive = false;
                loadingRing.Visibility = Visibility.Collapsed;
                searchBtn.IsEnabled = true;

                matchCombo.Items.Clear();
                foreach (var c in res.Candidates) matchCombo.Items.Add(c.DisplayLabel);

                boxLyrics.Text = res.Lyrics.Replace("\r\n", "\r").Replace("\n", "\r");

                artThumbsPanel.Children.Clear();
                artThumbBorders.Clear();
                if (res.ArtOptions.Count > 0)
                {
                    artHintText.Visibility = Visibility.Collapsed;
                    for (int i = 0; i < res.ArtOptions.Count; i++)
                    {
                        int capturedIdx = i;
                        var opt = res.ArtOptions[i];
                        var thumbImg = new Image { Width = 60, Height = 60, Stretch = Stretch.UniformToFill };
                        await SetImageFromBytesAsync(opt.Bytes, thumbImg);
                        var b = new Border
                        {
                            Width = 62,
                            Height = 62,
                            CornerRadius = new CornerRadius(6),
                            BorderThickness = new Thickness(1),
                            BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
                            Child = thumbImg
                        };
                        ToolTipService.SetToolTip(b, $"{opt.Source} cover #{i + 1}");
                        b.Tapped += async (_, _) => await SelectArtOptionAsync(opt.Bytes, capturedIdx);
                        artThumbBorders.Add(b);
                        artThumbsPanel.Children.Add(b);
                    }
                    await SelectArtOptionAsync(res.ArtOptions[0].Bytes, 0);
                }
                else
                {
                    artHintText.Visibility = Visibility.Visible;
                    await SelectArtOptionAsync(null, -1);
                }

                if (res.Candidates.Count > 0)
                    matchCombo.SelectedIndex = 0;

                statusLabel.Text = $"{res.Candidates.Count} match(es), {res.ArtOptions.Count} cover(s){(string.IsNullOrEmpty(res.Lyrics) ? "" : ", lyrics found")}";
            }

            searchBtn.Click += async (_, _) => await RunSearchAsync();

            var bodyGrid = new Grid { ColumnSpacing = 18, Width = 860 };
            bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
            Grid.SetColumn(leftCol, 0);
            Grid.SetColumn(rightCol, 1);
            bodyGrid.Children.Add(leftCol);
            bodyGrid.Children.Add(rightCol);

            var rootPanel = new StackPanel { Spacing = 10, Width = 860 };
            rootPanel.Children.Add(searchRow);
            rootPanel.Children.Add(matchCombo);
            rootPanel.Children.Add(presetRow);
            rootPanel.Children.Add(bodyGrid);

            var scroll = new ScrollViewer { Content = rootPanel, MaxHeight = 620 };
            string trackCountSuffix = tracks.Count > 1 ? $" ({trackIdx + 1} / {tracks.Count})" : "";
            var dlg = new ContentDialog
            {
                Title = $"Auto-Tag{trackCountSuffix} — {track.DisplayTitle}",
                Content = scroll,
                PrimaryButtonText = tracks.Count > 1 && trackIdx < tracks.Count - 1 ? "Apply & Next" : "Apply Selected",
                SecondaryButtonText = tracks.Count > 1 && trackIdx < tracks.Count - 1 ? "Skip" : "",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = ElementTheme.Dark
            };
            dlg.Resources["ContentDialogMaxWidth"] = 980.0;
            dlg.Resources["ContentDialogMaxHeight"] = 800.0;

            _ = RunSearchAsync();
            var dlgResult = await dlg.ShowAsync();
            searchCts?.Cancel();

            if (dlgResult == ContentDialogResult.None) break;
            if (dlgResult == ContentDialogResult.Secondary) continue;

            var updated = new MusicLibrary.TrackDetails
            {
                Title = chkTitle.IsChecked == true ? boxTitle.Text.Trim() : current.Title,
                Artist = chkArtist.IsChecked == true ? boxArtist.Text.Trim() : current.Artist,
                Album = chkAlbum.IsChecked == true ? boxAlbum.Text.Trim() : current.Album,
                AlbumArtist = chkAlbumArtist.IsChecked == true ? boxAlbumArtist.Text.Trim() : current.AlbumArtist,
                Genre = chkGenre.IsChecked == true ? boxGenre.Text.Trim() : current.Genre,
                Year = chkYear.IsChecked == true && int.TryParse(boxYear.Text.Trim(), out int yVal) ? yVal : current.Year,
                TrackNumber = chkTrack.IsChecked == true && int.TryParse(boxTrack.Text.Trim(), out int tVal) ? tVal : current.TrackNumber,
                Rating = current.Rating,
                Lyrics = chkLyrics.IsChecked == true ? boxLyrics.Text.Replace("\r", Environment.NewLine) : current.Lyrics,
                ArtBytes = chkArt.IsChecked == true ? chosenArtBytes : current.ArtBytes,
                ArtMimeType = "image/jpeg",
                ArtChanged = chkArt.IsChecked == true && chosenArtBytes is { Length: > 0 }
            };

            await SaveTrackDetailsAsync(track, updated);
        }
    }

    private void AddTracksToPlaylist(SavedPlaylist pl, IReadOnlyList<TrackItem> tracks)
    {
        var existing = new HashSet<string>(pl.TrackPaths, StringComparer.OrdinalIgnoreCase);
        foreach (var t in tracks)
        {
            if (existing.Add(t.Path)) pl.TrackPaths.Add(t.Path);
        }
        _settings.Save();
    }

    private async void NewPlaylistBtn_Click(object sender, RoutedEventArgs e) => await CreatePlaylistAsync(Array.Empty<TrackItem>());

    private async void SaveQueueAsPlaylistBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_queueTracks.Count > 0)
            await CreatePlaylistAsync(_queueTracks.ToList());
    }

    private async Task CreatePlaylistAsync(IReadOnlyList<TrackItem> initialTracks)
    {
        var input = new TextBox { PlaceholderText = "Playlist name", Text = $"Playlist {_playlists.Count + 1}" };
        var dlg = new ContentDialog
        {
            Title = "New playlist",
            Content = input,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = ElementTheme.Dark
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(input.Text)) return;

        var pl = new SavedPlaylist { Name = input.Text.Trim() };
        foreach (var t in initialTracks) pl.TrackPaths.Add(t.Path);
        _playlists.Add(pl);
        _settings.Playlists.Add(pl);
        _settings.Save();
    }

    private async void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        var devices = WasapiAudioEngine.GetOutputDevices();
        var deviceBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var d in devices) deviceBox.Items.Add(new ComboBoxItem { Content = d.FriendlyName, Tag = d.Id });
        deviceBox.SelectedIndex = Math.Max(0, devices.ToList().FindIndex(d => d.Id == _settings.OutputDeviceId));

        var layoutBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        layoutBox.Items.Add(new ComboBoxItem { Content = "2.0 Stereo (Default)", Tag = SpeakerLayout.Stereo20 });
        layoutBox.Items.Add(new ComboBoxItem { Content = "2.1 Stereo + Subwoofer", Tag = SpeakerLayout.Stereo21 });
        layoutBox.Items.Add(new ComboBoxItem { Content = "5.1 Surround (Smart Stereo Upmix)", Tag = SpeakerLayout.Surround51Upmix });
        layoutBox.Items.Add(new ComboBoxItem { Content = "5.1 Surround (Direct Multichannel)", Tag = SpeakerLayout.Surround51Direct });
        layoutBox.SelectedIndex = (int)_settings.SpeakerLayout;

        var centerSlider = new Slider { Minimum = 0, Maximum = 100, Value = _settings.CenterLevel * 100, Header = "5.1 Center Vocal Fill (%)" };
        var lfeSlider = new Slider { Minimum = 0, Maximum = 150, Value = _settings.LfeGain * 100, Header = "Subwoofer (LFE) Boost (%)" };
        var rearSlider = new Slider { Minimum = 0, Maximum = 100, Value = _settings.RearLevel * 100, Header = "5.1 Rear Surround Stage (%)" };

        var vizBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Header = "Visualizer style" };
        vizBox.Items.Add(new ComboBoxItem { Content = "Spectrum Bars (60 fps Win2D)", Tag = "Spectrum" });
        vizBox.Items.Add(new ComboBoxItem { Content = "Silk Waveform", Tag = "Waveform" });
        vizBox.Items.Add(new ComboBoxItem { Content = "Off (0% GPU/CPU)", Tag = "Off" });
        vizBox.SelectedIndex = !_settings.VisualizerEnabled || _settings.VisualizerMode == "Off" ? 2
            : _settings.VisualizerMode == "Waveform" ? 1 : 0;

        var endpointInfoText = new TextBlock
        {
            FontSize = 11,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.LightGray),
            TextWrapping = TextWrapping.Wrap
        };
        void UpdateEndpointNote()
        {
            string id = (deviceBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
            int ch = WasapiAudioEngine.GetEndpointChannelCount(id);
            var selLayout = (SpeakerLayout)((layoutBox.SelectedItem as ComboBoxItem)?.Tag ?? SpeakerLayout.Stereo20);
            endpointInfoText.Text = selLayout != SpeakerLayout.Stereo20 && ch < 6
                ? $"Note: Selected Windows device currently reports {ch}ch. ZenMusic will apply spatial stereo fold-down until Windows Speaker Setup is set to 5.1."
                : $"Detected Windows endpoint channels: {ch}ch.";
        }
        deviceBox.SelectionChanged += (_, _) => UpdateEndpointNote();
        layoutBox.SelectionChanged += (_, _) => UpdateEndpointNote();
        UpdateEndpointNote();

        var panel = new StackPanel { Spacing = 12, MinWidth = 380 };
        panel.Children.Add(new TextBlock { Text = "Audio output device", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(deviceBox);
        panel.Children.Add(new TextBlock { Text = "Speaker / channel configuration", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(layoutBox);
        panel.Children.Add(endpointInfoText);
        panel.Children.Add(centerSlider);
        panel.Children.Add(lfeSlider);
        panel.Children.Add(rearSlider);
        panel.Children.Add(vizBox);

        string appVersion = typeof(MainWindow).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?.Split('+')[0]
            ?? typeof(MainWindow).Assembly.GetName().Version?.ToString(3)
            ?? "0.1.0";

        var aboutBtn = new Button { Content = "About ZenMusic..." };
        var aboutCard = new Border
        {
            Margin = new Thickness(0, 6, 0, 0),
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(16, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Child = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto }
                },
                Children =
                {
                    new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            new TextBlock { Text = $"ZenMusic  v{appVersion}", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                            new TextBlock { Text = "WASAPI Audio · TagLib# · WinUI 3", FontSize = 11, Opacity = 0.7 }
                        }
                    }
                }
            }
        };
        Grid.SetColumn(aboutBtn, 1);
        aboutBtn.VerticalAlignment = VerticalAlignment.Center;
        ((Grid)aboutCard.Child).Children.Add(aboutBtn);
        panel.Children.Add(aboutCard);

        bool openAbout = false;
        ContentDialog? dlg = null;
        aboutBtn.Click += (_, _) =>
        {
            openAbout = true;
            dlg?.Hide();
        };

        dlg = new ContentDialog
        {
            Title = "Settings",
            Content = new ScrollViewer { Content = panel, MaxHeight = 480 },
            PrimaryButtonText = "Apply & Save",
            SecondaryButtonText = "Reset to defaults",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = ElementTheme.Dark
        };

        var result = await dlg.ShowAsync();
        if (openAbout)
        {
            var aboutDlg = new ContentDialog
            {
                Title = "About ZenMusic",
                Content = new StackPanel
                {
                    Spacing = 8,
                    MinWidth = 340,
                    Children =
                    {
                        new TextBlock { Text = "ZenMusic", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.Bold },
                        new TextBlock { Text = $"Version {appVersion}", FontSize = 13, Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 200, 59)) },
                        new TextBlock { Text = "A fast, distraction-free desktop music player with WASAPI multi-channel audio, live Win2D visualizers, and built-in Auto-Tagging.", FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 },
                        new TextBlock { Text = $"Runtime: .NET {Environment.Version} · Windows App SDK / WinUI 3\nAudio Engine: NAudio WASAPI\nMetadata: TagLib# · MusicBrainz · iTunes · LRCLIB", FontSize = 11, Opacity = 0.65, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) }
                    }
                },
                CloseButtonText = "OK",
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = ElementTheme.Dark
            };
            await aboutDlg.ShowAsync();
            return;
        }
        if (result == ContentDialogResult.Secondary)
        {
            _settings.ResetAudioAndUiToDefaults();
            _settings.Save();
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p &&
                p.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized)
            {
                p.Restore();
            }
            AppWindow.Resize(new Windows.Graphics.SizeInt32(_settings.WindowWidth, _settings.WindowHeight));
            _leftPaneOpen = false;
            LeftPaneCol.Width = new GridLength(0);
            LeftSidebar.Visibility = Visibility.Collapsed;
            SelectedInspectorScroll.MaxHeight = 208;
            _inspectorArtMode = 2;
            ApplyInspectorArtMode();
            ApplyTheme(_settings.Theme);
            ApplyAudioConfigFromSettings();
            ApplyVisualizerFromSettings();
            return;
        }
        if (result != ContentDialogResult.Primary) return;

        _settings.OutputDeviceId = (deviceBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
        _settings.SpeakerLayout = (SpeakerLayout)((layoutBox.SelectedItem as ComboBoxItem)?.Tag ?? SpeakerLayout.Stereo20);
        _settings.CenterLevel = (float)(centerSlider.Value / 100.0);
        _settings.LfeGain = (float)(lfeSlider.Value / 100.0);
        _settings.RearLevel = (float)(rearSlider.Value / 100.0);

        string vizTag = (vizBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Spectrum";
        _settings.VisualizerMode = vizTag;
        _settings.VisualizerEnabled = vizTag != "Off";

        _settings.Save();
        ApplyAudioConfigFromSettings();
        ApplyVisualizerFromSettings();
    }

    private void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.F11)
        {
            ToggleFocusVisualizer();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape && FocusVisualizerOverlay.Visibility == Visibility.Visible)
        {
            FocusVisualizerOverlay.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Space && FocusManager.GetFocusedElement(RootGrid.XamlRoot) is not TextBox)
        {
            PlayPauseBtn_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.F && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            SearchBox.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
        else if (FocusManager.GetFocusedElement(RootGrid.XamlRoot) is not TextBox &&
                 !Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down) &&
                 !Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down) &&
                 TryGetTypeJumpChar(e.Key, out char ch))
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

        // If multi-char prefix has no match, try just the newly pressed single letter
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

        TypeJumpText.Text = match != null
            ? $"{fieldLabel}: \"{_typeJumpBuffer}\""
            : $"{fieldLabel}: \"{_typeJumpBuffer}\" (no match)";
        ShowTypeJumpPill();

        if (match != null)
        {
            TracksListView.SelectedItem = match;
            TracksListView.ScrollIntoView(match, ScrollIntoViewAlignment.Leading);
        }
    }

    private async void ShowTypeJumpPill()
    {
        _jumpPillCts?.Cancel();
        var cts = new CancellationTokenSource();
        _jumpPillCts = cts;
        TypeJumpPill.Visibility = Visibility.Visible;
        try
        {
            await Task.Delay(1500, cts.Token);
            if (!cts.IsCancellationRequested)
                TypeJumpPill.Visibility = Visibility.Collapsed;
        }
        catch (TaskCanceledException) { }
    }
}
