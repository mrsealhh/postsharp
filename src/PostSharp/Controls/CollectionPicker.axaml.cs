using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using PostSharp.Models;

namespace PostSharp.Controls;

public partial class CollectionPicker : UserControl
{
    private Point _dragStart;
    private SuiteSourceCollection? _dragRow;
    private PointerPressedEventArgs? _dragPressed;

    public CollectionPicker()
    {
        InitializeComponent();
        SourceScroll.ScrollChanged += (_, _) => TryLoadMore();
        SourceScroll.LayoutUpdated += (_, _) => TryLoadMore();
    }

    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: SuiteSourceCollection row })
        {
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Source is Control source && source.GetVisualAncestors().Any(a => a is Button))
        {
            return;
        }

        _dragRow = row;
        _dragStart = e.GetPosition(this);
        _dragPressed = e;
    }

    private async void OnRowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (
            _dragRow is null
            || _dragPressed is null
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
        )
        {
            return;
        }

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragStart.X) < 4 && Math.Abs(position.Y - _dragStart.Y) < 4)
        {
            return;
        }

        var row = _dragRow;
        var pressed = _dragPressed;
        _dragRow = null;
        _dragPressed = null;
        var payload = new SuiteSourceDragData(row.Collection, row.Request);
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(SuiteSourceDragFormats.Source, payload));
        await DragDrop.DoDragDropAsync(pressed, transfer, DragDropEffects.Copy);
    }

    private void TryLoadMore()
    {
        if (
            DataContext is ViewModels.CollectionPickerViewModel viewModel
            && viewModel.HasMore
            && IsNearBottom(SourceScroll)
        )
        {
            viewModel.LoadMore();
        }
    }

    private static bool IsNearBottom(ScrollViewer area) =>
        area.Extent.Height > area.Viewport.Height + 1
        && area.Offset.Y + area.Viewport.Height >= area.Extent.Height - 120;
}
