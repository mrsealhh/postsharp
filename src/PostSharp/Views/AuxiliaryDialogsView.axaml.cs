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
        // 弹窗宽度跟随主窗口，而不是各自写死一个宽度。
        var width = Math.Max(420, Bounds.Width * 0.8);
        dialog.Width = width;
        dialog.MaxWidth = width;
        if (dialog.Surface is { } surface)
        {
            surface.Width = width;
            surface.MaxWidth = width;
        }
    }
}
