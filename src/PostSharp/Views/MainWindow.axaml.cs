using System.Globalization;
using System.Runtime.InteropServices;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using PostSharp.Models;
using PostSharp.ViewModels;

namespace PostSharp.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;
    private object? _treeActionTarget;
    private TreeDisplayNode? _dragCandidate;
    private Point _dragStart;
    private bool _dragging;
    private Border? _dropTargetBorder;

    public MainWindow()
    {
        InitializeComponent();
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://PostSharp/Assets/logo-icon.png")));
        Application.Current?.Resources["PostSharpBodyFont"] = new FontFamily("Inter");
        Application.Current?.Resources["PostSharpCodeFont"] = new FontFamily("Geist Mono");
        Application.Current?.Resources["PostSharpCodeFontSize"] = 12d;
        AddHandler(
            InputElement.KeyDownEvent,
            OnWindowKeyDown,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        AddHandler(InputElement.PointerMovedEvent, OnWindowPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(InputElement.PointerReleasedEvent, OnWindowPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        ResponseTabs.SizeChanged += (_, _) =>
        {
            JsonResponseViewer.MaxTreeHeight = Math.Max(140, ResponseTabs.Bounds.Height - 150);
        };
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
        SizeChanged += (_, _) =>
        {
            if (TreeMenuOverlay.IsVisible)
            {
                HideTreeMenu();
            }

            if (TreeActionMenuOverlay.IsVisible)
            {
                HideTreeActionMenu();
            }
        };
        Opened += (_, _) =>
        {
            EnablePopupOverlayLayer();
            ApplyTitleBarTheme();
        };
        ActualThemeVariantChanged += (_, _) => ApplyTitleBarTheme();
    }

    /// <summary>
    /// Arc popups (Select, menus, tooltips) render inside the window's popup overlay layer instead of a native
    /// popup window, so the floating shadow stays part of the window. Avalonia only creates that layer when the
    /// window's VisualLayerManager has the internal EnablePopupOverlayLayer flag set; the AtomUI window template
    /// does this for the gallery, and a plain Avalonia window must opt in the same way.
    /// </summary>
    private void EnablePopupOverlayLayer()
    {
        var manager = this.GetVisualDescendants()
            .OfType<Avalonia.Controls.Primitives.VisualLayerManager>()
            .FirstOrDefault();

        manager?.GetType()
            .GetProperty(
                "EnablePopupOverlayLayer",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.SetValue(manager, true);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        if (_viewModel?.IsManagementOpen == true)
        {
            _viewModel.CloseManagementCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (TreeActionMenuOverlay.IsVisible)
        {
            HideTreeActionMenu();
            e.Handled = true;
            return;
        }

        if (TreeMenuOverlay.IsVisible)
        {
            HideTreeMenu();
            e.Handled = true;
        }
    }

    private void OnManagementDismiss(object? sender, PointerPressedEventArgs e)
    {
        _viewModel?.CloseManagementCommand.Execute(null);
        e.Handled = true;
    }

    private void OnAuditRowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is not null && sender is Control { DataContext: AuditEntry entry })
        {
            _viewModel.SelectedAuditEntry = entry;
        }
    }

    private void OnSuiteRowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is not null && sender is Control { DataContext: TestSuite suite })
        {
            _viewModel.SelectedSuite = suite;
        }
    }

    private async void OnPickUploadFileClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择上传文件",
            AllowMultiple = false
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _viewModel.Editing.UploadFilePath = path;
        _viewModel.NotifyUploadChanged();
    }

    private void OnClearUploadFileClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is null || string.IsNullOrWhiteSpace(_viewModel.Editing.UploadFilePath))
        {
            return;
        }

        _viewModel.Editing.UploadFilePath = string.Empty;
        _viewModel.NotifyUploadChanged();
    }

    private void OnOpenResponseDetailClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is null || string.IsNullOrEmpty(_viewModel.FullResponseBody))
        {
            return;
        }

        var window = new TextViewerWindow(
            "响应详情",
            _viewModel.FullResponseBody,
            $"{_viewModel.ResponseSizeText} · {_viewModel.ResponseContentType} · {_viewModel.ResponseTimeText}");
        window.Topmost = Topmost;
        window.Show(this);
        window.Activate();
    }

    private void OnTreeRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is null
            || sender is not Control control
            || IsInsideButton(e.Source))
        {
            return;
        }

        var properties = e.GetCurrentPoint(control).Properties;
        if (properties.IsRightButtonPressed)
        {
            SelectTreeNode(control);
            ShowTreeMenu(e.GetPosition(RootPanel));
            e.Handled = true;
            return;
        }

        if (properties.IsLeftButtonPressed)
        {
            SelectTreeNode(control, expandCollection: true);
            if (e.ClickCount == 2)
            {
                switch (control.DataContext)
                {
                    case TreeDisplayNode { Request: { } request }:
                        _viewModel.RequestRenameNode(request);
                        break;
                    case TreeDisplayNode { Collection: { } collection }:
                        _viewModel.RequestRenameNode(collection);
                        break;
                }

                e.Handled = true;
                return;
            }

            _dragCandidate = control.DataContext as TreeDisplayNode;
            _dragStart = e.GetPosition(RootPanel);
        }
    }

    private void SelectTreeNode(Control control, bool expandCollection = false)
    {
        if (_viewModel is null)
        {
            return;
        }

        switch (control.DataContext)
        {
            case TreeDisplayNode { Request: { } request }:
                _viewModel.OpenRequest(request);
                break;
            case TreeDisplayNode { Collection: { } collection }:
                _viewModel.SelectCollection(collection);
                if (expandCollection && !collection.IsExpanded)
                {
                    _viewModel.ToggleCollection(collection, true);
                }

                break;
        }
    }

    private static bool IsInsideButton(object? source)
    {
        var current = source as Visual;
        while (current is not null)
        {
            if (current is Avalonia.Controls.Button)
            {
                return true;
            }

            if (current is Border border && border.Classes.Contains("treeRow"))
            {
                return false;
            }

            current = current.GetVisualParent();
        }

        return false;
    }

    private void OnTreeCollectionChevronClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && sender is Control { DataContext: TreeDisplayNode { Collection: { } collection } })
        {
            _viewModel.ToggleCollection(collection, !collection.IsExpanded);
        }
    }

    private void OnTreeRowActionsClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not Control control)
        {
            return;
        }

        var target = control.DataContext switch
        {
            TreeDisplayNode { Request: { } request } => (object)request,
            TreeDisplayNode { Collection: { } collection } => collection,
            _ => null
        };

        if (target is null)
        {
            return;
        }

        _treeActionTarget = target;
        SelectTreeNode(control);
        var isCollection = target is RequestCollection;
        TreeActionCollectionSection.IsVisible = isCollection;
        TreeActionFavoriteLabel.Text = IsFavorite(target) ? "取消收藏" : "收藏";
        ShowTreeActionMenu(control);
        e.Handled = true;
    }

    private static bool IsFavorite(object target) => target switch
    {
        RequestItem request => request.IsFavorite,
        RequestCollection collection => collection.IsFavorite,
        _ => false
    };

    private void ShowTreeActionMenu(Control anchor)
    {
        HideTreeMenu();
        TreeActionMenuOverlay.IsVisible = true;
        TreeActionMenuCard.Measure(Size.Infinity);

        var desired = TreeActionMenuCard.DesiredSize;
        var area = RootPanel.Bounds.Size;
        var position = anchor.TranslatePoint(
                           new Point(anchor.Bounds.Width - desired.Width, anchor.Bounds.Height + 2),
                           RootPanel)
                       ?? new Point(24, 200);
        var left = Math.Clamp(position.X, 8, Math.Max(8, area.Width - desired.Width - 8));
        var top = Math.Clamp(position.Y, 8, Math.Max(8, area.Height - desired.Height - 8));

        Canvas.SetLeft(TreeActionMenuCard, left);
        Canvas.SetTop(TreeActionMenuCard, top);
        TreeActionFavoriteButton.Focus();
    }

    private void HideTreeActionMenu()
    {
        TreeActionMenuOverlay.IsVisible = false;
        _treeActionTarget = null;
    }

    private void OnTreeActionMenuDismiss(object? sender, PointerPressedEventArgs e)
    {
        HideTreeActionMenu();
        e.Handled = true;
    }

    private void OnTreeActionFavoriteClick(object? sender, RoutedEventArgs e)
    {
        var target = _treeActionTarget;
        HideTreeActionMenu();

        if (_viewModel is not null && target is not null)
        {
            _viewModel.ToggleFavoriteNode(target);
        }
    }

    private void OnTreeActionNewRequestClick(object? sender, RoutedEventArgs e)
    {
        var target = _treeActionTarget as RequestCollection;
        HideTreeActionMenu();

        if (_viewModel is not null && target is not null)
        {
            _viewModel.AddRequestToCollection(target);
        }
    }

    private void OnTreeActionNewSubcollectionClick(object? sender, RoutedEventArgs e)
    {
        var target = _treeActionTarget as RequestCollection;
        HideTreeActionMenu();

        if (_viewModel is not null && target is not null)
        {
            _viewModel.AddSubcollection(target);
        }
    }

    private void OnTreeActionRenameClick(object? sender, RoutedEventArgs e)
    {
        var target = _treeActionTarget;
        HideTreeActionMenu();

        if (_viewModel is not null && target is not null)
        {
            _viewModel.RequestRenameNode(target);
        }
    }

    private void OnTreeActionDeleteClick(object? sender, RoutedEventArgs e)
    {
        var target = _treeActionTarget;
        HideTreeActionMenu();

        if (_viewModel is not null && target is not null)
        {
            _viewModel.RequestDeleteNode(target);
        }
    }

    private void OnWindowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragCandidate is null)
        {
            return;
        }

        var left = e.GetCurrentPoint(RootPanel).Properties.IsLeftButtonPressed;
        if (!left)
        {
            _dragCandidate = null;
            _dragging = false;
            ClearDropTarget();
            return;
        }

        var position = e.GetPosition(RootPanel);
        if (!_dragging)
        {
            if (Math.Abs(position.X - _dragStart.X) < 8
                && Math.Abs(position.Y - _dragStart.Y) < 8)
            {
                return;
            }

            _dragging = true;
        }

        var (target, border) = FindDropTarget(e.GetPosition(CollectionScrollArea));
        if (_viewModel is not null
            && target is not null
            && _viewModel.CanMoveTreeNode(_dragCandidate, target))
        {
            SetDropTarget(border);
        }
        else
        {
            ClearDropTarget();
        }
    }

    private void OnWindowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging && _dragCandidate is { } source)
        {
            var (target, _) = FindDropTarget(e.GetPosition(CollectionScrollArea));
            if (_viewModel is not null && target is not null)
            {
                _viewModel.MoveTreeNode(source, target);
            }
        }

        _dragCandidate = null;
        _dragging = false;
        ClearDropTarget();
    }

    private (RequestCollection? Collection, Border? Border) FindDropTarget(Point position)
    {
        var hit = CollectionScrollArea.InputHitTest(position) as Visual;
        for (var current = hit; current is not null; current = current.GetVisualParent())
        {
            if (current is Border border
                && border.Classes.Contains("treeRow")
                && border.DataContext is TreeDisplayNode { Collection: { } collection })
            {
                return (collection, border);
            }

            if (current is Border requestBorder
                && requestBorder.Classes.Contains("treeRow")
                && requestBorder.DataContext is TreeDisplayNode { Request: not null })
            {
                return (null, null);
            }
        }

        return (null, null);
    }

    private void SetDropTarget(Border? border)
    {
        if (ReferenceEquals(_dropTargetBorder, border))
        {
            return;
        }

        _dropTargetBorder?.Classes.Remove("dropTarget");
        _dropTargetBorder = border;
        border?.Classes.Add("dropTarget");
    }

    private void ClearDropTarget() => SetDropTarget(null);

    private void ShowTreeMenu(Point position)
    {
        HideTreeActionMenu();
        TreeMenuOverlay.IsVisible = true;
        TreeMenuCard.Measure(Size.Infinity);

        var desired = TreeMenuCard.DesiredSize;
        var area = RootPanel.Bounds.Size;
        var left = Math.Clamp(position.X, 8, Math.Max(8, area.Width - desired.Width - 8));
        var top = Math.Clamp(position.Y, 8, Math.Max(8, area.Height - desired.Height - 8));

        Canvas.SetLeft(TreeMenuCard, left);
        Canvas.SetTop(TreeMenuCard, top);
        TreeMenuAddButton.Focus();
    }

    private void HideTreeMenu()
    {
        TreeMenuOverlay.IsVisible = false;
    }

    private void OnTreeMenuItemClick(object? sender, RoutedEventArgs e) => HideTreeMenu();

    private void OnTreeMenuDismiss(object? sender, PointerPressedEventArgs e)
    {
        HideTreeMenu();
        e.Handled = true;
    }

    /// <summary>Keeps the native Windows title bar in the same light/dark appearance as the Arc window.</summary>
    private void ApplyTitleBarTheme()
    {
        if (!OperatingSystem.IsWindows() || TryGetPlatformHandle() is not { } handle)
        {
            return;
        }

        var useDark = ActualThemeVariant == ThemeVariant.Dark ? 1 : 0;
        DwmSetWindowAttribute(handle.Handle, 20, ref useDark, sizeof(int));
    }

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.AppearanceChanged -= OnAppearanceChanged;
        }

        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel is not null)
        {
            _viewModel.AppearanceChanged += OnAppearanceChanged;
            ApplyAppearance();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.AppearanceChanged -= OnAppearanceChanged;
            _viewModel.Shutdown();
            _viewModel = null;
        }
    }

    private void OnAppearanceChanged(object? sender, EventArgs e) => ApplyAppearance();

    private void ApplyAppearance()
    {
        if (_viewModel is null)
        {
            return;
        }

        var settings = _viewModel.Settings;
        var bodyFont = settings.UiFont switch
        {
            "系统默认" => FontFamily.Default,
            "微软雅黑" => new FontFamily("Microsoft YaHei UI"),
            _ => new FontFamily("Inter")
        };

        var codeFont = settings.CodeFont switch
        {
            "Consolas" => new FontFamily("Consolas"),
            "Cascadia Mono" => new FontFamily("Cascadia Mono"),
            _ => new FontFamily("Geist Mono")
        };

        var codeSize = double.TryParse(
            settings.CodeFontSize,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : 12d;

        FontFamily = bodyFont;
        if (Application.Current is { } application)
        {
            application.Resources["PostSharpBodyFont"] = bodyFont;
            application.Resources["PostSharpCodeFont"] = codeFont;
            application.Resources["PostSharpCodeFontSize"] = codeSize;
        }
    }
}
