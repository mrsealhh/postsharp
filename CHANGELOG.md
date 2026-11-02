# 更新日志

## v1.11.17 — 2026-11-02

### 流水线页面修复

- 修复流水线页左侧文件列表和右侧 YAML 编辑区空白：改用 Arc 文本框/按钮控件，不再依赖未接入主题的原生控件。
- 左侧列表增加空状态提示；文件改为可点击的行按钮，选中后加载内容。
- 图像编辑器的节点列表改为扁平缩进列表（Arc 按钮），避免原生 TreeView 无主题导致节点不可见。

### MCP 接入说明

- 工具说明按类别分行展示（集合/测试工作流/环境/审计/设置/导入导出/长任务/意图/流水线/客户端/协议），不再挤成一行。
- 补充 `visualize_pipeline`、`run_pipeline`、`list_clients`、客户端 token 等新工具。

### 测试

- 流水线中心保存/重载与编辑往返测试保持通过；326 个测试通过。

## v1.11.16 — 2026-11-02

### 流水线图像编辑

- 流水线页新增「图像编辑」：树形节点管理请求、循环和并发组，支持添加、删除、上移/下移和给分组添加子节点。
- 右侧面板编辑名称、别名、方法、URL、请求头、查询参数、请求体（JSON/原始）、状态码、bodyContains、JSONPath、重试和失败策略；循环/并发组编辑 over/as/index/max 与并发数。
- 保存时只改写 `steps` 和名称/baseUrl，保留 `data`、`vars`、`retry`、`defaults` 等未编辑字段；保存前用同一套解析器校验，错误直接显示。
- 图像编辑结果回填到 YAML 编辑区，确认后点「保存」写入文件。

### 入口整理

- 移除顶栏「管理层」按钮，统一从「自动化」进入；审计与指南仍可在页内切换。
- 归档的 `cli-v1.1.0` Release 说明改为正确的归档提示（此前显示乱码）。

### 测试

- 新增流水线图像编辑用例：嵌套循环/并发行往返、字段编辑、YAML 写出可解析；326 个测试通过。

## v1.11.15 — 2026-11-02

### 自动化前置与流水线中心

- 顶栏新增「自动化」主按钮，一键打开自动化页，不再藏在「管理层」里。
- 自动化页新增「流水线」页签：`pipelines` 目录列表、YAML 编辑、保存 / 校验 / 生成流程图 / 运行，以及结果面板。
- 内置 4 个场景模板：链式接口、并发检查、数据矩阵、CI 冒烟；点一下即可载入编辑或保存。
- 界面文案统一把「套件」改名为「测试工作流」，MCP 工具名和数据结构保持不变。

### 画布布局记忆

- 拖拽节点结束后立即把 X/Y 写回测试工作流并持久化，下次打开保持用户摆放的位置。
- 「自动布局」只在点击时执行，并同样持久化结果；打开时不再自动平铺。
- 只有从未保存过位置的工作流才按顺序排布。

### 测试

- 新增流水线中心保存/重载用例；323 个测试通过。

## v1.11.14 — 2026-11-02

### 流水线可视化（只读）

- 新增 `visualize_pipeline`：返回节点/边、层级、Mermaid 和 DOT，不发送请求。
- CLI 新增 `pipeline graph <文件> --format mermaid|dot|json`。
- 循环与并发组渲染为子图；并发组内不生成兄弟依赖边；节点标签带方法与地址。

### 客户端管理

- 新增 `list_clients`：聚合最近 MCP 审计，显示调用次数、失败数、最近活动、常用工具、绑定 token 和当前客户端。
- 新增 `get_client_activity`：查看单个客户端最近的工具、接口、状态、耗时和成功与否。
- 新增按客户端的 token：`create_client_token` / `list_client_tokens` / `revoke_client_token`；只保存 SHA256，明文只返回一次，可分别限定发送/修改/管理权限。
- 客户端 token 与全局 token 并存；吊销立即生效，审计记录客户端名称。
- `run_pipeline` 现在写入审计（`Tool=run_pipeline`），客户端活动可统计流水线运行。

### 测试

- 新增流水线图、客户端 token 权限用例；322 个测试通过；MCP 工具总数 95。

## v1.11.13 — 2026-11-02

### 流水线并发与 HTTP

- 新增并发组 `parallel: { max, steps: [...] }`：组内步骤同时执行，`max` 控制并发度，默认取流水线 `maxParallelism`（4）。
- 组内步骤互不可见，整组结束后别名才对后续步骤可见；某步失败且 `stopOnError` 时会取消同组未完成的请求。
- 新增常用 HTTP 字段：`query`、`form`、`redirects`、`timeoutSeconds`、`auth: { username, password }`（Basic）。
- 循环、并发组可以互相嵌套。

### CI 与无头 CLI

