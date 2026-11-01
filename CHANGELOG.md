# 更新日志

## v1.11.7 — 2026-11-02

### OpenAPI 契约 diff

- 新增 MCP `diff_openapi`：对比两份 OpenAPI/Swagger 文档，支持 URL 或直接内容。
- 比较操作增删、参数必填/类型、请求体必填/媒体类型/字段、响应状态和响应字段，并标记 `breaking`。
- 只读权限即可调用；AI 可直接用它判断升级是否破坏现有客户端，无需桌面 UI。
- 工具总数 84 → 85。

### 测试

- 新增契约 diff 单元测试：参数必填/类型、请求体必填、响应字段类型、新增响应和删除操作。

## v1.11.6 — 2026-11-01

### 录制会话

- HAR 导入新增「预览会话」：选择文件或粘贴内容后先预览条目，再决定导入范围。
- 会话支持按 URL/方法/状态筛选、包含静态资源、全选/全不选；超长 HAR 只预览前 500 条并提示分批。
- 预览只显示方法、URL、状态、耗时和类型，不显示请求头和请求体；查询参数中的 token、key、secret、signature 等值在界面上显示为 `***`。
- 选中条目可「导入选中」，或「导入并生成套件」直接打开套件编辑器；单次导入复用现有 HAR 解析和集合树。

### 测试

- 新增 HAR 会话解析、静态资源分类、筛选、选择、导入回调和敏感查询参数脱敏的单元测试。

## v1.11.5 — 2026-11-01

### 从导入到套件

- 导入成功对话框新增「生成套件」：HAR / Postman / OpenAPI 导入后可直接打开套件编辑器，并预填导入集合的请求。
- 集合导入结果新增 `RootId`；无请求、环境导入或集合已删除时不显示按钮或给出提示。
- 批量导入以最后一个成功导入的集合作为生成目标。

### 运行对比

- 自动化运行历史新增「对比」按钮：选择一次运行后，与更早的一次运行做对比。
- 对比对话框展示新增失败、修复、状态码变化、耗时回归/改善、目标未运行步骤和节点状态变化；无差异时给出明确提示。
- 新增 `SuiteRunDiffViewModel` 投影和单元测试。

### 套件可用性

- MCP `list_suites` / `get_suite` / `create_suite` / `update_suite` 改用显式投影，修复 observable 属性（名称、最近结果、步骤数）在 JSON 中缺失的问题；CLI `suite list` 恢复名称和步骤数。
- 自动化工具栏新增「预检」按钮；保存套件后自动预检，并在 Toast 和预检问题面板给出结果。
- 复核套件保存、预检和执行：改名后重启仍保留；缺失请求的套件被预检阻止运行；UI 同步运行 4/4 通过；MCP 后台任务 4/4 完成；运行历史和单次运行详情可读。

## v1.11.4 — 2026-11-01

### CI Action

- 新增可复用复合 Action `.github/actions/postsharp-mcp`：安装 Xvfb、下载 Linux 包、无头启动，等待健康端点和 MCP `initialize` 成功，输出 `endpoint` 和 `log`。
- CI 增加 action-smoke：用本地 Action 启动 1.11.3 发布包并校验 84 个 MCP 工具。
- `docs/ci.md` 增加 Action 输入、输出和清理示例。

### 性能基线

- `scripts/perf-smoke.ps1` 新增 `McpReadyMs`：健康后发送 `initialize` 并记录 MCP 就绪时间。
- 修复端口扫描导致的测量虚高：改为直连 5199 的 HTTP 轮询，接口未就绪时最多额外等待约 1.8 秒。
- 1.11.3 复测：JIT MCP 就绪 664–690 ms、空闲内存 100–108 MB；AOT 581–590 ms、约 81 MB。
- 更新 `docs/performance.md` 的测量方式、基线和首次运行说明。

## v1.11.3 — 2026-11-01

### CI 与无头模式

- 新增 `--headless`：只启动 MCP 和内置 Mock，不创建窗口和托盘，供容器和 CI 使用。
- 新增根目录 `Dockerfile`、`docker/entrypoint.sh` 和 `.dockerignore`：多阶段构建 Linux 自包含包，镜像内启动 Xvfb 后运行无头模式，数据目录挂载到 `/data`。
- CI 新增 Ubuntu headless 冒烟：发布 Linux 包，`xvfb-run` 启动后用 `initialize` + `tools/list` 校验 84 个工具，并运行内置演示套件；新增 Docker job 构建镜像并验证容器内健康端点和工具列表。
- 新增 `docs/ci.md`：无头启动、Docker、GitHub Actions 示例、数据目录准备和限制。

### 测试

- 修复 WebSocket / Socket.IO 测试在服务端主动关闭时偶发抛出关闭握手异常的问题，改为尽力关闭。

## v1.11.2 — 2026-11-01

### MCP 长任务 job

- 新增 `start_sequence_run` / `get_sequence_run_job` / `cancel_sequence_run`：后台顺序发送、每步进度、取消和幂等键。
- 新增 `start_import` / `get_import_job` / `cancel_import`：后台导入，支持取消和幂等键。
- 新增 `start_storage_sync` / `get_storage_sync_job` / `cancel_storage_sync`：后台存储同步，支持取消和幂等键。
- 统一 `McpJob` 状态（Running/Cancelling/Completed/Failed/Cancelled）、进度、终态和最近 50 条任务保留。
- 工具总数 75 → 84。

