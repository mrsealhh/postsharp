using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PostSharp.Models;
using PostSharp.Services;

namespace PostSharp.ViewModels;

public sealed record PipelineFileItem(string Name, string Path, DateTimeOffset Modified);

public sealed record PipelineTemplateItem(string Name, string Description, string Content);

/// <summary>
/// 自动化页的流水线中心：文件列表、YAML 编辑、校验/可视化/运行，以及场景模板。
/// 流水线以文件形式保存在数据目录 `pipelines` 下，可直接复制进仓库做 CI。
/// </summary>
public sealed partial class PipelineCenterViewModel : ObservableObject
{
    private const string DefaultBaseUrl = "http://127.0.0.1:5199";

    private readonly string _directory;

    public PipelineCenterViewModel(string? directory = null)
    {
        _directory = directory ?? Path.Combine(AppPaths.Root, "pipelines");
        Directory.CreateDirectory(_directory);
        Templates = new ObservableCollection<PipelineTemplateItem>(BuildTemplates());
        Refresh();
        if (Files.Count > 0)
        {
            SelectedFile = Files[0];
        }
        else
        {
            NameText = "新建流水线";
            Content = Templates[0].Content;
        }
    }

    public ObservableCollection<PipelineFileItem> Files { get; } = new();

    public ObservableCollection<PipelineTemplateItem> Templates { get; }

    public string DirectoryPath => _directory;

    [ObservableProperty]
    private PipelineFileItem? _selectedFile;

    [ObservableProperty]
    private string _nameText = string.Empty;

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private string _statusText = "选择左侧流水线，或从场景模板开始。";

    [ObservableProperty]
    private string _previewText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public bool HasSelection => SelectedFile is not null;

    public bool HasPreview => PreviewText.Length > 0;

    [RelayCommand]
    private void RefreshFiles()
    {
        Refresh();
        StatusText =
            Files.Count == 0
                ? "还没有流水线，点击「新建」或选择场景模板。"
                : $"已加载 {Files.Count} 个流水线。";
    }

    [RelayCommand]
    private void NewPipeline()
    {
        SelectedFile = null;
        NameText = $"流水线-{DateTime.Now:MMdd-HHmm}";
        Content = Templates[0].Content;
        PreviewText = string.Empty;
        StatusText = "已新建草稿，保存后写入 pipelines 目录。";
        OnPropertyChanged(nameof(HasSelection));
    }

    [RelayCommand]
    private void ApplyTemplate(PipelineTemplateItem? template)
    {
        if (template is null)
        {
            return;
        }

        SelectedFile = null;
        NameText = template.Name;
        Content = template.Content;
        PreviewText = string.Empty;
        StatusText = $"已载入场景模板：{template.Name}";
        OnPropertyChanged(nameof(HasSelection));
    }

