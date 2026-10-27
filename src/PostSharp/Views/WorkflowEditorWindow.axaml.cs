using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using PostSharp.Models;
using PostSharp.Services;
using PostSharp.ViewModels;

namespace PostSharp.Views;

public partial class WorkflowEditorWindow : Window
{
    private static readonly DataFormat<WorkflowPaletteItem> PaletteFormat =
        DataFormat.CreateInProcessFormat<WorkflowPaletteItem>("PostSharp.WorkflowPaletteItem");
    private readonly WorkflowEditorViewModel _viewModel;
    private Point _dragStart;
    private WorkflowPaletteItem? _dragItem;
    private PointerPressedEventArgs? _dragPressed;

    public WorkflowEditorWindow()
        : this(new WorkflowEditorViewModel(Array.Empty<RequestCollection>())) { }

    public WorkflowEditorWindow(WorkflowEditorViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        UiScaleService.Attach(this);
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://PostSharp/Assets/logo-icon.png")));
        Canvas.Graph = viewModel.Graph;
        DragDrop.SetAllowDrop(Canvas, true);
        Canvas.AddHandler(
            DragDrop.DragOverEvent,
            OnCanvasDragOver,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true
        );
        Canvas.AddHandler(
            DragDrop.DropEvent,
            OnCanvasDrop,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true
        );
    }

    public event EventHandler<WorkflowSuiteResult>? SuiteCreated;

    private void OnPalettePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: WorkflowPaletteItem item })
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragItem = item;
        _dragStart = e.GetPosition(this);
        _dragPressed = e;
    }

    private async void OnPalettePointerMoved(object? sender, PointerEventArgs e)
    {
        if (
            _dragItem is null
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

        var item = _dragItem;
        var pressed = _dragPressed;
        _dragItem = null;
        _dragPressed = null;
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(PaletteFormat, item));
        await DragDrop.DoDragDropAsync(pressed, transfer, DragDropEffects.Copy);
    }

    private void OnPaletteDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: WorkflowPaletteItem item })
        {
            _viewModel.AddNode(item, new Point(100, 100));
        }
    }

    private void OnCanvasDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(PaletteFormat))
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnCanvasDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetValue(PaletteFormat) is { } item)
        {
            _viewModel.AddNode(item, e.GetPosition(Canvas));
            e.Handled = true;
        }
    }

    private void OnAutoLayoutClick(object? sender, RoutedEventArgs e) => Canvas.ArrangeNodes();

    private void OnClearWorkflowClick(object? sender, RoutedEventArgs e)
    {
        _viewModel.Clear();
        Canvas.Graph = _viewModel.Graph;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnCreateWorkflowSuiteClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.BuildSuite() is not { } result)
        {
            return;
        }

        SuiteCreated?.Invoke(this, result);
        Close();
    }
}