### MCP 细粒度权限

- 设置 →「MCP 访问控制」新增发送、修改、管理三个独立开关；只读模式优先。
- 工具按读取/发送/修改/管理分类，`tools/list` 按权限裁剪，越权调用返回 `-32003`。
- `McpAuthHint` 显示当前权限组合。

### 首次体验

- 首次启动弹出 3 步欢迎对话框（发送请求 / 接入 MCP / 运行套件），可「打开指南」或「跳过」；完成状态写入设置。
- 指南页「一分钟上手」增加「重置演示工作区」：清空当前集合、请求、环境、套件、发送历史和回收站，恢复内置 Mock 示例；审计记录和系统设置保留。
- 重置前使用确认弹窗，说明影响范围与不可撤销性；`Ctrl+Enter` 确认、`Esc` 取消。

### 性能基线

- 新增 `scripts/perf-smoke.ps1`：启动自包含包，测量到健康端点就绪的冷启动时间和空闲私有内存，支持 `-Json`。
- 新增 `docs/performance.md`，记录 JIT/AOT 基线和 1.11.1 复测结果。
- 新增 `.github/workflows/release.yml`：手动触发构建三平台 JIT、Windows AOT 和校验文件，可选创建 draft release。

## v1.11.1 — 2026-10-31

### MCP 安全与可重复性

- 破坏性工具新增 `dryRun`：删除集合/请求/套件/审计/历史/环境/变量/存储配置，清空或永久删除回收站，恢复记录和切换存储，都先返回目标名称、影响数量和提示，不执行实际修改。
- `start_suite_run` 新增 `idempotencyKey`：同一 key 重复调用返回同一个 jobId；key 用于另一个套件时返回明确错误。
- `get_suite_run_job` 返回 `idempotencyKey`，便于客户端对账。

### 分发

- 新增 `packaging/winget`、`packaging/scoop`、`packaging/homebrew` 清单模板。
- 新增 `scripts/update-packaging.ps1`，从发布 ZIP 的 `.sha256` 生成对应版本清单。
- 新增 `docs/distribution.md`，说明本地安装、上游提交和限制。

### 工程

- 新增 MCP 安全与幂等测试；278 个测试通过，Debug 构建 0 警告 0 错误。
- CI 增加 packaging 校验（Scoop JSON、winget/Homebrew 哈希）。

## v1.11.0 — 2026-10-31

主题：MCP 意图层、集合树体验与托盘可靠性。

### MCP 意图工具

- 新增 `verify_endpoint`：一次发送并逐项检查状态码、响应体和 JSONPath/响应头断言，返回结论、检查项和掩码响应预览。
- 新增 `generate_suite`：从集合或请求生成套件，按最近一次成功发送固定期望状态，`dryRun` 只预览草稿和预检结果。
- 新增 `export_report`：把运行导出为 Markdown / HTML / JUnit / JSON，写入数据目录 `reports/` 并返回路径。
- 新增 `diff_runs`：对比两次运行，分类新增失败、修复、状态码变化、耗时回归/改善、增删步骤和节点变化。
- 新增 `explain_failure`：把失败归类到预检/网络/状态/正文/断言/依赖，定位到步骤和断言并给出下一步工具调用。
- 工具总数 70 → 75；`export_report` / `diff_runs` / `explain_failure` 归入只读模式，`verify_endpoint` 写入审计。

### 桌面体验

- 集合树恢复上次的展开/收起状态和选中节点；节点被删除时回退第一个请求，空工作区保持「新建请求」草稿。
- 集合右键新增「复制集合」：递归复制子集合与请求，参数、请求头、表单、上传和授权独立，所有节点换新 id，套件仍指向原请求；副本不继承 OpenAPI 来源和收藏。
- 托盘图标改用 Windows `.ico`，减少异常色块。

### 托盘与最小化

- Windows 改用原生 `Shell_NotifyIcon` + `TrackPopupMenu` 托盘菜单，Windows 11 通知区溢出场景下右键菜单也能显示。
- 菜单提供「显示主页面」「最大化打开」「退出 PostSharp」；后台服务提供「显示主页面」「退出后台服务」。
- 托盘可用时最小化按钮隐藏到托盘，恢复时保留最大化状态。
- 退出、切后台和后台服务停止时显式释放托盘图标，减少残留。

### 工程

- 新增 GitHub Actions CI：Windows runner 上执行 `csharpier check`、Debug 构建和全部测试。
- Windows 原生托盘菜单等 Windows 专属能力集中在新服务中，macOS/Linux 继续使用 Avalonia 托盘并安全降级。
- 265 个测试通过；Debug/Release 构建 0 警告 0 错误。

## v1.10.24 — 2026-10-29

- 三平台自包含 JIT 包 + Windows NativeAOT 包；AOT 与 JIT 使用独立更新通道。
- 更新器按 `<资产名>.sha256` 精确校验，AOT 包可自更新。
- Arc 0.3.2 升级、MCP 本机 token/只读模式、SOAP/gRPC 支持、AOT source-generated JSON。
- 232 个测试通过。