    [RelayCommand]
    private void SavePipeline()
    {
        try
        {
            var name = Sanitize(NameText);
            if (name.Length == 0)
            {
                StatusText = "请先填写流水线名称。";
                return;
            }

            var path = Path.Combine(_directory, $"{name}.yaml");
            File.WriteAllText(path, Content, new UTF8Encoding(false));
            Refresh();
            SelectedFile = Files.FirstOrDefault(file =>
                string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase)
            );
            StatusText = $"已保存：{path}";
        }
        catch (Exception exception)
        {
            StatusText = $"保存失败：{exception.Message}";
        }
    }

    [RelayCommand]
    private async Task ValidatePipelineAsync(CancellationToken cancellationToken)
    {
        await EvaluateAsync(
            "校验",
            (pipeline, issues) =>
            {
                var errors = issues.Count(issue => issue.IsError);
                var warnings = issues.Count - errors;
                var builder = new StringBuilder();
                builder
                    .Append(pipeline.Name)
                    .Append("：")
                    .Append(errors == 0 ? "通过" : $"{errors} 个错误")
                    .Append(warnings > 0 ? $"，{warnings} 条提醒" : string.Empty)
                    .AppendLine();
                foreach (var issue in issues.Take(20))
                {
                    builder
                        .Append(issue.IsError ? "[错误] " : "[提醒] ")
                        .Append(issue.Path)
                        .Append(' ')
                        .AppendLine(issue.Message);
                }

                return builder.ToString();
            },
            cancellationToken
        );
    }

    [RelayCommand]
    private async Task VisualizePipelineAsync(CancellationToken cancellationToken)
    {
        await EvaluateAsync(
            "生成流程图",
            (pipeline, issues) => PipelineVisualization.Build(pipeline, issues).Mermaid,
            cancellationToken
        );
    }

    [RelayCommand]
    private async Task RunPipelineAsync(CancellationToken cancellationToken)
    {
        var parsed = PipelineParser.Parse(Content);
        var errors = parsed.Issues.Where(issue => issue.IsError).ToList();
        if (errors.Count > 0)
        {
            StatusText = "流水线有错误，先修正再运行。";
            PreviewText = string.Join(
                Environment.NewLine,
                errors.Take(20).Select(issue => $"{issue.Path} {issue.Message}")
            );
            OnPropertyChanged(nameof(HasPreview));
            return;
        }

        IsBusy = true;
        StatusText = "正在运行…";
        try
        {
            var runner = new PipelineRunner();
            var result = await runner.RunAsync(
                parsed.Pipeline,
                new PipelineRunOptions(),
                cancellationToken
            );
            var builder = new StringBuilder();
            builder
                .Append(result.Passed ? "通过" : "失败")
                .Append("（")
                .Append(result.ElapsedMs)
                .AppendLine(" ms）");
            foreach (var item in result.Cases)
            {
                builder.Append(item.Label).Append("：").AppendLine(item.Passed ? "通过" : "失败");
                foreach (var step in item.Steps)
                {
                    builder
                        .Append("  ")
                        .Append(step.Passed ? "✓ " : "✗ ")
                        .Append(step.Method)
                        .Append(' ')
                        .Append(step.Url)
                        .Append(" → ")
                        .Append(step.StatusCode?.ToString() ?? "-");
                    if (!string.IsNullOrWhiteSpace(step.Error))
                    {
                        builder.Append("  ").Append(step.Error);
                    }

                    builder.AppendLine();
                }
            }

            PreviewText = builder.ToString();
            StatusText = result.Passed ? "运行通过。" : "运行失败，详见下方结果。";
        }
        catch (Exception exception)
        {
            StatusText = $"运行失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasPreview));
        }
    }

    partial void OnSelectedFileChanged(PipelineFileItem? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        if (value is null)
        {
            return;
        }

        try
        {
            NameText = value.Name;
            Content = File.ReadAllText(value.Path);
            PreviewText = string.Empty;
            StatusText = $"已打开 {value.Name}";
            OnPropertyChanged(nameof(HasPreview));
        }
        catch (Exception exception)
        {
            StatusText = $"读取失败：{exception.Message}";
        }
    }

    private async Task EvaluateAsync(
        string action,
        Func<AutomationPipeline, IReadOnlyList<PipelineIssue>, string> render,
        CancellationToken cancellationToken
    )
    {
        IsBusy = true;
        StatusText = $"正在{action}…";
        try
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            var parsed = PipelineParser.Parse(Content);
            PreviewText = render(parsed.Pipeline, parsed.Issues);
            StatusText = parsed.HasErrors ? $"{action}完成，存在错误。" : $"{action}完成。";
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消。";
        }
        catch (Exception exception)
        {
            StatusText = $"{action}失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasPreview));
        }
    }

    private void Refresh()
    {
        Files.Clear();
        if (Directory.Exists(_directory))
        {
            foreach (
                var path in Directory
                    .EnumerateFiles(_directory, "*.yaml")
                    .Concat(Directory.EnumerateFiles(_directory, "*.yml"))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
            )
            {
                Files.Add(
                    new PipelineFileItem(
                        Path.GetFileNameWithoutExtension(path),
                        path,
                        File.GetLastWriteTime(path)
                    )
                );
            }
        }

        OnPropertyChanged(nameof(HasSelection));
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var character in name.Trim())
        {
            builder.Append(invalid.Contains(character) ? '-' : character);
        }

        return builder.ToString().Trim('-', ' ');
    }

    private static IReadOnlyList<PipelineTemplateItem> BuildTemplates() =>
        new[]
        {
            new PipelineTemplateItem(
                "链式接口",
                "创建 → 取字段 → 查询 → 更新，覆盖 ${步骤别名.字段} 的核心用法。",
                $$"""
                # 链式接口：后一步用 {{'$'}}{respA.id} 引用前一步响应。
                # 改成自己的 baseUrl 后运行；示例默认指向内置 Mock。
                name: 链式接口
                baseUrl: {{DefaultBaseUrl}}
                steps:
                  - id: respA
                    name: 创建订单
                    method: POST
                    url: /api/orders
                    json: { item: book, qty: 2 }
                    expect:
                      status: 201
                      jsonPath:
                        id: { exists: true }
                  - id: respB
                    name: 查询用户
                    method: GET
                    url: /api/users/1
                    expect: { status: 200 }
                  - name: 更新订单
                    method: PATCH
                    url: /api/orders/{{'$'}}{respA.id}
                    json:
                      param1: {{'$'}}{respB.id}
                    expect: { status: 200 }
                """
            ),
            new PipelineTemplateItem(
                "并发检查",
                "登录/准备后并发请求多个只读接口，验证 max 并发度。",
                $$"""
                name: 并发检查
                baseUrl: {{DefaultBaseUrl}}
                steps:
                  - name: 准备
                    method: POST
                    url: /api/orders
                    json: { item: demo }
                    expect: { status: 201 }
                  - parallel:
                      max: 3
                      steps:
                        - { method: GET, url: /api/health }
                        - { method: GET, url: /api/users }
                        - { method: GET, url: /api/users/1 }
                """
            ),
            new PipelineTemplateItem(
                "数据矩阵",
                "每行数据跑一遍整条流水线，适合多账号/多租户回归。",
                $$"""
                name: 数据矩阵
                baseUrl: {{DefaultBaseUrl}}
                data:
                  rows:
                    - { item: book, qty: 2 }
                    - { item: pen, qty: 5 }
                steps:
                  - method: POST
                    url: /api/orders
                    json:
                      item: "{{'$'}}{item}"
                      qty: "{{'$'}}{qty}"
                    expect: { status: 201 }
                """
            ),
            new PipelineTemplateItem(
                "CI 冒烟",
                "健康检查 + JSON 断言，适合放进 GitHub Actions。",
                $$"""
                name: CI 冒烟
                baseUrl: {{DefaultBaseUrl}}
                steps:
                  - method: GET
                    url: /api/health
                    expect:
                      status: 200
                  - method: GET
                    url: /api/users/1
                    expect:
                      status: 200
                      jsonPath:
                        id: { equals: 1 }
                """
            ),
        };
}
