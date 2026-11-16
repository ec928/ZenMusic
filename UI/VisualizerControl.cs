using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Numerics;
using Windows.UI;
using ZenMusic.Audio;

namespace ZenMusic.UI;

public enum VisualizerMode
{
    Spectrum = 0,
    Waveform = 1,
    Off = 2,
    BarsAndWaveform = 3
}

/// <summary>
/// Hardware-accelerated 60 fps Win2D audio visualizer supporting Spectrum Bars with peak hold,
/// Silk Waveform, combined Bars + Waveform, and zero-GPU Off mode.
/// </summary>
public sealed class VisualizerControl : UserControl
{
    private readonly CanvasControl _canvas;
    private readonly DispatcherQueueTimer _renderTimer;
    private readonly float[] _bands = new float[FftAnalyzer.BandCount];
    private readonly float[] _peaks = new float[FftAnalyzer.BandCount];
    private readonly float[] _waveform = new float[FftAnalyzer.WaveformPoints];

    private FftAnalyzer? _analyzer;
    private VisualizerMode _mode = VisualizerMode.Spectrum;
    private bool _isPlaying;
    private bool _isStageVisible = true;
    private bool _useLightTheme;

    public bool MirrorHorizontally { get; set; }

    public bool UseLightTheme
    {
        get => _useLightTheme;
        set
        {
            if (_useLightTheme == value) return;
            _useLightTheme = value;
            _canvas.Invalidate();
        }
    }

