# 更新日志

## v1.11.2（待发布）

### 首次体验

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
