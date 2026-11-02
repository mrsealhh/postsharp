using Avalonia.Controls;
using Avalonia.Interactivity;
using PostSharp.ViewModels;

namespace PostSharp.Views;

public partial class PipelineCenterView : UserControl
{
    public PipelineCenterView()
    {
        InitializeComponent();
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
            editor = new PipelineEditorWindow(new PipelineEditorViewModel(viewModel.Content));
        }
        catch (Exception exception)
        {
            viewModel.StatusText = $"无法解析流水线：{exception.Message}";
            return;
        }

        editor.Saved += (_, yaml) =>
        {
            viewModel.Content = yaml;
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
