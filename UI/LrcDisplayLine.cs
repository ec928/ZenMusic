using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Media;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Windows.UI;
using Windows.UI.Text;

namespace ZenMusic.UI;

public sealed class LrcDisplayLine : INotifyPropertyChanged
{
    private static readonly SolidColorBrush ActiveBrush = new(Color.FromArgb(255, 255, 210, 82));
    private static readonly SolidColorBrush InactiveDarkBrush = new(Color.FromArgb(165, 255, 255, 255));
    private static readonly SolidColorBrush ActiveLightBrush = new(Color.FromArgb(255, 180, 83, 9));
    private static readonly SolidColorBrush InactiveLightBrush = new(Color.FromArgb(170, 40, 40, 40));

    private bool _isActive;
    private bool _useLightTheme;

    public TimeSpan Timestamp { get; init; }
    public string Text { get; init; } = string.Empty;

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LineBrush));
            OnPropertyChanged(nameof(LineWeight));
        }
    }

    public bool UseLightTheme
    {
        get => _useLightTheme;
        set
        {
            if (_useLightTheme == value) return;
            _useLightTheme = value;
            OnPropertyChanged(nameof(LineBrush));
        }
    }

    public Brush LineBrush => _useLightTheme
        ? (_isActive ? ActiveLightBrush : InactiveLightBrush)
        : (_isActive ? ActiveBrush : InactiveDarkBrush);

    public FontWeight LineWeight => _isActive ? FontWeights.SemiBold : FontWeights.Normal;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
