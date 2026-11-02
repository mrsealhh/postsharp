using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PostSharp.Controls;
using PostSharp.Services;
using PostSharp.ViewModels;

namespace PostSharp.Views;

public partial class PipelineCenterView : UserControl
{
    public PipelineCenterView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
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
