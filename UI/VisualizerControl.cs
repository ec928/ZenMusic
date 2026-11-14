using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
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
    Off = 2
}

/// <summary>
/// Hardware-accelerated 60 fps Win2D audio visualizer supporting Spectrum Bars with peak hold,
/// Silk Waveform, and zero-GPU Off mode.
/// </summary>
public sealed class VisualizerControl : UserControl
{
    private readonly CanvasAnimatedControl _canvas;
    private readonly float[] _bands = new float[FftAnalyzer.BandCount];
    private readonly float[] _peaks = new float[FftAnalyzer.BandCount];
    private readonly float[] _waveform = new float[FftAnalyzer.WaveformPoints];

    private FftAnalyzer? _analyzer;
    private VisualizerMode _mode = VisualizerMode.Spectrum;
    private bool _isPlaying;

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

    public VisualizerControl()
    {
        _canvas = new CanvasAnimatedControl
        {
            ClearColor = Colors.Transparent,
            TargetElapsedTime = TimeSpan.FromMilliseconds(16.6),
            IsFixedTimeStep = false,
            Paused = true
        };
        _canvas.Draw += Canvas_Draw;
        Content = _canvas;
        Unloaded += (_, _) => _canvas.RemoveFromVisualTree();
    }

    public void AttachAnalyzer(FftAnalyzer analyzer)
    {
        _analyzer = analyzer;
    }

    private void UpdatePausedState()
    {
        bool shouldRun = _mode != VisualizerMode.Off && _isPlaying;
        _canvas.Paused = !shouldRun;
        _canvas.Visibility = _mode == VisualizerMode.Off ? Visibility.Collapsed : Visibility.Visible;
        if (!shouldRun)
        {
            _canvas.Invalidate();
        }
    }

    private void Canvas_Draw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        float w = (float)sender.Size.Width;
        float h = (float)sender.Size.Height;
        if (w <= 4 || h <= 4) return;

        if (_mode == VisualizerMode.Off) return;

        if (_analyzer != null && _isPlaying)
        {
            _analyzer.ComputeFrame(_bands, _peaks, _waveform);
        }
        else
        {
            Array.Clear(_bands);
            Array.Clear(_peaks);
            Array.Clear(_waveform);
        }

        if (_mode == VisualizerMode.Spectrum)
        {
            DrawSpectrum(ds, w, h);
        }
        else if (_mode == VisualizerMode.Waveform)
        {
            DrawWaveform(ds, w, h);
        }
    }

    private void DrawSpectrum(Microsoft.Graphics.Canvas.CanvasDrawingSession ds, float w, float h)
    {
        int count = _bands.Length;
        float gap = Math.Clamp(w / (count * 5f), 1.5f, 4f);
        float barW = Math.Max(2f, (w - gap * (count - 1)) / count);
        float corner = Math.Min(2.5f, barW * 0.4f);

        using var brush = new CanvasLinearGradientBrush(ds, new[]
        {
            new CanvasGradientStop { Position = 0.0f, Color = Color.FromArgb(245, 255, 200, 59) },   // Gold top
            new CanvasGradientStop { Position = 0.45f, Color = Color.FromArgb(230, 56, 189, 248) }, // Sky mid
            new CanvasGradientStop { Position = 1.0f, Color = Color.FromArgb(210, 0, 120, 212) }    // Fluent Blue base
        })
        {
            StartPoint = new Vector2(0, 0),
            EndPoint = new Vector2(0, h)
        };

        var peakColor = Color.FromArgb(220, 255, 220, 120);

        for (int i = 0; i < count; i++)
        {
            float x = i * (barW + gap);
            float level = Math.Clamp(_bands[i], 0.02f, 1f);
            float barH = Math.Max(2f, level * (h - 4f));
            float y = h - barH;

            ds.FillRoundedRectangle(x, y, barW, barH, corner, corner, brush);

            float peak = Math.Clamp(_peaks[i], 0f, 1f);
            if (peak > 0.04f)
            {
                float py = Math.Max(1f, h - peak * (h - 4f) - 2f);
                ds.FillRoundedRectangle(x, py, barW, 2f, 1f, 1f, peakColor);
            }
        }
    }

    private void DrawWaveform(Microsoft.Graphics.Canvas.CanvasDrawingSession ds, float w, float h)
    {
        int count = _waveform.Length;
        if (count < 2) return;

        float midY = h * 0.5f;
        float amp = h * 0.44f;

        using var builder = new CanvasPathBuilder(ds);
        builder.BeginFigure(0, midY - Math.Clamp(_waveform[0], -1f, 1f) * amp);
        for (int i = 1; i < count; i++)
        {
            float x = (w * i) / (count - 1);
            float y = midY - Math.Clamp(_waveform[i], -1f, 1f) * amp;
            builder.AddLine(x, y);
        }
        builder.EndFigure(CanvasFigureLoop.Open);

        using var geo = CanvasGeometry.CreatePath(builder);
        ds.DrawGeometry(geo, Color.FromArgb(70, 56, 189, 248), 5f);
        ds.DrawGeometry(geo, Color.FromArgb(240, 255, 200, 59), 1.75f);
    }
}
