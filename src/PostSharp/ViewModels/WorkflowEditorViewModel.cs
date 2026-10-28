using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PostSharp.Models;
using PostSharp.Services;
using TKCode.Arc;

namespace PostSharp.ViewModels;

public sealed record WorkflowSuiteResult(TestSuite Suite);

/// <summary>
/// The single suite editor: a collection browser, a canvas/list of steps and a node inspector. Both structure
/// views and the inspector edit the same <see cref="TestStep"/> objects.
/// </summary>
public sealed partial class WorkflowEditorViewModel : ObservableObject
{
    private readonly Dictionary<Guid, SuiteBuilderStep> _steps = new();
    private readonly Dictionary<Guid, SuiteBuilderStep> _detachedSteps = new();
    private readonly Dictionary<string, WorkflowNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Source, string Target), SuiteWorkflowEdge> _edgeModels =
        new();
    private readonly List<TestRunResult> _completedRuns = new();
    private readonly IReadOnlyList<RequestCollection> _collections;
    private readonly string _defaultName;
    private SuiteWorkflowEdge? _selectedEdgeModel;
    private bool _updatingGraph;

    public WorkflowEditorViewModel(IEnumerable<RequestCollection> collections, TestSuite? draft)
    {
        _collections = collections.ToList();
        Sources = new CollectionPickerViewModel();
        Sources.Load(_collections);
        Sources.SelectionChanged += OnSourceSelectionChanged;
        Steps.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSteps));
        ValidationIssues.CollectionChanged += (_, _) =>
            OnPropertyChanged(nameof(HasValidationIssues));

        if (draft is not null)
        {
            _defaultName = string.IsNullOrWhiteSpace(draft.Name) ? "套件" : draft.Name;
            LoadDraft(draft);
        }
        else
        {
            _defaultName = "套件";
            SuiteName = "套件";
            ScheduleMinutesText = "0";
            StopOnError = true;
            MaxParallelismText = "4";
        }

        UpdateDependencies();
        RefreshStatus();
    }

    public CollectionPickerViewModel Sources { get; }

    public WorkflowGraph Graph { get; } = new();

    public EnvironmentModel? ValidationEnvironment { get; init; }

    public ObservableCollection<SuiteValidationIssue> ValidationIssues { get; } = new();

    [ObservableProperty]
    private bool _isValidationOpen;

    [ObservableProperty]
    private string _validationSummary = string.Empty;

    public bool HasValidationIssues => ValidationIssues.Count > 0;

    public Func<
        TestSuite,
        Action<SuiteWorkflowProgress>?,
        CancellationToken,
        Task<TestRunResult>
    >? SuiteRunner { get; init; }

    public Func<TestRunResult, Task>? RunRecorder { get; init; }

    public event EventHandler? ContentChanged;

    public ObservableCollection<SuiteBuilderStep> Steps { get; } = new();

    public ObservableCollection<InputMappingEditor> MappingEditors { get; } = new();

    public ObservableCollection<AssertionEditor> AssertionEditors { get; } = new();

    public ObservableCollection<ConditionRuleEditor> ConditionEditors { get; } = new();

    public ObservableCollection<ConditionRuleEditor> EdgeRuleEditors { get; } = new();

    public IReadOnlyList<SuiteChoice> BranchConditions { get; } =
        new[]
        {
            new SuiteChoice("Always", "总是执行"),
            new SuiteChoice("OnSuccess", "上游成功"),
            new SuiteChoice("OnFailure", "上游失败"),
            new SuiteChoice("OnSkipped", "上游跳过"),
            new SuiteChoice("Expression", "条件表达式"),
        };

    public IReadOnlyList<SuiteChoice> ConditionChoices { get; } =
        new[]
        {
            new SuiteChoice("Always", "总是执行"),
            new SuiteChoice("OnPreviousSuccess", "上一步成功"),
            new SuiteChoice("OnPreviousFailure", "上一步失败"),
            new SuiteChoice("Expression", "条件表达式"),
        };

    public IReadOnlyList<SuiteChoice> JoinModeChoices { get; } =
        new[] { new SuiteChoice("All", "全部上游完成"), new SuiteChoice("Any", "任一上游到达") };

    public IReadOnlyList<SuiteChoice> RuleModeChoices { get; } =
        new[] { new SuiteChoice("All", "全部满足"), new SuiteChoice("Any", "任一满足") };

    [ObservableProperty]
    private string _suiteName = "套件";

    [ObservableProperty]
    private string _scheduleMinutesText = "0";

    [ObservableProperty]
    private bool _stopOnError = true;

    [ObservableProperty]
    private string _maxParallelismText = "4";

    [ObservableProperty]
    private string _statusText = "勾选左侧集合或接口，把它们加入画布";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunButtonText))]
    private bool _isSuiteRunning;

    public string RunButtonText => IsSuiteRunning ? "运行中…" : "运行套件";

    private CancellationTokenSource? _runCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNodeSelected))]
    [NotifyPropertyChangedFor(nameof(HasNoSelection))]
    private SuiteBuilderStep? _selectedStep;

    [ObservableProperty]
    private SuiteChoice _selectedJoinModeChoice = new("All", "全部上游完成");

    [ObservableProperty]
    private SuiteChoice _selectedConditionChoice = new("Always", "总是执行");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoSelection))]
    private bool _isEdgeSelected;

    [ObservableProperty]
    private string _selectedEdgeTitle = string.Empty;

    [ObservableProperty]
    private SuiteChoice _selectedEdgeCondition = new("Always", "总是执行");

    [ObservableProperty]
    private string _edgeRuleMode = "All";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExpressionBranch))]
    private string _edgeConditionValue = "Always";

    [ObservableProperty]
    private bool _isLeftPaneCollapsed;

    [ObservableProperty]
    private bool _isRightPaneCollapsed;

    [ObservableProperty]
    private bool _isMappingEditorOpen;

    [ObservableProperty]
    private string _mappingTitle = string.Empty;

    [ObservableProperty]
    private string _mappingHint = string.Empty;

    [ObservableProperty]
    private bool _isMappingEmpty;

    [ObservableProperty]
    private bool _isAssertionEditorOpen;

    [ObservableProperty]
    private string _assertionTitle = string.Empty;

    [ObservableProperty]
    private bool _isConditionEditorOpen;

    [ObservableProperty]
    private string _conditionTitle = string.Empty;

    [ObservableProperty]
    private string _conditionMode = "All";

    [ObservableProperty]
    private SuiteChoice _selectedEdgeRuleModeChoice = new("All", "全部满足");

    [ObservableProperty]
    private SuiteChoice _selectedConditionModeChoice = new("All", "全部满足");

    public bool HasSteps => Steps.Count > 0;

    public bool IsNodeSelected => SelectedStep is not null;

    public bool IsExpressionBranch => EdgeConditionValue == "Expression";

    public bool HasNoSelection => !IsNodeSelected && !IsEdgeSelected;

    public void AddDroppedSource(SuiteSourceDragData data, Point position)
    {
        if (data.Collection is not { } collection && data.Request is null)
        {
            return;
        }

        _updatingGraph = true;
        try
        {
            if (data.Collection is { } droppedCollection)
            {
                var requests = FlattenRequests(droppedCollection).ToList();
                WorkflowNode? previous = null;
                for (var index = 0; index < requests.Count; index++)
                {
                    var wrapper = GetOrCreateStep(requests[index]);
                    var nodeId = wrapper.Step.Id.ToString();
                    var node =
                        _nodes.GetValueOrDefault(nodeId)
                        ?? CreateNode(
                            wrapper,
                            new Point(position.X, position.Y + index * 110),
                            raiseEdited: false
                        );
                    if (previous is not null && previous.Id != node.Id)
                    {
                        EnsureEdge(previous.Id, node.Id);
                    }

                    previous = node;
                }

                StatusText = $"已拖入集合：{droppedCollection.Name}（{requests.Count} 个请求）";
            }
            else if (data.Request is { } droppedRequest)
            {
                var wrapper = GetOrCreateStep(droppedRequest);
                var nodeId = wrapper.Step.Id.ToString();
                var node = _nodes.GetValueOrDefault(nodeId);
                if (node is null)
                {
                    node = CreateNode(wrapper, position, raiseEdited: false);
                }
                else
                {
                    node.Position = position;
                }

                StatusText = $"已拖入接口：{droppedRequest.Name}";
            }

            Sources.SetSelection(
                Steps.Select(wrapper => wrapper.Step.RequestId).Where(id => id != Guid.Empty),
                raiseChange: false
            );
            UpdateDependencies();
            RefreshStatus();
            ContentChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _updatingGraph = false;
        }
    }

    public void SetSelection(IReadOnlyList<WorkflowNode> nodes, IReadOnlyList<WorkflowEdge> edges)
    {
        SaveEdgeEditors();
        if (edges.Count > 0)
        {
            SelectEdge(edges[0]);
            return;
        }

        if (nodes.Count == 1 && Guid.TryParse(nodes[0].Id, out var stepId))
        {
            _selectedEdgeModel = null;
            EdgeRuleEditors.Clear();
            IsEdgeSelected = false;
            SelectedEdgeTitle = string.Empty;
            SelectedStep = _steps.GetValueOrDefault(stepId);
            return;
        }

        _selectedEdgeModel = null;
        EdgeRuleEditors.Clear();
        IsEdgeSelected = false;
        SelectedStep = null;
    }

    public void Clear()
    {
        _updatingGraph = true;
        try
        {
            Graph.Nodes.Clear();
            Graph.Edges.Clear();
            _nodes.Clear();
            _edgeModels.Clear();
            Steps.Clear();
            _steps.Clear();
            _detachedSteps.Clear();
            Sources.ClearSelectionCommand.Execute(null);
            _selectedEdgeModel = null;
            EdgeRuleEditors.Clear();
            SelectedStep = null;
            IsEdgeSelected = false;
            SelectedEdgeTitle = string.Empty;
        }
        finally
        {
            _updatingGraph = false;
        }

        StatusText = "画布已清空";
    }

    public void SyncGraphEdits()
    {
        if (_updatingGraph)
        {
            return;
        }

        _updatingGraph = true;
        foreach (var node in Graph.Nodes)
        {
            if (
                Guid.TryParse(node.Id, out var stepId)
                && !_steps.ContainsKey(stepId)
                && _detachedSteps.Remove(stepId, out var detached)
            )
            {
                _steps[stepId] = detached;
                _nodes[node.Id] = node;
                Steps.Add(detached);
            }
        }

        foreach (
            var node in Graph
                .Nodes.Where(node =>
                    !Guid.TryParse(node.Id, out var stepId) || !_steps.ContainsKey(stepId)
                )
                .ToList()
        )
        {
            foreach (
                var edge in Graph
                    .Edges.Where(edge => edge.Source.Id == node.Id || edge.Target.Id == node.Id)
                    .ToList()
            )
            {
                Graph.Edges.Remove(edge);
                _edgeModels.Remove((edge.Source.Id, edge.Target.Id));
            }

            Graph.Nodes.Remove(node);
            _nodes.Remove(node.Id);
        }

        var liveNodeIds = Graph.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var stepIds = Graph
            .Nodes.Select(node => Guid.TryParse(node.Id, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        foreach (var stepId in _steps.Keys.Where(id => !stepIds.Contains(id)).ToList())
        {
            if (_steps.Remove(stepId, out var wrapper))
            {
                Steps.Remove(wrapper);
                _detachedSteps[stepId] = wrapper;
            }
        }

        foreach (var nodeId in _nodes.Keys.Where(id => !liveNodeIds.Contains(id)).ToList())
        {
            _nodes.Remove(nodeId);
        }

        var live = Graph
            .Edges.Select(edge => (Source: edge.Source.Id, Target: edge.Target.Id))
            .ToHashSet();
        foreach (var key in _edgeModels.Keys.Where(key => !live.Contains(key)).ToList())
        {
            _edgeModels.Remove(key);
        }

        foreach (var edge in Graph.Edges)
        {
            EnsureEdgeModel(edge.Source.Id, edge.Target.Id);
        }

        if (_selectedEdgeModel is not null && !_edgeModels.ContainsValue(_selectedEdgeModel))
        {
            _selectedEdgeModel = null;
            EdgeRuleEditors.Clear();
            IsEdgeSelected = false;
            SelectedEdgeTitle = string.Empty;
        }

        if (SelectedStep is not null && !Steps.Contains(SelectedStep))
        {
            SelectedStep = null;
        }

        SyncPickerFromSteps();
        UpdateDependencies();
        RefreshStatus();
        _updatingGraph = false;
    }

    public WorkflowSuiteResult? BuildSuite()
    {
        SaveEdgeEditors();
        ApplyStepEditors();
        var ordered = TopologicalSteps();
        if (ordered.Count == 0)
        {
            StatusText = "至少加入一个接口后才能保存套件";
            return null;
        }

        var suite = new TestSuite
        {
            Name = string.IsNullOrWhiteSpace(SuiteName) ? _defaultName : SuiteName.Trim(),
            StopOnError = StopOnError,
            RunInParallel = false,
            ScheduleMinutes = ParseInt(ScheduleMinutesText, 0, 0, 1440),
            MaxParallelism = ParseInt(MaxParallelismText, 4, 0, 64),
        };
        foreach (var step in ordered)
        {
            suite.Steps.Add(step.Step);
        }

        suite.Workflow = CaptureWorkflow();
        foreach (var run in _completedRuns.Take(TestSuite.RunHistoryLimit))
        {
            run.SuiteId = suite.Id;
            run.SuiteName = suite.Name;
            suite.Runs.Add(run);
        }

        var validation = ValidateSuite(suite);
        if (validation.HasErrors)
        {
            StatusText = $"{validation.Summary}，保存和运行已阻止";
            return null;
        }

        StatusText =
            validation.WarningCount > 0
                ? $"已保存 {suite.Steps.Count} 个步骤 · {validation.Summary}"
                : $"已保存 {suite.Steps.Count} 个步骤";
        return new WorkflowSuiteResult(suite);
    }

    [RelayCommand]
    private void ValidateWorkflow()
    {
        if (BuildSuite() is null)
        {
            return;
        }

        StatusText = ValidationSummary;
        IsValidationOpen = HasValidationIssues;
    }

    [RelayCommand]
    private void CloseValidation() => IsValidationOpen = false;

    private SuiteValidationResult ValidateSuite(TestSuite suite)
    {
        var validation = SuitePreflight.Validate(suite, _collections, ValidationEnvironment);
        ValidationIssues.Clear();
        foreach (var issue in validation.Issues)
        {
            issue.LocateCommand = new RelayCommand(() => LocateValidationIssue(issue));
            ValidationIssues.Add(issue);
        }

        ValidationSummary = validation.Summary;
        IsValidationOpen = validation.HasErrors;
        return validation;
    }

    private void LocateValidationIssue(SuiteValidationIssue issue)
    {
        if (issue.StepId is { } stepId && _steps.TryGetValue(stepId, out var step))
        {
            SelectedStep = step;
        }

        IsValidationOpen = false;
    }

    [RelayCommand]
    private async Task RunWorkflowAsync()
    {
        if (IsSuiteRunning)
        {
            return;
        }

        if (SuiteRunner is null)
        {
            StatusText = "当前窗口没有接入运行器，无法运行套件";
            return;
        }

        if (BuildSuite() is not { } built)
        {
            return;
        }

        ResetRunVisuals();
        using var cancellation = new CancellationTokenSource();
        _runCts = cancellation;
        IsSuiteRunning = true;
        StatusText = "运行中：等待步骤开始";
        try
        {
            var result = await SuiteRunner(built.Suite, OnWorkflowProgress, cancellation.Token);
            if (result.Error is null)
            {
                _completedRuns.Insert(0, result);
                while (_completedRuns.Count > TestSuite.RunHistoryLimit)
                {
                    _completedRuns.RemoveAt(_completedRuns.Count - 1);
                }
            }

            if (RunRecorder is not null)
            {
                try
                {
                    await RunRecorder(result);
                }
                catch (Exception exception)
                {
                    Log.Error(
                        exception,
                        "保存套件编辑器运行历史失败 {SuiteName}",
                        built.Suite.Name
                    );
                }
            }

            StatusText = result.Cancelled
                ? $"运行已取消：{result.Outcome}"
                : $"运行完成：{result.Outcome}";
        }
        catch (Exception exception)
        {
            Log.Error(exception, "套件编辑器运行失败 {SuiteName}", built.Suite.Name);
            StatusText = "运行失败，请查看日志后重试";
        }
        finally
        {
            _runCts = null;
            IsSuiteRunning = false;
        }
    }

    [RelayCommand]
    private void CancelRun() => _runCts?.Cancel();

    private void ResetRunVisuals()
    {
        foreach (var node in Graph.Nodes)
        {
            node.State = WorkflowNodeState.Pending;
            node.Duration = null;
            node.Result = null;
            node.Error = null;
        }

        foreach (var edge in Graph.Edges)
        {
            edge.State = WorkflowEdgeState.Idle;
        }
    }

    private void OnWorkflowProgress(SuiteWorkflowProgress progress) =>
        Dispatcher.UIThread.Post(() => ApplyWorkflowProgress(progress));

    private void ApplyWorkflowProgress(SuiteWorkflowProgress progress)
    {
        if (progress.Kind == SuiteWorkflowProgressKind.EdgeResolved)
        {
            if (
                progress.SourceStepId is not { } sourceStepId
                || progress.TargetStepId is not { } targetStepId
            )
            {
                return;
            }

            var edge = Graph.Edges.FirstOrDefault(candidate =>
                candidate.Source.Id == sourceStepId.ToString()
                && candidate.Target.Id == targetStepId.ToString()
            );
            if (edge is not null)
            {
                edge.State =
                    progress.EdgeActive == true
                        ? WorkflowEdgeState.Active
                        : WorkflowEdgeState.Skipped;
            }

            return;
        }

        if (progress.StepId is not { } stepId)
        {
            return;
        }

        var node = Graph.FindNode(stepId.ToString());
        if (node is null)
        {
            return;
        }

        switch (progress.Kind)
        {
            case SuiteWorkflowProgressKind.StepStarted:
                node.State = WorkflowNodeState.Running;
                node.Duration = null;
                node.Result = null;
                node.Error = null;
                break;
            case SuiteWorkflowProgressKind.StepCompleted:
                node.State = WorkflowNodeState.Done;
                node.Duration = TimeSpan.FromMilliseconds(progress.ElapsedMs);
                node.Result = progress.Detail;
                node.Error = null;
                break;
            case SuiteWorkflowProgressKind.StepFailed:
                node.State = WorkflowNodeState.Failed;
                node.Duration = TimeSpan.FromMilliseconds(progress.ElapsedMs);
                node.Result = null;
                node.Error = progress.Detail ?? "步骤执行失败";
                break;
            case SuiteWorkflowProgressKind.StepSkipped:
            case SuiteWorkflowProgressKind.StepNotRun:
                node.State = WorkflowNodeState.Skipped;
                node.Duration = null;
                node.Result = null;
                node.Error = progress.Detail;
                foreach (var incoming in Graph.Edges.Where(edge => edge.Target.Id == node.Id))
                {
                    incoming.State = WorkflowEdgeState.Skipped;
                }

                break;
        }

        var finished = Graph.Nodes.Count(candidate =>
            candidate.State
                is WorkflowNodeState.Done
                    or WorkflowNodeState.Failed
                    or WorkflowNodeState.Skipped
        );
        var failed = Graph.Nodes.Count(candidate => candidate.State == WorkflowNodeState.Failed);
        if (IsSuiteRunning)
        {
            StatusText =
                failed > 0
                    ? $"运行中：{finished}/{Graph.Nodes.Count} 已结束 · {failed} 失败"
                    : $"运行中：{finished}/{Graph.Nodes.Count} 已结束";
        }
    }

    [RelayCommand]
    private void ShowMappings()
    {
        if (SelectedStep is null)
        {
            return;
        }

        var options = BuildMappingOptions();
        IsMappingEmpty = options.Count == 0;
        MappingHint = IsMappingEmpty
            ? "当前节点还没有上游步骤。请先在画布上把上游节点连到它，或先加入一个上游接口，再配置规则映射。"
            : "把上游步骤响应中的字段映射到当前请求的请求体、请求头、查询参数或 URL 占位符。";
        MappingEditors.Clear();
        if (!IsMappingEmpty)
        {
            foreach (var mapping in SelectedStep.Step.InputMappings)
            {
                MappingEditors.Add(CreateMappingEditor(mapping, options));
            }

            if (MappingEditors.Count == 0)
            {
                MappingEditors.Add(CreateMappingEditor(new InputMapping(), options));
            }
        }

        MappingTitle = $"规则映射 · {SelectedStep.RequestName}";
        IsMappingEditorOpen = true;
    }

    [RelayCommand]
    private void ShowAssertions()
    {
        if (SelectedStep is null)
        {
            return;
        }

        AssertionEditors.Clear();
        foreach (var assertion in SelectedStep.Step.Assertions)
        {
            AssertionEditors.Add(CreateAssertionEditor(assertion));
        }

        if (AssertionEditors.Count == 0)
        {
            AssertionEditors.Add(CreateAssertionEditor(new TestAssertion()));
        }

        AssertionTitle = $"规则断言 · {SelectedStep.RequestName}";
        IsAssertionEditorOpen = true;
    }

    [RelayCommand]
    private void ShowConditions()
    {
        if (SelectedStep is null)
        {
            return;
        }

        ConditionEditors.Clear();
        foreach (var rule in SelectedStep.Step.ConditionRules.Rules)
        {
            ConditionEditors.Add(CreateConditionEditor(rule));
        }

        if (ConditionEditors.Count == 0)
        {
            ConditionEditors.Add(CreateConditionEditor(new SuiteConditionRule()));
        }

        ConditionMode = SelectedStep.Step.ConditionRules.Mode;
        SelectedConditionModeChoice =
            RuleModeChoices.FirstOrDefault(choice => choice.Value == ConditionMode)
            ?? RuleModeChoices[0];
        ConditionTitle = $"条件规则 · {SelectedStep.RequestName}";
        IsConditionEditorOpen = true;
    }

    [RelayCommand]
    private void AddMappingRule()
    {
        if (!IsMappingEmpty)
        {
            MappingEditors.Add(CreateMappingEditor(new InputMapping(), BuildMappingOptions()));
        }
    }

    [RelayCommand]
    private void SaveMappings()
    {
        if (SelectedStep is null)
        {
            return;
        }

        foreach (var editor in MappingEditors)
        {
            if (editor.SelectedSource is null)
            {
                editor.Error = "请选择来源步骤";
                return;
            }

            if (string.IsNullOrWhiteSpace(editor.TargetPath))
            {
                editor.Error = "请填写目标字段";
                return;
            }
        }

        SelectedStep.Step.InputMappings.Clear();
        foreach (var editor in MappingEditors)
        {
            SelectedStep.Step.InputMappings.Add(
                new InputMapping
                {
                    SourceStepId = editor.SelectedSource?.Id ?? Guid.Empty,
                    SourcePath = editor.SourcePath.Trim(),
                    TargetKind = editor.TargetKind,
                    TargetPath = editor.TargetPath.Trim(),
                    Template = string.IsNullOrWhiteSpace(editor.Template)
                        ? "{value}"
                        : editor.Template,
                }
            );
        }

        SelectedStep.RefreshMappingLabel();
        IsMappingEditorOpen = false;
    }

    [RelayCommand]
    private void CancelMappings() => IsMappingEditorOpen = false;

    [RelayCommand]
    private void AddAssertionRule() =>
        AssertionEditors.Add(CreateAssertionEditor(new TestAssertion()));

    [RelayCommand]
    private void SaveAssertions()
    {
        if (SelectedStep is null)
        {
            return;
        }

        foreach (var editor in AssertionEditors)
        {
            if (string.IsNullOrWhiteSpace(editor.Path))
            {
                editor.Error = editor.Kind == "Header" ? "请填写响应头名称" : "请填写 JSONPath";
                return;
            }

            if (string.IsNullOrWhiteSpace(editor.Expected))
            {
                editor.Error = "请填写期望值";
                return;
            }
        }

        SelectedStep.Step.Assertions.Clear();
        foreach (var editor in AssertionEditors)
        {
            SelectedStep.Step.Assertions.Add(
                new TestAssertion
                {
                    Kind = editor.Kind == "Header" ? "Header" : "JSONPath",
                    Path = editor.Path.Trim(),
                    Expected = editor.Expected.Trim(),
                }
            );
        }

        SelectedStep.RefreshAssertionLabel();
        IsAssertionEditorOpen = false;
    }

    [RelayCommand]
    private void CancelAssertions() => IsAssertionEditorOpen = false;

    [RelayCommand]
    private void AddConditionRule() =>
        ConditionEditors.Add(CreateConditionEditor(new SuiteConditionRule()));

    [RelayCommand]
    private void SaveConditions()
    {
        if (SelectedStep is null)
        {
            return;
        }

        foreach (var editor in ConditionEditors)
        {
            if (editor.NeedsPath && string.IsNullOrWhiteSpace(editor.Path))
            {
                editor.Error = "请填写字段或变量名";
                return;
            }

            if (editor.NeedsExpected && string.IsNullOrWhiteSpace(editor.Expected))
            {
                editor.Error = "请填写期望值";
                return;
            }
        }

        SelectedStep.Step.ConditionRules.Mode = ConditionMode;
        SelectedStep.Step.ConditionRules.Rules.Clear();
        foreach (var editor in ConditionEditors)
        {
            SelectedStep.Step.ConditionRules.Rules.Add(editor.ToModel());
        }

        if (SelectedStep.Step.ConditionRules.Rules.Count > 0)
        {
            SelectedStep.Condition = "Expression";
            SelectedStep.Step.Condition = "Expression";
        }
        else if (SelectedStep.Condition == "Expression")
        {
            SelectedStep.Condition = "Always";
            SelectedStep.Step.Condition = "Always";
        }

        SelectedConditionChoice =
            ConditionChoices.FirstOrDefault(choice => choice.Value == SelectedStep.Condition)
            ?? ConditionChoices[0];
        SelectedStep.RefreshConditionLabel();
        IsConditionEditorOpen = false;
    }

    [RelayCommand]
    private void CancelConditions() => IsConditionEditorOpen = false;

    [RelayCommand]
    private void AddEdgeRule()
    {
        var editor = new ConditionRuleEditor();
        editor.RemoveCommand = new RelayCommand(() => EdgeRuleEditors.Remove(editor));
        EdgeRuleEditors.Add(editor);
    }

    [RelayCommand]
    private void RemoveSelectedEdge()
    {
        if (_selectedEdgeModel is null)
        {
            return;
        }

        _updatingGraph = true;
        var edge = Graph.Edges.FirstOrDefault(candidate =>
            candidate.Source.Id == _selectedEdgeModel.SourceId
            && candidate.Target.Id == _selectedEdgeModel.TargetId
        );
        if (edge is not null)
        {
            Graph.Edges.Remove(edge);
        }

        _edgeModels.Remove((_selectedEdgeModel.SourceId, _selectedEdgeModel.TargetId));
        _updatingGraph = false;
        _selectedEdgeModel = null;
        EdgeRuleEditors.Clear();
        IsEdgeSelected = false;
        SelectedEdgeTitle = string.Empty;
        UpdateDependencies();
        RefreshStatus();
    }

    partial void OnSelectedEdgeConditionChanged(SuiteChoice value)
    {
        EdgeConditionValue = value.Value;
    }

    partial void OnSelectedStepChanged(SuiteBuilderStep? value)
    {
        foreach (var step in Steps)
        {
            step.IsSelected = ReferenceEquals(step, value);
        }

        if (value is null)
        {
            return;
        }

        SelectedConditionChoice =
            ConditionChoices.FirstOrDefault(choice => choice.Value == value.Condition)
            ?? ConditionChoices[0];
        SelectedJoinModeChoice =
            JoinModeChoices.FirstOrDefault(choice => choice.Value == value.JoinMode)
            ?? JoinModeChoices[0];
    }

    partial void OnSelectedJoinModeChoiceChanged(SuiteChoice value)
    {
        if (SelectedStep is not null)
        {
            SelectedStep.JoinMode = value.Value;
        }
    }

    partial void OnSelectedConditionChoiceChanged(SuiteChoice value)
    {
        if (SelectedStep is not null)
        {
            SelectedStep.Condition = value.Value;
        }
    }

    partial void OnSelectedEdgeRuleModeChoiceChanged(SuiteChoice value) =>
        EdgeRuleMode = value.Value;

    partial void OnSelectedConditionModeChoiceChanged(SuiteChoice value) =>
        ConditionMode = value.Value;

    private void LoadDraft(TestSuite draft)
    {
        SuiteName = draft.Name;
        ScheduleMinutesText = draft.ScheduleMinutes.ToString();
        StopOnError = draft.StopOnError;
        MaxParallelismText = draft.MaxParallelism.ToString();

        foreach (var step in draft.Steps)
        {
            var wrapper = new SuiteBuilderStep
            {
                Step = step,
                DelayText = step.DelayMs.ToString(),
                RetryCountText = step.RetryCount.ToString(),
                RetryDelayText = step.RetryDelayMs.ToString(),
                Condition = step.Condition,
                ParallelGroupText = step.ParallelGroup.ToString(),
                JoinMode = string.IsNullOrWhiteSpace(step.JoinMode) ? "All" : step.JoinMode,
            };
            Steps.Add(wrapper);
            _steps[step.Id] = wrapper;
        }

        if (draft.Workflow is { HasStepGraph: true } workflow)
        {
            foreach (var saved in workflow.Nodes)
            {
                if (!_steps.TryGetValue(saved.StepId, out var wrapper))
                {
                    continue;
                }

                var node = CreateNode(wrapper, new Point(saved.X, saved.Y), raiseEdited: false);
                if (
                    double.IsNaN(saved.X)
                    || double.IsNaN(saved.Y)
                    || (saved.X == 0 && saved.Y == 0)
                )
                {
                    node.Position = new Point(80, 60 + (Graph.Nodes.Count - 1) * 110);
                }
            }

            foreach (var saved in workflow.Edges)
            {
                if (
                    !_nodes.TryGetValue(saved.SourceId, out var source)
                    || !_nodes.TryGetValue(saved.TargetId, out var target)
                )
                {
                    continue;
                }

                Graph.Connect(source, target);
                var model = new SuiteWorkflowEdge
                {
                    SourceId = saved.SourceId,
                    TargetId = saved.TargetId,
                    Condition = saved.Condition,
                };
                CopyCondition(saved.Rules, model.Rules);
                _edgeModels[(saved.SourceId, saved.TargetId)] = model;
            }

            foreach (
                var wrapper in Steps.Where(wrapper =>
                    !_nodes.ContainsKey(wrapper.Step.Id.ToString())
                )
            )
            {
                CreateNode(wrapper, new Point(80, 60 + Graph.Nodes.Count * 110), false);
            }
        }
        else
        {
            LayoutSequential();
        }

        Sources.SetSelection(
            draft.Steps.Select(step => step.RequestId).Where(id => id != Guid.Empty),
            raiseChange: false
        );
    }

    private void LayoutSequential()
    {
        WorkflowNode? previous = null;
        var index = 0;
        foreach (var wrapper in Steps)
        {
            var node = CreateNode(wrapper, new Point(80, 60 + index * 110));
            if (previous is not null)
            {
                Graph.Connect(previous, node);
                _edgeModels[(previous.Id, node.Id)] = new SuiteWorkflowEdge
                {
                    SourceId = previous.Id,
                    TargetId = node.Id,
                };
            }

            previous = node;
            index++;
        }
    }

    private void OnSourceSelectionChanged(object? sender, EventArgs e)
    {
        if (_updatingGraph)
        {
            return;
        }

        _updatingGraph = true;
        var selected = Sources.SelectedRequests;
        var selectedIds = selected.Select(request => request.Id).ToHashSet();

        foreach (var stepId in _steps.Keys.ToList())
        {
            var wrapper = _steps[stepId];
            if (selectedIds.Contains(wrapper.Step.RequestId))
            {
                continue;
            }

            RemoveStep(stepId);
        }

        WorkflowNode? previous = null;
        foreach (var request in selected)
        {
            var wrapper = GetOrCreateStep(request);
            var nodeId = wrapper.Step.Id.ToString();
            var node =
                _nodes.GetValueOrDefault(nodeId)
                ?? CreateNode(wrapper, new Point(80, 60 + Steps.Count * 110), raiseEdited: false);
            if (previous is not null && previous.Id != node.Id)
            {
                EnsureEdge(previous.Id, node.Id);
            }

            previous = node;
        }

        UpdateDependencies();
        RefreshStatus();
        _updatingGraph = false;
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    private SuiteBuilderStep GetOrCreateStep(RequestItem request)
    {
        var existing = _steps.Values.FirstOrDefault(candidate =>
            candidate.Step.RequestId == request.Id
        );
        if (existing is not null)
        {
            return existing;
        }

        var detached = _detachedSteps.Values.FirstOrDefault(candidate =>
            candidate.Step.RequestId == request.Id
        );
        if (detached is not null)
        {
            _detachedSteps.Remove(detached.Step.Id);
            _steps[detached.Step.Id] = detached;
            Steps.Add(detached);
            return detached;
        }

        var wrapper = new SuiteBuilderStep
        {
            Step = new TestStep
            {
                RequestId = request.Id,
                RequestName = request.Name,
                Method = request.Method,
                Url = request.Url,
                DelayMs = 200,
            },
            DelayText = "200",
        };
        _steps[wrapper.Step.Id] = wrapper;
        Steps.Add(wrapper);
        return wrapper;
    }

    private static IReadOnlyList<RequestItem> FlattenRequests(RequestCollection collection)
    {
        var requests = new List<RequestItem>();
        requests.AddRange(collection.Requests);
        foreach (var child in collection.Children)
        {
            requests.AddRange(FlattenRequests(child));
        }

        return requests;
    }

    private void RemoveStep(Guid stepId)
    {
        if (!_steps.Remove(stepId, out var wrapper))
        {
            return;
        }

        Steps.Remove(wrapper);
        _detachedSteps.Remove(stepId);
        if (SelectedStep == wrapper)
        {
            SelectedStep = null;
        }

        var nodeId = wrapper.Step.Id.ToString();
        if (_nodes.Remove(nodeId, out var node))
        {
            foreach (
                var edge in Graph
                    .Edges.Where(edge => edge.Source.Id == nodeId || edge.Target.Id == nodeId)
                    .ToList()
            )
            {
                Graph.Edges.Remove(edge);
                _edgeModels.Remove((edge.Source.Id, edge.Target.Id));
            }

            Graph.Nodes.Remove(node);
        }
    }

    private void SyncPickerFromSteps()
    {
        Sources.SetSelection(
            Steps.Select(wrapper => wrapper.Step.RequestId).Where(id => id != Guid.Empty),
            raiseChange: false
        );
    }

    private WorkflowNode CreateNode(
        SuiteBuilderStep wrapper,
        Point position,
        bool raiseEdited = true
    )
    {
        var id = wrapper.Step.Id.ToString();
        if (_nodes.TryGetValue(id, out var existing))
        {
            existing.Position = position;
            return existing;
        }

        var node = new WorkflowNode(id, wrapper.RequestName)
        {
            Kind = "接口",
            Description = $"{wrapper.Method} {wrapper.Url}",
            Position = position,
        };
        _nodes[id] = node;
        Graph.Nodes.Add(node);
        if (raiseEdited)
        {
            StatusText = $"已添加：{wrapper.RequestName}";
        }

        return node;
    }

    private void EnsureEdge(string sourceId, string targetId)
    {
        if (sourceId == targetId)
        {
            return;
        }

        if (Graph.Edges.Any(edge => edge.Source.Id == sourceId && edge.Target.Id == targetId))
        {
            return;
        }

        if (
            !_nodes.TryGetValue(sourceId, out var source)
            || !_nodes.TryGetValue(targetId, out var target)
        )
        {
            return;
        }

        Graph.Connect(source, target);
        EnsureEdgeModel(sourceId, targetId);
    }

    private SuiteWorkflowEdge EnsureEdgeModel(string sourceId, string targetId)
    {
        var key = (sourceId, targetId);
        if (_edgeModels.TryGetValue(key, out var model))
        {
            return model;
        }

        model = new SuiteWorkflowEdge { SourceId = sourceId, TargetId = targetId };
        _edgeModels[key] = model;
        return model;
    }

    private void SelectEdge(WorkflowEdge edge)
    {
        var model = EnsureEdgeModel(edge.Source.Id, edge.Target.Id);
        _selectedEdgeModel = model;
        SelectedStep = null;
        IsEdgeSelected = true;
        var conditionLabel =
            BranchConditions.FirstOrDefault(choice => choice.Value == model.Condition)?.Label
            ?? "总是执行";
        SelectedEdgeTitle = $"{edge.Source.Title} → {edge.Target.Title} · {conditionLabel}";
        SelectedEdgeCondition =
            BranchConditions.FirstOrDefault(choice => choice.Value == model.Condition)
            ?? BranchConditions[0];
        EdgeConditionValue = SelectedEdgeCondition.Value;
        EdgeRuleMode = model.Rules.Mode;
        SelectedEdgeRuleModeChoice =
            RuleModeChoices.FirstOrDefault(choice => choice.Value == EdgeRuleMode)
            ?? RuleModeChoices[0];
        EdgeRuleEditors.Clear();
        foreach (var rule in model.Rules.Rules)
        {
            var editor = ConditionRuleEditor.FromModel(rule);
            editor.RemoveCommand = new RelayCommand(() => EdgeRuleEditors.Remove(editor));
            EdgeRuleEditors.Add(editor);
        }

        if (model.Condition == "Expression" && EdgeRuleEditors.Count == 0)
        {
            AddEdgeRule();
        }
    }

    private void SaveEdgeEditors()
    {
        if (_selectedEdgeModel is null)
        {
            return;
        }

        _selectedEdgeModel.Condition = SelectedEdgeCondition.Value;
        _selectedEdgeModel.Rules.Mode = EdgeRuleMode;
        _selectedEdgeModel.Rules.Rules.Clear();
        foreach (var editor in EdgeRuleEditors)
        {
            _selectedEdgeModel.Rules.Rules.Add(editor.ToModel());
        }
    }

    private void ApplyStepEditors()
    {
        foreach (var wrapper in Steps)
        {
            wrapper.Step.DelayMs = ParseInt(wrapper.DelayText, 200, 0, 60_000);
            wrapper.Step.RetryCount = ParseInt(
                wrapper.RetryCountText,
                0,
                0,
                SuiteRetryPolicy.MaxRetries
            );
            wrapper.Step.RetryDelayMs = ParseInt(wrapper.RetryDelayText, 500, 0, 60_000);
            wrapper.Step.Condition = wrapper.Condition;
            wrapper.Step.JoinMode = wrapper.JoinMode;
            wrapper.Step.ParallelGroup = ParseInt(wrapper.ParallelGroupText, 0, 0, 9);
        }
    }

    private SuiteWorkflow CaptureWorkflow()
    {
        var workflow = new SuiteWorkflow();
        foreach (var wrapper in Steps)
        {
            var nodeId = wrapper.Step.Id.ToString();
            if (!_nodes.TryGetValue(nodeId, out var node))
            {
                continue;
            }

            workflow.Nodes.Add(
                new SuiteWorkflowNode
                {
                    Id = nodeId,
                    StepId = wrapper.Step.Id,
                    Kind = "step",
                    X = node.Position.X,
                    Y = node.Position.Y,
                    JoinMode = wrapper.JoinMode,
                }
            );
        }

        foreach (var edge in Graph.Edges)
        {
            var model = EnsureEdgeModel(edge.Source.Id, edge.Target.Id);
            var saved = new SuiteWorkflowEdge
            {
                SourceId = edge.Source.Id,
                TargetId = edge.Target.Id,
                Condition = model.Condition,
            };
            CopyCondition(model.Rules, saved.Rules);
            workflow.Edges.Add(saved);
        }

        return workflow;
    }

    private IReadOnlyList<SuiteBuilderStep> TopologicalSteps()
    {
        var byId = Steps.ToDictionary(wrapper => wrapper.Step.Id.ToString());
        var incoming = byId.Keys.ToDictionary(id => id, _ => 0);
        var outgoing = byId.Keys.ToDictionary(id => id, _ => new List<string>());
        foreach (var edge in Graph.Edges)
        {
            if (
                !byId.ContainsKey(edge.Source.Id)
                || !byId.ContainsKey(edge.Target.Id)
                || edge.Source.Id == edge.Target.Id
            )
            {
                continue;
            }

            outgoing[edge.Source.Id].Add(edge.Target.Id);
            incoming[edge.Target.Id]++;
        }

        var queue = Steps.Where(wrapper => incoming[wrapper.Step.Id.ToString()] == 0).ToList();
        var ordered = new List<SuiteBuilderStep>();
        while (queue.Count > 0)
        {
            var wrapper = queue[0];
            queue.RemoveAt(0);
            ordered.Add(wrapper);
            foreach (var target in outgoing[wrapper.Step.Id.ToString()])
            {
                incoming[target]--;
                if (incoming[target] == 0)
                {
                    queue.Add(byId[target]);
                }
            }
        }

        foreach (var wrapper in Steps.Where(wrapper => !ordered.Contains(wrapper)))
        {
            ordered.Add(wrapper);
        }

        return ordered;
    }

    private void UpdateDependencies()
    {
        var names = Steps.ToDictionary(
            wrapper => wrapper.Step.Id.ToString(),
            wrapper => wrapper.RequestName
        );
        foreach (var wrapper in Steps)
        {
            var id = wrapper.Step.Id.ToString();
            var sources = Graph
                .Edges.Where(edge => edge.Target.Id == id)
                .Select(edge =>
                {
                    var name = names.GetValueOrDefault(edge.Source.Id, "外部节点");
                    var condition = _edgeModels.TryGetValue(
                        (edge.Source.Id, edge.Target.Id),
                        out var model
                    )
                        ? model.Condition
                        : "Always";
                    var conditionLabel = condition switch
                    {
                        "OnSuccess" => "成功",
                        "OnFailure" => "失败",
                        "OnSkipped" => "跳过",
                        "Expression" => "条件",
                        _ => "总是",
                    };
                    return $"{name}·{conditionLabel}";
                })
                .Distinct(StringComparer.Ordinal)
                .ToList();
            wrapper.DependencyLabel =
                sources.Count == 0 ? "无上游"
                : sources.Count <= 2 ? $"上游：{string.Join("、", sources)}"
                : $"上游：{sources[0]}、{sources[1]} 等 {sources.Count} 个";
            wrapper.RefreshDependencyLabel();
        }
    }

    private void RefreshStatus()
    {
        var maxParallelism = ParseInt(MaxParallelismText, 4, 0, 64);
        var parallelism = maxParallelism == 0 ? "不限并行" : $"最多 {maxParallelism} 路并行";
        StatusText = $"{Steps.Count} 个步骤 · {Graph.Edges.Count} 条依赖 · {parallelism}";
    }

    private IReadOnlyList<SuiteStepOption> BuildMappingOptions()
    {
        if (SelectedStep is null)
        {
            return Array.Empty<SuiteStepOption>();
        }

        var ancestors = new HashSet<Guid>();
        var queue = new Queue<Guid>();
        queue.Enqueue(SelectedStep.Step.Id);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue().ToString();
            foreach (var edge in Graph.Edges.Where(edge => edge.Target.Id == current))
            {
                if (!Guid.TryParse(edge.Source.Id, out var sourceId))
                {
                    continue;
                }

                if (ancestors.Add(sourceId))
                {
                    queue.Enqueue(sourceId);
                }
            }
        }

        return Steps
            .Select((wrapper, index) => (Wrapper: wrapper, Index: index))
            .Where(item => ancestors.Contains(item.Wrapper.Step.Id))
            .Select(item => new SuiteStepOption(
                item.Wrapper.Step.Id,
                $"{item.Index + 1}. {item.Wrapper.RequestName}"
            ))
            .ToList();
    }

    private InputMappingEditor CreateMappingEditor(
        InputMapping source,
        IReadOnlyList<SuiteStepOption> options
    )
    {
        var editor = new InputMappingEditor
        {
            SourceOptions = options,
            SelectedSource = options.FirstOrDefault(option => option.Id == source.SourceStepId),
            SourcePath = source.SourcePath,
            TargetKind = string.IsNullOrWhiteSpace(source.TargetKind) ? "Body" : source.TargetKind,
            TargetPath = source.TargetPath,
            Template = string.IsNullOrWhiteSpace(source.Template) ? "{value}" : source.Template,
        };
        editor.RemoveCommand = new RelayCommand(() => MappingEditors.Remove(editor));
        return editor;
    }

    private AssertionEditor CreateAssertionEditor(TestAssertion source)
    {
        var editor = new AssertionEditor
        {
            Kind = string.IsNullOrWhiteSpace(source.Kind) ? "JSONPath" : source.Kind,
            Path = source.Path,
            Expected = source.Expected,
        };
        editor.RemoveCommand = new RelayCommand(() => AssertionEditors.Remove(editor));
        return editor;
    }

    private ConditionRuleEditor CreateConditionEditor(SuiteConditionRule source)
    {
        var editor = ConditionRuleEditor.FromModel(source);
        editor.RemoveCommand = new RelayCommand(() => ConditionEditors.Remove(editor));
        return editor;
    }

    private static void CopyCondition(SuiteCondition source, SuiteCondition copy)
    {
        copy.Mode = source.Mode;
        foreach (var rule in source.Rules)
        {
            copy.Rules.Add(
                new SuiteConditionRule
                {
                    Source = rule.Source,
                    Path = rule.Path,
                    Operator = rule.Operator,
                    Expected = rule.Expected,
                }
            );
        }
    }

    private static int ParseInt(string text, int fallback, int min, int max) =>
        int.TryParse(text, out var value) ? Math.Clamp(value, min, max) : fallback;
}
