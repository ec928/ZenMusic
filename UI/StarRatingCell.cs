using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using Windows.Foundation;
using Windows.UI;

namespace ZenMusic.UI;

/// <summary>
/// Lightweight 5-star interactive rating cell for virtualized track rows (supports 0.5–5.0★)
/// with automatic Light/Dark theme legibility.
/// </summary>
public sealed class StarRatingCell : Grid
{
    private const double StarWidth = 14.0;
    private static readonly SolidColorBrush DarkDimBrush = new(Color.FromArgb(52, 255, 255, 255));
    private static readonly SolidColorBrush DarkGoldBrush = new(Color.FromArgb(255, 255, 200, 59));
    private static readonly SolidColorBrush DarkHoverBrush = new(Color.FromArgb(220, 255, 222, 110));

    private static readonly SolidColorBrush LightDimBrush = new(Color.FromArgb(72, 96, 96, 96));
    private static readonly SolidColorBrush LightAmberBrush = new(Color.FromArgb(255, 217, 119, 6));
    private static readonly SolidColorBrush LightHoverBrush = new(Color.FromArgb(225, 180, 83, 9));

    private readonly TextBlock[] _bgs = new TextBlock[5];
    private readonly TextBlock[] _fills = new TextBlock[5];
    private readonly Grid[] _fillHosts = new Grid[5];
    private double? _hoverValue;
    private bool _forceDarkPalette;

    public bool ForceDarkPalette
    {
        get => _forceDarkPalette;
        set
        {
            if (_forceDarkPalette == value) return;
            _forceDarkPalette = value;
            UpdateVisuals();
        }
    }

    private static readonly SolidColorBrush DarkQuietBrush = new(Color.FromArgb(255, 150, 126, 66));
    private static readonly SolidColorBrush LightQuietBrush = new(Color.FromArgb(255, 205, 160, 104));
    private bool _quiet;

    /// <summary>Muted fill for dense lists; full colour returns while the pointer is over the cell.</summary>
    public bool Quiet
    {
        get => _quiet;
        set
        {
            if (_quiet == value) return;
            _quiet = value;
            UpdateVisuals();
        }
    }

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value),
            typeof(double?),
            typeof(StarRatingCell),
            new PropertyMetadata(null, (d, _) => ((StarRatingCell)d).UpdateVisuals()));

    public double? Value
    {
        get => (double?)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public event EventHandler<double?>? RatingSelected;

    public StarRatingCell()
    {
        Width = StarWidth * 5;
        Height = 24;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

        for (int i = 0; i < 5; i++)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(StarWidth) });

            var bg = new TextBlock
            {
                Text = "★",
                FontSize = 12,
                Foreground = DarkDimBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            // Screen readers get one "Rating" name for the cell, not ten star glyphs.
            AutomationProperties.SetAccessibilityView(bg, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            SetColumn(bg, i);
            Children.Add(bg);
            _bgs[i] = bg;

            var fill = new TextBlock
            {
                Text = "★",
                FontSize = 12,
                Foreground = DarkGoldBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            // Clip the full-column host rather than the centered TextBlock so x = StarWidth * 0.5
            // bisects the star glyph instead of clipping ~70% of its intrinsic text width.
            var fillHost = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            AutomationProperties.SetAccessibilityView(fill, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            fillHost.Children.Add(fill);
            SetColumn(fillHost, i);
            Children.Add(fillHost);
            _fills[i] = fill;
            _fillHosts[i] = fillHost;
        }

        PointerPressed += StarRatingCell_PointerPressed;
        PointerMoved += StarRatingCell_PointerMoved;
        PointerExited += StarRatingCell_PointerExited;
        Loaded += (_, _) => UpdateVisuals();
        ActualThemeChanged += (_, _) => UpdateVisuals();
    }

    private double ComputeRatingAtX(double x)
    {
        int slot = Math.Clamp((int)(x / StarWidth), 0, 4);
        bool half = (x - slot * StarWidth) < (StarWidth * 0.5);
        return slot + (half ? 0.5 : 1.0);
    }

    private void StarRatingCell_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        double hovered = ComputeRatingAtX(e.GetCurrentPoint(this).Position.X);
        if (!Nullable.Equals(_hoverValue, hovered))
        {
            _hoverValue = hovered;
            UpdateVisuals();
        }
    }

    private void StarRatingCell_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_hoverValue != null)
        {
            _hoverValue = null;
            UpdateVisuals();
        }
    }

    private void StarRatingCell_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(this);
        if (!pt.Properties.IsLeftButtonPressed) return;
        double clicked = ComputeRatingAtX(pt.Position.X);
        double? next = Nullable.Equals(Value, clicked) ? null : clicked;
        _hoverValue = null;
        RatingSelected?.Invoke(this, next);
    }

    private void UpdateVisuals()
    {
        bool useDark = _forceDarkPalette || ActualTheme != ElementTheme.Light;
        var dim = useDark ? DarkDimBrush : LightDimBrush;
        var active = useDark ? DarkGoldBrush : LightAmberBrush;
        var hover = useDark ? DarkHoverBrush : LightHoverBrush;

        AutomationProperties.SetName(this, Value is double v && v > 0
            ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "Rating {0:0.#} stars", v)
            : "Not rated");

        double r = _hoverValue ?? Value ?? 0;
        var brush = _hoverValue.HasValue ? hover
            : _quiet ? (useDark ? DarkQuietBrush : LightQuietBrush)
            : active;
        for (int i = 0; i < 5; i++)
        {
            _bgs[i].Foreground = dim;
            _fills[i].Foreground = brush;
            double threshold = i + 1;
            if (r >= threshold)
            {
                _fillHosts[i].Visibility = Visibility.Visible;
                _fillHosts[i].Clip = null;
            }
            else if (r >= threshold - 0.5)
            {
                _fillHosts[i].Visibility = Visibility.Visible;
                _fillHosts[i].Clip = new RectangleGeometry { Rect = new Rect(0, 0, StarWidth * 0.5, Height) };
            }
            else
            {
                _fillHosts[i].Visibility = Visibility.Collapsed;
                _fillHosts[i].Clip = null;
            }
        }
    }
}
