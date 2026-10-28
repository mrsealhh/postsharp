using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace PostSharp.Controls;

/// <summary>
/// A native, theme-aware row surface for dense lists. It deliberately does not inherit Button:
/// list rows need full-width content layout, a single selection marker and keyboard activation
/// without button padding/background behaviour leaking into the row.
/// </summary>
public sealed class SelectableRow : ContentControl
{
    public static readonly StyledProperty<bool> IsSelectedProperty = AvaloniaProperty.Register<
        SelectableRow,
        bool
    >(nameof(IsSelected));

    public static readonly RoutedEvent<RoutedEventArgs> ClickEvent = RoutedEvent.Register<
        SelectableRow,
        RoutedEventArgs
    >(nameof(Click), RoutingStrategies.Bubble);

    static SelectableRow()
    {
        FocusableProperty.OverrideDefaultValue<SelectableRow>(true);
    }

    public bool IsSelected
    {
        get => GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    public event EventHandler<RoutedEventArgs>? Click
    {
        add => AddHandler(ClickEvent, value);
        remove => RemoveHandler(ClickEvent, value);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus();
        RaiseEvent(new RoutedEventArgs(ClickEvent));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
        {
            RaiseEvent(new RoutedEventArgs(ClickEvent));
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
