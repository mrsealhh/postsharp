using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using TKCode.Arc;

namespace PostSharp.Controls;

/// <summary>
/// A small, consistently spaced icon + label for Arc buttons. Arc buttons take arbitrary
/// content, so this keeps icon size, gap and vertical alignment identical across toolbars.
/// </summary>
public sealed class IconLabel : StackPanel
{
    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<IconLabel, Geometry?>(nameof(Icon));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<IconLabel, string?>(nameof(Text));

    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<IconLabel, double>(nameof(IconSize), 14d);

    public static readonly StyledProperty<double> TextFontSizeProperty =
        AvaloniaProperty.Register<IconLabel, double>(nameof(TextFontSize));

    public static readonly StyledProperty<IBrush?> TextForegroundProperty =
        AvaloniaProperty.Register<IconLabel, IBrush?>(nameof(TextForeground));

    public static readonly StyledProperty<IBrush?> IconForegroundProperty =
        AvaloniaProperty.Register<IconLabel, IBrush?>(nameof(IconForeground));

    public IconLabel()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 6;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;
    }

    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public double TextFontSize
    {
        get => GetValue(TextFontSizeProperty);
        set => SetValue(TextFontSizeProperty, value);
    }

    public IBrush? TextForeground
    {
        get => GetValue(TextForegroundProperty);
        set => SetValue(TextForegroundProperty, value);
    }

    public IBrush? IconForeground
    {
        get => GetValue(IconForegroundProperty);
        set => SetValue(IconForegroundProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IconProperty
            || change.Property == TextProperty
            || change.Property == IconSizeProperty
            || change.Property == TextFontSizeProperty
            || change.Property == TextForegroundProperty
            || change.Property == IconForegroundProperty)
        {
            Rebuild();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SyncAutomationName();
    }

    private void SyncAutomationName()
    {
        if (string.IsNullOrWhiteSpace(Text))
        {
            return;
        }

        var ancestor = this.GetVisualParent();
        while (ancestor is not null && ancestor is not Avalonia.Controls.Button)
        {
            ancestor = ancestor.GetVisualParent();
        }

        if (ancestor is Avalonia.Controls.Button button)
        {
            AutomationProperties.SetName(button, Text);
        }
    }

    private void Rebuild()
    {
        Children.Clear();

        if (Icon is { } geometry)
        {
            var glyph = new ArcGlyph
            {
                Data = geometry,
                Size = IconSize,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (IconForeground is not null)
            {
                glyph.Foreground = IconForeground;
            }

            Children.Add(glyph);
        }

        if (!string.IsNullOrEmpty(Text))
        {
            var text = new TextBlock
            {
                Text = Text,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (TextFontSize > 0)
            {
                text.FontSize = TextFontSize;
            }

            if (TextForeground is not null)
            {
                text.Foreground = TextForeground;
            }

            Children.Add(text);
        }

        SyncAutomationName();
    }
}
