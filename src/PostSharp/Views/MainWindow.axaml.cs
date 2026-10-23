using System.Runtime.InteropServices;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using PostSharp.Models;
using PostSharp.ViewModels;
using ArcTreeViewItem = TKCode.Arc.TreeViewItem;
using ArcTreeViewSelectionChangedEventArgs = TKCode.Arc.TreeViewSelectionChangedEventArgs;
using ArcTreeViewItemEventArgs = TKCode.Arc.TreeViewItemEventArgs;

namespace PostSharp.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;
    private bool _suppressSelection;

    public MainWindow()
    {
        InitializeComponent();
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://PostSharp/Assets/logo-icon.png")));
        CollectionTree.AddHandler(
            InputElement.PointerPressedEvent,
            OnCollectionTreePointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        CollectionTree.AddHandler(
            InputElement.KeyDownEvent,
            OnCollectionTreeKeyDown,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        AddHandler(
            InputElement.KeyDownEvent,
            OnWindowKeyDown,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
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

    private void OnCollectionTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(CollectionTree).Properties.IsRightButtonPressed)
        {
            return;
        }

        SelectTreeItemAt(e.Source, e.GetPosition(CollectionTree));
        ShowTreeMenu(e.GetPosition(RootPanel));
        e.Handled = true;
    }

    private void OnCollectionTreeKeyDown(object? sender, KeyEventArgs e)
    {
        var isContextKey = e.Key == Key.Apps
            || (e.Key == Key.F10 && e.KeyModifiers.HasFlag(KeyModifiers.Shift));

        if (!isContextKey)
        {
            return;
        }

        SelectTreeItemAt(CollectionTree, null);
        var origin = CollectionTree.TranslatePoint(new Point(16, 16), RootPanel)
                     ?? new Point(24, 200);
        ShowTreeMenu(origin);
        e.Handled = true;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && TreeMenuOverlay.IsVisible)
        {
            HideTreeMenu();
            e.Handled = true;
        }
    }

    /// <summary>Right-click selects the row before the menu opens, so every command acts on what was clicked.</summary>
    private void SelectTreeItemAt(object? source, Point? point)
    {
        var row = FindTreeRow(source);
        if (row is null && point is { } position)
        {
            row = FindTreeRow(CollectionTree.InputHitTest(position));
        }

        if (row is not null && FindTreeViewItem(row) is { } treeItem)
        {
            CollectionTree.SelectedItem = treeItem;
        }
    }

    private void ShowTreeMenu(Point position)
    {
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
        CollectionTree.Focus();
    }

    private void OnTreeMenuItemClick(object? sender, RoutedEventArgs e) => HideTreeMenu();

    private void OnTreeMenuDismiss(object? sender, PointerPressedEventArgs e)
    {
        HideTreeMenu();
        e.Handled = true;
    }

    /// <summary>The tree draws its rows, so the row is found through its visual type and its node model.</summary>
    private static Control? FindTreeRow(object? source)
    {
        var current = source as Visual;
        while (current is not null)
        {
            if (current.GetType().Name == "TreeViewRow")
            {
                return current as Control;
            }

            current = current.GetVisualParent();
        }

        return null;
    }

    private static ArcTreeViewItem? FindTreeViewItem(Control row)
    {
        var node = row.GetType()
            .GetProperty("Node", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(row);
        var item = node?.GetType()
            .GetProperty("Item", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(node);
        return item as ArcTreeViewItem;
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
            _viewModel.TreeChanged -= OnTreeChanged;
        }

        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel is not null)
        {
            _viewModel.TreeChanged += OnTreeChanged;
            RebuildTree();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.TreeChanged -= OnTreeChanged;
            _viewModel.Shutdown();
            _viewModel = null;
        }
    }

    private void OnTreeChanged(object? sender, EventArgs e) => RebuildTree();

    private void RebuildTree()
    {
        if (_viewModel is null)
        {
            return;
        }

        var filter = _viewModel.FilterText.Trim();
        var hasFilter = filter.Length > 0;
        ArcTreeViewItem? toSelect = null;

        _suppressSelection = true;
        CollectionTree.Items.Clear();

        foreach (var collection in _viewModel.Collections)
        {
            var collectionMatches = Contains(collection.Name, filter);
            var requests = hasFilter && !collectionMatches
                ? collection.Requests.Where(request => Matches(request, filter)).ToList()
                : collection.Requests.ToList();

            if (hasFilter && !collectionMatches && requests.Count == 0)
            {
                continue;
            }

            var collectionNode = new ArcTreeViewItem
            {
                Header = collection.Name,
                Value = collection,
                IsExpanded = hasFilter
                    || _viewModel.IsExpanded(collection)
                    || ReferenceEquals(_viewModel.Editing.Owner, collection)
            };

            foreach (var request in requests)
            {
                var requestNode = new ArcTreeViewItem
                {
                    Header = request.Name,
                    Value = request
                };

                collectionNode.Items.Add(requestNode);

                if (ReferenceEquals(_viewModel.Editing, request))
                {
                    toSelect = requestNode;
                }
            }

            CollectionTree.Items.Add(collectionNode);

            if (_viewModel.SelectedRequest is null
                && ReferenceEquals(_viewModel.SelectedCollection, collection))
            {
                toSelect = collectionNode;
            }
        }

        if (_viewModel.Editing.Owner is null)
        {
            toSelect = null;
        }

        CollectionTree.SelectedItem = toSelect;
        _suppressSelection = false;
        _viewModel.TreeIsEmpty = CollectionTree.Items.Count == 0;
    }

    private static bool Matches(RequestItem request, string filter) =>
        Contains(request.Name, filter)
        || Contains(request.Url, filter)
        || Contains(request.Method, filter);

    private static bool Contains(string? value, string filter) =>
        filter.Length == 0
        || (value?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);

    private void OnCollectionTreeSelectionChanged(object? sender, ArcTreeViewSelectionChangedEventArgs e)
    {
        if (_suppressSelection || _viewModel is null)
        {
            return;
        }

        if (e.NewItem is not ArcTreeViewItem node)
        {
            return;
        }

        switch (node.Value)
        {
            case RequestItem request:
                _viewModel.OpenRequest(request);
                break;
            case RequestCollection collection:
                _viewModel.SelectCollection(collection);
                break;
        }
    }

    private void OnCollectionTreeItemExpanded(object? sender, ArcTreeViewItemEventArgs e)
    {
        if (_viewModel is not null && e.Item is ArcTreeViewItem { Value: RequestCollection collection })
        {
            _viewModel.ToggleCollection(collection, true);
        }
    }

    private void OnCollectionTreeItemCollapsed(object? sender, ArcTreeViewItemEventArgs e)
    {
        if (_viewModel is not null && e.Item is ArcTreeViewItem { Value: RequestCollection collection })
        {
            _viewModel.ToggleCollection(collection, false);
        }
    }
}
