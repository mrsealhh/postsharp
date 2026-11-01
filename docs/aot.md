# NativeAOT 构建与验证

PostSharp 现在可以在 NativeAOT 下完整启动、读取工作区、收发请求并持久化设置与套件。正式发布包仍默认使用自包含 JIT，AOT 是可选通道：体积更小、启动不依赖 JIT，但会失去进程内存储插件。

## 命令

```powershell
dotnet publish src/PostSharp/PostSharp.csproj -c Release -r win-x64 `
    -p:PublishAot=true -p:SelfContained=true -p:UseOptimizedPublish=false
```

一条命令完成构建与冒烟（会临时启动原生进程并通过 MCP 驱动真实请求）：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/aot-smoke.ps1
```

`-SkipPublish` 复用 `-PublishDirectory` 里已有的产物；不传 `-RuntimeIdentifier` 时按当前系统自动选择（`win-x64` / `linux-x64` / `osx-arm64` 等）。Linux/macOS 上用 PowerShell 7（`pwsh`）执行同一套脚本。

## 已验证

| 项目 | 结果 |
| --- | --- |
| Windows x64 产物 | 单个原生 `PostSharp.exe` 约 63 MB |
| 启动 | 原生进程正常启动，Mock/MCP 服务在默认端口就绪 |
| 构建告警 | 自有代码 0 条 IL 告警；仅 `Google.Protobuf` 依赖包产生 IL2104 / IL3053 |
| MCP | `tools/list` 84 个工具（1.11.4 AOT 冒烟 18 项通过）；集合、环境、套件、历史、审计、存储配置、回收站、导出均返回结果 |
| 请求 | `create_request` → `send_request` 200 → `get_curl` → `delete_request` 全链路通过 |
| 套件 | `create_suite` → `run_suite` 1/1 通过 → `delete_suite` |
| 持久化 | `update_settings` 写入后重启原生进程，设置仍然存在 |
| 冒烟脚本 | 18 项检查通过，日志无 `[ERR]`、无反射序列化异常 |
| 自更新 | Windows AOT 包带 `.postsharp-aot` 与更新标记，`check_updates` 返回「已是最新」而非「不支持的目录」；AOT 资产自更新从下一版生效 |

## 其他平台

NativeAOT **不支持跨操作系统编译**，在 Windows 上执行 `-r linux-x64 -p:PublishAot=true` 会直接失败：

```text
error : Cross-OS native compilation is not supported.
```

因此 Linux / macOS 的 AOT 包必须在对应系统（或容器、CI runner）上构建和冒烟：

| 平台 | 构建环境 | 命令 |
| --- | --- | --- |
| Linux x64 | .NET 10 SDK + `clang`、`zlib1g-dev`、`binutils`（Ubuntu/Debian：`sudo apt install -y clang zlib1g-dev binutils`）+ PowerShell 7 | `pwsh ./scripts/build-aot-release.ps1 -Version 1.10.23` |
| macOS arm64 | macOS + Xcode Command Line Tools（`xcode-select --install`）+ PowerShell 7 | `pwsh ./scripts/build-aot-release.ps1 -Version 1.10.23 -RuntimeIdentifier osx-arm64` |
| Windows x64 | VS 2022/2026 Build Tools（MSVC）+ .NET 10 SDK | `powershell -ExecutionPolicy Bypass -File scripts/build-aot-release.ps1 -Version 1.10.23` |

构建后跑冒烟：

```bash
pwsh ./scripts/aot-smoke.ps1 -SkipPublish -PublishDirectory artifacts/PostSharp-1.10.23-aot-linux-x64
```

跨架构同样不支持（如 x64 主机直接产出 linux-arm64 / win-arm64 原生包），需要对应架构的工具链，或使用 `dotnet publish` 在目标架构机器上构建。

**当前状态**：本机 WSL（`Microsoft-Windows-Subsystem-Linux`、`VirtualMachinePlatform`）未启用，也没有 Docker，所以这里只实际验证了 `win-x64`；Linux/macOS 的 AOT 构建与冒烟需要在目标环境执行。代码侧没有平台相关反射，跨平台风险主要来自 SkiaSharp / Avalonia / SQLite 原生库在目标系统的链接，建议在正式发布 Linux/macOS AOT 包前各跑一次冒烟。GitHub Actions（`ubuntu-latest` + `macos-latest`）可以自动化这一步，但仓库目前没有 CI，需要产品负责人确认后再加。

## 关键改动

- 本地文档（设置、工作区、套件、审计、存储配置、待安装更新、OpenAPI 操作文档）全部改用 source-generated JSON（`DataJsonContext`），不再走反射序列化。
- MCP 工具结果改用 `McpJsonContext`：所有返回类型都显式注册，`ToolResult` 按运行时类型取元数据；错误和计数类结果直接构造 `JsonObject`。
- `AppSettings` 由 MVVM 生成器产出属性，System.Text.Json 源生成器看不到这些属性，因此改用显式转换器 `AppSettingsJsonConverter`，读写保持原有 PascalCase 字段并忽略未知字段。
- 响应格式化、SSE 文本格式化、gRPC 动态消息、Socket.IO 事件、Postman 导出和 Mock API 全部改为 `JsonNode` / `Utf8JsonWriter`，不再反射序列化匿名类型。
- 动态 XAML 样式改为编译样式类，Arc 弹层所需的 `VisualLayerManager` 属性通过 `DynamicDependency` 保留。

## 限制

- 进程内存储插件依赖 `AssemblyLoadContext` 和反射实例化，NativeAOT 下不可用：`StoragePluginRegistry.Refresh()` 直接返回提示并回退本地 SQLite，设置页仍显示远程配置但不能加载插件。需要远程存储时请使用 JIT 包。
- `Google.Protobuf` 3.19.4 自身仍会给出 IL2104 / IL3053 告警；gRPC 一元调用在冒烟中可用，但复杂 `Any` / 自定义选项类型未验证。
- AOT 产物不进入 `scripts/build-*-release.ps1` 的三平台主包：由 `scripts/build-aot-release.ps1` 单独产出 ZIP，随同版本 Release 发布。目前 Release 只上传 `win-x64` AOT 包；Linux/macOS 需在对应平台构建后再上传。产物未签名/公证。\n- Windows AOT 包带 `.postsharp-aot` 与 `.postsharp-update-enabled` 标记，按 `PostSharp-aot-win-x64.zip` 资产自更新；JIT 安装只认 `PostSharp-win-x64.zip`。两个通道各自替换整份目录，不会互相混装；校验值按精确资产名匹配（同版本并存多个 `.sha256` 时不会取错）。
- 裁剪是 NativeAOT 默认行为，若后续新增反射式序列化或动态加载代码，`scripts/aot-smoke.ps1` 会在启动或工具调用阶段暴露问题，提交前应运行一次。
