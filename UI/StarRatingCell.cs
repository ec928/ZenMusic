using Microsoft.UI.Xaml;
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
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            SetColumn(fill, i);
            Children.Add(fill);
            _fills[i] = fill;
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
        double clicked = ComputeRatingAtX(e.GetCurrentPoint(this).Position.X);
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

        double r = _hoverValue ?? Value ?? 0;
        var brush = _hoverValue.HasValue ? hover : active;
        for (int i = 0; i < 5; i++)
        {
            _bgs[i].Foreground = dim;
            _fills[i].Foreground = brush;
            double threshold = i + 1;
            if (r >= threshold)
            {
                _fills[i].Visibility = Visibility.Visible;
                _fills[i].Clip = null;
            }
            else if (r >= threshold - 0.5)
            {
                _fills[i].Visibility = Visibility.Visible;
                _fills[i].Clip = new RectangleGeometry { Rect = new Rect(0, 0, StarWidth * 0.5, 18) };
            }
            else
            {
                _fills[i].Visibility = Visibility.Collapsed;
                _fills[i].Clip = null;
            }
        }
    }
}
