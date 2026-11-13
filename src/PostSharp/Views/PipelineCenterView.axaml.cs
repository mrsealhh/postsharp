using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PostSharp.Controls;
using PostSharp.Services;
using PostSharp.ViewModels;

namespace PostSharp.Views;

public partial class PipelineCenterView : UserControl
{
    private PipelineCenterViewModel? _viewModel;

    public PipelineCenterView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        LayoutUpdated += (_, _) => ResizeYamlEditor();
        YamlHost.SizeChanged += (_, _) => ResizeYamlEditor();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => ApplyPreviewLayout();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as PipelineCenterViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        ApplyPreviewLayout();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PipelineCenterViewModel.IsSideBySideLayout))
        {
            ApplyPreviewLayout();
        }
    }

    /// <summary>YAML 与流程图支持上下 / 左右两种排列，分隔条随方向切换为横向或纵向拖动。</summary>
    private void ApplyPreviewLayout()
    {
        var sideBySide = _viewModel?.IsSideBySideLayout == true;
        var rows = PreviewLayout.RowDefinitions;
        var columns = PreviewLayout.ColumnDefinitions;

        if (sideBySide)
        {
            // 主方向保留一个 Star 轨道：跨三行/三列的子元素才能拿到真实尺寸。
            columns[0].Width = new GridLength(1, GridUnitType.Star);
            columns[1].Width = new GridLength(6);
            columns[2].Width = new GridLength(1, GridUnitType.Star);
            rows[0].Height = new GridLength(1, GridUnitType.Star);
            rows[1].Height = new GridLength(0);
            rows[2].Height = new GridLength(0);

            Grid.SetRow(YamlHost, 0);
            Grid.SetColumn(YamlHost, 0);
            Grid.SetRowSpan(YamlHost, 3);
            Grid.SetColumnSpan(YamlHost, 1);

            Grid.SetRow(PreviewSplitter, 0);
            Grid.SetColumn(PreviewSplitter, 1);
            Grid.SetRowSpan(PreviewSplitter, 3);
            Grid.SetColumnSpan(PreviewSplitter, 1);

            Grid.SetRow(GraphHost, 0);
            Grid.SetColumn(GraphHost, 2);
            Grid.SetRowSpan(GraphHost, 3);
            Grid.SetColumnSpan(GraphHost, 1);

            PreviewSplitter.ResizeDirection = GridResizeDirection.Columns;
            PreviewSplitter.Width = 6;
            PreviewSplitter.Height = double.NaN;
        }
        else
        {
            rows[0].Height = new GridLength(1, GridUnitType.Star);
            rows[1].Height = new GridLength(6);
            rows[2].Height = new GridLength(1, GridUnitType.Star);
            columns[0].Width = new GridLength(1, GridUnitType.Star);
            columns[1].Width = new GridLength(0);
            columns[2].Width = new GridLength(0);

            Grid.SetRow(YamlHost, 0);
            Grid.SetColumn(YamlHost, 0);
            Grid.SetRowSpan(YamlHost, 1);
            Grid.SetColumnSpan(YamlHost, 3);

            Grid.SetRow(PreviewSplitter, 1);
            Grid.SetColumn(PreviewSplitter, 0);
            Grid.SetRowSpan(PreviewSplitter, 1);
            Grid.SetColumnSpan(PreviewSplitter, 3);

            Grid.SetRow(GraphHost, 2);
            Grid.SetColumn(GraphHost, 0);
            Grid.SetRowSpan(GraphHost, 1);
            Grid.SetColumnSpan(GraphHost, 3);

            PreviewSplitter.ResizeDirection = GridResizeDirection.Rows;
            PreviewSplitter.Height = 6;
            PreviewSplitter.Width = double.NaN;
        }

        PreviewSplitter.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        PreviewSplitter.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        ResizeYamlEditor();
    }

    /// <summary>把 YAML 编辑框撑满它所在的行，内容超出时由编辑框内部滚动。</summary>
    private void ResizeYamlEditor()
    {
        var height = YamlHost.Bounds.Height;
        if (height <= 0)
        {
            return;
        }

        if (double.IsNaN(YamlEditor.FrameHeight) || Math.Abs(YamlEditor.FrameHeight - height) > 0.5)
        {
            YamlEditor.FrameHeight = height;
        }
    }

    private void OnTemplateClick(object? sender, PointerPressedEventArgs e)
    {
        if (
            DataContext is PipelineCenterViewModel viewModel
            && sender is Control { DataContext: PipelineTemplateItem template } card
            && e.GetCurrentPoint(card).Properties.IsLeftButtonPressed
        )
        {
            card.Classes.Add("pressed");
            Avalonia.Threading.DispatcherTimer.RunOnce(
                () => card.Classes.Remove("pressed"),
                TimeSpan.FromMilliseconds(140)
            );
            viewModel.ApplyTemplateCommand.Execute(template);
            e.Handled = true;
        }
    }

    private void OnKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (DataContext is not PipelineCenterViewModel viewModel)
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control))
        {
            if (e.Key == Avalonia.Input.Key.S)
            {
                viewModel.SavePipelineCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Avalonia.Input.Key.Enter)
            {
                viewModel.RunPipelineCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    private void OnFileRowClick(object? sender, RoutedEventArgs e)
    {
        if (
            DataContext is PipelineCenterViewModel viewModel
            && sender is SelectableRow { DataContext: PipelineFileItem file }
        )
        {
            viewModel.OpenFileCommand.Execute(file);
        }
    }

    private void OnVisualizeClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PipelineCenterViewModel viewModel)
        {
            return;
        }

        var graph = viewModel.BuildGraph();
        if (graph is null)
        {
            return;
        }

        GraphCanvas.Graph = PipelineWorkflowMapper.ToWorkflowGraph(graph);
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                GraphCanvas.ArrangeNodes(false);
                GraphCanvas.FitToContent(false);
            },
            Avalonia.Threading.DispatcherPriority.Loaded
        );
    }

    private async void OnGraphicalEditClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PipelineCenterViewModel viewModel)
        {
            return;
        }

        PipelineEditorWindow editor;
        try
        {
            editor = new PipelineEditorWindow(new PipelineEditorViewModel(viewModel.YamlText));
        }
        catch (Exception exception)
        {
            viewModel.StatusText = $"无法解析流水线：{exception.Message}";
            return;
        }

        editor.Saved += (_, yaml) =>
        {
            viewModel.YamlText = yaml;
            viewModel.StatusText = "图像编辑结果已应用到 YAML，点击「保存」写入文件。";
        };
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null)
        {
            editor.Show();
        }
        else
        {
            await editor.ShowDialog(owner);
        }
    }
}