- 主 Release 起同时发布 `PostSharp.Cli-win-x64.zip`、`PostSharp.Cli-linux-x64.zip`、`PostSharp.Cli-osx-arm64.zip`（自包含）。
- 新增 Linux 一体化无头包 `PostSharp-headless-linux-x64.zip`：应用 + `cli/postsharp-cli` + `postsharp-cli.sh`。
- 新增可复用 workflow `.github/workflows/postsharp-pipeline.yml`：一条 `uses` 下载无头包、启动服务、运行调用方的 YAML 流水线并上传 JUnit。
- CI 增加流水线自测：`order-flow`、`parallel`、数据循环、CSV 矩阵四份示例全部执行并上传报告。
- CLI `pipeline run` 新增 `--copy-report <路径>`，把服务端生成的报告复制到 CI 工作区。
- 旧的 `cli-v1.1.0` pre-release 由主 Release 中的 CLI 包取代。

### 测试

- 新增并发组并发度、并发组内引用拒绝、query/form/Basic 认证用例；320 个测试通过。

## v1.11.12 — 2026-11-02

### 更新提示卡

- 新版本下载并校验完成后弹出「可用更新」卡片：显示版本号、「会话不会被中断」说明、本次发行说明摘要，以及「发行说明 / 稍后 / 更新」操作。
- 发行说明来自 GitHub Release 正文，展示前会去掉 Markdown 标记；点击「发行说明」在浏览器打开 Release 页面。
- 更新信息随 `pending.json` 持久化，重启后仍能看到同一版本的说明；已提示过的版本不会重复弹窗。
- 主窗口「更新」按钮改为打开该卡片，避免误触立即重启。

## v1.11.11 — 2026-11-02

### YAML/JSON 流水线

- 新增 `validate_pipeline` / `run_pipeline` MCP 工具：一份 YAML 或 JSON 就能描述「请求 A → 取字段 → 请求 B → 用 A/B 结果请求 C」。
- 步骤别名与占位符：`id: respA` 后可用 `${respA.id}`、`${respA.items[0].id}`；整串占位符保留原始类型，混排按字符串拼接。
- 断言：状态码（数字/数组/`2xx`）、`bodyContains`、`jsonPath`（exists / equals / contains）。
- 重试：`retry: { count, delayMs, on }` 可写在流水线或单步，默认重试网络错误与 408/429/500/502/503/504。
- 数据驱动：`data.rows` 或 `data.file`（CSV/JSON），每行跑一遍；循环节点 `loop: { over, as, index, max, steps }`。
- 报告：Markdown / JUnit / JSON 写入数据目录 `reports`，可通过 `postsharp://reports/{文件名}` 读取。
- CLI 新增 `pipeline validate <文件>` 和 `pipeline run <文件> --data ... --var k=v --format junit`，按结果返回退出码。
- 示例：`docs/examples/order-flow.yaml`、`data-driven-loop.yaml`、`data-matrix.yaml` + `order-cases.csv`；说明见 `docs/pipeline.md`。

### 测试

- 新增流水线解析、引用校验、链式执行、重试、循环、CSV 数据行和断言失败用例；317 个测试通过；MCP 工具总数 89。

## v1.11.10 — 2026-11-02

### 套件修复

- 修复工作区存在多级集合时「预检 / 运行套件 / 保存套件」全部无响应：预检把扁平集合列表里的子集合请求统计两次，`ToDictionary` 抛 `ArgumentException: An item with the same key has already been added`。
- 预检改为按请求 ID 去重构建查找表，嵌套集合与重复请求都不再中断校验。
- 新增嵌套集合回归测试；308 个测试通过。

## v1.11.9 — 2026-11-02

### 套件可用性修复

- 主窗口和套件编辑器按屏幕缩放换算 DIP，启动时居中并限制在当前工作区内。
- 修复 125% / 150% 缩放下窗口高度超过逻辑屏幕、底部「预检 / 运行套件 / 保存套件」被裁切的问题。

### MCP 资源

- `initialize` 声明 `resources` 能力。
- `resources/list` 列出数据目录 `reports` 下最近 50 个报告（Markdown / HTML / JSON / XML / CSV / 文本）。
- `resources/read` 读取 `postsharp://reports/{文件名}`，限制 1 MB，拒绝路径穿越。
- AI 可以直接读取已生成的报告，不必重复执行 diff 工具。

### 测试

- 新增 resources 能力、列表、读取和路径穿越测试；307 个测试通过。

## v1.11.8 — 2026-11-02

### Diff 报告导出

- 新增 MCP `export_run_diff`：把两次运行差异写成 Markdown / HTML / JSON，写入数据目录 `reports`，返回路径、内容和截断标记。
- 新增 MCP `export_openapi_diff`：把 OpenAPI 契约差异写成 Markdown / HTML / JSON，复用 `diff_openapi` 的对比和破坏性标记。
- 报告文件使用 UTF-8 无 BOM；HTML 对操作名、步骤名和明细做转义，避免报告里的响应内容变成可执行标记。
- 工具总数 85 → 87。

### 测试

- 新增运行 diff / 契约 diff 的 Markdown、HTML 和内容截断测试。

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
