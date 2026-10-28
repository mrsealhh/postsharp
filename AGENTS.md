# PostSharp 开发约定

## 项目概览

- Avalonia 12.1.4 + TKCode.Arc 0.3.0-preview.2 的桌面应用（net10.0），MVVM 使用 CommunityToolkit.Mvvm。
- 定位：面向 AI 的本地接口调试工作台 —— UI 手动调试、MCP 触发请求、审计留痕、回归套件闭环。
- 目录：`src/PostSharp`（Models / Services / ViewModels / Views / Controls）、`docs/`（界面截图）；功能说明与验证记录以 README 为准。
- 只有一个应用工程，没有测试工程。验收 = 构建通过 + 手动验证 + README「验证记录」。
- 版本锁定：不自行升级或替换 Avalonia、Avalonia.Desktop、TKCode.Arc 及 `global.json` 中的 SDK；新增 NuGet 包前先说明理由。

## 构建与验证

改完代码必须执行：

```powershell
$env:AVALONIA_TELEMETRY_OPTOUT = 1
dotnet build src/PostSharp/PostSharp.csproj -c Debug
csharpier format .
```

- 交付标准：构建 **0 警告 0 错误**，且 `csharpier format .` 不再产生改动。
- `AVALONIA_TELEMETRY_OPTOUT` 只用于受限/沙箱终端（否则 Avalonia 统计任务写日志会失败）；正常桌面终端可不设。
- 界面外观有变化时，同步更新 `docs/` 下对应截图，并在 README「验证记录」补一行。
- 不新建测试工程、不加 CI 配置，除非明确要求。

## 代码格式化

- 统一使用 **CSharpier 1.3.0**，配置见 `.csharpierrc.json`：缩进 4 空格、行宽 100、LF。
- 覆盖 `.cs` / `.axaml` / `.csproj` / `.slnx`，格式化结果即最终样式，不得手工调整其换行与缩进。
- 工作完成后执行 `csharpier format .`；只想检查时用 `csharpier check .`。
- 命令是全局工具 `csharpier`，不要用 `dotnet csharpier`（仓库不提交工具清单）。未安装时执行 `dotnet tool install -g csharpier`。

## UI/UX 规范

### 反馈（每个可交互元素都要有明确四态）

- 空闲 / 进行中 / 成功 / 失败四态齐全，不允许「点了没反应」。
- 进行中：禁用触发控件，文案改为进行时（如「发送中…」），并提供取消；长任务同时给出进度。
- 成功：轻量结果用 Toast，不用弹窗打断主流程。
- 失败：错误必须持久可见（结果区 / 状态区）且可复制；一闪而过的 Toast 不算失败反馈。
- 失败文案 = 发生了什么 + 为什么 + 下一步怎么办，不直接甩 `exception.Message` 或堆栈。
- 不可逆操作先确认，文案含对象名与后果（如「删除请求「登录」？该操作不可撤销」）；Esc 取消，默认焦点在安全项；删除类操作走回收站，不做硬删除。
- 同一操作进行中不得重复触发；重复点击复用上一次任务，而不是并发发起。
- 异常不得静默吞掉，`catch` 必须落到可见反馈。
- 忙碌指示延迟约 150–300ms 出现再消失，避免快速操作时闪烁。
- 加载中保留旧内容加遮罩，不要先清空再填，避免整块跳动。

### 布局与视觉稳定性

- 状态切换不得改变布局尺寸：按钮/标签宽度稳定，为滚动条预留空间，加载占位与内容同高。
- 只用 Arc 设计令牌：禁止硬编码色值、圆角、字号（`.axaml` 中不得出现 `#RRGGBB`）。
- 空状态必须给出说明 + 下一步入口，不允许整片留白。
- 不可用操作保留位置并用 ToolTip 说明原因，不要直接隐藏。
- 长文本统一 `TextTrimming="CharacterEllipsis"` + `ToolTip.Tip` 显示全文。
- 列表/树刷新后保持选中项、展开状态与滚动位置；分栏宽度、窗口尺寸与位置持久化。
- 列表行不要用 `arc:Button` / `Button` 包裹整行内容：按钮的默认内边距、内容对齐和背景会破坏块级列表。优先使用原生 `ListBoxItem` 或本仓库的 `controls:SelectableRow` 等行组件，只在行内操作（删除、更多）使用按钮。
- 同类列表（历史、审计、套件、结果等）必须共用同一套行布局规范：行占满容器宽度，左侧主信息、右侧状态/时间对齐，删除类操作固定在行内末尾；不得依赖内容自适应宽度导致行宽不一或居中堆叠。
- 列表选中态只使用一种视觉信号：要么左侧强调块，要么边框，二者不要同时出现；删除按钮只在选中行显示，并位于行背景内部。
- 不强制使用 Arc 控件完成所有布局。Arc 适合表单控件和小型操作按钮；当 Arc 控件的行为或布局不适合时，使用原生 Avalonia 控件或自定义功能组件，但颜色、圆角、字号必须通过 Arc 设计令牌适配主题。
- 浅色与深色主题都要验证；文本与背景对比度满足 WCAG AA。

