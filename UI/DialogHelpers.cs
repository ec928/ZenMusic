using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using System.Linq;

namespace ZenMusic.UI;

/// <summary>
/// Grid splitter handle that displays a vertical North-South resize cursor on hover.
/// </summary>
public sealed class ResizeGripBorder : Grid
{
    public ResizeGripBorder()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
    }
}

/// <summary>
/// Column splitter handle that displays a horizontal West-East resize cursor on hover.
/// </summary>
public sealed class ColumnResizeGripBorder : Grid
{
    public ColumnResizeGripBorder()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }
}

/// <summary>
/// Places the primary action button on the bottom-right with secondary/cancel buttons to its left,
/// matching Windows &amp; ModernImageViewer desktop conventions.
/// </summary>
public static class DialogFooter
{
    public static void MainButtonOnRight(ContentDialog dialog)
    {
        dialog.Loaded += (_, _) =>
        {
            if (FindCommandSpace(dialog) is not Grid footer) return;
            footer.FlowDirection = FlowDirection.RightToLeft;
            foreach (var button in footer.Children.OfType<Button>())
                button.FlowDirection = FlowDirection.LeftToRight;
        };
    }

    private static DependencyObject? FindCommandSpace(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement { Name: "CommandSpace" }) return child;
            if (FindCommandSpace(child) is { } found) return found;
        }
        return null;
    }
}

/// <summary>
/// ModernImageViewer-aligned fluent SettingsCard builder for Settings, Properties, and Auto-Tag dialogs.
/// </summary>
public static class SettingsCard
{
    public const double Quiet = 0.7;

    private static T FromXaml<T>(string attributes) where T : class =>
        (T)XamlReader.Load($"<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" {attributes}/>");

    public static TextBlock Section(string text, bool first = false) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        CharacterSpacing = 80,
        Opacity = Quiet,
        Margin = new Thickness(2, first ? 0 : 16, 0, 6)
    };

    public static ToggleSwitch Switch(string name, bool on)
    {
        var toggle = new ToggleSwitch
        {
            IsOn = on,
            OnContent = string.Empty,
            OffContent = string.Empty,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        AutomationProperties.SetName(toggle, name);
        return toggle;
    }

    /// <param name="controlBelow">Put the control on its own line under the words, full width, instead of at the right.</param>
    public static (Grid Row, TextBlock Title, TextBlock Detail) Row(string glyph, string title, string? detail, FrameworkElement control, bool controlBelow = false)
    {
        var row = new Grid { ColumnSpacing = 12, MinHeight = 44, Padding = new Thickness(14, 8, 14, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            Glyph = glyph,
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = Quiet
        };
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        var name = new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis };
        var line = new TextBlock
        {
            Text = detail ?? string.Empty,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Opacity = Quiet
        };
        words.Children.Add(name);
        words.Children.Add(line);
        line.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) =>
            line.Visibility = string.IsNullOrEmpty(line.Text) ? Visibility.Collapsed : Visibility.Visible);
        line.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;

        Grid.SetColumn(words, 1);
        if (controlBelow)
        {
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowSpacing = 8;
            Grid.SetColumnSpan(words, 2);
            Grid.SetRow(control, 1);
            Grid.SetColumn(control, 1);
            Grid.SetColumnSpan(control, 2);
            control.HorizontalAlignment = HorizontalAlignment.Stretch;
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Margin = new Thickness(0, 4, 0, 0);
        }
        else
        {
            Grid.SetColumn(control, 2);
        }
        row.Children.Add(icon);
        row.Children.Add(words);
        row.Children.Add(control);
        return (row, name, line);
    }

    /// <summary>Width every right-hand drop-down in Settings shares, so the column lines up.</summary>
    public const double ChoiceWidth = 200;

    public static Border Divider() =>
        FromXaml<Border>("Height=\"1\" Background=\"{ThemeResource DividerStrokeColorDefaultBrush}\"");

    /// <summary>
    /// A card whose rows can come with their own divider (shown above the row), so a row can be
    /// collapsed together with its divider. Rows given a null divider get a fixed one.
    /// </summary>
    public static Border CardWithDividers(params (FrameworkElement Row, Border? Divider)[] rows)
    {
        var stack = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0) stack.Children.Add(rows[i].Divider ?? Divider());
            stack.Children.Add(rows[i].Row);
        }
        var card = FromXaml<Border>("CornerRadius=\"8\" BorderThickness=\"1\" BorderBrush=\"{ThemeResource CardStrokeColorDefaultBrush}\" Background=\"{ThemeResource CardBackgroundFillColorDefaultBrush}\"");
        card.Child = stack;
        return card;
    }

    public static Border Card(params FrameworkElement[] rows)
    {
        var stack = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0)
                stack.Children.Add(FromXaml<Border>("Height=\"1\" Background=\"{ThemeResource DividerStrokeColorDefaultBrush}\""));
            stack.Children.Add(rows[i]);
        }
        var card = FromXaml<Border>("CornerRadius=\"8\" BorderThickness=\"1\" BorderBrush=\"{ThemeResource CardStrokeColorDefaultBrush}\" Background=\"{ThemeResource CardBackgroundFillColorDefaultBrush}\"");
        card.Child = stack;
        return card;
    }
}
