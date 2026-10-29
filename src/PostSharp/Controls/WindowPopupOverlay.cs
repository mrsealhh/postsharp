using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace PostSharp.Controls;

/// <summary>
/// Arc 的弹层（Select、菜单、ToolTip）在窗口内渲染，依赖窗口自带的弹出层。
/// 窗口模板不一定创建该层，Avalonia 的 VisualLayerManager 又默认关闭它，
/// 弹层拿不到原生 popup 时就会抛 "no overlay layer is found for the target control"。
/// </summary>
public static class WindowPopupOverlay
{
    private static readonly PropertyInfo? PopupOverlayLayerProperty =
        typeof(VisualLayerManager).GetProperty(
            "EnablePopupOverlayLayer",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
        );

    extension(Window window)
    {
        /// <summary>
        /// 在窗口打开后调用：打开该窗口的弹出层，让 Arc 弹层留在窗口内部渲染。
        /// </summary>
        [DynamicDependency(
            DynamicallyAccessedMemberTypes.PublicProperties
                | DynamicallyAccessedMemberTypes.NonPublicProperties,
            typeof(VisualLayerManager)
        )]
        public void EnablePopupOverlayLayer()
        {
            if (!window.TryEnablePopupOverlayLayer())
            {
                Log.Warning("窗口没有 VisualLayerManager，Arc 弹层将无法使用窗口弹出层");
            }
        }

        /// <summary>模板可能晚于 Opened 创建；在窗口生命周期内重试直到弹出层可用。</summary>
        public async Task EnsurePopupOverlayLayerAsync()
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                window.ApplyTemplate();
                if (window.TryEnablePopupOverlayLayer())
                {
                    return;
                }

                await Task.Delay(100);
            }

            Log.Warning("窗口没有 VisualLayerManager，Arc 弹层将无法使用窗口弹出层");
        }

        private bool TryEnablePopupOverlayLayer()
        {
            var managers = window.GetVisualDescendants().OfType<VisualLayerManager>().ToList();
            if (managers.Count == 0 || PopupOverlayLayerProperty is null)
            {
                return false;
            }

            foreach (var manager in managers)
            {
                PopupOverlayLayerProperty.SetValue(manager, true);
            }

            return true;
        }
    }
}