### 交互与键盘

- 所有功能可纯键盘完成；常用命令登记快捷键并同步 README「快捷键」表，新增前检查冲突。
- Esc 语义统一：先关浮层/菜单，再关管理页；有模态框时交给模态框处理。
- 对话框打开时焦点移入首个可编辑项，关闭后焦点回到触发控件。
- 同一对象在不同入口（右键菜单 / 工具栏 / 快捷键）行为一致。
- 拖拽、移动类操作要有插入位置指示，落点明确。
- 双击、右键、拖拽遵循桌面惯例，不自造交互。

### 文案

- 中文、全角标点；按钮用动词（「删除请求」而非「确定」）；不用感叹号和客套话。
- 术语固定：请求、集合、环境、套件、审计、回收站、MCP，不自造同义词。
- 错误提示面向用户，不出现类名、堆栈、英文原始错误。
- 占位符用真实可用示例，不用「请输入」。

### 可访问性与平台

- 图标按钮必须有 `AutomationProperties.Name`，表单控件有可见 Label。
- Tab 顺序与视觉顺序一致，焦点框始终可见。
- 125% / 150% 缩放与多显示器下不溢出、不裁切；窗口有最小尺寸。
- 耗时操作有超时与取消，取消后 UI 必须回到可用状态。

## 编码规范

- 命名空间使用文件作用域命名空间（`namespace X;`），不使用块状命名空间。
- Nullable 语义：引用类型只要没有声明为 `T?`，就表示它一定不为 null —— 不得写 `x == null` / `x != null` 判断，不得用 `x?.y` 条件访问，也不得写 `x?.y = new Z()`；确实可能为 null 的值必须显式声明为 `T?`，再在需要处判空或用 `??` 兜底。
- 该规则适用于本仓库自有类型；外部 API（BCL、Avalonia、Arc）以实际可空标注为准，判空统一写 `is null` / `is not null`。
- 字符串可空性表达业务语义，而不是消除编译器警告：可能缺失或未赋值的字符串字段声明为 `string?`（默认 null），不要用 `= string.Empty` 掩盖「未赋值」状态；只有业务上必定有值时，才声明为非空 `string`。把 `string?` 写入非空目标时，在赋值处显式 `?? string.Empty`（或先校验），兜底收敛在边界。
- 私有字段：`_camelCase` 前缀。
- 始终使用大括号（即使是单行 `if` / `for`）。
- 不允许行尾内联注释。
- 不允许 public 字段，使用属性。
- 只有一个构造函数时使用主构造函数；XAML 视图与框架派生类型（`Window`、`UserControl` 等）例外，保留无参构造 + `InitializeComponent()`。
- 异步方法以 `Async` 结尾，并接受 `CancellationToken cancellationToken = default`；仅限确实涉及 I/O 的方法，接口实现、重写方法、事件处理器例外。
- 只用结构化日志，日志不得使用字符串插值
- 方法返回集合时使用具体类型（如 `IReadOnlyList<T>`），且永不为 null，无项时返回空集合；只有 `byte[]?` 这类有业务含义的才允许为空。
- 参数尽可能抽象（收 `IReadOnlyList<T>` 而不是 `List<T>`），返回值尽可能具体（返回 `IReadOnlyList<T>` 而不是 `IEnumerable<T>`）；私有方法不受此限。
- 公共方法的返回值不得使用匿名类型、元组等临时类型；私有方法不受此限。
- 使用 C# 14 的 `extension` 成员语法，不使用传统 `this` 参数。
- 不得重复枚举 `IEnumerable`；需要多次遍历时先物化为 `List` / 数组。
- 写完代码必须自检未使用的方法、变量、类型、using 等，无用的直接删除。

## 项目内约定

- MVVM 一律用源生成器：`[ObservableProperty]`、`[RelayCommand]`，不手写 `INotifyPropertyChanged`。
- XAML 编译绑定默认开启，`Binding` 必须能解析到 `x:DataType` 上的成员，必要时显式声明 `x:DataType`。
- 跨线程更新 UI 必须走 `Dispatcher.UIThread`（`InvokeAsync` / `Post`）。
- 数据落盘只走 `SettingsStore`、`WorkspaceStore`、`AuditStore`，目录为 `%APPDATA%\PostSharp`；不另起存储位置或文件格式。
- 单文件过长时先拆分再改：`MainWindowViewModel.cs`、`MainWindow.axaml` 已明显超限，新功能应抽到子 ViewModel / 子控件 / 独立 Resource，不再继续往里堆。
- 命名空间与目录结构保持一致：`PostSharp.Models`、`PostSharp.Services`、`PostSharp.ViewModels`、`PostSharp.Views`、`PostSharp.Controls`。

## 禁止事项

- 不升级、不锁定漂移：不改 `Avalonia` / `TKCode.Arc` 版本，不改 `global.json`。
- 不硬编码颜色、字号、圆角；不写 `#RRGGBB`。
