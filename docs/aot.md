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

`-SkipPublish` 复用 `-PublishDirectory` 里已有的产物；`-RuntimeIdentifier` 可换成 Linux/macOS 目标。

## 已验证

| 项目 | 结果 |
| --- | --- |
| Windows x64 产物 | 单个原生 `PostSharp.exe` 约 63 MB |
| 启动 | 原生进程正常启动，Mock/MCP 服务在默认端口就绪 |
| 构建告警 | 自有代码 0 条 IL 告警；仅 `Google.Protobuf` 依赖包产生 IL2104 / IL3053 |
| MCP | `tools/list` 70 个工具；集合、环境、套件、历史、审计、存储配置、回收站、导出均返回结果 |
| 请求 | `create_request` → `send_request` 200 → `get_curl` → `delete_request` 全链路通过 |
| 套件 | `create_suite` → `run_suite` 1/1 通过 → `delete_suite` |
| 持久化 | `update_settings` 写入后重启原生进程，设置仍然存在 |
| 冒烟脚本 | 18 项检查通过，日志无 `[ERR]`、无反射序列化异常 |

## 关键改动

- 本地文档（设置、工作区、套件、审计、存储配置、待安装更新、OpenAPI 操作文档）全部改用 source-generated JSON（`DataJsonContext`），不再走反射序列化。
- MCP 工具结果改用 `McpJsonContext`：所有返回类型都显式注册，`ToolResult` 按运行时类型取元数据；错误和计数类结果直接构造 `JsonObject`。
- `AppSettings` 由 MVVM 生成器产出属性，System.Text.Json 源生成器看不到这些属性，因此改用显式转换器 `AppSettingsJsonConverter`，读写保持原有 PascalCase 字段并忽略未知字段。
- 响应格式化、SSE 文本格式化、gRPC 动态消息、Socket.IO 事件、Postman 导出和 Mock API 全部改为 `JsonNode` / `Utf8JsonWriter`，不再反射序列化匿名类型。
- 动态 XAML 样式改为编译样式类，Arc 弹层所需的 `VisualLayerManager` 属性通过 `DynamicDependency` 保留。

## 限制

- 进程内存储插件依赖 `AssemblyLoadContext` 和反射实例化，NativeAOT 下不可用：`StoragePluginRegistry.Refresh()` 直接返回提示并回退本地 SQLite，设置页仍显示远程配置但不能加载插件。需要远程存储时请使用 JIT 包。
- `Google.Protobuf` 3.19.4 自身仍会给出 IL2104 / IL3053 告警；gRPC 一元调用在冒烟中可用，但复杂 `Any` / 自定义选项类型未验证。
- AOT 产物不进入 `scripts/build-*-release.ps1` 的三平台主包：由 `scripts/build-aot-release.ps1` 单独产出 ZIP，随同版本 Release 发布。当前只在 Windows 上构建 `win-x64`；Linux/macOS 的 NativeAOT 需要对应平台工具链。产物未签名/公证。
- 裁剪是 NativeAOT 默认行为，若后续新增反射式序列化或动态加载代码，`scripts/aot-smoke.ps1` 会在启动或工具调用阶段暴露问题，提交前应运行一次。
