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
    private bool _backdrop;
    private bool _isFocusStage;
    private Color? _accent;
    private CanvasLinearGradientBrush? _spectrumBrush;
    private bool _spectrumBrushIsLight;
    private CanvasLinearGradientBrush? _backdropBrush;
    private CanvasLinearGradientBrush? _barBrush;
    private CanvasLinearGradientBrush? _reflectBrush;
    private CanvasRadialGradientBrush? _poolBrush;
    private CanvasRadialGradientBrush? _glowBrush;
    private bool _accentKeyReady;
    private bool _accentKeyLight;
    private bool _accentKeyHas;
    private bool _accentKeyFocusStage;
    private byte _accentKeyR, _accentKeyG, _accentKeyB;

    public bool MirrorHorizontally { get; set; }

    /// <summary>
    /// Draw as a calm ambient wash along the bottom of the library table, in <see cref="Accent"/> when set.
    /// </summary>
    public bool Backdrop
    {
        get => _backdrop;
        set
        {
            if (_backdrop == value) return;
            _backdrop = value;
            DisposeBackdropBrush();
            _canvas.Invalidate();
        }
    }

    /// <summary>
    /// Full-vibrancy stage renderer with cover-tinted lighting, reflection horizon, and peak hold for the F11 Focus Stage.
    /// </summary>
    public bool IsFocusStage
    {
        get => _isFocusStage;
        set
        {
            if (_isFocusStage == value) return;
            _isFocusStage = value;
            DisposeBackdropBrush();
            _canvas.Invalidate();
        }
    }

    public Color? Accent
    {
        get => _accent;
        set
        {
            if (_accent == value) return;
            _accent = value;
            DisposeSpectrumBrush();
            DisposeBackdropBrush();
            _canvas.Invalidate();
        }
    }

    public bool UseLightTheme
    {
        get => _useLightTheme;
        set
        {
            if (_useLightTheme == value) return;
            _useLightTheme = value;
            DisposeSpectrumBrush();
            DisposeBackdropBrush();
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
        _canvas.CreateResources += (_, _) =>
        {
            DisposeSpectrumBrush();
            DisposeBackdropBrush();
        };
        _renderTimer = DispatcherQueue.CreateTimer();
        _renderTimer.Interval = TimeSpan.FromMilliseconds(16);
        _renderTimer.Tick += (_, _) => _canvas.Invalidate();
        Content = _canvas;
        Unloaded += (_, _) =>
        {
            _renderTimer.Stop();
            DisposeSpectrumBrush();
            DisposeBackdropBrush();
            _canvas.RemoveFromVisualTree();
        };
    }

    private void DisposeSpectrumBrush()
    {
        _spectrumBrush?.Dispose();
        _spectrumBrush = null;
    }

    private void DisposeBackdropBrush()
    {
        _backdropBrush?.Dispose();
        _backdropBrush = null;
        _barBrush?.Dispose();
        _barBrush = null;
        _reflectBrush?.Dispose();
        _reflectBrush = null;
        _poolBrush?.Dispose();
        _poolBrush = null;
        _glowBrush?.Dispose();
        _glowBrush = null;
        _accentKeyReady = false;
    }

    private Color AccentRgb()
    {
        if (_accent.HasValue) return _accent.Value;
        return _useLightTheme
            ? Color.FromArgb(255, 217, 119, 6)
            : Color.FromArgb(255, 255, 200, 59);
    }

    private bool AccentKeyMatches(Color rgb) =>
        _accentKeyReady
        && _accentKeyLight == _useLightTheme
        && _accentKeyHas == _accent.HasValue
        && _accentKeyFocusStage == _isFocusStage
        && _accentKeyR == rgb.R && _accentKeyG == rgb.G && _accentKeyB == rgb.B;

    /// <summary>
    /// One set of brushes for the window backdrop or focus stage. Recreated only when the theme, stage role, or cover colour changes.
    /// </summary>
    private void EnsureBackdropBrushes(CanvasControl sender, Color rgb, Color tip, Color deep)
    {
        if (AccentKeyMatches(rgb)
            && _backdropBrush != null && _barBrush != null && _reflectBrush != null
            && _poolBrush != null && _glowBrush != null)
            return;

        DisposeBackdropBrush();
        _accentKeyReady = true;
        _accentKeyLight = _useLightTheme;
        _accentKeyHas = _accent.HasValue;
        _accentKeyFocusStage = _isFocusStage;
        _accentKeyR = rgb.R;
        _accentKeyG = rgb.G;
        _accentKeyB = rgb.B;

        byte floorA = _isFocusStage ? (byte)78 : (_useLightTheme ? (byte)22 : (byte)42);
        _backdropBrush = new CanvasLinearGradientBrush(sender, new[]
        {
            new CanvasGradientStop { Position = 0f, Color = Color.FromArgb(0, rgb.R, rgb.G, rgb.B) },
            new CanvasGradientStop { Position = 0.62f, Color = Color.FromArgb((byte)(floorA / 3), rgb.R, rgb.G, rgb.B) },
            new CanvasGradientStop { Position = 1f, Color = Color.FromArgb(floorA, rgb.R, rgb.G, rgb.B) }
        });

        // Calm translucency behind the library table; full luminosity on the F11 Focus Stage.
        byte topA = _isFocusStage ? (byte)252 : (_useLightTheme ? (byte)112 : (byte)148);
        byte upperA = _isFocusStage ? (byte)242 : (_useLightTheme ? (byte)104 : (byte)136);
        byte midA = _isFocusStage ? (byte)228 : (_useLightTheme ? (byte)92 : (byte)120);
        byte baseA = _isFocusStage ? (byte)205 : (_useLightTheme ? (byte)76 : (byte)98);
        _barBrush = new CanvasLinearGradientBrush(sender, new[]
        {
            new CanvasGradientStop { Position = 0f, Color = Color.FromArgb(topA, tip.R, tip.G, tip.B) },
            new CanvasGradientStop { Position = 0.18f, Color = Color.FromArgb(upperA, tip.R, tip.G, tip.B) },
            new CanvasGradientStop { Position = 0.58f, Color = Color.FromArgb(midA, rgb.R, rgb.G, rgb.B) },
            new CanvasGradientStop { Position = 1f, Color = Color.FromArgb(baseA, deep.R, deep.G, deep.B) }
        });

        byte reflectA = _isFocusStage ? (byte)110 : (_useLightTheme ? (byte)36 : (byte)64);
        _reflectBrush = new CanvasLinearGradientBrush(sender, new[]
        {
            new CanvasGradientStop { Position = 0f, Color = Color.FromArgb(reflectA, rgb.R, rgb.G, rgb.B) },
            new CanvasGradientStop { Position = 1f, Color = Color.FromArgb(0, rgb.R, rgb.G, rgb.B) }
        });

        var hot = Lighten(rgb, _useLightTheme ? 0.2f : 0.55f);
        byte poolA = _isFocusStage ? (byte)135 : (_useLightTheme ? (byte)52 : (byte)90);
        _poolBrush = new CanvasRadialGradientBrush(sender, new[]
        {
            new CanvasGradientStop { Position = 0f, Color = Color.FromArgb(poolA, hot.R, hot.G, hot.B) },
            new CanvasGradientStop { Position = 0.42f, Color = Color.FromArgb((byte)(poolA / 2), rgb.R, rgb.G, rgb.B) },
            new CanvasGradientStop { Position = 1f, Color = Color.FromArgb(0, rgb.R, rgb.G, rgb.B) }
        });

        byte glowA = _isFocusStage ? (byte)145 : (_useLightTheme ? (byte)54 : (byte)95);
        _glowBrush = new CanvasRadialGradientBrush(sender, new[]
        {
            new CanvasGradientStop { Position = 0f, Color = Color.FromArgb(glowA, tip.R, tip.G, tip.B) },
            new CanvasGradientStop { Position = 1f, Color = Color.FromArgb(0, tip.R, tip.G, tip.B) }
        });
    }

    private CanvasLinearGradientBrush SpectrumBrush(CanvasControl sender, float height)
    {
        if (_spectrumBrush != null && _spectrumBrushIsLight == _useLightTheme)
        {
            _spectrumBrush.EndPoint = new Vector2(0, height);
            return _spectrumBrush;
        }

        DisposeSpectrumBrush();
        _spectrumBrushIsLight = _useLightTheme;
        Color rgb = AccentRgb();
        Color tip = Lighten(rgb, _useLightTheme ? -0.18f : 0.65f);
        Color deep = Lighten(rgb, _useLightTheme ? 0.15f : -0.16f);
        _spectrumBrush = new CanvasLinearGradientBrush(sender, new[]
        {
            new CanvasGradientStop { Position = 0.0f, Color = Color.FromArgb(248, tip.R, tip.G, tip.B) },
            new CanvasGradientStop { Position = 0.45f, Color = Color.FromArgb(232, rgb.R, rgb.G, rgb.B) },
            new CanvasGradientStop { Position = 1.0f, Color = Color.FromArgb(170, deep.R, deep.G, deep.B) }
        })
        {
            StartPoint = new Vector2(0, 0),
            EndPoint = new Vector2(0, height)
        };
        return _spectrumBrush;
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

        if (_backdrop || _isFocusStage)
        {
            // Horizon sits just above the bottom edge so the lamps have a reflection.
            float marginFrac = _isFocusStage ? 0.14f : 0.07f;
            float margin = Math.Clamp(h * marginFrac, Math.Min(28f, h * 0.2f), Math.Min(_isFocusStage ? 88f : 72f, h * 0.28f));
            float floor = h - margin;
            float room = Math.Max(32f, floor - 12f);
            float riseFrac = _isFocusStage ? 0.78f : 0.44f;
            float rise = Math.Clamp(h * riseFrac, Math.Min(160f, room), room);
            DrawBackdropAtmosphere(ds, sender, w, h, floor, rise);
            if (_mode == VisualizerMode.Spectrum || _mode == VisualizerMode.BarsAndWaveform)
                DrawBackdropSpectrum(ds, w, h, floor, rise);
            if (_mode == VisualizerMode.Waveform || _mode == VisualizerMode.BarsAndWaveform)
                DrawBackdropWaveform(ds, w, floor, rise);
            return;
        }

        if (_mode == VisualizerMode.Spectrum)
        {
            DrawSpectrum(ds, sender, w, h);
        }
        else if (_mode == VisualizerMode.Waveform)
        {
            DrawWaveform(ds, w, h);
        }
        else if (_mode == VisualizerMode.BarsAndWaveform)
        {
            DrawSpectrum(ds, sender, w, h);
            DrawWaveform(ds, w, h);
        }
    }

    private void DrawBackdropAtmosphere(Microsoft.Graphics.Canvas.CanvasDrawingSession ds, CanvasControl sender, float w, float h, float floor, float rise)
    {
        Color rgb = AccentRgb();
        Color tip = Lighten(rgb, _useLightTheme ? 0.28f : 0.52f);
        Color deep = Lighten(rgb, _useLightTheme ? -0.32f : -0.18f);
        EnsureBackdropBrushes(sender, rgb, tip, deep);

        float bass = 0f;
        int bassN = Math.Min(5, _bands.Length);
        for (int i = 0; i < bassN; i++) bass += _bands[i];
        bass = bassN > 0 ? Math.Clamp(bass / bassN, 0f, 1f) : 0f;

        float washTop = floor - rise * 0.22f;
        _backdropBrush!.StartPoint = new Vector2(0, washTop);
        _backdropBrush.EndPoint = new Vector2(0, h);
        ds.FillRectangle(0, washTop, w, h - washTop, _backdropBrush);

        float poolRx = Math.Max(12f, w * (0.18f + 0.22f * bass));
        float poolRy = Math.Max(12f, rise * (0.22f + 0.50f * bass));
        var poolCenter = new Vector2(w * 0.5f, floor - rise * 0.06f);
        _poolBrush!.Center = poolCenter;
        _poolBrush.RadiusX = poolRx;
        _poolBrush.RadiusY = poolRy;
        ds.FillEllipse(poolCenter, poolRx, poolRy, _poolBrush);

        _reflectBrush!.StartPoint = new Vector2(0, floor);
        _reflectBrush.EndPoint = new Vector2(0, h);
    }

    private void DrawBackdropSpectrum(Microsoft.Graphics.Canvas.CanvasDrawingSession ds, float w, float h, float floor, float rise)
    {
        int totalBands = _bands.Length;
        int count = _isFocusStage
            ? Math.Clamp((int)MathF.Round(w / 22f), 28, 48)
            : Math.Clamp((int)MathF.Round(w / 30f), 20, 36);
        const float gapRatio = 0.36f;
        float barW = w / (count + gapRatio * (count - 1));
        float gap = barW * gapRatio;
        float step = barW + gap;
        float origin = Math.Max(0f, (w - (count * barW + (count - 1) * gap)) * 0.5f);
        float mirrorRoom = Math.Max(0f, h - floor - 4f);
        Color rgb = AccentRgb();
        Color tip = Lighten(rgb, _useLightTheme ? 0.28f : 0.52f);
        var peakColor = _useLightTheme
            ? Color.FromArgb(210, 40, 32, 16)
            : Color.FromArgb(235, tip.R, tip.G, tip.B);
        var rest = Color.FromArgb(_isFocusStage ? (byte)90 : (_useLightTheme ? (byte)45 : (byte)55), rgb.R, rgb.G, rgb.B);
        var barBrush = _barBrush!;
        var glow = _glowBrush!;

        for (int i = 0; i < count; i++)
        {
            float t = count > 1 ? (float)i / (count - 1) : 0.5f;
            float bandPos = t * (totalBands - 1);
            int b0 = Math.Clamp((int)MathF.Floor(bandPos), 0, totalBands - 1);
            int b1 = Math.Clamp(b0 + 1, 0, totalBands - 1);
            float frac = bandPos - b0;
            // Perceptual high-frequency tilt + shallow stage arch so mids and highs stay lively.
            float tilt = (0.85f + 0.62f * MathF.Pow(t, 0.78f)) * (0.92f + 0.16f * MathF.Sin(t * MathF.PI));
            float level = Math.Clamp((_bands[b0] * (1f - frac) + _bands[b1] * frac) * tilt, 0f, 1f);
            float peak = Math.Clamp((_peaks[b0] * (1f - frac) + _peaks[b1] * frac) * tilt, 0f, 1f);
            float x = origin + i * step;

            float barH = Math.Max(level > 0.035f ? 14f : 6f, level * rise);
            float y = floor - barH;
            float radius = Math.Min(barW * 0.5f, barH * 0.5f);

            if (level > 0.05f)
            {
                float cx = x + barW * 0.5f;
                float cy = y + Math.Min(barH * 0.18f, 36f);
                float rx = barW * (0.7f + 0.45f * level);
                float ry = Math.Max(12f, barH * 0.28f);
                glow.Center = new Vector2(cx, cy);
                glow.RadiusX = rx;
                glow.RadiusY = ry;
                ds.FillEllipse(new Vector2(cx, cy), rx, ry, glow);
            }

            barBrush.StartPoint = new Vector2(x, y);
            barBrush.EndPoint = new Vector2(x, floor);
            if (level > 0.035f)
                ds.FillRoundedRectangle(x, y, barW, barH, radius, radius, barBrush);
            else
                ds.FillRoundedRectangle(x, floor - 6f, barW, 6f, 3f, 3f, rest);

            float refH = Math.Min(mirrorRoom, barH * (_isFocusStage ? 0.45f : 0.34f));
            if (refH > 3f)
            {
                float rr = Math.Min(barW * 0.5f, refH * 0.5f);
                ds.FillRoundedRectangle(x, floor + 3f, barW, refH, rr, rr, _reflectBrush!);
            }

            // Omit floating peak dashes behind the library table so they never look like text strikethroughs.
            if (_isFocusStage && peak > 0.07f)
            {
                float py = Math.Max(6f, floor - peak * rise - 6f);
                ds.FillRoundedRectangle(x + barW * 0.08f, py, barW * 0.84f, 3f, 1.5f, 1.5f, peakColor);
            }
        }
    }

    private static Color Lighten(Color c, float t)
    {
        float toward = t >= 0 ? 255f : 0f;
        float k = MathF.Abs(t);
        byte Mix(byte v) => (byte)Math.Clamp(v + (toward - v) * k, 0, 255);
        return Color.FromArgb(255, Mix(c.R), Mix(c.G), Mix(c.B));
    }

    private void DrawBackdropWaveform(Microsoft.Graphics.Canvas.CanvasDrawingSession ds, float w, float floor, float rise)
    {
        int count = _waveform.Length;
        if (count < 2) return;

        float midY = floor - rise * (_isFocusStage ? 0.48f : 0.52f);
        float amp = rise * (_isFocusStage ? 0.34f : 0.22f);
        Color rgb = AccentRgb();
        Color tip = Lighten(rgb, _useLightTheme ? 0.15f : 0.62f);

        float PointX(int idx) => (w * idx) / (count - 1);
        float PointY(int idx) => midY - Math.Clamp(_waveform[idx], -1f, 1f) * amp;
        // Secondary smoothed harmonic under-ribbon for richer silk depth
        float EchoY(int idx)
        {
            int i0 = Math.Max(0, idx - 2);
            int i1 = Math.Min(count - 1, idx + 2);
            float avg = (_waveform[i0] + _waveform[idx] + _waveform[i1]) / 3f;
            return midY + Math.Clamp(avg, -1f, 1f) * (amp * 0.68f);
        }

        using var echoBuilder = new CanvasPathBuilder(ds);
        using var strokeBuilder = new CanvasPathBuilder(ds);
        using var fillBuilder = new CanvasPathBuilder(ds);
        float x0 = PointX(0);
        float y0 = PointY(0);
        float ey0 = EchoY(0);
        echoBuilder.BeginFigure(x0, ey0);
        strokeBuilder.BeginFigure(x0, y0);
        fillBuilder.BeginFigure(x0, midY);
        fillBuilder.AddLine(x0, y0);
        for (int i = 1; i < count; i++)
        {
            float px = PointX(i - 1);
            float py = PointY(i - 1);
            float cx = PointX(i);
            float cy = PointY(i);
            float mx = (px + cx) * 0.5f;
            float my = (py + cy) * 0.5f;
            strokeBuilder.AddQuadraticBezier(new Vector2(px, py), new Vector2(mx, my));
            fillBuilder.AddQuadraticBezier(new Vector2(px, py), new Vector2(mx, my));

            float epy = EchoY(i - 1);
            float ecy = EchoY(i);
            echoBuilder.AddQuadraticBezier(new Vector2(px, epy), new Vector2(mx, (epy + ecy) * 0.5f));
        }
        float xLast = PointX(count - 1);
        float yLast = PointY(count - 1);
        echoBuilder.AddLine(xLast, EchoY(count - 1));
        echoBuilder.EndFigure(CanvasFigureLoop.Open);
        strokeBuilder.AddLine(xLast, yLast);
        strokeBuilder.EndFigure(CanvasFigureLoop.Open);
        fillBuilder.AddLine(xLast, yLast);
        fillBuilder.AddLine(xLast, midY);
        fillBuilder.EndFigure(CanvasFigureLoop.Closed);

        using var fillGeo = CanvasGeometry.CreatePath(fillBuilder);
        byte fillA = _isFocusStage ? (byte)52 : (_useLightTheme ? (byte)22 : (byte)32);
        ds.FillGeometry(fillGeo, Color.FromArgb(fillA, rgb.R, rgb.G, rgb.B));

        using var echoGeo = CanvasGeometry.CreatePath(echoBuilder);
        byte echoA = _isFocusStage ? (byte)110 : (_useLightTheme ? (byte)55 : (byte)75);
        ds.DrawGeometry(echoGeo, Color.FromArgb(echoA, rgb.R, rgb.G, rgb.B), _isFocusStage ? 6f : 4.5f);

        using var geo = CanvasGeometry.CreatePath(strokeBuilder);
        byte haloA = _isFocusStage ? (byte)155 : (_useLightTheme ? (byte)72 : (byte)108);
        byte coreA = _isFocusStage ? (byte)248 : (_useLightTheme ? (byte)170 : (byte)195);
        ds.DrawGeometry(geo, Color.FromArgb(haloA, rgb.R, rgb.G, rgb.B), _isFocusStage ? 12f : 9.5f);
        ds.DrawGeometry(geo, Color.FromArgb(coreA, tip.R, tip.G, tip.B), _isFocusStage ? 2.6f : 2.1f);
    }

    private void DrawSpectrum(Microsoft.Graphics.Canvas.CanvasDrawingSession ds, CanvasControl sender, float w, float h)
    {
        int totalBands = _bands.Length;
        bool isCompactWing = w < 340f;
        int count = isCompactWing ? Math.Clamp((int)MathF.Round(w / 6.5f), 20, 32) : totalBands;
        float gap = Math.Clamp(w / (count * 3.8f), 1.5f, 4f);
        float barW = Math.Max(2f, (w - gap * (count - 1)) / count);
        float corner = Math.Min(2.5f, barW * 0.4f);

        var brush = SpectrumBrush(sender, h);
        Color rgb = AccentRgb();
        var peakColor = _useLightTheme ? Color.FromArgb(210, 40, 40, 40) : Color.FromArgb(235, 255, 255, 255);
        var baselineColor = Color.FromArgb(_useLightTheme ? (byte)55 : (byte)45, rgb.R, rgb.G, rgb.B);
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

        Color rgb = AccentRgb();
        Color tip = Lighten(rgb, _useLightTheme ? -0.18f : 0.72f);
        if (fillBuilder != null)
        {
            fillBuilder.AddLine(xLast, yLast);
            fillBuilder.AddLine(xLast, midY);
            fillBuilder.EndFigure(CanvasFigureLoop.Closed);
            using var fillGeo = CanvasGeometry.CreatePath(fillBuilder);
            var fillColor = Color.FromArgb(_useLightTheme ? (byte)32 : (byte)38, rgb.R, rgb.G, rgb.B);
            ds.FillGeometry(fillGeo, fillColor);
        }

        using var geo = CanvasGeometry.CreatePath(strokeBuilder);
        ds.DrawGeometry(geo, Color.FromArgb(_useLightTheme ? (byte)75 : (byte)90, rgb.R, rgb.G, rgb.B), isCompact ? 5f : 7f);
        ds.DrawGeometry(geo, Color.FromArgb(_useLightTheme ? (byte)235 : (byte)245, tip.R, tip.G, tip.B), isCompact ? 1.75f : 2.1f);
    }
}
