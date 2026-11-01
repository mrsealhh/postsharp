using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PostSharp.Controls;
using PostSharp.Models;
using PostSharp.Services;
using PostSharp.ViewModels;

namespace PostSharp.Views;

public partial class MainWindow : Window
{
    private const double DefaultSidebarWidth = 320;
    private const double MinSidebarWidth = 240;
    private const double MaxSidebarWidth = 520;
    private const double DefaultRequestPaneRatio = 0.5;
    private const double MinRequestPaneRatio = 0.25;
    private const double MaxRequestPaneRatio = 0.8;
    private const string GitHubRepositoryUrl = "https://github.com/mrsealhh/postsharp";

    private MainWindowViewModel? _viewModel;
    private SettingsWindow? _settingsWindow;
    private AuthorizationWindow? _authorizationWindow;
    private TreeDisplayNode? _dragCandidate;
    private Point _dragStart;
    private bool _dragging;
    private Border? _dropTargetBorder;
    private TreeDropPosition _dropPosition;
    private bool _isSideBySide;

    public MainWindow()
    {
        InitializeComponent();
        UiScaleService.Attach(this);
        UpdateWindowStateVisuals();
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://PostSharp/Assets/logo-icon.png")));
        Application.Current?.Resources["PostSharpBodyFont"] = new FontFamily("Inter");
        Application.Current?.Resources["PostSharpCodeFont"] = new FontFamily("Geist Mono");
        Application.Current?.Resources["PostSharpCodeFontSize"] = 12d;
        AddHandler(
            InputElement.KeyDownEvent,
            OnWindowKeyDown,
            RoutingStrategies.Tunnel,
            handledEventsToo: true
        );
        AddHandler(
            InputElement.PointerMovedEvent,
            OnWindowPointerMoved,
            RoutingStrategies.Tunnel,
            handledEventsToo: true
        );
        AddHandler(
            InputElement.PointerReleasedEvent,
            OnWindowPointerReleased,
            RoutingStrategies.Tunnel,
            handledEventsToo: true
        );
        PropertyChanged += OnWindowPropertyChanged;
        ResponseTabs.SizeChanged += (_, _) =>
        {
            JsonResponseViewer.MaxTreeHeight = Math.Clamp(
                ResponseTabs.Bounds.Height - 220,
                140,
                420
            );
        };
        ResponsePane.SizeChanged += (_, _) =>
        {
            // Arc Tabs measures its content without a finite height. Cap the raw viewer so
            // long responses scroll inside the pane instead of being clipped by its border.
            var available = Math.Max(120, ResponsePane.Bounds.Height - 220);
            ResponseRawScroll.MaxHeight = available;
            ResponseBodyPanel.MaxHeight = available + 80;
        };
        RequestTabs.SizeChanged += (_, _) =>
        {
            var available = RequestTabs.Bounds.Height;
            RequestBodyTextarea.FrameHeight = Math.Clamp(available - 170, 80, 560);
            JsonRequestBodyViewer.MaxTreeHeight = Math.Clamp(available - 190, 80, 520);
        };
        CollectionScrollArea.ScrollChanged += (_, _) =>
        {
            if (IsNearBottom(CollectionScrollArea))
            {
                _viewModel?.TreePage.LoadMore();
            }
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
            this.EnablePopupOverlayLayer();
            ApplyTitleBarTheme();
            foreach (var dialog in this.GetVisualDescendants().OfType<TKCode.Arc.Dialog>())
            {
                dialog.Opened += OnDialogOpened;
            }
        };
        ActualThemeVariantChanged += (_, _) => ApplyTitleBarTheme();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (_viewModel.TryCancelActiveDialog())
            {
                e.Handled = true;
                return;
            }

            if (_viewModel.IsManagementOpen)
            {
                _viewModel.CloseManagementCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (TreeMenuOverlay.IsVisible)
            {
                HideTreeMenu();
                e.Handled = true;
            }

            return;
        }

        if (
            e.Key == Key.Enter
            && e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && _viewModel.HasOpenDialog
        )
        {
            // A modal dialog owns Ctrl+Enter; it must never fall through to "send request".
            _viewModel.TryConfirmActiveDialog();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && _viewModel.HasOpenDialog && e.Source is not Button)
        {
            if (_viewModel.TryConfirmActiveDialog())
            {
                e.Handled = true;
            }

            return;
        }

        if (
            e.Key == Key.Delete
            && !_viewModel.HasOpenDialog
            && !_viewModel.IsManagementOpen
            && !TreeMenuOverlay.IsVisible
            && IsTreeFocusSource(e.Source)
            && (_viewModel.SelectedRequest is not null || _viewModel.SelectedCollection is not null)
        )
        {
            _viewModel.DeleteSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }

    private bool IsTreeFocusSource(object? source)
    {
        for (
            var current = source as Visual;
            current is not null;
            current = current.GetVisualParent()
        )
        {
            if (ReferenceEquals(current, CollectionScrollArea))
            {
                return true;
            }

            if (ReferenceEquals(current, RootPanel))
            {
                return false;
            }
        }

        return false;
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty)
        {
            UpdateWindowStateVisuals();
        }
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || IsInteractiveTitleBarSource(e.Source))
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }

        BeginMoveDrag(e);
        e.Handled = true;
    }

    private void OnMinimizeWindowClick(object? sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
        {
            app.MinimizeToTray(this);
            return;
        }

        WindowState = WindowState.Minimized;
    }

    private void OnToggleMaximizeWindowClick(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseWindowClick(object? sender, RoutedEventArgs e) => Close();

    private void OnOpenGitHubClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(
                new ProcessStartInfo { FileName = GitHubRepositoryUrl, UseShellExecute = true }
            );
        }
        catch (Exception exception)
        {
            Log.Error(exception, "打开 GitHub 项目主页失败");
            _viewModel?.ShowToast($"无法打开 GitHub 项目主页，请手动访问 {GitHubRepositoryUrl}");
        }
    }

    private void ToggleMaximize()
    {
        WindowState =
            WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void UpdateWindowStateVisuals()
    {
        var maximized = WindowState == WindowState.Maximized;
        WindowMaximizeGlyph.IsVisible = !maximized;
        WindowRestoreGlyph.IsVisible = maximized;
    }

    private bool IsInteractiveTitleBarSource(object? source)
    {
        for (
            var current = source as Visual;
            current is not null;
            current = current.GetVisualParent()
        )
        {
            if (ReferenceEquals(current, TitleBar))
            {
                return false;
            }

            if (
                current is Button or TextBox or ComboBox or CheckBox or Slider
                || current is Control { Focusable: true }
            )
            {
                return true;
            }
        }

        return false;
    }

    private void OnHistoryRowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is not null && sender is Control { DataContext: HistoryEntry entry })
        {
            _viewModel.LoadHistory(entry);
        }
    }

    private async void OnPickUploadFilesClick(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e
    )
    {
        if (_viewModel is null)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions { Title = "选择上传文件", AllowMultiple = true }
        );

        AddUploadFiles(
            files
                .Select(file => file.TryGetLocalPath())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path!)
        );
    }

    private void OnClearUploadFilesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is null || _viewModel.Editing.UploadFiles.Count == 0)
        {
            return;
        }

        _viewModel.Editing.UploadFiles.Clear();
        _viewModel.NotifyUploadChanged();
    }

    private void OnRemoveUploadFileClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not Button { CommandParameter: UploadFileItem file })
        {
            return;
        }

        _viewModel.Editing.UploadFiles.Remove(file);
        _viewModel.NotifyUploadChanged();
    }

    private void OnUploadDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnUploadDrop(object? sender, DragEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var paths = e
            .DataTransfer.TryGetFiles()
            ?.Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!);
        if (paths is not null)
        {
            AddUploadFiles(paths);
        }

        e.Handled = true;
    }

    private void AddUploadFiles(IEnumerable<string> paths)
    {
        if (_viewModel is null)
        {
            return;
        }

        var added = false;
        foreach (var path in paths)
        {
            if (
                _viewModel.Editing.UploadFiles.Any(file =>
                    string.Equals(file.FilePath, path, StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                continue;
            }

            var item = new UploadFileItem { FilePath = path, FieldName = "file" };
            try
            {
                item.SizeBytes = new FileInfo(path).Length;
            }
            catch (IOException)
            {
                item.SizeBytes = 0;
            }
            catch (UnauthorizedAccessException)
            {
                item.SizeBytes = 0;
            }

            _viewModel.Editing.UploadFiles.Add(item);
            added = true;
        }

        if (added)
        {
            _viewModel.NotifyUploadChanged();
        }
    }

    private void OnSuiteEditorRequested(object? sender, RequestCollection? collection)
    {
        if (_viewModel is null)
        {
            Log.Warning("套件编辑器请求被忽略：主 ViewModel 为空");
            return;
        }

        OpenSuiteEditor(_viewModel.CreateSuiteEditorDraft(collection), null);
    }

    private void OnEditSuiteClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedSuite is not { } suite)
        {
            return;
        }

        OpenSuiteEditor(_viewModel.CreateSuiteDraftForEditing(suite), suite);
    }

    /// <summary>首次打开管理层时才构建那块界面；之后复用同一个实例。</summary>
    private void EnsureManagementView()
    {
        if (_viewModel is null || ManagementHost.Children.Count > 0)
        {
            return;
        }

        var view = new ManagementOverlayView { DataContext = _viewModel };
        view.AuditReachedBottom += (_, _) => _viewModel?.AuditPage.LoadMore();
        view.SettingsRequested += (_, _) => OnOpenSettingsClick(null, new RoutedEventArgs());
        view.SuiteEditRequested += (_, _) => OnEditSuiteClick(null, new RoutedEventArgs());
        ManagementHost.Children.Add(view);
    }

    /// <summary>这些弹窗只在第一次真正打开时才实例化，平时不占控件树。</summary>
    private static readonly string[] AuxiliaryDialogFlags =
    [
        nameof(MainWindowViewModel.IsEnvironmentDialogOpen),
        nameof(MainWindowViewModel.IsRenameDialogOpen),
        nameof(MainWindowViewModel.IsDeleteDialogOpen),
        nameof(MainWindowViewModel.IsHistoryTrashConfirmOpen),
        nameof(MainWindowViewModel.IsHistoryTrashOpen),
        nameof(MainWindowViewModel.IsHistoryPurgeConfirmOpen),
        nameof(MainWindowViewModel.IsAuditTrashConfirmOpen),
        nameof(MainWindowViewModel.IsAuditTrashOpen),
        nameof(MainWindowViewModel.IsAuditPurgeConfirmOpen),
        nameof(MainWindowViewModel.IsMcpDialogOpen),
        nameof(MainWindowViewModel.IsCookieDialogOpen),
        nameof(MainWindowViewModel.IsOnboardingOpen),
        nameof(MainWindowViewModel.IsResetDemoDialogOpen),
        nameof(MainWindowViewModel.IsRunDiffOpen),
    ];

    private void EnsureAuxiliaryDialogs()
    {
        if (_viewModel is null || AuxiliaryDialogsHost.Children.Count > 0)
        {
            return;
        }

        AuxiliaryDialogsHost.Children.Add(new AuxiliaryDialogsView { DataContext = _viewModel });
    }

    private void OnImportViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImportViewModel.IsImportSuccessOpen))
        {
            EnsureAuxiliaryDialogs();
        }
    }

    private void OpenSuiteEditor(TestSuite draft, TestSuite? existing)
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            var editor = new WorkflowEditorWindow(
                _viewModel.CreateWorkflowEditorViewModel(draft, existing)
            );
            editor.SuiteCreated += (_, result) =>
                _viewModel.ApplySuiteEditorResult(result.Suite, existing);
            _ = editor.ShowDialog(this);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "打开套件编辑器失败");
            _viewModel.ShowToast("套件编辑器打开失败，请查看日志");
        }
    }

    private void OnImportDialogOpened(object? sender, EventArgs e) =>
        ApplyDialogWidth(ImportDialog);

    private void OnDialogOpened(object? sender, EventArgs e)
    {
        if (sender is TKCode.Arc.Dialog dialog)
        {
            ApplyDialogWidth(dialog);
        }
    }

    private void ApplyDialogWidth(TKCode.Arc.Dialog dialog)
    {
        // Popups follow the main window instead of each using a different fixed width.
        var width = Math.Max(420, Bounds.Width * 0.8);
        dialog.Width = width;
        dialog.MaxWidth = width;
        if (dialog.Surface is { } surface)
        {
            surface.Width = width;
            surface.MaxWidth = width;
        }
    }

    private void OnOpenSettingsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.IsManagementOpen = false;
        if (_settingsWindow is { } existing)
        {
            existing.Activate();
            return;
        }

        var window = new SettingsWindow { DataContext = _viewModel.CreateSettingsViewModel() };
        // Keep the settings window inside the owner on smaller or scaled screens; its content scrolls.
        window.Width = Math.Clamp(Bounds.Width - 120, window.MinWidth, 860);
        window.Height = Math.Clamp(Bounds.Height - 80, window.MinHeight, 900);
        _settingsWindow = window;
        window.Closed += (_, _) =>
        {
            _settingsWindow = null;
            HeapTrim.Request();
        };
        window.Show(this);
        window.Activate();
    }

    private void OnOpenAuthorizationClick(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e
    ) => OpenCurrentAuthorization();

    private void OnOpenCurrentAuthorizationClick(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e
    ) => OpenCurrentAuthorization();

    /// <summary>工具栏钥匙和授权标签页都按当前树选中对象打开：集合或请求。</summary>
    private void OpenCurrentAuthorization()
    {
        if (_viewModel is null)
        {
            return;
        }

        if (_viewModel.IsCollectionSelected && _viewModel.SelectedCollection is { } collection)
        {
            OpenCollectionAuthorization(collection);
            return;
        }

        OpenRequestAuthorization(_viewModel.Editing);
    }

    private void OpenRequestAuthorization(RequestItem request)
    {
        if (_viewModel is null)
        {
            return;
        }

        OpenAuthorizationWindow(
            request.Auth,
            $"请求「{request.Name}」",
            () => _viewModel.DescribeRequestAuthTarget(request)
        );
    }

    private void OnOpenEnclosingCollectionAuthorizationClick(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e
    )
    {
        if (_viewModel?.EnclosingCollectionAuthTarget is not { } collection)
        {
            return;
        }

        OpenCollectionAuthorization(collection);
    }

    private void OnTreeMenuAuthorizationClick(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e
    )
    {
        HideTreeMenu();
        if (_viewModel is null)
        {
            return;
        }

        if (_viewModel.SelectedRequest is { } request)
        {
            OpenRequestAuthorization(request);
            return;
        }

        if (_viewModel.SelectedCollection is { } collection)
        {
            OpenCollectionAuthorization(collection);
        }
    }

    private void OpenCollectionAuthorization(RequestCollection collection)
    {
        if (_viewModel is null)
        {
            return;
        }

        OpenAuthorizationWindow(
            collection.Auth,
            $"集合「{collection.Name}」",
            () => _viewModel.DescribeCollectionAuthTarget(collection)
        );
    }

    private void OpenAuthorizationWindow(
        RequestAuth auth,
        string targetLabel,
        Func<string> targetDetailFactory
    )
    {
        if (_viewModel is null)
        {
            return;
        }

        if (_authorizationWindow is { } existing)
        {
            existing.Close();
            _authorizationWindow = null;
        }

        var window = new AuthorizationWindow
        {
            DataContext = _viewModel.CreateAuthorizationViewModel(
                auth,
                targetLabel,
                targetDetailFactory
            ),
        };
        window.Width = Math.Clamp(Bounds.Width - 160, window.MinWidth, 1120);
        window.Height = Math.Clamp(Bounds.Height - 100, window.MinHeight, 840);
        _authorizationWindow = window;
        window.Closed += (_, _) =>
        {
            _authorizationWindow = null;
            _viewModel?.OnAuthorizationDialogClosed();
            HeapTrim.Request();
        };
        window.Show(this);
        window.Activate();
    }

    private async void OnPickOpenApiFileClick(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e
    )
    {
        if (_viewModel is null)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "导入 OpenAPI 文档",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("OpenAPI 文档")
                    {
                        Patterns = new[] { "*.json", "*.yaml", "*.yml" },
                        MimeTypes = new[]
                        {
                            "application/json",
                            "application/yaml",
                            "text/yaml",
                            "text/plain",
                        },
                    },
                },
            }
        );

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await _viewModel.Import.ImportFromFileAsync(path);
        }
    }

    private async void OnPickPostmanFileClick(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e
    )
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "导入 Postman Collection",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("Postman Collection")
                        {
                            Patterns = new[] { "*.json" },
                            MimeTypes = new[] { "application/json", "text/plain" },
                        },
                    },
                }
            );

            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                await _viewModel.Import.ImportPostmanFileAsync(path);
            }
        }
        catch (Exception exception)
        {
            _viewModel.Import.HasImportError = true;
            _viewModel.Import.ImportStatus =
                "无法打开文件选择器，请稍后重试或把 Collection JSON 粘贴到下方。";
            Log.Error(exception, "打开 Postman Collection 文件选择器失败");
        }
    }

    private async void OnPickPostmanEnvironmentFileClick(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e
    )
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "导入 Postman 环境文件",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("Postman 环境文件")
                        {
                            Patterns = new[] { "*.json" },
                            MimeTypes = new[] { "application/json", "text/plain" },
                        },
                    },
                }
            );

            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                await _viewModel.Import.ImportPostmanEnvironmentFileAsync(path);
            }
        }
        catch (Exception exception)
        {
            _viewModel.Import.HasImportError = true;
            _viewModel.Import.ImportStatus = "无法打开环境文件选择器，请稍后重试。";
            Log.Error(exception, "打开 Postman 环境文件选择器失败");
        }
    }

    private async void OnPickHarFileClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "导入 HAR 文件",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("HTTP Archive")
                        {
                            Patterns = new[] { "*.har", "*.json" },
                            MimeTypes = new[] { "application/json", "text/plain" },
                        },
                    },
                }
            );

            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                await _viewModel.Import.ImportHarFileAsync(path);
            }
        }
        catch (Exception exception)
        {
            _viewModel.Import.HasImportError = true;
            _viewModel.Import.ImportStatus =
                "无法打开 HAR 文件选择器，请稍后重试或把 HAR JSON 粘贴到下方。";
            Log.Error(exception, "打开 HAR 文件选择器失败");
        }
    }

    private async void OnPickInsomniaFileClick(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e
    )
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "导入 Insomnia v4 JSON",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("Insomnia v4 JSON")
                        {
                            Patterns = new[] { "*.json" },
                            MimeTypes = new[] { "application/json", "text/plain" },
                        },
                    },
                }
            );

            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                await _viewModel.Import.ImportInsomniaFileAsync(path);
            }
        }
        catch (Exception exception)
        {
            _viewModel.Import.HasImportError = true;
            _viewModel.Import.ImportStatus =
                "无法打开 Insomnia 文件选择器，请稍后重试或把 JSON 粘贴到下方。";
            Log.Error(exception, "打开 Insomnia 文件选择器失败");
        }
    }

    private async void OnPickHoppscotchFileClick(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e
    )
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "导入 Hoppscotch Collection",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("Hoppscotch Collection")
                        {
                            Patterns = new[] { "*.json" },
                            MimeTypes = new[] { "application/json", "text/plain" },
                        },
                    },
                }
            );

            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                await _viewModel.Import.ImportHoppscotchFileAsync(path);
            }
        }
        catch (Exception exception)
        {
            _viewModel.Import.HasImportError = true;
            _viewModel.Import.ImportStatus =
                "无法打开 Hoppscotch 文件选择器，请稍后重试或把 JSON 粘贴到下方。";
            Log.Error(exception, "打开 Hoppscotch 文件选择器失败");
        }
    }

    private async void OnPickBatchImportFilesClick(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e
    )
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "批量导入文件",
                    AllowMultiple = true,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("支持的导入文件")
                        {
                            Patterns = new[]
                            {
                                "*.json",
                                "*.har",
                                "*.yaml",
                                "*.yml",
                                "*.sh",
                                "*.txt",
                            },
                            MimeTypes = new[]
                            {
                                "application/json",
                                "application/yaml",
                                "text/yaml",
                                "text/plain",
                            },
                        },
                    },
                }
            );

            var paths = files
                .Select(file => file.TryGetLocalPath())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path!)
                .ToList();
            if (paths.Count > 0)
            {
                await _viewModel.Import.ImportBatchFilesAsync(paths);
            }
        }
        catch (Exception exception)
        {
            _viewModel.Import.HasImportError = true;
            _viewModel.Import.ImportStatus = "无法打开批量文件选择器，请稍后重试。";
            Log.Error(exception, "打开批量导入文件选择器失败");
        }
    }

    private async void OnExportPostmanClick(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e
    ) => await ExportPostmanAsync();

    private async void OnTreeMenuExportPostmanClick(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e
    )
    {
        HideTreeMenu();
        await ExportPostmanAsync();
    }

    private async Task ExportPostmanAsync()
    {
        if (_viewModel is null)
        {
            return;
        }

        var document = _viewModel.BuildPostmanExport();
        if (document is null)
        {
            _viewModel.Import.CompleteBackgroundTask(
                "当前工作区没有可导出的集合。请先创建或导入集合。",
                success: false
            );
            return;
        }

        await SaveExportAsync(
            document,
            "导出 Postman Collection",
            new FilePickerFileType("Postman Collection")
            {
                Patterns = new[] { "*.json" },
                MimeTypes = new[] { "application/json" },
            }
        );
    }

    private async void OnExportCurlClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var document = _viewModel.BuildCurlExport();
        if (document is null)
        {
            _viewModel.Import.CompleteBackgroundTask(
                "当前请求还没有可导出的 cURL，请先选择请求并填写有效地址。",
                success: false
            );
            return;
        }

        await SaveExportAsync(
            document,
            "保存 cURL",
            new FilePickerFileType("Shell 脚本")
            {
                Patterns = new[] { "*.sh" },
                MimeTypes = new[] { "text/x-shellscript", "text/plain" },
            }
        );
    }

    private async void OnExportPowerShellClick(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e
    )
    {
        if (_viewModel is null)
        {
            return;
        }

        var document = _viewModel.BuildPowerShellExport();
        if (document is null)
        {
            _viewModel.Import.CompleteBackgroundTask(
                "当前请求还没有可导出的 PowerShell 命令，请先选择请求并填写有效地址。",
                success: false
            );
            return;
        }

        await SaveExportAsync(
            document,
            "保存 PowerShell",
            new FilePickerFileType("PowerShell 脚本")
            {
                Patterns = new[] { "*.ps1" },
                MimeTypes = new[] { "text/plain" },
            }
        );
    }

    private async void OnExportCodeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var document = _viewModel.BuildCodeExport();
        if (document is null)
        {
            _viewModel.Import.CompleteBackgroundTask(
                "当前请求还没有可导出的调用代码，请先选择请求并填写有效地址。",
                success: false
            );
            return;
        }

        await SaveExportAsync(
            document,
            "保存调用代码",
            new FilePickerFileType("代码文件")
            {
                Patterns = new[] { "*" + Path.GetExtension(document.FileName) },
                MimeTypes = new[] { "text/plain" },
            }
        );
    }

    private async Task SaveExportAsync(
        ExportDocument document,
        string title,
        FilePickerFileType fileType
    )
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = title,
                    SuggestedFileName = document.FileName,
                    DefaultExtension = Path.GetExtension(document.FileName).TrimStart('.'),
                    ShowOverwritePrompt = true,
                    FileTypeChoices = new[] { fileType },
                }
            );
            if (file is null)
            {
                return;
            }

            var path = file.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                _viewModel.Import.CompleteBackgroundTask(
                    "无法写入所选位置，请换一个本地文件夹。",
                    success: false
                );
                return;
            }

            _viewModel.Import.StartBackgroundTask($"正在导出：{Path.GetFileName(path)}");
            await File.WriteAllTextAsync(path, document.Content);
            _viewModel.Import.CompleteBackgroundTask(
                $"已导出：{Path.GetFileName(path)}",
                success: true
            );
            Log.Information("导出文件完成 {FilePath}", path);
        }
        catch (Exception exception)
        {
            _viewModel.Import.CompleteBackgroundTask(
                "导出失败，请检查目标文件夹是否可写后重试。",
                success: false
            );
            Log.Error(exception, "导出文件失败 {Title}", title);
        }
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
            $"{_viewModel.ResponseSizeText} · {_viewModel.ResponseContentType} · {_viewModel.ResponseTimeText}"
        );
        window.Topmost = Topmost;
        window.Show(this);
        window.Activate();
    }

    private void OnTreeRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is null || sender is not Control control || IsInsideButton(e.Source))
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

        control.Focus();

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
        if (
            _viewModel is not null
            && sender is Control { DataContext: TreeDisplayNode { Collection: { } collection } }
        )
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
            _ => null,
        };

        if (target is null)
        {
            return;
        }

        SelectTreeNode(control);
        var point =
            control.TranslatePoint(
                new Point(control.Bounds.Width - 190, control.Bounds.Height + 2),
                RootPanel
            ) ?? new Point(24, 200);
        ShowTreeMenu(point);
        e.Handled = true;
    }

    private void OnTreeMoveRowUpClick(object? sender, RoutedEventArgs e)
    {
        MoveTreeRow(sender, -1);
        e.Handled = true;
    }

    private void OnTreeMoveRowDownClick(object? sender, RoutedEventArgs e)
    {
        MoveTreeRow(sender, 1);
        e.Handled = true;
    }

    private void MoveTreeRow(object? sender, int delta)
    {
        if (_viewModel is null || sender is not Control control)
        {
            return;
        }

        var target = control.DataContext switch
        {
            TreeDisplayNode { Request: { } request } => (object)request,
            TreeDisplayNode { Collection: { } collection } => collection,
            _ => null,
        };

        if (target is not null)
        {
            _viewModel.MoveNode(target, delta);
        }
    }

    private static bool IsFavorite(object target) =>
        target switch
        {
            RequestItem request => request.IsFavorite,
            RequestCollection collection => collection.IsFavorite,
            _ => false,
        };

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
            if (Math.Abs(position.X - _dragStart.X) < 8 && Math.Abs(position.Y - _dragStart.Y) < 8)
            {
                return;
            }

            _dragging = true;
        }

        var (target, border) = FindDropTarget(e.GetPosition(CollectionScrollArea));
        if (
            _viewModel is not null
            && _dragCandidate is { } source
            && target is not null
            && border is not null
        )
        {
            var dropPosition = GetDropPosition(target, border, e.GetPosition(border));
            if (_viewModel.CanMoveTreeNode(source, target, dropPosition))
            {
                SetDropTarget(border, dropPosition);
                return;
            }
        }

        ClearDropTarget();
    }

    private void OnWindowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging && _dragCandidate is { } source)
        {
            var (target, border) = FindDropTarget(e.GetPosition(CollectionScrollArea));
            if (_viewModel is not null && target is not null && border is not null)
            {
                var dropPosition = GetDropPosition(target, border, e.GetPosition(border));
                _viewModel.MoveTreeNode(source, target, dropPosition);
            }
        }

        _dragCandidate = null;
        _dragging = false;
        ClearDropTarget();
    }

    private (TreeDisplayNode? Node, Border? Border) FindDropTarget(Point position)
    {
        var hit = CollectionScrollArea.InputHitTest(position) as Visual;
        for (var current = hit; current is not null; current = current.GetVisualParent())
        {
            if (
                current is Border border
                && border.Classes.Contains("treeRow")
                && border.DataContext is TreeDisplayNode node
                && (node.Collection is not null || node.Request is not null)
            )
            {
                return (node, border);
            }
        }

        return (null, null);
    }

    private static TreeDropPosition GetDropPosition(
        TreeDisplayNode target,
        Border border,
        Point positionInRow
    )
    {
        var ratio =
            border.Bounds.Height <= 0
                ? 0.5
                : Math.Clamp(positionInRow.Y / border.Bounds.Height, 0, 1);

        if (target.Collection is not null)
        {
            return ratio switch
            {
                < 0.28 => TreeDropPosition.Before,
                > 0.72 => TreeDropPosition.After,
                _ => TreeDropPosition.Inside,
            };
        }

        return ratio < 0.5 ? TreeDropPosition.Before : TreeDropPosition.After;
    }

    private void SetDropTarget(Border? border, TreeDropPosition position)
    {
        if (ReferenceEquals(_dropTargetBorder, border) && _dropPosition == position)
        {
            return;
        }

        ClearDropTargetClasses();
        _dropTargetBorder = border;
        _dropPosition = position;

        if (border is null)
        {
            return;
        }

        border.Classes.Add(
            position switch
            {
                TreeDropPosition.Before => "dropBefore",
                TreeDropPosition.After => "dropAfter",
                _ => "dropTarget",
            }
        );
    }

    private void ClearDropTarget() => SetDropTarget(null, TreeDropPosition.Inside);

    private void ClearDropTargetClasses()
    {
        _dropTargetBorder?.Classes.Remove("dropTarget");
        _dropTargetBorder?.Classes.Remove("dropBefore");
        _dropTargetBorder?.Classes.Remove("dropAfter");
    }

    private void ShowTreeMenu(Point position)
    {
        var favoriteTarget = _viewModel?.SelectedRequest ?? (object?)_viewModel?.SelectedCollection;
        var isCollectionNode =
            _viewModel?.SelectedRequest is null && _viewModel?.SelectedCollection is not null;
        var isRequestNode = _viewModel?.SelectedRequest is not null;
        TreeMenuCollectionSection.IsVisible = isCollectionNode;
        TreeMenuRequestSection.IsVisible = isRequestNode;
        var orderTarget = _viewModel?.SelectedRequest ?? (object?)_viewModel?.SelectedCollection;
        TreeMenuMoveUpButton.IsEnabled =
            orderTarget is not null && _viewModel?.CanMoveNode(orderTarget, -1) == true;
        TreeMenuMoveDownButton.IsEnabled =
            orderTarget is not null && _viewModel?.CanMoveNode(orderTarget, 1) == true;
        TreeMenuFavoriteButton.IsVisible = favoriteTarget is not null;
        TreeMenuFavoriteLabel.Text =
            favoriteTarget is not null && IsFavorite(favoriteTarget) ? "取消收藏" : "收藏";
        TreeMenuOverlay.IsVisible = true;
        TreeMenuCard.Measure(Size.Infinity);

        var desired = TreeMenuCard.DesiredSize;
        var area = RootPanel.Bounds.Size;
        var left = Math.Clamp(position.X, 8, Math.Max(8, area.Width - desired.Width - 8));
        var top = Math.Clamp(position.Y, 8, Math.Max(8, area.Height - desired.Height - 8));

        Canvas.SetLeft(TreeMenuCard, left);
        Canvas.SetTop(TreeMenuCard, top);
        if (isCollectionNode)
        {
            TreeMenuNewRequestButton.Focus();
        }
        else
        {
            TreeMenuFavoriteButton.Focus();
        }
    }

    private void HideTreeMenu()
    {
        TreeMenuOverlay.IsVisible = false;
    }

    private void OnTreeMenuItemClick(object? sender, RoutedEventArgs e) => HideTreeMenu();

    private void OnTreeMenuFavoriteClick(object? sender, RoutedEventArgs e)
    {
        var target = _viewModel?.SelectedRequest ?? (object?)_viewModel?.SelectedCollection;
        HideTreeMenu();

        if (_viewModel is not null && target is not null)
        {
            _viewModel.ToggleFavoriteNode(target);
        }
    }

    private void OnTreeFavoriteClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not Control { DataContext: TreeDisplayNode node })
        {
            return;
        }

        if (node.Request is { } request)
        {
            _viewModel.ToggleFavoriteNode(request);
        }
        else if (node.Collection is { } collection)
        {
            _viewModel.ToggleFavoriteNode(collection);
        }

        e.Handled = true;
    }

    private void OnTreeMenuNewRequestClick(object? sender, RoutedEventArgs e)
    {
        var collection = _viewModel?.SelectedCollection;
        HideTreeMenu();

        if (_viewModel is not null && collection is not null)
        {
            _viewModel.AddRequestToCollection(collection);
        }
    }

    private void OnTreeMenuNewSubcollectionClick(object? sender, RoutedEventArgs e)
    {
        var collection = _viewModel?.SelectedCollection;
        HideTreeMenu();

        if (_viewModel is not null && collection is not null)
        {
            _viewModel.AddSubcollection(collection);
        }
    }

    private void OnTreeMenuSuiteClick(object? sender, RoutedEventArgs e)
    {
        var collection = _viewModel?.SelectedCollection;
        HideTreeMenu();

        if (_viewModel is not null && collection is not null)
        {
            _viewModel.OpenSuiteBuilderForCollection(collection);
        }
    }

    private void OnTreeMenuMoveUpClick(object? sender, RoutedEventArgs e)
    {
        var target = _viewModel?.SelectedRequest ?? (object?)_viewModel?.SelectedCollection;
        HideTreeMenu();

        if (_viewModel is not null && target is not null)
        {
            _viewModel.MoveNode(target, -1);
        }
    }

    private void OnTreeMenuMoveDownClick(object? sender, RoutedEventArgs e)
    {
        var target = _viewModel?.SelectedRequest ?? (object?)_viewModel?.SelectedCollection;
        HideTreeMenu();

        if (_viewModel is not null && target is not null)
        {
            _viewModel.MoveNode(target, 1);
        }
    }

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
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int value,
        int size
    );

    private static bool IsNearBottom(ScrollViewer area) =>
        area.Extent.Height > area.Viewport.Height + 1
        && area.Offset.Y + area.Viewport.Height >= area.Extent.Height - 80;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.AppearanceChanged -= OnAppearanceChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.TreeNodes.CollectionChanged -= OnTreeNodesCollectionChanged;
            _viewModel.SuiteEditorRequested -= OnSuiteEditorRequested;
        }

        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel is not null)
        {
            _viewModel.AppearanceChanged += OnAppearanceChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.TreeNodes.CollectionChanged += OnTreeNodesCollectionChanged;
            _viewModel.SuiteEditorRequested += OnSuiteEditorRequested;
            _viewModel.Import.PropertyChanged += OnImportViewModelPropertyChanged;
            ApplyAppearance();
            ApplyRequestResponseLayout();
            ApplyWorkspaceLayout(_viewModel.IsWorkspaceSideBySide);
            EnsureAuxiliaryDialogs();
        }
    }

    private void OnTreeNodesCollectionChanged(
        object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e
    ) => Dispatcher.UIThread.Post(ScrollSelectedTreeNodeIntoView, DispatcherPriority.Background);

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            SaveSidebarWidth();
            SaveRequestResponseRatio();
            _viewModel.AppearanceChanged -= OnAppearanceChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.TreeNodes.CollectionChanged -= OnTreeNodesCollectionChanged;
            _viewModel.SuiteEditorRequested -= OnSuiteEditorRequested;
            _viewModel.Import.PropertyChanged -= OnImportViewModelPropertyChanged;
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
        var bodyFont = FontService.Resolve(settings.UiFont);
        var codeFont = FontService.Resolve(settings.CodeFont);

        var codeSize = double.TryParse(
            settings.CodeFontSize,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed
        )
            ? parsed
            : 12d;

        FontFamily = bodyFont;
        if (Application.Current is { } application)
        {
            application.Resources["PostSharpBodyFont"] = bodyFont;
            application.Resources["PostSharpCodeFont"] = codeFont;
            application.Resources["PostSharpCodeFontSize"] = codeSize;
        }

        ApplySidebarLayout();

        if (
            FontManager.Current.TryGetGlyphTypeface(new Typeface(bodyFont), out var bodyGlyph)
            && FontManager.Current.TryGetGlyphTypeface(new Typeface(codeFont), out var codeGlyph)
        )
        {
            Log.Debug(
                "字体已应用：界面 {UiFont} -> {BodyGlyph}，代码 {CodeFont} -> {CodeGlyph}",
                settings.UiFont,
                bodyGlyph.FamilyName,
                settings.CodeFont,
                codeGlyph.FamilyName
            );
        }
        else
        {
            Log.Warning(
                "字体解析失败：界面 {UiFont}，代码 {CodeFont}，已使用系统回退字体",
                settings.UiFont,
                settings.CodeFont
            );
        }

        RootPanel.Classes.Set("compact", _viewModel.IsCompactLayout);
        RequestTabs.Classes.Set("wrap-tabs", _viewModel.IsWrapTabs);
        RequestTabWrap.IsVisible = _viewModel.IsWrapTabs;
    }

    private void ApplySidebarLayout()
    {
        if (_viewModel is null)
        {
            return;
        }

        var width = Math.Clamp(_viewModel.Settings.TreePaneWidth, MinSidebarWidth, MaxSidebarWidth);
        WorkspaceLayout.ColumnDefinitions[0].Width = new GridLength(width);
    }

    private void OnSidebarSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        SaveSidebarWidth();
    }

    private void OnRequestResponseSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        SaveRequestResponseRatio();
    }

    private void OnRequestResponseSplitterDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.Settings.RequestPaneRatio = DefaultRequestPaneRatio;
        ApplyRequestResponseLayout();
        e.Handled = true;
    }

    private void ApplyRequestResponseLayout()
    {
        if (_viewModel is null || _isSideBySide)
        {
            return;
        }

        var ratio = Math.Clamp(
            _viewModel.Settings.RequestPaneRatio,
            MinRequestPaneRatio,
            MaxRequestPaneRatio
        );
        EditorResponseLayout.RowDefinitions[2].Height = new GridLength(ratio, GridUnitType.Star);
        EditorResponseLayout.RowDefinitions[4].Height = new GridLength(
            1 - ratio,
            GridUnitType.Star
        );
    }

    private void ApplyWorkspaceLayout(bool sideBySide)
    {
        _isSideBySide = sideBySide;
        var collapsed = _viewModel?.IsResponseCollapsed == true;
        var columns = EditorResponseLayout.ColumnDefinitions;
        var rows = EditorResponseLayout.RowDefinitions;

        columns[0].Width = new GridLength(1, GridUnitType.Star);
        columns[1].Width = new GridLength(sideBySide && !collapsed ? 8 : 0);
        columns[2].Width = new GridLength(sideBySide && !collapsed ? 1 : 0, GridUnitType.Star);
        rows[2].Height = new GridLength(1, GridUnitType.Star);
        rows[3].Height = new GridLength(!sideBySide && !collapsed ? 8 : 0);
        rows[4].Height = new GridLength(!sideBySide && !collapsed ? 1 : 0, GridUnitType.Star);

        Grid.SetRow(RequestTabsHost, 2);
        Grid.SetColumn(RequestTabsHost, 0);
        Grid.SetColumnSpan(RequestTabsHost, sideBySide && !collapsed ? 1 : 3);
        Grid.SetRowSpan(RequestTabsHost, 1);

        Grid.SetRow(ResponsePane, sideBySide ? 2 : 4);
        Grid.SetColumn(ResponsePane, sideBySide ? 2 : 0);
        Grid.SetColumnSpan(ResponsePane, sideBySide && !collapsed ? 1 : 3);
        Grid.SetRowSpan(ResponsePane, 1);

        Grid.SetRow(RequestResponseSplitter, sideBySide ? 2 : 3);
        Grid.SetColumn(RequestResponseSplitter, sideBySide ? 1 : 0);
        Grid.SetColumnSpan(RequestResponseSplitter, sideBySide && !collapsed ? 1 : 3);
        Grid.SetRowSpan(RequestResponseSplitter, 1);

        RequestResponseSplitter.ResizeDirection = sideBySide
            ? GridResizeDirection.Columns
            : GridResizeDirection.Rows;
        RequestResponseSplitter.Width = sideBySide ? 8 : double.NaN;
        RequestResponseSplitter.Height = sideBySide ? double.NaN : 8;
        ResponsePane.IsVisible = !collapsed;
        RequestResponseSplitter.IsVisible = !collapsed;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (e.PropertyName == nameof(MainWindowViewModel.IsWorkspaceSideBySide))
        {
            ApplyWorkspaceLayout(_viewModel.IsWorkspaceSideBySide);
        }
        else if (
            e.PropertyName == nameof(MainWindowViewModel.IsManagementOpen)
            && _viewModel.IsManagementOpen
        )
        {
            EnsureManagementView();
        }
        else if (e.PropertyName is { } propertyName && AuxiliaryDialogFlags.Contains(propertyName))
        {
            EnsureAuxiliaryDialogs();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsResponseCollapsed))
        {
            ApplyWorkspaceLayout(_isSideBySide);
        }
        else if (
            e.PropertyName
            is nameof(MainWindowViewModel.SelectedRequest)
                or nameof(MainWindowViewModel.SelectedCollection)
        )
        {
            Dispatcher.UIThread.Post(ScrollSelectedTreeNodeIntoView, DispatcherPriority.Background);
        }
    }

    private void ScrollSelectedTreeNodeIntoView()
    {
        if (_viewModel is null)
        {
            return;
        }

        object? target = _viewModel.SelectedRequest ?? (object?)_viewModel.SelectedCollection;
        if (target is null)
        {
            return;
        }

        var node = _viewModel.TreeNodes.FirstOrDefault(candidate =>
            ReferenceEquals(candidate.Request, target)
            || ReferenceEquals(candidate.Collection, target)
        );
        if (node is null)
        {
            return;
        }

        if (FindTreeRow(node) is { } row)
        {
            row.BringIntoView();
            return;
        }

        var index = _viewModel.TreeNodes.IndexOf(node);
        if (index < 0)
        {
            return;
        }

        _viewModel.TreePage.EnsureIndex(index);

        // The new row may not be realized yet in the virtualizing panel. Scroll near its
        // estimated slot first, then bring the realized row into view on the next layout pass.
        var rowHeight = 30d;
        var offset = Math.Max(0, index * rowHeight - CollectionScrollArea.Bounds.Height / 2);
        CollectionScrollArea.ScrollToOffset(new Vector(0, offset));
        Dispatcher.UIThread.Post(
            () => FindTreeRow(node)?.BringIntoView(),
            DispatcherPriority.Background
        );
    }

    private Border? FindTreeRow(TreeDisplayNode node)
    {
        foreach (var visual in CollectionScrollArea.GetVisualDescendants())
        {
            if (
                visual is Border border
                && border.Classes.Contains("treeRow")
                && ReferenceEquals(border.DataContext, node)
            )
            {
                return border;
            }
        }

        return null;
    }

    private void SaveRequestResponseRatio()
    {
        if (_viewModel is null || _isSideBySide)
        {
            return;
        }

        var total =
            EditorResponseLayout.RowDefinitions[2].ActualHeight
            + EditorResponseLayout.RowDefinitions[4].ActualHeight;
        if (total <= 0)
        {
            return;
        }

        var ratio = Math.Clamp(
            EditorResponseLayout.RowDefinitions[2].ActualHeight / total,
            MinRequestPaneRatio,
            MaxRequestPaneRatio
        );
        if (Math.Abs(_viewModel.Settings.RequestPaneRatio - ratio) < 0.005)
        {
            return;
        }

        _viewModel.Settings.RequestPaneRatio = ratio;
    }

    private void OnSidebarSplitterDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        WorkspaceLayout.ColumnDefinitions[0].Width = new GridLength(DefaultSidebarWidth);
        _viewModel.Settings.TreePaneWidth = DefaultSidebarWidth;
        ApplySidebarLayout();
        e.Handled = true;
    }

    private void SaveSidebarWidth()
    {
        if (_viewModel is null)
        {
            return;
        }

        var width = WorkspaceLayout.ColumnDefinitions[0].ActualWidth;
        if (width <= 0)
        {
            return;
        }

        width = Math.Clamp(width, MinSidebarWidth, MaxSidebarWidth);
        if (Math.Abs(_viewModel.Settings.TreePaneWidth - width) < 0.5)
        {
            return;
        }

        _viewModel.Settings.TreePaneWidth = width;
    }
}
