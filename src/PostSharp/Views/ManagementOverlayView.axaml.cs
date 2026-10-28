using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PostSharp.Models;
using PostSharp.ViewModels;

namespace PostSharp.Views;

/// <summary>
/// 管理层浮层：AI 调用审计与自动化套件。整块界面接近 1500 行 XAML，
/// 只有用户点开「管理层」时才实例化，避免启动阶段就构建这棵控件树。
/// </summary>
public partial class ManagementOverlayView : UserControl
{
    public ManagementOverlayView()
    {
        InitializeComponent();
        AuditScrollArea.ScrollChanged += (_, _) =>
        {
            if (IsNearBottom(AuditScrollArea))
            {
                AuditReachedBottom?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    /// <summary>审计列表滚到底时请求加载下一页；由宿主窗口接回 ViewModel。</summary>
    public event EventHandler? AuditReachedBottom;

    /// <summary>点「设置」时请求宿主窗口打开设置。</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>点「编辑套件」时请求宿主窗口打开工作流编辑器。</summary>
    public event EventHandler? SuiteEditRequested;

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void OnManagementDismiss(object? sender, PointerPressedEventArgs e)
    {
        ViewModel?.CloseManagementCommand.Execute(null);
        e.Handled = true;
    }

    private void OnAuditRowClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel && sender is Control { DataContext: AuditEntry entry })
        {
            viewModel.SelectedAuditEntry = entry;
        }
    }

    private void OnSuiteRowClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel && sender is Control { DataContext: TestSuite suite })
        {
            viewModel.SelectedSuite = suite;
        }
    }

    private void OnEditSuiteClick(object? sender, RoutedEventArgs e) =>
        SuiteEditRequested?.Invoke(this, EventArgs.Empty);

    private void OnOpenSettingsClick(object? sender, RoutedEventArgs e) =>
        SettingsRequested?.Invoke(this, EventArgs.Empty);

    private static bool IsNearBottom(ScrollViewer area) =>
        area.Extent.Height > area.Viewport.Height + 1
        && area.Offset.Y + area.Viewport.Height >= area.Extent.Height - 80;
}
