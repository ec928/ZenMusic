using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;
using ZenMusic.Audio;
using ZenMusic.Library;
using ZenMusic.Tagging;
using ZenMusic.UI;

namespace ZenMusic;

public sealed partial class MainWindow
{
    private const int MaxFavouriteFolders = 10;
    private const int MaxRecentFolders = 12;
    private CancellationTokenSource? _rgScanCts;

    private async Task SwitchToFolderAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            await ShowErrorDialogAsync("Folder unavailable", $"The folder could not be found:\n{path}");
            return;
        }
        _settings.MusicFolder = path;
        RecordRecentFolder(path);
        _settings.Save();
        await ScanFolderAsync(path);
    }

    private void RecordRecentFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string normalized = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (_settings.FavouriteFolders.Any(f => string.Equals(f.TrimEnd('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase)))
            return;
        _settings.RecentFolders.RemoveAll(f => string.Equals(f.TrimEnd('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase));
        _settings.RecentFolders.Insert(0, normalized);
        if (_settings.RecentFolders.Count > MaxRecentFolders)
            _settings.RecentFolders.RemoveRange(MaxRecentFolders, _settings.RecentFolders.Count - MaxRecentFolders);
    }

    private void RecentFoldersMenu_Opening(object sender, object e)
    {
        BuildRecentFoldersFlyoutContent();
    }

    private void BuildRecentFoldersFlyoutContent()
    {
        var panel = new StackPanel { MinWidth = 300, MaxWidth = 420 };
        var favs = _settings.FavouriteFolders;
        var favSet = new HashSet<string>(favs.Select(f => f.TrimEnd('\\', '/')), StringComparer.OrdinalIgnoreCase);

        var favHeader = new TextBlock
        {
            Text = favs.Count > 0 ? $"Favourites ({favs.Count} of {MaxFavouriteFolders})" : "Favourites",
            Style = (Style)RootGrid.Resources["FolderMenuHeaderTextStyle"]
        };
        panel.Children.Add(favHeader);

        if (favs.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Star a recent folder to pin it here",
                Style = (Style)RootGrid.Resources["FolderMenuEmptyTextStyle"]
            });
        }
        else
        {
            foreach (string path in favs.ToList())
            {
                panel.Children.Add(BuildFolderFlyoutRow(path, isFavourite: true, favCount: favs.Count));
            }
        }

        panel.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Style = (Style)RootGrid.Resources["FolderMenuDividerStyle"]
        });

        panel.Children.Add(new TextBlock
        {
            Text = "Recent",
            Style = (Style)RootGrid.Resources["FolderMenuHeaderTextStyle"]
        });

        var recents = _settings.RecentFolders
            .Where(r => !favSet.Contains(r.TrimEnd('\\', '/')))
            .ToList();
        if (recents.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "No recent folders",
                Style = (Style)RootGrid.Resources["FolderMenuEmptyTextStyle"]
            });
        }
        else
        {
            foreach (string path in recents)
            {
                panel.Children.Add(BuildFolderFlyoutRow(path, isFavourite: false, favCount: favs.Count));
            }
        }

        RecentFoldersMenu.Content = panel;
    }

    private Grid BuildFolderFlyoutRow(string path, bool isFavourite, int favCount)
    {
        bool exists = Directory.Exists(path);
        bool isCurrent = !string.IsNullOrEmpty(_settings.MusicFolder) &&
                         string.Equals(_settings.MusicFolder.TrimEnd('\\', '/'), path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

        var row = new Grid { ColumnSpacing = 4 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        string leaf = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(leaf)) leaf = path;

        var labelStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        labelStack.Children.Add(new FontIcon
        {
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            Glyph = isCurrent ? "\uE8D6" : "\uE8B7",
            FontSize = 13,
            Opacity = exists ? 0.85 : 0.4
        });
        labelStack.Children.Add(new TextBlock
        {
            Text = exists ? leaf : $"{leaf} (unavailable)",
            FontSize = 13,
            FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Opacity = exists ? 1.0 : 0.45
        });

        var pathBtn = new Button
        {
            Style = (Style)RootGrid.Resources["FolderMenuRowButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 6, 10, 6),
            IsEnabled = exists,
            Content = labelStack
        };
        ToolTipService.SetToolTip(pathBtn, path);
        pathBtn.Click += async (_, _) =>
        {
            RecentFoldersMenu.Hide();
            await SwitchToFolderAsync(path);
        };

        bool atCap = !isFavourite && favCount >= MaxFavouriteFolders;
        var starBtn = new Button
        {
            Style = (Style)RootGrid.Resources["FolderMenuStarButtonStyle"],
            IsEnabled = !atCap,
            Content = new FontIcon
            {
                Style = (Style)RootGrid.Resources[isFavourite ? "FolderMenuStarFilledIconStyle" : "FolderMenuStarEmptyIconStyle"]
            }
        };
        var starWrapper = new Border { Background = new SolidColorBrush(Colors.Transparent), Child = starBtn };
        ToolTipService.SetToolTip(starWrapper, atCap
            ? $"Favourites limit reached ({MaxFavouriteFolders} of {MaxFavouriteFolders})"
            : (isFavourite ? "Remove from Favourites" : "Pin to Favourites"));

        starBtn.Click += (_, _) =>
        {
            string norm = path.TrimEnd('\\', '/');
            if (isFavourite)
            {
                _settings.FavouriteFolders.RemoveAll(f => string.Equals(f.TrimEnd('\\', '/'), norm, StringComparison.OrdinalIgnoreCase));
                RecordRecentFolder(norm);
            }
            else if (_settings.FavouriteFolders.Count < MaxFavouriteFolders)
            {
                _settings.RecentFolders.RemoveAll(f => string.Equals(f.TrimEnd('\\', '/'), norm, StringComparison.OrdinalIgnoreCase));
                _settings.FavouriteFolders.Add(norm);
            }
            _settings.Save();
            BuildRecentFoldersFlyoutContent();
        };

        Grid.SetColumn(pathBtn, 0);
        Grid.SetColumn(starWrapper, 1);
        row.Children.Add(pathBtn);
        row.Children.Add(starWrapper);
        return row;
    }

    private void ClearRecentFolders_Click(object sender, RoutedEventArgs e)
    {
        _settings.RecentFolders.Clear();
        _settings.Save();
        ShowHudPill("Recent folders cleared");
    }

    private static void PopulateSpeakerLayoutCombo(ComboBox layoutBox, int endpointChannels, SpeakerLayout selectedLayout)
    {
        bool sixCh = endpointChannels >= 6;
        layoutBox.Items.Clear();
        layoutBox.Items.Add(new ComboBoxItem
        {
            Content = sixCh ? "2.0 Stereo" : "2.0 Stereo (Direct — Recommended)",
            Tag = SpeakerLayout.Stereo20
        });
        layoutBox.Items.Add(new ComboBoxItem
        {
            Content = "2.1 Stereo + Subwoofer",
            Tag = SpeakerLayout.Stereo21
        });
        layoutBox.Items.Add(new ComboBoxItem
        {
            Content = "5.1 Surround (Smart Stereo Upmix)",
            Tag = SpeakerLayout.Surround51Upmix
        });
        layoutBox.Items.Add(new ComboBoxItem
        {
            Content = sixCh ? "5.1 Surround (Direct — Recommended)" : "5.1 Surround (Direct Multichannel)",
            Tag = SpeakerLayout.Surround51Direct
        });
        layoutBox.SelectedIndex = (int)selectedLayout;
    }

    private static string DescribeEndpointStatus(int ch, SpeakerLayout layout)
    {
        if (ch >= 6)
        {
            return layout switch
            {
                SpeakerLayout.Surround51Direct => $"Endpoint active: {ch}ch native WASAPI (Direct — unadjusted signal).",
                SpeakerLayout.Stereo20 => $"Endpoint is {ch}ch WASAPI — 5.1 Direct is recommended for native unadjusted output.",
                _ => $"Endpoint active: {ch}ch WASAPI (DSP upmix active)."
            };
        }
        return layout == SpeakerLayout.Stereo20
            ? $"Endpoint active: {ch}ch native WASAPI (Direct — unadjusted signal)."
            : $"Device reports {ch}ch; spatial stereo fold-down active (2.0 Direct recommended).";
    }

    private void SpeakerBadgeBtn_Click(object sender, RoutedEventArgs e)
    {
        var devices = WasapiAudioEngine.GetOutputDevices();
        var deviceBox = new ComboBox { Header = "Output device", HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 260 };
        foreach (var d in devices) deviceBox.Items.Add(new ComboBoxItem { Content = d.FriendlyName, Tag = d.Id });
        deviceBox.SelectedIndex = Math.Max(0, devices.ToList().FindIndex(d => d.Id == _settings.OutputDeviceId));

        var layoutBox = new ComboBox { Header = "Speaker configuration", HorizontalAlignment = HorizontalAlignment.Stretch };
        int initCh = WasapiAudioEngine.GetEndpointChannelCount(_settings.OutputDeviceId);
        PopulateSpeakerLayoutCombo(layoutBox, initCh, _settings.SpeakerLayout);

        var noteText = new TextBlock { FontSize = 11, Opacity = 0.72, TextWrapping = TextWrapping.Wrap, MaxWidth = 280 };
        noteText.Text = DescribeEndpointStatus(initCh, _settings.SpeakerLayout);

        bool updating = false;
        void ApplyQuickAudio(bool deviceChanged)
        {
            if (updating) return;
            updating = true;
            try
            {
                string devId = (deviceBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
                int ch = WasapiAudioEngine.GetEndpointChannelCount(devId);
                var currentLayout = (SpeakerLayout)((layoutBox.SelectedItem as ComboBoxItem)?.Tag ?? _settings.SpeakerLayout);
                if (deviceChanged)
                {
                    if (currentLayout is SpeakerLayout.Stereo20 or SpeakerLayout.Surround51Direct)
                    {
                        currentLayout = WasapiAudioEngine.GetRecommendedDirectLayout(devId);
                    }
                    PopulateSpeakerLayoutCombo(layoutBox, ch, currentLayout);
                }

                _settings.OutputDeviceId = devId;
                _settings.SpeakerLayout = currentLayout;
                _settings.Save();
                ApplyAudioConfigFromSettings();
                noteText.Text = DescribeEndpointStatus(ch, currentLayout);
            }
            finally
            {
                updating = false;
            }
        }

        deviceBox.SelectionChanged += (_, _) => ApplyQuickAudio(deviceChanged: true);
        layoutBox.SelectionChanged += (_, _) => ApplyQuickAudio(deviceChanged: false);

        var panel = new StackPanel { Spacing = 10, Padding = new Thickness(4) };
        panel.Children.Add(deviceBox);
        panel.Children.Add(layoutBox);
        panel.Children.Add(noteText);

        var flyout = new Flyout
        {
            Content = panel,
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top
        };
        bool isDark = RootGrid.RequestedTheme != ElementTheme.Light;
        var presenterStyle = new Style(typeof(FlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(isDark ? Color.FromArgb(245, 28, 28, 28) : Color.FromArgb(250, 248, 248, 248))));
        presenterStyle.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(isDark ? Color.FromArgb(51, 255, 255, 255) : Color.FromArgb(36, 0, 0, 0))));
        presenterStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        presenterStyle.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(10)));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, isDark ? ElementTheme.Dark : ElementTheme.Light));
        flyout.FlyoutPresenterStyle = presenterStyle;
        flyout.ShowAt(SpeakerBadgeBtn);
    }

    // Settings live in a panel that slides in over the right of the window. Every change applies as it is made;
    // there is nothing to confirm or cancel, and the panel closes with its X, Esc, or a click outside it.
    private void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsOverlay.Visibility == Visibility.Visible) CloseSettingsPanel();
        else OpenSettingsPanel();
    }

    private void SettingsCloseBtn_Click(object sender, RoutedEventArgs e) => CloseSettingsPanel();

    private void SettingsScrim_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => CloseSettingsPanel();

    private DispatcherTimer? _settingsAudioTimer;
    private bool _settingsAudioPending;

    private void OpenSettingsPanel()
    {
        if (_isMiniPlayer) SetMiniPlayerMode(false);
        SettingsPanelHost.Content = BuildSettingsContent();

        // Start off to the right and transparent without animating, then slide and fade in.
        var slide = SettingsPanel.TranslationTransition;
        var fade = SettingsOverlay.OpacityTransition;
        SettingsPanel.TranslationTransition = null;
        SettingsOverlay.OpacityTransition = null;
        SettingsPanel.Translation = new System.Numerics.Vector3(40, 0, 0);
        SettingsOverlay.Opacity = 0;
        SettingsPanel.TranslationTransition = slide;
        SettingsOverlay.OpacityTransition = fade;
        SettingsOverlay.Visibility = Visibility.Visible;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            SettingsPanel.Translation = System.Numerics.Vector3.Zero;
            SettingsOverlay.Opacity = 1;
        });
    }

    private void CloseSettingsPanel()
    {
        if (SettingsOverlay.Visibility != Visibility.Visible) return;
        FlushPendingAudioSettings();
        SettingsOverlay.Visibility = Visibility.Collapsed;
        SettingsPanelHost.Content = null;
    }

    // Slider drags fire many changes: write the file and reconfigure the output once the value settles.
    private void ApplyAudioSettingsSoon()
    {
        _settingsAudioPending = true;
        if (_settingsAudioTimer == null)
        {
            _settingsAudioTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _settingsAudioTimer.Tick += (_, _) => FlushPendingAudioSettings();
        }
        _settingsAudioTimer.Stop();
        _settingsAudioTimer.Start();
    }

    private void FlushPendingAudioSettings()
    {
        _settingsAudioTimer?.Stop();
        if (!_settingsAudioPending) return;
        _settingsAudioPending = false;
        _settings.Save();
        ApplyAudioConfigFromSettings();
    }

    private FrameworkElement BuildSettingsContent()
    {
        bool loading = true;

        var devices = WasapiAudioEngine.GetOutputDevices();
        var deviceBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var d in devices) deviceBox.Items.Add(new ComboBoxItem { Content = d.FriendlyName, Tag = d.Id });
        deviceBox.SelectedIndex = Math.Max(0, devices.ToList().FindIndex(d => d.Id == _settings.OutputDeviceId));

        var layoutBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        int initSettingsCh = WasapiAudioEngine.GetEndpointChannelCount(_settings.OutputDeviceId);
        PopulateSpeakerLayoutCombo(layoutBox, initSettingsCh, _settings.SpeakerLayout);

        var centerSlider = new Slider { Minimum = 0, Maximum = 100, Value = _settings.CenterLevel * 100, Width = 150, VerticalAlignment = VerticalAlignment.Center };
        var lfeSlider = new Slider { Minimum = 0, Maximum = 150, Value = _settings.LfeGain * 100, Width = 150, VerticalAlignment = VerticalAlignment.Center };
        var rearSlider = new Slider { Minimum = 0, Maximum = 100, Value = _settings.RearLevel * 100, Width = 150, VerticalAlignment = VerticalAlignment.Center };

        var (deviceRow, _, _) = SettingsCard.Row("", "Output device", "WASAPI audio endpoint", deviceBox, controlBelow: true);
        var (layoutRow, _, layoutDetail) = SettingsCard.Row("", "Speaker layout", "Checking endpoint...", layoutBox, controlBelow: true);
        var (centerRow, _, _) = SettingsCard.Row("", "5.1 Center vocal fill", "Center balance (%)", centerSlider);
        var (lfeRow, _, _) = SettingsCard.Row("", "Subwoofer (LFE) boost", "<80 Hz crossover gain (%)", lfeSlider);
        var (rearRow, _, _) = SettingsCard.Row("", "5.1 Rear surround stage", "Rear ambient fill (%)", rearSlider);

        bool updatingSettingsAudio = false;
        void UpdateEndpointAndSliders(bool deviceChanged)
        {
            if (updatingSettingsAudio) return;
            updatingSettingsAudio = true;
            try
            {
                string id = (deviceBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
                int ch = WasapiAudioEngine.GetEndpointChannelCount(id);
                var selLayout = (SpeakerLayout)((layoutBox.SelectedItem as ComboBoxItem)?.Tag ?? _settings.SpeakerLayout);
                if (deviceChanged)
                {
                    if (selLayout is SpeakerLayout.Stereo20 or SpeakerLayout.Surround51Direct)
                    {
                        selLayout = WasapiAudioEngine.GetRecommendedDirectLayout(id);
                    }
                    PopulateSpeakerLayoutCombo(layoutBox, ch, selLayout);
                }

                layoutDetail.Text = DescribeEndpointStatus(ch, selLayout);

                bool usesLfe = selLayout is SpeakerLayout.Stereo21 or SpeakerLayout.Surround51Upmix;
                bool usesSurround = selLayout == SpeakerLayout.Surround51Upmix;
                lfeSlider.IsEnabled = usesLfe;
                centerSlider.IsEnabled = usesSurround;
                rearSlider.IsEnabled = usesSurround;
                lfeRow.Opacity = usesLfe ? 1.0 : 0.45;
                centerRow.Opacity = usesSurround ? 1.0 : 0.45;
                rearRow.Opacity = usesSurround ? 1.0 : 0.45;
            }
            finally
            {
                updatingSettingsAudio = false;
            }
        }

        void CommitAudio()
        {
            if (loading || updatingSettingsAudio) return;
            _settings.OutputDeviceId = (deviceBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
            _settings.SpeakerLayout = (SpeakerLayout)((layoutBox.SelectedItem as ComboBoxItem)?.Tag ?? _settings.SpeakerLayout);
            _settings.CenterLevel = (float)(centerSlider.Value / 100.0);
            _settings.LfeGain = (float)(lfeSlider.Value / 100.0);
            _settings.RearLevel = (float)(rearSlider.Value / 100.0);
            ApplyAudioSettingsSoon();
        }

        deviceBox.SelectionChanged += (_, _) => { UpdateEndpointAndSliders(deviceChanged: true); CommitAudio(); };
        layoutBox.SelectionChanged += (_, _) => { UpdateEndpointAndSliders(deviceChanged: false); CommitAudio(); };
        centerSlider.ValueChanged += (_, _) => CommitAudio();
        lfeSlider.ValueChanged += (_, _) => CommitAudio();
        rearSlider.ValueChanged += (_, _) => CommitAudio();
        UpdateEndpointAndSliders(deviceChanged: false);

        var replayGainBox = new ComboBox { MinWidth = 180, FontSize = 12 };
        replayGainBox.Items.Add(new ComboBoxItem { Content = "Track (Balanced)", Tag = "Track" });
        replayGainBox.Items.Add(new ComboBoxItem { Content = "Album (Preserve)", Tag = "Album" });
        replayGainBox.Items.Add(new ComboBoxItem { Content = "Off", Tag = "Off" });
        replayGainBox.SelectedIndex = _settings.ReplayGainMode switch
        {
            "Album" => 1,
            "Off" => 2,
            _ => 0
        };
        var (rgRow, _, rgDetail) = SettingsCard.Row("\uE767", "Loudness leveling",
            "On-the-fly volume match", replayGainBox);
        replayGainBox.SelectionChanged += (_, _) =>
        {
            if (loading) return;
            string mode = (replayGainBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Track";
            if (string.Equals(_settings.ReplayGainMode, mode, StringComparison.OrdinalIgnoreCase)) return;
            _settings.ReplayGainMode = mode;
            _settings.Save();
            ApplyLiveReplayGain();
        };

        var eqPresetBox = new ComboBox { MinWidth = 180, FontSize = 12 };
        string[] eqOrder = { "Flat", "Rock", "Pop", "Jazz", "Classical", "Bass Boost", "Vocal", "Acoustic", "Electronic", "Custom" };
        ComboBoxItem? customEqItem = null;
        foreach (string pName in eqOrder)
        {
            var item = new ComboBoxItem
            {
                Content = pName == "Flat" ? "Flat (Off)" : pName == "Custom" ? "Custom..." : pName,
                Tag = pName
            };
            if (pName == "Custom") customEqItem = item;
            eqPresetBox.Items.Add(item);
        }
        int eqSelectedIdx = Array.FindIndex(eqOrder, p => string.Equals(p, _settings.EqualizerPreset, StringComparison.OrdinalIgnoreCase));
        eqPresetBox.SelectedIndex = eqSelectedIdx >= 0 ? eqSelectedIdx : 0;

        var (eqRow, _, _) = SettingsCard.Row("\uE9E9", "Equalizer", "10-band tone shaping", eqPresetBox);

        bool openingEqDialog = false;
        async Task OpenCustomEqOnceAsync()
        {
            if (openingEqDialog) return;
            openingEqDialog = true;
            try
            {
                eqPresetBox.IsDropDownOpen = false;
                await OpenEqualizerDialogAsync(eqPresetBox);
            }
            finally { openingEqDialog = false; }
        }

        if (customEqItem != null)
        {
            customEqItem.Tapped += async (_, _) => await OpenCustomEqOnceAsync();
        }

        eqPresetBox.SelectionChanged += async (_, _) =>
        {
            if (loading) return;
            string tag = (eqPresetBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Flat";
            if (tag == "Custom")
            {
                _settings.EqualizerPreset = "Custom";
                _settings.Save();
                await OpenCustomEqOnceAsync();
                return;
            }
            _settings.EqualizerPreset = tag;
            if (EqualizerSampleProvider.Presets.TryGetValue(tag, out var presetGains))
            {
                _settings.EqualizerGains = (double[])presetGains.Clone();
            }
            _settings.Save();
            _engine.SetEqualizerGains(_settings.EqualizerGains);
        };

        var exclusiveToggle = new ToggleSwitch
        {
            IsOn = _settings.WasapiExclusive,
            MinWidth = 0,
            OnContent = "On",
            OffContent = "Off",
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var (exclusiveRow, _, _) = SettingsCard.Row("\uE8D6", "WASAPI Exclusive",
            "Bypass Windows mixer (locks output device)", exclusiveToggle);
        exclusiveToggle.Toggled += (_, _) =>
        {
            if (loading) return;
            _settings.WasapiExclusive = exclusiveToggle.IsOn;
            _settings.Save();
            ApplyAudioConfigFromSettings();
        };

        var vizBox = new ComboBox { MinWidth = 200 };
        vizBox.Items.Add(new ComboBoxItem { Content = "Spectrum bars", Tag = "Spectrum" });
        vizBox.Items.Add(new ComboBoxItem { Content = "Silk waveform", Tag = "Waveform" });
        vizBox.Items.Add(new ComboBoxItem { Content = "Bars + waveform", Tag = "BarsAndWaveform" });
        vizBox.Items.Add(new ComboBoxItem { Content = "Off", Tag = "Off" });
        vizBox.SelectedIndex = !_settings.VisualizerEnabled || _settings.VisualizerMode == "Off" ? 3
            : _settings.VisualizerMode == "Waveform" ? 1
            : _settings.VisualizerMode == "BarsAndWaveform" ? 2 : 0;
        vizBox.SelectionChanged += (_, _) =>
        {
            if (loading) return;
            string vizTag = (vizBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Spectrum";
            _settings.VisualizerMode = vizTag;
            _settings.VisualizerEnabled = vizTag != "Off";
            _settings.Save();
            ApplyVisualizerFromSettings();
        };

        var themeBox = new ComboBox { MinWidth = 140 };
        themeBox.Items.Add(new ComboBoxItem { Content = "Dark", Tag = "Dark" });
        themeBox.Items.Add(new ComboBoxItem { Content = "Light", Tag = "Light" });
        themeBox.Items.Add(new ComboBoxItem { Content = "Use system setting", Tag = "System" });
        themeBox.SelectedIndex = string.Equals(_settings.Theme, "Light", StringComparison.OrdinalIgnoreCase) ? 1
            : string.Equals(_settings.Theme, "System", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
        themeBox.SelectionChanged += (_, _) =>
        {
            if (loading) return;
            string themeTag = (themeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Dark";
            if (string.Equals(_settings.Theme, themeTag, StringComparison.OrdinalIgnoreCase)) return;
            _settings.Theme = themeTag;
            _settings.Save();
            ApplyTheme(themeTag);
        };

        static string TintLabel(double v) => v <= 0 ? "Off: the window keeps its plain theme colours"
            : $"How strongly the window takes the cover's colour: {v:0} %";
        var tintSlider = new Slider
        {
            Minimum = 0, Maximum = 200, StepFrequency = 10, SmallChange = 10, LargeChange = 50,
            Value = Math.Clamp(_settings.CoverTint, 0, 200), Width = 150, VerticalAlignment = VerticalAlignment.Center
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tintSlider, "Cover colour");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(tintSlider, "CoverTintSlider");
        var (tintRow, _, tintDetail) = SettingsCard.Row("", "Cover colour", TintLabel(tintSlider.Value), tintSlider);
        tintSlider.ValueChanged += (_, e) =>
        {
            if (loading) return;
            int value = (int)Math.Round(e.NewValue);
            if (value == _settings.CoverTint) return;
            _settings.CoverTint = value;
            _settings.Save();
            tintDetail.Text = TintLabel(value);
            ApplyCoverTint(_lastCoverTint, fade: false);
        };

        var rescanNowBtn = new Button { Content = "Rescan now" };
        rescanNowBtn.Click += (_, _) => RescanBtn_Click(this, new RoutedEventArgs());
        string librarySubtitle = string.IsNullOrWhiteSpace(_settings.MusicFolder)
            ? "No music folder selected"
            : $"{_settings.MusicFolder} · Watched automatically for changes (F5)";
        var (rescanRow, _, _) = SettingsCard.Row("", "Music library folder", librarySubtitle, rescanNowBtn);

        int localCount = _allTracks.Count(t => !t.IsCloudOnly);
        int measuredCount = _allTracks.Count(t => !t.IsCloudOnly && t.ReplayGainTrackDb.HasValue);
        string RgStatusText(int measured, int total) =>
            total == 0 ? "No local tracks loaded" : $"{measured:N0} of {total:N0} tracks measured (read-only)";
        var rgAnalyzeBtn = new Button { Content = _rgScanCts != null ? "Cancel" : "Analyze" };
        var (rgAnalyzeRow, _, rgAnalyzeDetail) = SettingsCard.Row("\uE9D9", "Analyze loudness", RgStatusText(measuredCount, localCount), rgAnalyzeBtn);
        rgAnalyzeBtn.Click += async (_, _) =>
        {
            if (_rgScanCts != null)
            {
                _rgScanCts.Cancel();
                return;
            }
            var pending = _allTracks.Where(t => !t.IsCloudOnly && !t.ReplayGainTrackDb.HasValue).ToList();
            if (pending.Count == 0)
            {
                rgAnalyzeDetail.Text = RgStatusText(_allTracks.Count(t => !t.IsCloudOnly && t.ReplayGainTrackDb.HasValue), _allTracks.Count(t => !t.IsCloudOnly));
                ShowHudPill("All local tracks are already measured");
                return;
            }
            var cts = new CancellationTokenSource();
            _rgScanCts = cts;
            rgAnalyzeBtn.Content = "Cancel";
            int total = _allTracks.Count(t => !t.IsCloudOnly);
            int done = _allTracks.Count(t => !t.IsCloudOnly && t.ReplayGainTrackDb.HasValue);
            try
            {
                await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cts.Token }, async (t, ct) =>
                {
                    if (ct.IsCancellationRequested) return;
                    var res = await Task.Run(() => ReplayGainAnalyzer.AnalyzeFile(t.Path), ct);
                    int curDone = Interlocked.Increment(ref done);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (res is ReplayGainAnalysis rg)
                        {
                            foreach (var peer in EnumerateMatchingTrackItems(t))
                            {
                                peer.ReplayGainTrackDb = rg.TrackGainDb;
                                peer.ReplayGainTrackPeak = rg.TrackPeak;
                            }
                            if (string.Equals(_engine.CurrentPath, t.Path, StringComparison.OrdinalIgnoreCase))
                                _engine.SetReplayGainFactor(GetTrackReplayGainFactor(t));
                        }
                        rgAnalyzeDetail.Text = $"Analyzing {curDone:N0} / {total:N0}...";
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
                int finalMeasured = _allTracks.Count(t => !t.IsCloudOnly && t.ReplayGainTrackDb.HasValue);
                rgAnalyzeBtn.Content = "Analyze";
                rgAnalyzeDetail.Text = RgStatusText(finalMeasured, _allTracks.Count(t => !t.IsCloudOnly));
            }
        };

        var resetBtn = new Button { Content = "Reset" };
        var (resetRow, _, _) = SettingsCard.Row("", "Reset to defaults", "Audio, appearance and window layout", resetBtn);
        resetBtn.Click += (_, _) =>
        {
            _settingsAudioPending = false;
            _settings.ResetAudioAndUiToDefaults();
            _settings.Save();
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p &&
                p.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized)
            {
                p.Restore();
            }
            AppWindow.Resize(new Windows.Graphics.SizeInt32(_settings.WindowWidth, _settings.WindowHeight));
            _rightPaneOpen = _settings.RightPaneOpen;
            ApplyPaneWidths();
            SelectedInspectorScroll.MaxHeight = _settings.InspectorHeight;
            _inspectorArtMode = _settings.InspectorArtMode;
            ApplyInspectorArtMode();
            ApplyTheme(_settings.Theme);
            ApplyAudioConfigFromSettings();
            ApplyLiveReplayGain();
            ApplyVisualizerFromSettings();
            SettingsPanelHost.Content = BuildSettingsContent();
        };

        var panel = new StackPanel();
        panel.Children.Add(SettingsCard.Section("Audio and speakers", first: true));
        panel.Children.Add(SettingsCard.Card(deviceRow, layoutRow, rgRow, eqRow, exclusiveRow, centerRow, lfeRow, rearRow));
        panel.Children.Add(SettingsCard.Section("Visualizer and appearance"));
        panel.Children.Add(SettingsCard.Card(
            SettingsCard.Row("", "Visualizer style", "Playbar and the F11 focus stage", vizBox).Row,
            SettingsCard.Row("", "App theme", "Applies to the whole window", themeBox).Row,
            tintRow));
        panel.Children.Add(SettingsCard.Section("Library and application"));
        panel.Children.Add(SettingsCard.Card(rescanRow, rgAnalyzeRow, resetRow));

        loading = false;
        return panel;
    }

    private async void InfoShortcuts_Click(object sender, RoutedEventArgs e)
    {
        CloseSettingsPanel();
        await ShowShortcutsHelpAsync();
    }

    private async void InfoAbout_Click(object sender, RoutedEventArgs e)
    {
        CloseSettingsPanel();
        await ShowAboutDialogAsync();
    }

    private async Task OpenPropertiesDialogAsync(IReadOnlyList<TrackItem> tracks)
    {
        if (tracks.Count == 0) return;
        if (tracks.Count > 1)
        {
            await OpenBatchPropertiesDialogAsync(tracks);
            return;
        }

        var track = tracks[0];
        var details = await Task.Run(() => MusicLibrary.ReadFullDetails(track.Path));
        string origDir = Path.GetDirectoryName(track.Path) ?? string.Empty;
        string origExt = Path.GetExtension(track.Path);
        string origNameNoExt = Path.GetFileNameWithoutExtension(track.Path);
        DateTime origLocalDate = track.DateAddedUtc.ToLocalTime();

        var artPreview = new Image { Width = 72, Height = 72, Stretch = Stretch.UniformToFill };
        var artPlaceholder = new FontIcon
        {
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            Glyph = "\uE93C",
            FontSize = 24,
            Opacity = 0.45
        };
        var artBox = new Border
        {
            Width = 72,
            Height = 72,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
            Child = new Grid { Children = { artPlaceholder, artPreview } }
        };

        byte[]? currentArtBytes = details.ArtBytes;
        string currentArtMime = details.ArtMimeType;
        bool artChanged = false;
        await SetImageFromBytesAsync(currentArtBytes, artPreview);

        Button MakeCompactIconBtn(string glyph, string text, Color? fg = null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            var icon = new FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Glyph = glyph, FontSize = 11.5 };
            var lbl = new TextBlock { Text = text, FontSize = 11.5 };
            if (fg.HasValue)
            {
                var brush = new SolidColorBrush(fg.Value);
                icon.Foreground = brush;
                lbl.Foreground = brush;
            }
            sp.Children.Add(icon);
            sp.Children.Add(lbl);
            return new Button { Content = sp, Padding = new Thickness(9, 4, 9, 4), MinHeight = 0, MinWidth = 0 };
        }

        var changeArtBtn = MakeCompactIconBtn("\uE722", "Art...");
        var removeArtBtn = MakeCompactIconBtn("\uE74D", "Clear");
        removeArtBtn.IsEnabled = currentArtBytes is { Length: > 0 };
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

        var artBtnStack = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        artBtnStack.Children.Add(changeArtBtn);
        artBtnStack.Children.Add(removeArtBtn);

        var heroArtSection = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        heroArtSection.Children.Add(artBox);
        heroArtSection.Children.Add(artBtnStack);

        Border MakeSpecPill(string text) => new()
        {
            Background = new SolidColorBrush(Color.FromArgb(26, 128, 128, 128)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Child = new TextBlock { Text = text, FontSize = 11, Opacity = 0.85 }
        };

        var heroTitleText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(details.Title) ? track.DisplayTitle : details.Title.Trim(),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var specStrip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 0, 2) };
        string codecPart = $"{track.Codec} {track.BitrateText}".Trim();
        if (!string.IsNullOrEmpty(codecPart)) specStrip.Children.Add(MakeSpecPill(codecPart));
        string ratePart = track.SampleRate > 0 ? $"{track.SampleRate / 1000.0:0.#} kHz · {track.ChannelsText}" : track.ChannelsText;
        if (!string.IsNullOrEmpty(ratePart)) specStrip.Children.Add(MakeSpecPill(ratePart));
        if (!string.IsNullOrEmpty(track.DurationText)) specStrip.Children.Add(MakeSpecPill(track.DurationText));

        var heroFolderText = new TextBlock
        {
            Text = origDir,
            FontSize = 11,
            Opacity = 0.62,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        ToolTipService.SetToolTip(heroFolderText, track.Path);

        var heroSummary = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        heroSummary.Children.Add(heroTitleText);
        heroSummary.Children.Add(specStrip);
        heroSummary.Children.Add(heroFolderText);

        double? selectedRating = details.Rating;
        var ratingCell = new StarRatingCell { Value = selectedRating, HorizontalAlignment = HorizontalAlignment.Right };
        ToolTipService.SetToolTip(ratingCell, "Click stars (left half = .5, right half = .0; click same rating again to clear)");
        ratingCell.RatingSelected += (_, val) =>
        {
            selectedRating = val;
            ratingCell.Value = val;
        };

        bool deletedFromDialog = false;
        ContentDialog? dlg = null;

        var explorerBtn = MakeCompactIconBtn("\uE8DA", "Folder");
        ToolTipService.SetToolTip(explorerBtn, "Open containing folder in File Explorer");
        explorerBtn.Click += (_, _) => OpenInExplorer(track.Path);

        var deleteBtn = MakeCompactIconBtn("\uE74D", "Delete", Color.FromArgb(255, 255, 118, 118));
        ToolTipService.SetToolTip(deleteBtn, "Move file to Recycle Bin");

        var fileQuickBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        fileQuickBtns.Children.Add(explorerBtn);
        fileQuickBtns.Children.Add(deleteBtn);

        var heroRight = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        heroRight.Children.Add(ratingCell);
        heroRight.Children.Add(fileQuickBtns);

        var heroGrid = new Grid { ColumnSpacing = 14 };
        heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(heroArtSection, 0);
        Grid.SetColumn(heroSummary, 1);
        Grid.SetColumn(heroRight, 2);
        heroGrid.Children.Add(heroArtSection);
        heroGrid.Children.Add(heroSummary);
        heroGrid.Children.Add(heroRight);

        var heroCard = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(18, 128, 128, 128)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(36, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Child = heroGrid
        };

        StackPanel MakeField(string label, FrameworkElement input)
        {
            var sp = new StackPanel { Spacing = 3 };
            sp.Children.Add(new TextBlock { Text = label, FontSize = 11.5, Opacity = 0.72, Margin = new Thickness(2, 0, 0, 0) });
            sp.Children.Add(input);
            return sp;
        }

        var titleBox = new TextBox { Text = details.Title };
        titleBox.TextChanged += (_, _) =>
        {
            string t = titleBox.Text.Trim();
            heroTitleText.Text = string.IsNullOrEmpty(t) ? track.FileName : t;
        };
        var artistBox = new TextBox { Text = details.Artist };
        var albumArtistBox = new TextBox { Text = details.AlbumArtist };
        var albumBox = new TextBox { Text = details.Album };
        var genreBox = new TextBox { Text = details.Genre };
        var yearBox = new NumberBox { Value = details.Year > 0 ? details.Year : double.NaN, Minimum = 0, Maximum = 2100, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
        var trackNumBox = new NumberBox { Value = details.TrackNumber > 0 ? details.TrackNumber : double.NaN, Minimum = 0, Maximum = 999, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
        var discNumBox = new NumberBox { Value = details.DiscNumber > 0 ? details.DiscNumber : double.NaN, Minimum = 0, Maximum = 99, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };

        var dateTextBox = new TextBox
        {
            Text = origLocalDate.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
            PlaceholderText = "dd/MM/yyyy HH:mm"
        };
        var nowBtn = new Button { Content = "Now", Padding = new Thickness(9, 4, 9, 4), MinHeight = 0, VerticalAlignment = VerticalAlignment.Stretch };
        nowBtn.Click += (_, _) => dateTextBox.Text = DateTime.Now.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        var dateInputGrid = new Grid { ColumnSpacing = 4 };
        dateInputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dateInputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(dateTextBox, 0); Grid.SetColumn(nowBtn, 1);
        dateInputGrid.Children.Add(dateTextBox); dateInputGrid.Children.Add(nowBtn);

        var row3Grid = new Grid { ColumnSpacing = 6 };
        row3Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        row3Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        row3Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
        row3Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var yearField = MakeField("Year", yearBox);
        var trackField = MakeField("Track #", trackNumBox);
        var discField = MakeField("Disc #", discNumBox);
        var dateField = MakeField("Date Added", dateInputGrid);
        Grid.SetColumn(yearField, 0); Grid.SetColumn(trackField, 1); Grid.SetColumn(discField, 2); Grid.SetColumn(dateField, 3);
        row3Grid.Children.Add(yearField); row3Grid.Children.Add(trackField); row3Grid.Children.Add(discField); row3Grid.Children.Add(dateField);

        var fileNameBox = new TextBox { Text = origNameNoExt };
        var extBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(24, 128, 128, 128)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(9, 0, 9, 0),
            Child = new TextBlock { Text = origExt, FontSize = 12, Opacity = 0.78, VerticalAlignment = VerticalAlignment.Center }
        };
        var fileNameGrid = new Grid { ColumnSpacing = 6 };
        fileNameGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fileNameGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(fileNameBox, 0); Grid.SetColumn(extBadge, 1);
        fileNameGrid.Children.Add(fileNameBox); fileNameGrid.Children.Add(extBadge);

        var leftFormGrid = new Grid { ColumnSpacing = 10, RowSpacing = 9 };
        leftFormGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        leftFormGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < 5; r++)
            leftFormGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var titleField = MakeField("Title", titleBox);
        var artistField = MakeField("Artist", artistBox);
        var albumArtistField = MakeField("Album Artist", albumArtistBox);
        var albumField = MakeField("Album", albumBox);
        var genreField = MakeField("Genre", genreBox);
        var fileNameField = MakeField("File Name", fileNameGrid);

        Grid.SetRow(titleField, 0); Grid.SetColumn(titleField, 0); Grid.SetColumnSpan(titleField, 2);
        Grid.SetRow(artistField, 1); Grid.SetColumn(artistField, 0);
        Grid.SetRow(albumArtistField, 1); Grid.SetColumn(albumArtistField, 1);
        Grid.SetRow(albumField, 2); Grid.SetColumn(albumField, 0);
        Grid.SetRow(genreField, 2); Grid.SetColumn(genreField, 1);
        Grid.SetRow(row3Grid, 3); Grid.SetColumn(row3Grid, 0); Grid.SetColumnSpan(row3Grid, 2);
        Grid.SetRow(fileNameField, 4); Grid.SetColumn(fileNameField, 0); Grid.SetColumnSpan(fileNameField, 2);

        leftFormGrid.Children.Add(titleField);
        leftFormGrid.Children.Add(artistField);
        leftFormGrid.Children.Add(albumArtistField);
        leftFormGrid.Children.Add(albumField);
        leftFormGrid.Children.Add(genreField);
        leftFormGrid.Children.Add(row3Grid);
        leftFormGrid.Children.Add(fileNameField);

        var lyricsBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            PlaceholderText = "Paste lyrics or [mm:ss.xx] synced .LRC lines, or click Fetch from web...",
            FontSize = 12.5,
            Height = 250,
            Text = details.Lyrics.Replace("\r\n", "\r").Replace("\n", "\r")
        };
        ScrollViewer.SetVerticalScrollBarVisibility(lyricsBox, ScrollBarVisibility.Auto);

        var lyricsStatusText = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        void UpdateLyricsBadge()
        {
            string raw = lyricsBox.Text ?? string.Empty;
            if (LrcParser.IsSynced(raw))
            {
                lyricsStatusText.Text = "· Synced .LRC";
                lyricsStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 200, 59));
                lyricsStatusText.Opacity = 0.95;
            }
            else if (!string.IsNullOrWhiteSpace(raw))
            {
                lyricsStatusText.Text = "· Plain text";
                lyricsStatusText.ClearValue(TextBlock.ForegroundProperty);
                lyricsStatusText.Opacity = 0.65;
            }
            else
            {
                lyricsStatusText.Text = "· None";
                lyricsStatusText.ClearValue(TextBlock.ForegroundProperty);
                lyricsStatusText.Opacity = 0.5;
            }
        }
        UpdateLyricsBadge();
        lyricsBox.TextChanged += (_, _) => UpdateLyricsBadge();

        var fetchLyricsBtn = MakeCompactIconBtn("\uE774", "Fetch lyrics");
        ToolTipService.SetToolTip(fetchLyricsBtn, "Search LRCLIB for synced .LRC / plain lyrics");
        fetchLyricsBtn.Click += async (_, _) =>
        {
            fetchLyricsBtn.IsEnabled = false;
            try
            {
                string fetchedLyrics = await AutoTagger.FetchLyricsAsync(
                    track.Path, titleBox.Text.Trim(), artistBox.Text.Trim(), track.Duration);
                if (!string.IsNullOrWhiteSpace(fetchedLyrics))
                {
                    lyricsBox.Text = fetchedLyrics.Replace("\r\n", "\r").Replace("\n", "\r");
                    UpdateLyricsBadge();
                }
                else
                {
                    lyricsStatusText.Text = "· Not found online";
                }
            }
            finally
            {
                fetchLyricsBtn.IsEnabled = true;
            }
        };

        var lyricsHeaderLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        lyricsHeaderLeft.Children.Add(new TextBlock { Text = "Lyrics", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Opacity = 0.85, VerticalAlignment = VerticalAlignment.Center });
        lyricsHeaderLeft.Children.Add(lyricsStatusText);

        var lyricsHeader = new Grid { Margin = new Thickness(2, 0, 0, 4) };
        lyricsHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lyricsHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(lyricsHeaderLeft, 0); Grid.SetColumn(fetchLyricsBtn, 1);
        lyricsHeader.Children.Add(lyricsHeaderLeft); lyricsHeader.Children.Add(fetchLyricsBtn);

        var rightLyricsPanel = new Grid();
        rightLyricsPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rightLyricsPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(lyricsHeader, 0); Grid.SetRow(lyricsBox, 1);
        rightLyricsPanel.Children.Add(lyricsHeader); rightLyricsPanel.Children.Add(lyricsBox);

        var bodyGrid = new Grid { ColumnSpacing = 18 };
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(430) });
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(leftFormGrid, 0);
        Grid.SetColumn(rightLyricsPanel, 1);
        bodyGrid.Children.Add(leftFormGrid);
        bodyGrid.Children.Add(rightLyricsPanel);

        var rootPanel = new StackPanel { Spacing = 12, Width = 860 };
        rootPanel.Children.Add(heroCard);
        rootPanel.Children.Add(bodyGrid);

        dlg = new ContentDialog
        {
            Title = "Song Properties & Lyrics",
            Content = rootPanel,
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Auto-Tag...",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.RequestedTheme
        };
        dlg.Resources["ContentDialogMaxWidth"] = 940.0;
        DialogFooter.MainButtonOnRight(dlg);

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

        var updated = new MusicLibrary.TrackDetails
        {
            Title = titleBox.Text.Trim(),
            Artist = artistBox.Text.Trim(),
            Album = albumBox.Text.Trim(),
            AlbumArtist = albumArtistBox.Text.Trim(),
            Genre = genreBox.Text.Trim(),
            Year = double.IsNaN(yearBox.Value) ? 0 : (int)yearBox.Value,
            TrackNumber = double.IsNaN(trackNumBox.Value) ? 0 : (int)trackNumBox.Value,
            DiscNumber = double.IsNaN(discNumBox.Value) ? 0 : (int)discNumBox.Value,
            Rating = selectedRating,
            Lyrics = lyricsBox.Text.Replace("\r", Environment.NewLine),
            ArtBytes = currentArtBytes,
            ArtMimeType = currentArtMime,
            ArtChanged = artChanged
        };

        bool tagsSaved = await SaveTrackDetailsAsync(track, updated);
        if (!tagsSaved) return;

        string rawDate = dateTextBox.Text.Trim();
        if (!string.IsNullOrEmpty(rawDate) &&
            (DateTime.TryParseExact(rawDate, new[] { "dd/MM/yyyy HH:mm", "dd/MM/yyyy H:mm", "dd/MM/yyyy", "yyyy-MM-dd HH:mm", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var newLocal) ||
             DateTime.TryParse(rawDate, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out newLocal)))
        {
            var newMinuteLocal = new DateTime(newLocal.Year, newLocal.Month, newLocal.Day, newLocal.Hour, newLocal.Minute, 0, DateTimeKind.Local);
            var origMinuteLocal = new DateTime(origLocalDate.Year, origLocalDate.Month, origLocalDate.Day, origLocalDate.Hour, origLocalDate.Minute, 0, DateTimeKind.Local);
            if (newMinuteLocal != origMinuteLocal)
            {
                DateTime newUtc = newMinuteLocal.ToUniversalTime();
                var (dateOk, dateErr) = await _engine.WithFileReleasedAsync(track.Path, () => Task.Run(() =>
                {
                    try
                    {
                        File.SetLastWriteTimeUtc(track.Path, newUtc);
                        return (true, (string?)null);
                    }
                    catch (Exception ex) { return (false, ex.Message); }
                }));
                if (dateOk)
                {
                    track.DateAddedUtc = newUtc;
                    track.FileWriteUtcTicks = newUtc.Ticks;
                    RefreshVisibleTracks();
                }
                else
                {
                    await ShowErrorDialogAsync("Could not update Date Added", dateErr ?? "Unknown error.");
                }
            }
        }

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
            var (renamed, renameErr) = await RenameTrackPathAsync(track, oldPath, newPath);
            if (!renamed)
            {
                await ShowErrorDialogAsync("Could not rename file", renameErr ?? "Unknown error.");
                return;
            }
            PushUndo(async () =>
            {
                await RenameTrackPathAsync(track, newPath, oldPath);
                ShowHudPill("Undone: File rename");
            });
        }
        SaveLibraryCacheSnapshot();
    }

    private async Task OpenBatchPropertiesDialogAsync(IReadOnlyList<TrackItem> tracks)
    {
        string CommonOrEmpty(Func<TrackItem, string> selector, out bool mixed)
        {
            var distinct = tracks.Select(selector).Distinct(StringComparer.Ordinal).Take(2).ToList();
            mixed = distinct.Count > 1;
            return mixed ? string.Empty : (distinct.Count == 1 ? distinct[0] : string.Empty);
        }

        string initArtist = CommonOrEmpty(t => t.Artist, out bool mixedArtist);
        string initAlbum = CommonOrEmpty(t => t.Album, out bool mixedAlbum);
        string initGenre = CommonOrEmpty(t => t.Genre, out bool mixedGenre);
        var distinctYears = tracks.Select(t => t.Year).Distinct().Take(2).ToList();
        bool mixedYear = distinctYears.Count > 1;
        int initYear = !mixedYear && distinctYears.Count == 1 ? distinctYears[0] : 0;

        var chkArtist = new CheckBox { Content = "Artist", MinWidth = 110 };
        var chkAlbum = new CheckBox { Content = "Album", MinWidth = 110 };
        var chkAlbumArtist = new CheckBox { Content = "Album Artist", MinWidth = 110 };
        var chkGenre = new CheckBox { Content = "Genre", MinWidth = 110 };
        var chkYear = new CheckBox { Content = "Year", MinWidth = 110 };
        var chkRating = new CheckBox { Content = "Rating", MinWidth = 110 };
        var chkArt = new CheckBox { Content = "Album Art", MinWidth = 110 };

        var artistBox = new TextBox { Text = initArtist, PlaceholderText = mixedArtist ? "<Multiple values>" : "" };
        var albumBox = new TextBox { Text = initAlbum, PlaceholderText = mixedAlbum ? "<Multiple values>" : "" };
        var albumArtistBox = new TextBox { Text = initArtist, PlaceholderText = mixedArtist ? "<Multiple values>" : "" };
        var genreBox = new TextBox { Text = initGenre, PlaceholderText = mixedGenre ? "<Multiple values>" : "" };
        var yearBox = new NumberBox { Value = initYear > 0 ? initYear : double.NaN, PlaceholderText = mixedYear ? "<Multiple>" : "", Minimum = 0, Maximum = 2100, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };

        var ratingCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        ratingCombo.Items.Add(new ComboBoxItem { Content = "Unrated", Tag = -1.0 });
        for (double r = 0.5; r <= 5.0; r += 0.5)
            ratingCombo.Items.Add(new ComboBoxItem { Content = $"{r:0.#} ★", Tag = r });
        ratingCombo.SelectedIndex = 0;

        artistBox.TextChanged += (_, _) => chkArtist.IsChecked = true;
        albumBox.TextChanged += (_, _) => chkAlbum.IsChecked = true;
        albumArtistBox.TextChanged += (_, _) => chkAlbumArtist.IsChecked = true;
        genreBox.TextChanged += (_, _) => chkGenre.IsChecked = true;
        yearBox.ValueChanged += (_, _) => chkYear.IsChecked = true;
        ratingCombo.SelectionChanged += (_, _) => chkRating.IsChecked = true;

        byte[]? batchArtBytes = null;
        string batchArtMime = "image/jpeg";
        var artPreview = new Image { Width = 56, Height = 56, Stretch = Stretch.UniformToFill };
        var artBox = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
            Child = artPreview
        };
        var chooseArtBtn = new Button { Content = "Choose Cover Art..." };
        var clearArtBtn = new Button { Content = "Remove Art" };

        var onlineSearchBox = new TextBox
        {
            PlaceholderText = "Album or artist to search...",
            Text = !string.IsNullOrWhiteSpace(initAlbum) ? initAlbum : initArtist,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var onlineGoBtn = new Button { Content = "Search" };
        var onlineStatusText = new TextBlock { Text = "", FontSize = 11, Opacity = 0.72 };
        var onlineThumbsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var onlineQueryGrid = new Grid { ColumnSpacing = 6 };
        onlineQueryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        onlineQueryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(onlineSearchBox, 0);
        Grid.SetColumn(onlineGoBtn, 1);
        onlineQueryGrid.Children.Add(onlineSearchBox);
        onlineQueryGrid.Children.Add(onlineGoBtn);

        var onlineArtTray = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 2, 0, 0) };
        onlineArtTray.Children.Add(onlineQueryGrid);
        onlineArtTray.Children.Add(onlineThumbsPanel);
        onlineArtTray.Children.Add(onlineStatusText);

        CancellationTokenSource? coverSearchCts = null;
        async Task RunOnlineCoverSearchAsync()
        {
            coverSearchCts?.Cancel();
            coverSearchCts = new CancellationTokenSource();
            var ct = coverSearchCts.Token;

            onlineGoBtn.IsEnabled = false;
            onlineStatusText.Text = "Searching iTunes album art...";
            onlineThumbsPanel.Children.Clear();

            string query = string.IsNullOrWhiteSpace(onlineSearchBox.Text)
                ? (!string.IsNullOrWhiteSpace(albumBox.Text) ? albumBox.Text : artistBox.Text)
                : onlineSearchBox.Text.Trim();
            string artistHint = !string.IsNullOrWhiteSpace(artistBox.Text) ? artistBox.Text.Trim() : initArtist;

            var opts = await AutoTagger.SearchAlbumCoversAsync(query, artistHint, ct);
            if (ct.IsCancellationRequested) return;

            onlineGoBtn.IsEnabled = true;
            if (opts.Count == 0)
            {
                onlineStatusText.Text = "No online covers found — try editing the search query.";
                return;
            }

            onlineStatusText.Text = $"Click a cover below to apply to all {tracks.Count:N0} tracks:";
            foreach (var opt in opts)
            {
                var thumb = new Image { Width = 62, Height = 62, Stretch = Stretch.UniformToFill };
                await SetImageFromBytesAsync(opt.Bytes, thumb);
                var border = new Border
                {
                    Width = 64,
                    Height = 64,
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
                    Child = thumb
                };
                ToolTipService.SetToolTip(border, opt.Source);
                var captured = opt.Bytes;
                border.Tapped += async (_, _) =>
                {
                    batchArtBytes = captured;
                    batchArtMime = captured.Length >= 4 && captured[0] == 0x89 && captured[1] == 0x50 ? "image/png" : "image/jpeg";
                    await SetImageFromBytesAsync(batchArtBytes, artPreview);
                    chkArt.IsChecked = true;
                };
                onlineThumbsPanel.Children.Add(border);
            }
        }
        onlineGoBtn.Click += async (_, _) => await RunOnlineCoverSearchAsync();
        onlineSearchBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                await RunOnlineCoverSearchAsync();
            }
        };

        var tracksWithArt = tracks.Where(t => t.HasEmbeddedArt).Take(4).ToList();
        var artFlyout = new MenuFlyout();
        foreach (var srcTrack in tracksWithArt)
        {
            var captured = srcTrack;
            var copyItem = new MenuFlyoutItem { Text = $"Copy from \"{captured.DisplayTitle}\"" };
            copyItem.Click += async (_, _) =>
            {
                var det = await Task.Run(() => MusicLibrary.ReadFullDetails(captured.Path));
                if (det.ArtBytes is { Length: > 0 })
                {
                    batchArtBytes = det.ArtBytes;
                    batchArtMime = string.IsNullOrWhiteSpace(det.ArtMimeType) ? "image/jpeg" : det.ArtMimeType;
                    await SetImageFromBytesAsync(batchArtBytes, artPreview);
                    chkArt.IsChecked = true;
                }
            };
            artFlyout.Items.Add(copyItem);
        }
        if (tracksWithArt.Count > 0)
            artFlyout.Items.Add(new MenuFlyoutSeparator());

        var findOnlineItem = new MenuFlyoutItem { Text = "Find cover online..." };
        findOnlineItem.Click += async (_, _) =>
        {
            onlineArtTray.Visibility = Visibility.Visible;
            if (!string.IsNullOrWhiteSpace(albumBox.Text))
                onlineSearchBox.Text = albumBox.Text.Trim();
            await RunOnlineCoverSearchAsync();
        };
        artFlyout.Items.Add(findOnlineItem);

        var browseFileItem = new MenuFlyoutItem { Text = "Browse image file..." };
        browseFileItem.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".jpg"); picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png"); picker.FileTypeFilter.Add(".webp");
            var picked = await picker.PickSingleFileAsync();
            if (picked == null) return;
            batchArtBytes = await File.ReadAllBytesAsync(picked.Path);
            string ext = Path.GetExtension(picked.Path).ToLowerInvariant();
            batchArtMime = ext == ".png" ? "image/png" : ext == ".webp" ? "image/webp" : "image/jpeg";
            await SetImageFromBytesAsync(batchArtBytes, artPreview);
            chkArt.IsChecked = true;
        };
        artFlyout.Items.Add(browseFileItem);
        chooseArtBtn.Flyout = artFlyout;

        clearArtBtn.Click += (_, _) =>
        {
            batchArtBytes = null;
            artPreview.Source = null;
            chkArt.IsChecked = true;
        };

        Grid MakeRow(CheckBox cb, FrameworkElement ctrl)
        {
            var g = new Grid { ColumnSpacing = 10 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(cb, 0);
            Grid.SetColumn(ctrl, 1);
            g.Children.Add(cb);
            g.Children.Add(ctrl);
            return g;
        }

        var artControls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        artControls.Children.Add(artBox);
        artControls.Children.Add(chooseArtBtn);
        artControls.Children.Add(clearArtBtn);

        var panel = new StackPanel { Spacing = 10, Width = 480 };
        panel.Children.Add(new TextBlock { Text = $"Checked fields will be applied to all {tracks.Count:N0} selected tracks.", FontSize = 12, Opacity = 0.75 });
        panel.Children.Add(MakeRow(chkArtist, artistBox));
        panel.Children.Add(MakeRow(chkAlbum, albumBox));
        panel.Children.Add(MakeRow(chkAlbumArtist, albumArtistBox));
        panel.Children.Add(MakeRow(chkGenre, genreBox));
        panel.Children.Add(MakeRow(chkYear, yearBox));
        panel.Children.Add(MakeRow(chkRating, ratingCombo));
        panel.Children.Add(MakeRow(chkArt, artControls));
        panel.Children.Add(onlineArtTray);

        var dlg = new ContentDialog
        {
            Title = $"Batch Properties ({tracks.Count:N0} tracks)",
            Content = panel,
            PrimaryButtonText = "Apply to all",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.RequestedTheme
        };
        dlg.Resources["ContentDialogMaxWidth"] = 560.0;
        DialogFooter.MainButtonOnRight(dlg);

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var snapshots = new List<(TrackItem Track, MusicLibrary.TrackDetails Before)>();
        foreach (var t in tracks)
        {
            var cur = await Task.Run(() => MusicLibrary.ReadFullDetails(t.Path));
            snapshots.Add((t, new MusicLibrary.TrackDetails
            {
                Title = cur.Title,
                Artist = cur.Artist,
                Album = cur.Album,
                AlbumArtist = cur.AlbumArtist,
                Genre = cur.Genre,
                Year = cur.Year,
                TrackNumber = cur.TrackNumber,
                DiscNumber = cur.DiscNumber,
                Rating = cur.Rating,
                Lyrics = cur.Lyrics,
                ArtBytes = cur.ArtBytes,
                ArtMimeType = cur.ArtMimeType,
                ArtChanged = chkArt.IsChecked == true
            }));

            var updated = new MusicLibrary.TrackDetails
            {
                Title = cur.Title,
                Artist = chkArtist.IsChecked == true ? artistBox.Text.Trim() : cur.Artist,
                Album = chkAlbum.IsChecked == true ? albumBox.Text.Trim() : cur.Album,
                AlbumArtist = chkAlbumArtist.IsChecked == true ? albumArtistBox.Text.Trim() : cur.AlbumArtist,
                Genre = chkGenre.IsChecked == true ? genreBox.Text.Trim() : cur.Genre,
                Year = chkYear.IsChecked == true ? (double.IsNaN(yearBox.Value) ? 0 : (int)yearBox.Value) : cur.Year,
                TrackNumber = cur.TrackNumber,
                DiscNumber = cur.DiscNumber,
                Rating = chkRating.IsChecked == true
                    ? ((ratingCombo.SelectedItem as ComboBoxItem)?.Tag is double rv && rv > 0 ? rv : null)
                    : cur.Rating,
                Lyrics = cur.Lyrics,
                ArtBytes = chkArt.IsChecked == true ? batchArtBytes : cur.ArtBytes,
                ArtMimeType = chkArt.IsChecked == true ? batchArtMime : cur.ArtMimeType,
                ArtChanged = chkArt.IsChecked == true
            };
            await SaveTrackDetailsAsync(t, updated, pushUndo: false);
        }

        PushUndo(async () =>
        {
            foreach (var (tr, before) in snapshots)
                await SaveTrackDetailsAsync(tr, before, pushUndo: false);
            RefreshVisibleTracks();
            ShowHudPill($"Undone: Batch edit ({snapshots.Count} tracks)");
        });

        RefreshVisibleTracks();
        SaveLibraryCacheSnapshot();
        ShowHudPill($"Updated {tracks.Count} tracks");
    }

    private async Task OpenBatchRenameDialogAsync(IReadOnlyList<TrackItem> tracks)
    {
        if (tracks.Count == 0) return;

        var patternCombo = new ComboBox { Header = "Filename pattern preset", HorizontalAlignment = HorizontalAlignment.Stretch };
        patternCombo.Items.Add("{Artist} - {Title}");
        patternCombo.Items.Add("{Track:00} - {Title}");
        patternCombo.Items.Add("{Track:00} - {Artist} - {Title}");
        patternCombo.Items.Add("{Album} - {Track:00} - {Title}");
        patternCombo.SelectedIndex = 0;

        var patternBox = new TextBox { Header = "Custom pattern (tokens: {Artist}, {Title}, {Album}, {Track:00}, {Year})", Text = "{Artist} - {Title}" };
        var previewList = new ListBox { MaxHeight = 280 };

        string SanitizeSegment(string s)
        {
            string cleaned = s ?? string.Empty;
            foreach (char c in Path.GetInvalidFileNameChars())
                cleaned = cleaned.Replace(c, '_');
            return Regex.Replace(cleaned.Trim(), @"\s{2,}", " ");
        }

        string ComputeNewName(TrackItem t, string pattern)
        {
            string result = pattern
                .Replace("{Artist}", string.IsNullOrWhiteSpace(t.Artist) ? "Unknown Artist" : t.Artist, StringComparison.OrdinalIgnoreCase)
                .Replace("{Title}", t.DisplayTitle, StringComparison.OrdinalIgnoreCase)
                .Replace("{Album}", string.IsNullOrWhiteSpace(t.Album) ? "Unknown Album" : t.Album, StringComparison.OrdinalIgnoreCase)
                .Replace("{Year}", t.Year > 0 ? t.Year.ToString() : string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("{Track:00}", t.TrackNumber > 0 ? t.TrackNumber.ToString("D2") : "00", StringComparison.OrdinalIgnoreCase)
                .Replace("{Track}", t.TrackNumber > 0 ? t.TrackNumber.ToString() : string.Empty, StringComparison.OrdinalIgnoreCase);
            return SanitizeSegment(result).Trim(' ', '-', '.');
        }

        List<(TrackItem Track, string OldPath, string NewPath)> ComputePlan()
        {
            var plan = new List<(TrackItem, string, string)>();
            string pat = patternBox.Text ?? "{Artist} - {Title}";
            foreach (var t in tracks)
            {
                string dir = Path.GetDirectoryName(t.Path) ?? string.Empty;
                string ext = Path.GetExtension(t.Path);
                string baseName = ComputeNewName(t, pat);
                if (string.IsNullOrWhiteSpace(baseName)) continue;
                string newPath = Path.Combine(dir, baseName + ext);
                if (!string.Equals(newPath, t.Path, StringComparison.OrdinalIgnoreCase))
                    plan.Add((t, t.Path, newPath));
            }
            return plan;
        }

        void RefreshPreview()
        {
            previewList.Items.Clear();
            var plan = ComputePlan();
            if (plan.Count == 0)
            {
                previewList.Items.Add("No files need renaming for this pattern.");
                return;
            }
            foreach (var (t, oldP, newP) in plan.Take(100))
            {
                previewList.Items.Add($"{Path.GetFileName(oldP)}  →  {Path.GetFileName(newP)}");
            }
        }

        patternCombo.SelectionChanged += (_, _) =>
        {
            if (patternCombo.SelectedItem is string s) patternBox.Text = s;
        };
        patternBox.TextChanged += (_, _) => RefreshPreview();
        RefreshPreview();

        var panel = new StackPanel { Spacing = 10, Width = 580 };
        panel.Children.Add(patternCombo);
        panel.Children.Add(patternBox);
        panel.Children.Add(new TextBlock { Text = "Preview (Before → After):", FontSize = 12, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(previewList);

        var dlg = new ContentDialog
        {
            Title = $"Rename {tracks.Count} {(tracks.Count == 1 ? "file" : "files")} from tags",
            Content = panel,
            PrimaryButtonText = "Rename files",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.RequestedTheme
        };
        dlg.Resources["ContentDialogMaxWidth"] = 660.0;
        DialogFooter.MainButtonOnRight(dlg);

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var finalPlan = ComputePlan();
        var succeeded = new List<(TrackItem Track, string OldPath, string NewPath)>();
        var errors = new List<string>();
        foreach (var (t, oldP, newP) in finalPlan)
        {
            if (File.Exists(newP))
            {
                errors.Add($"{Path.GetFileName(newP)} already exists.");
                continue;
            }
            var (ok, err) = await RenameTrackPathAsync(t, oldP, newP);
            if (ok) succeeded.Add((t, oldP, newP));
            else errors.Add($"{Path.GetFileName(oldP)}: {err}");
        }

        if (succeeded.Count > 0)
        {
            PushUndo(async () =>
            {
                foreach (var (t, oldP, newP) in succeeded)
                {
                    if (!File.Exists(oldP))
                        await RenameTrackPathAsync(t, newP, oldP);
                }
                SaveLibraryCacheSnapshot();
                ShowHudPill($"Undone: Renamed {succeeded.Count} file(s)");
            });
            SaveLibraryCacheSnapshot();
            ShowHudPill($"Renamed {succeeded.Count} file(s)");
        }
        if (errors.Count > 0)
            await ShowErrorDialogAsync("Some files could not be renamed", string.Join("\n", errors));
    }

    private async Task OpenAutoTagDialogAsync(IReadOnlyList<TrackItem> tracks)
    {
        if (tracks.Count == 0) return;

        for (int trackIdx = 0; trackIdx < tracks.Count; trackIdx++)
        {
            var track = tracks[trackIdx];
            var current = await Task.Run(() => MusicLibrary.ReadFullDetails(track.Path));
            var parsed = AutoTagger.ParseFromFilename(track.Path);

            // Same quiet field label as the Properties dialog.
            TextBlock FieldLabel(string text) => new() { Text = text, FontSize = 11.5, Opacity = 0.72, Margin = new Thickness(2, 0, 0, 0) };
            string initialTitle = string.IsNullOrWhiteSpace(current.Title) ? parsed.Title : current.Title;
            string cleanedInitialTitle = AutoTagger.CleanQueryTitle(initialTitle);
            var queryTitleBox = new TextBox
            {
                Header = FieldLabel("Search Title"),
                Text = string.IsNullOrWhiteSpace(cleanedInitialTitle) ? initialTitle : cleanedInitialTitle,
                Width = 240
            };
            var queryArtistBox = new TextBox
            {
                Header = FieldLabel("Search Artist"),
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

            var matchCombo = new ComboBox { Header = FieldLabel("Matched Release / Source"), HorizontalAlignment = HorizontalAlignment.Stretch };

            var selectAllBtn = new Button { Content = "Select All (Recommended)", Padding = new Thickness(10, 4, 10, 4), FontSize = 11 };
            var emptyOnlyBtn = new Button { Content = "Only Empty Fields", Padding = new Thickness(10, 4, 10, 4), FontSize = 11 };
            var uncheckAllBtn = new Button { Content = "Uncheck All", Padding = new Thickness(10, 4, 10, 4), FontSize = 11 };
            var presetRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            presetRow.Children.Add(selectAllBtn);
            presetRow.Children.Add(emptyOnlyBtn);
            presetRow.Children.Add(uncheckAllBtn);

            CheckBox MakeCheck(string label) => new() { Content = label, VerticalAlignment = VerticalAlignment.Center, MinWidth = 110 };
            TextBox MakeProposedBox() => new() { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
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
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(138) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(124) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var curTb = MakeCurrentHint(currentVal);
                Grid.SetColumn(chk, 0); Grid.SetColumn(curTb, 1); Grid.SetColumn(proposedCtrl, 2);
                g.Children.Add(chk); g.Children.Add(curTb); g.Children.Add(proposedCtrl);
                return g;
            }

            var diffHeader = new Grid { ColumnSpacing = 8 };
            diffHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(138) });
            diffHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(124) });
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

            var curArtImg = new Image { Width = 64, Height = 64, Stretch = Stretch.UniformToFill };
            await SetImageFromBytesAsync(current.ArtBytes, curArtImg);
            var curArtBorder = new Border
            {
                Width = 64,
                Height = 64,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
                Child = curArtImg
            };

            var selArtImg = new Image { Width = 120, Height = 120, Stretch = Stretch.UniformToFill };
            var selArtBorder = new Border
            {
                Width = 120,
                Height = 120,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
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
                        : new SolidColorBrush(Color.FromArgb(50, 128, 128, 128));
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

                var res = await AutoTagger.SearchAsync(
                    track.Path,
                    queryTitleBox.Text,
                    queryArtistBox.Text,
                    track.Duration,
                    token,
                    albumHint: current.Album,
                    yearHint: current.Year);
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
                            BorderBrush = new SolidColorBrush(Color.FromArgb(50, 128, 128, 128)),
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
            Grid.SetColumn(leftCol, 0); Grid.SetColumn(rightCol, 1);
            bodyGrid.Children.Add(leftCol); bodyGrid.Children.Add(rightCol);

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
                RequestedTheme = RootGrid.RequestedTheme
            };
            dlg.Resources["ContentDialogMaxWidth"] = 980.0;
            dlg.Resources["ContentDialogMaxHeight"] = 800.0;
            DialogFooter.MainButtonOnRight(dlg);

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
                DiscNumber = current.DiscNumber,
                Rating = current.Rating,
                Lyrics = chkLyrics.IsChecked == true ? boxLyrics.Text.Replace("\r", Environment.NewLine) : current.Lyrics,
                ArtBytes = chkArt.IsChecked == true ? chosenArtBytes : current.ArtBytes,
                ArtMimeType = "image/jpeg",
                ArtChanged = chkArt.IsChecked == true && chosenArtBytes is { Length: > 0 }
            };

            await SaveTrackDetailsAsync(track, updated);
        }
        SaveLibraryCacheSnapshot();
    }

    private async Task ShowAboutDialogAsync()
    {
        var dlg = new AboutDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.RequestedTheme
        };
        dlg.SetTint(_lastCoverTint.HasValue && TintScale > 0 ? PlaybarTintStop.Color : null);
        if (RootGrid.ActualWidth >= 860) dlg.UseWideLayout();
        await dlg.ShowAsync();
    }

    private async Task ShowShortcutsHelpAsync()
    {
        (string Key, string Desc)[] items =
        {
            ("Space", "Play / Pause"),
            ("Enter", "Play selected song or queue item"),
            ("Left / Right", "Seek 5s (Ctrl+Left/Right in Tile View)"),
            ("Home / End", "Jump to start / end of track list"),
            ("Ctrl+O", "Open music folder"),
            ("Ctrl+F", "Focus search box"),
            ("Ctrl+L", "Locate currently playing track"),
            ("Ctrl+B / Tab", "Toggle Playlists, Queue & Inspector sidebar"),
            ("Ctrl+M", "Toggle Mini Player"),
            ("Ctrl+Z", "Undo last tag, rating, rename, or playlist deletion"),
            ("F1", "Show keyboard shortcuts"),
            ("F2", "Edit Song Properties & Lyrics (or Batch Properties)"),
            ("F5", "Rescan current music folder"),
            ("F11 / Esc", "Toggle / Exit Focus Visualizer Stage"),
            ("Delete", "Remove from playlist or delete to Recycle Bin"),
            ("A–Z / 0–9", "Type-to-jump in current track list")
        };

        var rows = new List<FrameworkElement>();
        foreach (var (key, desc) in items)
        {
            var badge = new Border
            {
                Padding = new Thickness(8, 3, 8, 3),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromArgb(26, 128, 128, 128)),
                Child = new TextBlock { Text = key, FontSize = 11.5, FontWeight = FontWeights.SemiBold }
            };
            rows.Add(SettingsCard.Row("\uE765", desc, null, badge).Row);
        }

        var dlg = new ContentDialog
        {
            Title = "Keyboard shortcuts",
            Content = new ScrollViewer { Content = SettingsCard.Card(rows.ToArray()), MaxHeight = 460 },
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.RequestedTheme
        };
        dlg.Resources["ContentDialogMaxWidth"] = 520.0;
        await dlg.ShowAsync();
    }

    private async Task OpenCustomSmartMixDialogAsync()
    {
        if (_allTracks.Count == 0)
        {
            ShowHudPill("Open a music folder first");
            return;
        }

        var nameBox = new TextBox { Header = "Mix / Playlist name", Text = $"Smart Mix {_playlists.Count + 1}" };

        var countBox = new ComboBox { Header = "Size", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (int n in new[] { 15, 25, 30, 50, 100 })
            countBox.Items.Add(new ComboBoxItem { Content = $"{n} tracks", Tag = n });
        countBox.SelectedIndex = 2;

        var ratingBox = new ComboBox { Header = "Rating", HorizontalAlignment = HorizontalAlignment.Stretch };
        ratingBox.Items.Add(new ComboBoxItem { Content = "Any rating", Tag = "any" });
        ratingBox.Items.Add(new ComboBoxItem { Content = "3.0★ or higher", Tag = "3.0" });
        ratingBox.Items.Add(new ComboBoxItem { Content = "3.5★ or higher", Tag = "3.5" });
        ratingBox.Items.Add(new ComboBoxItem { Content = "4.0★ or higher", Tag = "4.0" });
        ratingBox.Items.Add(new ComboBoxItem { Content = "4.5★ or higher", Tag = "4.5" });
        ratingBox.Items.Add(new ComboBoxItem { Content = "5.0★ only", Tag = "5.0" });
        ratingBox.Items.Add(new ComboBoxItem { Content = "Unrated only", Tag = "unrated" });
        ratingBox.SelectedIndex = 0;

        var genreBox = new ComboBox { Header = "Genre", HorizontalAlignment = HorizontalAlignment.Stretch };
        genreBox.Items.Add(new ComboBoxItem { Content = "All genres", Tag = string.Empty });
        foreach (string g in _allTracks
            .SelectMany(t => (t.Genre ?? string.Empty).Split(new[] { ';', ',', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .Take(40))
        {
            genreBox.Items.Add(new ComboBoxItem { Content = g, Tag = g });
        }
        genreBox.SelectedIndex = 0;

        var historyBox = new ComboBox { Header = "Play history", HorizontalAlignment = HorizontalAlignment.Stretch };
        historyBox.Items.Add(new ComboBoxItem { Content = "Any history", Tag = SmartPlayHistoryFilter.Any });
        historyBox.Items.Add(new ComboBoxItem { Content = "Never played (0 plays)", Tag = SmartPlayHistoryFilter.Unplayed });
        historyBox.Items.Add(new ComboBoxItem { Content = "Not heard in 14+ days", Tag = SmartPlayHistoryFilter.NotPlayedRecently });
        historyBox.Items.Add(new ComboBoxItem { Content = "Least played", Tag = SmartPlayHistoryFilter.LeastPlayed });
        historyBox.Items.Add(new ComboBoxItem { Content = "Most played", Tag = SmartPlayHistoryFilter.MostPlayed });
        historyBox.SelectedIndex = 0;

        var eraBox = new ComboBox { Header = "Era / Decade", HorizontalAlignment = HorizontalAlignment.Stretch };
        eraBox.Items.Add(new ComboBoxItem { Content = "Any year", Tag = "any" });
        eraBox.Items.Add(new ComboBoxItem { Content = "2020s", Tag = "2020s" });
        eraBox.Items.Add(new ComboBoxItem { Content = "2010s", Tag = "2010s" });
        eraBox.Items.Add(new ComboBoxItem { Content = "2000s", Tag = "2000s" });
        eraBox.Items.Add(new ComboBoxItem { Content = "1990s", Tag = "1990s" });
        eraBox.Items.Add(new ComboBoxItem { Content = "Before 1990", Tag = "classic" });
        eraBox.SelectedIndex = 0;

        var orderBox = new ComboBox { Header = "Mix style", HorizontalAlignment = HorizontalAlignment.Stretch };
        orderBox.Items.Add(new ComboBoxItem { Content = "Balanced mix (favours gems)", Tag = SmartMixOrder.BalancedMix });
        orderBox.Items.Add(new ComboBoxItem { Content = "Pure random", Tag = SmartMixOrder.Random });
        orderBox.Items.Add(new ComboBoxItem { Content = "Highest rated first", Tag = SmartMixOrder.HighestRated });
        orderBox.Items.Add(new ComboBoxItem { Content = "Most played first", Tag = SmartMixOrder.MostPlayed });
        orderBox.Items.Add(new ComboBoxItem { Content = "Newest added first", Tag = SmartMixOrder.NewestAdded });
        orderBox.SelectedIndex = 0;

        var previewTracks = new List<TrackItem>();
        var previewList = new ListBox { MaxHeight = 220, SelectionMode = SelectionMode.Extended };
        var summaryText = new TextBlock { FontSize = 12, Opacity = 0.72, VerticalAlignment = VerticalAlignment.Center };

        SmartMixRule ReadRule()
        {
            string rTag = (ratingBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "any";
            bool unrated = rTag == "unrated";
            double? minRating = !unrated && double.TryParse(rTag, NumberStyles.Float, CultureInfo.InvariantCulture, out double rv) ? rv : null;

            string era = (eraBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "any";
            (int? minY, int? maxY) = era switch
            {
                "2020s" => (2020, (int?)2029),
                "2010s" => (2010, (int?)2019),
                "2000s" => (2000, (int?)2009),
                "1990s" => (1990, (int?)1999),
                "classic" => (1, (int?)1989),
                _ => ((int?)null, (int?)null)
            };

            return new SmartMixRule
            {
                Name = string.IsNullOrWhiteSpace(nameBox.Text) ? "Smart Mix" : nameBox.Text.Trim(),
                MaxTracks = (countBox.SelectedItem as ComboBoxItem)?.Tag is int c ? c : 30,
                MinRating = minRating,
                UnratedOnly = unrated,
                GenreContains = (genreBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
                MinYear = minY,
                MaxYear = maxY,
                History = (historyBox.SelectedItem as ComboBoxItem)?.Tag is SmartPlayHistoryFilter h ? h : SmartPlayHistoryFilter.Any,
                Order = (orderBox.SelectedItem as ComboBoxItem)?.Tag is SmartMixOrder o ? o : SmartMixOrder.BalancedMix,
                StaleDays = 14
            };
        }

        void RenderPreview()
        {
            previewList.Items.Clear();
            foreach (var t in previewTracks)
            {
                string meta = string.IsNullOrWhiteSpace(t.Artist) ? t.DurationText : $"{t.Artist} · {t.DurationText}";
                previewList.Items.Add($"{t.DisplayTitle}  —  {meta}");
            }
            summaryText.Text = previewTracks.Count == 0
                ? "No tracks match — relax a rule"
                : $"{previewTracks.Count} tracks ready · Select rows to drop";
        }

        void RebuildMix()
        {
            previewTracks = SmartPlaylistGenerator.Generate(_allTracks, ReadRule());
            RenderPreview();
        }

        countBox.SelectionChanged += (_, _) => RebuildMix();
        ratingBox.SelectionChanged += (_, _) => RebuildMix();
        genreBox.SelectionChanged += (_, _) => RebuildMix();
        historyBox.SelectionChanged += (_, _) => RebuildMix();
        eraBox.SelectionChanged += (_, _) => RebuildMix();
        orderBox.SelectionChanged += (_, _) => RebuildMix();

        var rerollBtn = new Button { Content = "Shuffle again" };
        rerollBtn.Click += (_, _) => RebuildMix();
        var removeBtn = new Button { Content = "Drop selected", IsEnabled = false };
        previewList.SelectionChanged += (_, _) => removeBtn.IsEnabled = previewList.SelectedItems.Count > 0;
        removeBtn.Click += (_, _) =>
        {
            var dropIndices = previewList.SelectedItems
                .Select(item => previewList.Items.IndexOf(item))
                .Where(idx => idx >= 0 && idx < previewTracks.Count)
                .OrderByDescending(idx => idx)
                .ToList();
            foreach (int idx in dropIndices)
                previewTracks.RemoveAt(idx);
            RenderPreview();
        };

        RebuildMix();

        var row1 = new Grid { ColumnSpacing = 8 };
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        Grid.SetColumn(nameBox, 0); Grid.SetColumn(countBox, 1);
        row1.Children.Add(nameBox); row1.Children.Add(countBox);

        var row2 = new Grid { ColumnSpacing = 8 };
        row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(ratingBox, 0); Grid.SetColumn(genreBox, 1); Grid.SetColumn(eraBox, 2);
        row2.Children.Add(ratingBox); row2.Children.Add(genreBox); row2.Children.Add(eraBox);

        var row3 = new Grid { ColumnSpacing = 8 };
        row3.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row3.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(historyBox, 0); Grid.SetColumn(orderBox, 1);
        row3.Children.Add(historyBox); row3.Children.Add(orderBox);

        var bar = new Grid { ColumnSpacing = 8 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(summaryText, 0); Grid.SetColumn(removeBtn, 1); Grid.SetColumn(rerollBtn, 2);
        bar.Children.Add(summaryText); bar.Children.Add(removeBtn); bar.Children.Add(rerollBtn);

        var panel = new StackPanel { Spacing = 10, Width = 580 };
        panel.Children.Add(row1);
        panel.Children.Add(row2);
        panel.Children.Add(row3);
        panel.Children.Add(bar);
        panel.Children.Add(previewList);

        var dlg = new ContentDialog
        {
            Title = "Custom Smart Mix",
            Content = panel,
            PrimaryButtonText = "Play in Queue",
            SecondaryButtonText = "Save Playlist",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.RequestedTheme
        };
        dlg.Resources["ContentDialogMaxWidth"] = 660.0;
        DialogFooter.MainButtonOnRight(dlg);

        var result = await dlg.ShowAsync();
        if (previewTracks.Count == 0 || result == ContentDialogResult.None) return;

        string mixName = string.IsNullOrWhiteSpace(nameBox.Text) ? "Smart Mix" : nameBox.Text.Trim();
        if (result == ContentDialogResult.Primary)
        {
            LoadSmartMixIntoQueue(mixName, previewTracks, autoPlay: true);
        }
        else if (result == ContentDialogResult.Secondary)
        {
            var pl = new SavedPlaylist { Name = mixName };
            foreach (string p in previewTracks.Select(t => t.Path).Distinct(StringComparer.OrdinalIgnoreCase))
                pl.TrackPaths.Add(p);
            _playlists.Add(pl);
            _settings.Playlists.Add(pl);
            _settings.Save();
            _editingPlaylist = pl;
            SyncRightPanelList();
            OpenRightPanel();
            ShowHudPill($"Saved \"{pl.Name}\" ({pl.TrackCount} tracks)");
        }
    }

    private async Task OpenBrowseAlbumsDialogAsync()
    {
        if (_allTracks.Count == 0)
        {
            ShowHudPill("Open a music folder first");
            return;
        }

        var cards = _allTracks
            .GroupBy(t => string.IsNullOrWhiteSpace(t.Album) ? string.Empty : t.Album.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var ordered = g.OrderBy(t => t.DiscNumber > 0 ? t.DiscNumber : int.MaxValue)
                               .ThenBy(t => t.TrackNumber > 0 ? t.TrackNumber : int.MaxValue)
                               .ThenBy(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase)
                               .ToList();
                var artists = ordered.Select(t => t.Artist.Trim()).Where(a => !string.IsNullOrEmpty(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                string artistLabel = artists.Count == 1 ? artists[0] : (artists.Count > 1 ? "Various Artists" : "Unknown Artist");
                var card = new AlbumCardItem
                {
                    Key = g.Key,
                    AlbumName = string.IsNullOrEmpty(g.Key) ? "Unknown Album" : g.Key,
                    Artist = artistLabel,
                    Year = ordered.Max(t => t.Year),
                    TrackCount = ordered.Count,
                    TotalDuration = TimeSpan.FromTicks(ordered.Sum(t => t.Duration.Ticks)),
                    CoverTrackPath = ordered.FirstOrDefault(t => t.HasEmbeddedArt)?.Path ?? ordered[0].Path
                };
                card.Tracks.AddRange(ordered);
                return card;
            })
            .OrderBy(c => string.Equals(c.AlbumName, "Unknown Album", StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(c => c.DisplayAlbum, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var searchBox = new TextBox { PlaceholderText = $"Search {cards.Count:N0} albums or artists..." };
        var list = new ListBox { MaxHeight = 420 };

        void Populate(string q)
        {
            list.Items.Clear();
            IEnumerable<AlbumCardItem> view = cards;
            if (!string.IsNullOrWhiteSpace(q))
            {
                string needle = q.Trim();
                view = view.Where(c =>
                    c.DisplayAlbum.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    c.DisplayArtist.Contains(needle, StringComparison.OrdinalIgnoreCase));
            }
            foreach (var c in view.Take(250))
            {
                list.Items.Add(new ListBoxItem
                {
                    Tag = c,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Content = new TextBlock
                    {
                        Text = $"{c.DisplayAlbum}  —  {c.DisplayArtist}  ({c.MetaText})",
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                });
            }
        }

        searchBox.TextChanged += (_, _) => Populate(searchBox.Text);
        Populate(string.Empty);

        var panel = new StackPanel { Spacing = 10, Width = 540 };
        panel.Children.Add(searchBox);
        panel.Children.Add(list);

        var dlg = new ContentDialog
        {
            Title = "Browse by Album",
            Content = panel,
            PrimaryButtonText = "Show in library",
            SecondaryButtonText = "Play album",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            IsSecondaryButtonEnabled = false,
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.RequestedTheme
        };
        list.SelectionChanged += (_, _) =>
        {
            bool hasSel = list.SelectedItem != null;
            dlg.IsPrimaryButtonEnabled = hasSel;
            dlg.IsSecondaryButtonEnabled = hasSel;
        };
        list.DoubleTapped += (_, _) =>
        {
            if ((list.SelectedItem as ListBoxItem)?.Tag is AlbumCardItem album)
            {
                dlg.Hide();
                if (!string.IsNullOrEmpty(album.Key))
                    ApplyAlbumScope(album.Key);
            }
        };
        dlg.Resources["ContentDialogMaxWidth"] = 620.0;
        DialogFooter.MainButtonOnRight(dlg);

        var res = await dlg.ShowAsync();
        if ((list.SelectedItem as ListBoxItem)?.Tag is not AlbumCardItem chosen) return;

        if (res == ContentDialogResult.Primary)
        {
            if (!string.IsNullOrEmpty(chosen.Key))
                ApplyAlbumScope(chosen.Key);
        }
        else if (res == ContentDialogResult.Secondary && chosen.Tracks.Count > 0)
        {
            PlayFromList(chosen.Tracks, chosen.Tracks[0]);
            ShowHudPill($"Playing {chosen.DisplayAlbum} ({chosen.TrackCount} tracks)");
        }
    }

    private async Task OpenBrowseArtistsDialogAsync()
    {
        if (_allTracks.Count == 0)
        {
            ShowHudPill("Open a music folder first");
            return;
        }

        var groups = _allTracks
            .Where(t => !string.IsNullOrWhiteSpace(t.Artist))
            .GroupBy(t => t.Artist.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Artist = g.Key,
                TrackCount = g.Count(),
                AlbumCount = g.Select(t => t.Album.Trim()).Where(a => !string.IsNullOrEmpty(a)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                Tracks = g.OrderBy(t => t.Album, StringComparer.OrdinalIgnoreCase)
                          .ThenBy(t => t.DiscNumber > 0 ? t.DiscNumber : int.MaxValue)
                          .ThenBy(t => t.TrackNumber > 0 ? t.TrackNumber : int.MaxValue)
                          .ToList()
            })
            .OrderBy(a => a.Artist, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var searchBox = new TextBox { PlaceholderText = $"Search {groups.Count:N0} artists..." };
        var list = new ListBox { MaxHeight = 420 };

        void Populate(string q)
        {
            list.Items.Clear();
            var view = string.IsNullOrWhiteSpace(q)
                ? groups
                : groups.Where(a => a.Artist.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase));
            foreach (var a in view.Take(250))
            {
                string albumPart = a.AlbumCount > 0 ? $"{a.AlbumCount} {(a.AlbumCount == 1 ? "album" : "albums")} · " : string.Empty;
                list.Items.Add(new ListBoxItem
                {
                    Tag = a.Artist,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Content = new TextBlock
                    {
                        Text = $"{a.Artist}  ({albumPart}{a.TrackCount} {(a.TrackCount == 1 ? "track" : "tracks")})",
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                });
            }
        }

        searchBox.TextChanged += (_, _) => Populate(searchBox.Text);
        Populate(string.Empty);

        var panel = new StackPanel { Spacing = 10, Width = 520 };
        panel.Children.Add(searchBox);
        panel.Children.Add(list);

        var dlg = new ContentDialog
        {
            Title = "Browse by Artist",
            Content = panel,
            PrimaryButtonText = "Show in library",
            SecondaryButtonText = "Play all",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            IsSecondaryButtonEnabled = false,
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.RequestedTheme
        };
        list.SelectionChanged += (_, _) =>
        {
            bool hasSel = list.SelectedItem != null;
            dlg.IsPrimaryButtonEnabled = hasSel;
            dlg.IsSecondaryButtonEnabled = hasSel;
        };
        list.DoubleTapped += (_, _) =>
        {
            if ((list.SelectedItem as ListBoxItem)?.Tag is string artist)
            {
                dlg.Hide();
                ApplyArtistScope(artist);
            }
        };
        dlg.Resources["ContentDialogMaxWidth"] = 600.0;
        DialogFooter.MainButtonOnRight(dlg);

        var res = await dlg.ShowAsync();
        if ((list.SelectedItem as ListBoxItem)?.Tag is not string selectedArtist) return;

        if (res == ContentDialogResult.Primary)
        {
            ApplyArtistScope(selectedArtist);
        }
        else if (res == ContentDialogResult.Secondary)
        {
            var match = groups.FirstOrDefault(g => string.Equals(g.Artist, selectedArtist, StringComparison.OrdinalIgnoreCase));
            if (match != null && match.Tracks.Count > 0)
            {
                PlayFromList(match.Tracks, match.Tracks[0]);
                ShowHudPill($"Playing {match.Artist} ({match.TrackCount} tracks)");
            }
        }
    }

    private async Task OpenEqualizerDialogAsync(ComboBox settingsPresetCombo)
    {
        if (_settings.EqualizerGains.Length < 10)
            _settings.EqualizerGains = new double[10];

        var gains = (double[])_settings.EqualizerGains.Clone();
        var sliders = new Slider[10];
        var valLabels = new TextBlock[10];
        bool syncing = false;

        var bandGrid = new Grid { ColumnSpacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        for (int i = 0; i < 10; i++)
            bandGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });

        void SyncPresetComboToSettings(string presetTag)
        {
            for (int i = 0; i < settingsPresetCombo.Items.Count; i++)
            {
                if ((settingsPresetCombo.Items[i] as ComboBoxItem)?.Tag?.ToString() == presetTag)
                {
                    settingsPresetCombo.SelectedIndex = i;
                    break;
                }
            }
        }

        string FormatGain(double db) => db >= 0.05 ? $"+{db:0.#}" : db <= -0.05 ? $"{db:0.#}" : "0";

        for (int i = 0; i < 10; i++)
        {
            int idx = i;
            var valText = new TextBlock
            {
                Text = FormatGain(gains[idx]),
                FontSize = 11,
                Opacity = 0.78,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var slider = new Slider
            {
                Orientation = Orientation.Vertical,
                Minimum = -12,
                Maximum = 12,
                StepFrequency = 0.5,
                SmallChange = 0.5,
                LargeChange = 2,
                Value = Math.Clamp(gains[idx], -12, 12),
                Height = 160,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var freqText = new TextBlock
            {
                Text = EqualizerSampleProvider.BandLabels[idx],
                FontSize = 11,
                Opacity = 0.72,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            slider.ValueChanged += (_, e) =>
            {
                if (syncing) return;
                double v = Math.Round(e.NewValue * 2.0) / 2.0;
                gains[idx] = v;
                valText.Text = FormatGain(v);
                _settings.EqualizerGains = (double[])gains.Clone();
                _settings.EqualizerPreset = "Custom";
                _settings.Save();
                _engine.SetEqualizerGains(_settings.EqualizerGains);
                SyncPresetComboToSettings("Custom");
            };

            sliders[idx] = slider;
            valLabels[idx] = valText;

            var col = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
            col.Children.Add(valText);
            col.Children.Add(slider);
            col.Children.Add(freqText);
            Grid.SetColumn(col, idx);
            bandGrid.Children.Add(col);
        }

        var dlg = new ContentDialog
        {
            Title = "10-Band Equalizer (dB)",
            Content = bandGrid,
            PrimaryButtonText = "Done",
            SecondaryButtonText = "Reset to Flat",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.RequestedTheme
        };
        dlg.SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            syncing = true;
            try
            {
                for (int i = 0; i < 10; i++)
                {
                    gains[i] = 0;
                    sliders[i].Value = 0;
                    valLabels[i].Text = "0";
                }
                _settings.EqualizerPreset = "Flat";
                _settings.EqualizerGains = new double[10];
                _settings.Save();
                _engine.SetEqualizerGains(_settings.EqualizerGains);
                SyncPresetComboToSettings("Flat");
            }
            finally { syncing = false; }
        };
        dlg.Resources["ContentDialogMaxWidth"] = 560.0;
        DialogFooter.MainButtonOnRight(dlg);
        await dlg.ShowAsync();
    }
}