    public VisualizerMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            UpdatePausedState();
        }
    }

    public bool IsAudioPlaying
    {
        get => _isPlaying;
        set
        {
            _isPlaying = value;
            UpdatePausedState();
        }
    }

    public bool IsStageVisible
    {
        get => _isStageVisible;
        set
        {
            _isStageVisible = value;
            UpdatePausedState();
        }
    }

    public VisualizerControl()
    {
        _canvas = new CanvasControl
        {
            ClearColor = Colors.Transparent,
            Visibility = Visibility.Collapsed
        };
        _canvas.Draw += Canvas_Draw;
        _renderTimer = DispatcherQueue.CreateTimer();
        _renderTimer.Interval = TimeSpan.FromMilliseconds(16);
        _renderTimer.Tick += (_, _) => _canvas.Invalidate();
        Content = _canvas;
        Unloaded += (_, _) =>
        {
            _renderTimer.Stop();
            _canvas.RemoveFromVisualTree();
        };
    }

    public void AttachAnalyzer(FftAnalyzer analyzer)
    {
        _analyzer = analyzer;
    }

    private void UpdatePausedState()
    {
        bool shouldRun = _mode != VisualizerMode.Off && _isPlaying && _isStageVisible;
        if (shouldRun)
        {
            _canvas.Visibility = Visibility.Visible;
            if (!_renderTimer.IsRunning) _renderTimer.Start();
            _canvas.Invalidate();
        }
        else
        {
            if (_renderTimer.IsRunning) _renderTimer.Stop();
            Array.Clear(_bands);
            Array.Clear(_peaks);
            Array.Clear(_waveform);
            _canvas.Visibility = Visibility.Collapsed;
        }
    }

    private void Canvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        float w = (float)sender.ActualWidth;
        float h = (float)sender.ActualHeight;
        if (w <= 4 || h <= 4) return;

        if (_mode == VisualizerMode.Off || _analyzer == null || !_isPlaying || !_isStageVisible)
        {
            Array.Clear(_bands);
            Array.Clear(_peaks);
            Array.Clear(_waveform);
            return;
        }

        _analyzer.ComputeFrame(_bands, _peaks, _waveform, w < 340f ? 0.60f : 0.54f);

        if (_mode == VisualizerMode.Spectrum)
        {
            DrawSpectrum(ds, w, h);
        }
        else if (_mode == VisualizerMode.Waveform)
        {
            DrawWaveform(ds, w, h);
        }
        else if (_mode == VisualizerMode.BarsAndWaveform)
        {
            DrawSpectrum(ds, w, h);
            DrawWaveform(ds, w, h);
        }
    }

    private void DrawSpectrum(Microsoft.Graphics.Canvas.CanvasDrawingSession ds, float w, float h)
    {
        int totalBands = _bands.Length;
        bool isCompactWing = w < 340f;
        int count = isCompactWing ? Math.Clamp((int)MathF.Round(w / 6.5f), 20, 32) : totalBands;
        float gap = Math.Clamp(w / (count * 3.8f), 1.5f, 4f);
        float barW = Math.Max(2f, (w - gap * (count - 1)) / count);
        float corner = Math.Min(2.5f, barW * 0.4f);

        using var brush = new CanvasLinearGradientBrush(ds, _useLightTheme
            ? new[]
            {
                new CanvasGradientStop { Position = 0.0f, Color = Color.FromArgb(245, 180, 83, 9) },
                new CanvasGradientStop { Position = 0.45f, Color = Color.FromArgb(235, 217, 119, 6) },
                new CanvasGradientStop { Position = 1.0f, Color = Color.FromArgb(180, 245, 158, 11) }
            }
            : new[]
            {
                new CanvasGradientStop { Position = 0.0f, Color = Color.FromArgb(250, 255, 246, 214) }, // Crisp Ivory/White top
                new CanvasGradientStop { Position = 0.45f, Color = Color.FromArgb(230, 255, 200, 59) }, // Signature Warm Gold mid
                new CanvasGradientStop { Position = 1.0f, Color = Color.FromArgb(165, 217, 119, 6) }    // Deep Amber base
            })
        {
            StartPoint = new Vector2(0, 0),
            EndPoint = new Vector2(0, h)
        };

        var peakColor = _useLightTheme ? Color.FromArgb(210, 40, 40, 40) : Color.FromArgb(235, 255, 255, 255);
        var baselineColor = _useLightTheme ? Color.FromArgb(55, 217, 119, 6) : Color.FromArgb(45, 255, 200, 59);
        float totalW = count * barW + (count - 1) * gap;
        float startX = Math.Max(0f, (w - totalW) * 0.5f);

        float minBand = isCompactWing ? 3f : 0f;
        float maxBand = isCompactWing ? Math.Min(totalBands - 1, 35) : (totalBands - 1);

        for (int i = 0; i < count; i++)
        {
            float t = count > 1 ? (float)i / (count - 1) : 0f;
            float bandPos = minBand + t * (maxBand - minBand);
            int b0 = Math.Clamp((int)MathF.Floor(bandPos), 0, totalBands - 1);
            int b1 = Math.Clamp(b0 + 1, 0, totalBands - 1);
            float frac = bandPos - b0;

            float tilt = isCompactWing
                ? (1.0f + 0.42f * t)
                : (0.90f + 0.72f * MathF.Pow(t, 0.85f));
            float rawLevel = _bands[b0] * (1f - frac) + _bands[b1] * frac;
            float rawPeak = _peaks[b0] * (1f - frac) + _peaks[b1] * frac;

            float level = Math.Clamp(rawLevel * tilt, 0f, 1f);
            float peak = Math.Clamp(rawPeak * tilt, 0f, 1f);

            float x = MirrorHorizontally
                ? (w - startX - barW - i * (barW + gap))
                : (startX + i * (barW + gap));

            if (level > 0.015f)
            {
                float barH = Math.Max(2f, level * (h - 2f));
                float y = h - barH;
                ds.FillRoundedRectangle(x, y, barW, barH, corner, corner, brush);
            }
            else
            {
                ds.FillRoundedRectangle(x, h - 2f, barW, 2f, 1f, 1f, baselineColor);
            }

            if (peak > 0.04f)
            {
                float py = Math.Max(1f, h - peak * (h - 2f) - 2f);
                ds.FillRoundedRectangle(x, py, barW, 2f, 1f, 1f, peakColor);
            }
        }
    }

    private void DrawWaveform(Microsoft.Graphics.Canvas.CanvasDrawingSession ds, float w, float h)
    {
        int count = _waveform.Length;
        if (count < 2) return;

        bool isCompact = w < 340f;
        float midY = h * 0.5f;
        float amp = h * (isCompact ? 0.44f : 0.46f);

        float PointX(int idx) => MirrorHorizontally
            ? w - (w * idx) / (count - 1)
            : (w * idx) / (count - 1);
        float PointY(int idx) => midY - Math.Clamp(_waveform[idx], -1f, 1f) * amp;

        using var strokeBuilder = new CanvasPathBuilder(ds);
        using var fillBuilder = !isCompact ? new CanvasPathBuilder(ds) : null;

        float x0 = PointX(0);
        float y0 = PointY(0);
        strokeBuilder.BeginFigure(x0, y0);
        fillBuilder?.BeginFigure(x0, midY);
        fillBuilder?.AddLine(x0, y0);

        for (int i = 1; i < count; i++)
        {
            float px = PointX(i - 1);
            float py = PointY(i - 1);
            float cx = PointX(i);
            float cy = PointY(i);
            float mx = (px + cx) * 0.5f;
            float my = (py + cy) * 0.5f;
            strokeBuilder.AddQuadraticBezier(new Vector2(px, py), new Vector2(mx, my));
            fillBuilder?.AddQuadraticBezier(new Vector2(px, py), new Vector2(mx, my));
        }
        float xLast = PointX(count - 1);
        float yLast = PointY(count - 1);
        strokeBuilder.AddLine(xLast, yLast);
        strokeBuilder.EndFigure(CanvasFigureLoop.Open);

        if (fillBuilder != null)
        {
            fillBuilder.AddLine(xLast, yLast);
            fillBuilder.AddLine(xLast, midY);
            fillBuilder.EndFigure(CanvasFigureLoop.Closed);
            using var fillGeo = CanvasGeometry.CreatePath(fillBuilder);
            var fillColor = _useLightTheme
                ? Color.FromArgb(32, 217, 119, 6)
                : Color.FromArgb(38, 255, 200, 59);
            ds.FillGeometry(fillGeo, fillColor);
        }

        using var geo = CanvasGeometry.CreatePath(strokeBuilder);
        ds.DrawGeometry(geo, _useLightTheme ? Color.FromArgb(75, 217, 119, 6) : Color.FromArgb(90, 255, 200, 59), isCompact ? 5f : 7f);
        ds.DrawGeometry(geo, _useLightTheme ? Color.FromArgb(235, 180, 83, 9) : Color.FromArgb(245, 255, 245, 214), isCompact ? 1.75f : 2.1f);
    }
}
