using Avalonia;
using Avalonia.Controls;
using TKCode.Arc;

namespace PostSharp.Views;

/// <summary>
/// 一批低频弹窗（导入结果、环境管理、重命名、删除确认、回收站、MCP）。
/// 加起来 700 多行，只有第一次真正弹窗时才实例化。
/// </summary>
public partial class AuxiliaryDialogsView : UserControl
{
    public AuxiliaryDialogsView() => InitializeComponent();

    private void OnDialogOpened(object? sender, EventArgs e)
    {
        if (sender is Dialog dialog)
        {
            ApplyDialogWidth(dialog);
        }
    }

    private void ApplyDialogWidth(Dialog dialog)
    {
        // 懒加载时本控件可能还没完成布局，Bounds.Width 仍是 0；以主窗口宽度算 80%。
        var hostWidth = TopLevel.GetTopLevel(this)?.Bounds.Width ?? Bounds.Width;
        var width = Math.Max(420, hostWidth * 0.8);
        dialog.Width = width;
        dialog.MaxWidth = width;
        if (dialog.Surface is { } surface)
        {
            surface.Width = width;
            surface.MaxWidth = width;
        }
    }
}
