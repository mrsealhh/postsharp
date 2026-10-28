# PostSharp CLI

`postsharp-cli` 通过本机 MCP 端点调用正在运行的 PostSharp，适合终端、CI 和 AI 客户端封装。

## 前置条件

1. PostSharp 桌面端正在运行。
2. 默认 MCP 地址为 `http://127.0.0.1:5199/mcp`。
3. 框架依赖包需要 .NET 10 运行时；自包含包不需要。

## 命令

```bash
postsharp-cli suite list
postsharp-cli suite get <suiteId>
postsharp-cli suite validate <suiteId>
postsharp-cli suite run <suiteId>
postsharp-cli suite runs <suiteId>
postsharp-cli suite rerun-failed <suiteId> <runId>
postsharp-cli suite continue <suiteId> <runId> <stepId>
postsharp-cli request send <requestId>
postsharp-cli audit list --source MCP --limit 20
postsharp-cli mcp-stdio
```

`mcp-stdio` 把 stdin/stdout 的 MCP JSON-RPC 转发到 PostSharp HTTP MCP，适合只支持 stdio MCP 的 harness：

```json
{
  "mcpServers": {
    "postsharp": {
      "command": "postsharp-cli",
      "args": ["mcp-stdio"]
    }
  }
}
```

Windows 可把 `command` 改为 `postsharp-cli.exe` 的绝对路径。桥接本身不启动 PostSharp，桌面端仍然需要运行。

通用选项：

```text
--endpoint URL     默认 http://127.0.0.1:5199/mcp
--json             输出原始 JSON；进度信息写 stderr
--timeout 秒       单次请求超时，默认 30 秒
--client 名称      写入 MCP clientInfo
```

退出码：

```text
0  成功
1  套件或请求失败
2  参数、工具或预检错误
3  无法连接、超时或取消
```

## 示例

```bash
# 机器可读
postsharp-cli --json suite list

# 运行后台套件并轮询进度；最终结果写 stdout
postsharp-cli suite run 6a1d... --json

# 只校验，不发送请求
postsharp-cli suite validate 6a1d...
```

`suite run` 使用 `start_suite_run`，随后轮询 `get_suite_run_job`；退出码根据最终状态返回。CLI 不开放删除、清空和切换存储等破坏性操作。

## 构建

```powershell
dotnet publish src/PostSharp.Cli/PostSharp.Cli.csproj -c Release -r win-x64 --self-contained false
```

Linux/macOS 将 RID 换成 `linux-x64`、`osx-arm64` 或 `osx-x64`。

CLI 不启动桌面窗口，也不直接操作工作区文件；它只调用 PostSharp 的 MCP 工具，因此审计仍会记录 CLI 的客户端名和工具名。
