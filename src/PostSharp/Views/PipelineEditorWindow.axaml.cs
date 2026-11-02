using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using PostSharp.Services;
using PostSharp.ViewModels;

namespace PostSharp.Views;

public partial class PipelineEditorWindow : Window
{
    private readonly PipelineEditorViewModel _viewModel;

    public PipelineEditorWindow()
        : this(new PipelineEditorViewModel("name: 新建流水线\nsteps: []")) { }

    public PipelineEditorWindow(PipelineEditorViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        UiScaleService.Attach(this);
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://PostSharp/Assets/logo-icon.png")));
    }

    /// <summary>Raised with the edited YAML after a successful save.</summary>
    public event EventHandler<string>? Saved;

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

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var yaml = _viewModel.ToYaml();
            Saved?.Invoke(this, yaml);
            Close();
        }
        catch (InvalidOperationException)
        {
            // The view model already put the reason into StatusText.
        }
    }
}
