using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using PostSharp.Controls;
using PostSharp.Models;
using PostSharp.Services;
using PostSharp.ViewModels;
using TKCode.Arc;

namespace PostSharp.Views;

public partial class WorkflowEditorWindow : Window
{
    private readonly WorkflowEditorViewModel _viewModel;
    private double _leftPaneWidth = 320;
    private double _rightPaneWidth = 342;

    public WorkflowEditorWindow()
        : this(new WorkflowEditorViewModel(Array.Empty<RequestCollection>(), null)) { }

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
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.ContentChanged += OnContentChanged;
        Closed += (_, _) =>
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.ContentChanged -= OnContentChanged;
            HeapTrim.Request();
        };
        Opened += (_, _) =>
        {
            _ = this.EnsurePopupOverlayLayerAsync();
            Dispatcher.UIThread.Post(
                () => Canvas.FitToContent(animate: false),
                DispatcherPriority.Loaded
            );
        };
    }

    /// <summary>Clamps the editor to the current screen so the save/run footer stays reachable.</summary>
    public void FitToWorkingArea()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var scale = screen.Scaling > 0 ? screen.Scaling : 1d;
        var workingWidth = screen.WorkingArea.Width / scale;
        var workingHeight = screen.WorkingArea.Height / scale;
        Width = Math.Max(MinWidth, Math.Min(Width, workingWidth - 40));
        Height = Math.Max(MinHeight, Math.Min(Height, workingHeight - 40));
    }

    private void OnCanvasDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(SuiteSourceDragFormats.Source))
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnCanvasDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetValue(SuiteSourceDragFormats.Source) is { } data)
        {
            _viewModel.AddDroppedSource(data, e.GetPosition(Canvas));
            e.Handled = true;
        }
    }

    private void OnContentChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(
            () => Canvas.FitToContent(animate: false),
            DispatcherPriority.Loaded
        );

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (
            e.PropertyName is not nameof(WorkflowEditorViewModel.SelectedStep)
            || _viewModel.SelectedStep is not { } step
        )
        {
            return;
        }

        var node = _viewModel.Graph.FindNode(step.Step.Id.ToString());
        if (node is not null)
        {
            Canvas.Select(new[] { node });
            Canvas.ScrollIntoView(node);
        }
    }

    public event EventHandler<WorkflowSuiteResult>? SuiteCreated;

    private void OnCanvasSelectionChanged(object? sender, WorkflowSelectionChangedEventArgs e) =>
        _viewModel.SetSelection(Canvas.SelectedNodes, Canvas.SelectedEdges);

    private void OnCanvasGraphEdited(object? sender, WorkflowEditEventArgs e)
    {
        if (e.Kind == WorkflowEditKind.Move)
        {
            return;
        }

        _viewModel.SyncGraphEdits();
    }

    private void OnAutoLayoutClick(object? sender, RoutedEventArgs e) => Canvas.ArrangeNodes();

    private void OnToggleLeftPaneClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.IsLeftPaneCollapsed)
        {
            _viewModel.IsLeftPaneCollapsed = false;
            MainEditorGrid.ColumnDefinitions[0].Width = new GridLength(_leftPaneWidth);
            MainEditorGrid.ColumnDefinitions[1].Width = new GridLength(8);
        }
        else
        {
            _leftPaneWidth = Math.Max(220, MainEditorGrid.ColumnDefinitions[0].ActualWidth);
            _viewModel.IsLeftPaneCollapsed = true;
            MainEditorGrid.ColumnDefinitions[0].Width = new GridLength(0);
            MainEditorGrid.ColumnDefinitions[1].Width = new GridLength(0);
        }
    }

    private void OnToggleRightPaneClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.IsRightPaneCollapsed)
        {
            _viewModel.IsRightPaneCollapsed = false;
            MainEditorGrid.ColumnDefinitions[4].Width = new GridLength(_rightPaneWidth);
            MainEditorGrid.ColumnDefinitions[3].Width = new GridLength(8);
        }
        else
        {
            _rightPaneWidth = Math.Max(280, MainEditorGrid.ColumnDefinitions[4].ActualWidth);
            _viewModel.IsRightPaneCollapsed = true;
            MainEditorGrid.ColumnDefinitions[4].Width = new GridLength(0);
            MainEditorGrid.ColumnDefinitions[3].Width = new GridLength(0);
        }
    }

    private void OnClearWorkflowClick(object? sender, RoutedEventArgs e)
    {
        _viewModel.Clear();
        Canvas.Graph = _viewModel.Graph;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        _viewModel.CancelRunCommand.Execute(null);
        Close();
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.IsSuiteRunning || _viewModel.BuildSuite() is not { } result)
        {
            return;
        }

        SuiteCreated?.Invoke(this, result);
        Close();
    }

    private void OnMappingDialogOpened(object? sender, EventArgs e) =>
        ApplyWideDialog(MappingDialog);

    private void OnAssertionDialogOpened(object? sender, EventArgs e) =>
        ApplyWideDialog(AssertionDialog);

    private void OnConditionDialogOpened(object? sender, EventArgs e) =>
        ApplyWideDialog(ConditionDialog);

    private void ApplyWideDialog(Dialog dialog)
    {
        if (dialog.Surface is not { } surface)
        {
            return;
        }

        var width = Math.Max(560, Bounds.Width * 0.8);
        surface.Width = width;
        surface.MaxWidth = width;
    }
}
