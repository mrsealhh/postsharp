# PostSharp CLI

`postsharp-cli` 有两种工作方式：

- **服务模式（默认）**：通过本机 MCP 端点调用正在运行的 PostSharp，可访问集合、套件、审计和工作区。
- **纯 CLI 模式（`--local`）**：不连接任何 PostSharp 进程，直接在本机执行 YAML/JSON 流水线；适合 SSH 终端、Linux 服务器和 CI。

两种模式共用同一套流水线引擎（`PostSharp.Core`），断言、变量、数据行、循环、并发、重试和报告格式完全一致。

## 桌面端入口

应用内「指南 → 命令行 CLI」卡片提供常用命令复制、MCP 地址复制、下载 CLI 和查看说明入口。CLI 是独立下载包，不随桌面安装包分发；桌面端开启「MCP 后台常驻」后，关闭主窗口仍会保持 MCP 端点可用。

## 前置条件

1. PostSharp 桌面端已启动，并开启设置中的「关闭窗口后保持 MCP 后台常驻」。
2. 默认 MCP 地址为 `http://127.0.0.1:5199/mcp`。
3. 自包含 CLI 包不需要额外运行时。
4. 如果设置中启用了「MCP 访问控制 → 要求本机 token」，需要加 `--token <token>` 或设置环境变量 `POSTSHARP_MCP_TOKEN`；只读模式下发送/修改类命令会被拒绝。

## 纯 CLI 模式（--local）

不需要启动 PostSharp，也不需要 MCP token：

```bash
postsharp-cli pipeline validate smoke.yaml --local
postsharp-cli pipeline graph smoke.yaml --local --format mermaid
postsharp-cli pipeline run smoke.yaml --local --format junit \
  --copy-report test-results/postsharp.xml
```

- 只支持 `pipeline validate / graph / run`；集合、套件、审计等命令仍然需要服务模式。
- 支持 `--data`、`--var k=v`、`--base-url`、`--format`、`--copy-report`，与 MCP 服务端的流水线参数一致。
- 报告默认写到当前目录的 `reports/`；`--copy-report <路径>` 直接写到指定位置，适合 CI 归档。
- 退出码：`0` 全部通过；`1` 有步骤失败或校验失败；`2` 参数错误；`3` 连接失败或取消。

### SSH 终端 / Linux 服务器

从 Release 下载 `PostSharp.Cli-linux-x64.zip`，解压后把 `postsharp-cli` 放到服务器：

```bash
scp postsharp-cli user@server:/usr/local/bin/
ssh user@server
chmod +x /usr/local/bin/postsharp-cli
postsharp-cli pipeline run /srv/api/smoke.yaml --local --format junit \
  --copy-report /srv/api/test-results/postsharp.xml
```

CLI 是自包含单文件（含 .NET 运行时），不需要在服务器安装 .NET，也不需要 Xvfb、Avalonia 或桌面环境。Alpine 等 musl 系统需要单独构建 `linux-musl-x64` 版本。

没有 Release 包时可以在仓库里交叉编译：

```powershell
dotnet publish src/PostSharp.Cli/PostSharp.Cli.csproj -c Release -r linux-x64 `
  --self-contained true -p:PublishSingleFile=true -o artifacts/cli-linux
```

把 `artifacts/cli-linux/postsharp-cli` 传到服务器即可。

### 本地 MCP 服务（AI 客户端）

```bash
postsharp-cli mcp serve --stdio
```

它只暴露流水线工具，直接在本机执行，不需要 PostSharp 进程：

- `validate_pipeline`
- `run_pipeline`
- `visualize_pipeline`

Claude Desktop / Cursor / OpenCode 等只支持 stdio 的客户端可以这样配置：

```json
{
  "mcpServers": {
    "postsharp": {
      "command": "postsharp-cli",
      "args": ["mcp", "serve", "--stdio"]
    }
  }
}
```

Windows 把 `command` 改成 `postsharp-cli.exe` 的绝对路径。需要集合/套件工具时，改用服务模式或 `mcp-stdio` 桥接。

## 命令

```bash
postsharp-cli suite list
postsharp-cli suite get <suiteId>
postsharp-cli suite validate <suiteId>
postsharp-cli suite run <suiteId>
postsharp-cli suite runs <suiteId>
postsharp-cli suite rerun-failed <suiteId> <runId>
postsharp-cli suite continue <suiteId> <runId> <stepId>
postsharp-cli pipeline validate <file> [--local] [--data <file>]
postsharp-cli pipeline graph <file> [--local] [--format mermaid|dot|json]
postsharp-cli pipeline run <file> [--local] [--data <file>] [--var k=v] [--base-url URL] [--format junit|json|markdown] [--copy-report <path>]
postsharp-cli request send <requestId>
postsharp-cli audit list --source MCP --limit 20
postsharp-cli mcp-stdio
postsharp-cli mcp serve --stdio
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

Windows 可把 `command` 改为 `postsharp-cli.exe` 的绝对路径。桥接本身不启动 PostSharp，桌面端仍然需要运行；启用 token 时在 `args` 中加 `"--token", "<token>"`（或给桥接进程设置 `POSTSHARP_MCP_TOKEN`）。

通用选项：

```text
--endpoint URL     默认 http://127.0.0.1:5199/mcp
--token TOKEN      MCP 访问控制要求的本机 token；也可用 POSTSHARP_MCP_TOKEN
--local            流水线命令直接在本机执行，不连接 PostSharp
--json             输出原始 JSON；进度信息写 stderr
--timeout 秒       单次请求超时，默认 30 秒
--client 名称      写入 MCP clientInfo
--junit 文件       suite run/rerun-failed/continue 完成后写 JUnit XML
--markdown 文件    suite run/rerun-failed/continue 完成后写 Markdown 报告
--html 文件        suite run/rerun-failed/continue 完成后写 HTML 报告
--version          显示 CLI 版本
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

# CI：运行并输出 JUnit
postsharp-cli suite run 6a1d... --json --junit test-results/postsharp.xml

# 同时输出 Markdown / HTML 报告
postsharp-cli suite run 6a1d... --markdown reports/suite.md --html reports/suite.html

# 启用 MCP token 后
postsharp-cli --token <token> suite list --json
$env:POSTSHARP_MCP_TOKEN = "<token>"; postsharp-cli suite run <suiteId> --junit result.xml

# 查看版本
postsharp-cli --version
```

CLI 随主 Release 发布：`PostSharp.Cli-win-x64.zip`、`PostSharp.Cli-linux-x64.zip`、`PostSharp.Cli-osx-arm64.zip`（自包含，无需预装 .NET）。Windows 解压后运行 `postsharp-cli.exe`，Linux/macOS 解压后运行 `bash run.sh`（启动脚本已带可执行位）。Linux CI 也可以直接下载 `PostSharp-headless-linux-x64.zip`，里面同时包含无头应用和 `cli/postsharp-cli`。

`suite run` 使用 `start_suite_run`，随后轮询 `get_suite_run_job`；退出码根据最终状态返回。CLI 不开放删除、清空和切换存储等破坏性操作。

## 构建

```powershell
.\scripts\build-cli-release.ps1 -Version 1.1.0 -RuntimeIdentifier win-x64 -SelfContained
```

Linux/macOS 将 RID 换成 `linux-x64` 或 `osx-arm64`。发布包默认自包含，不需要预装 .NET。

CLI 不启动桌面窗口，也不直接操作工作区文件；它只调用 PostSharp 的 MCP 工具，因此审计仍会记录 CLI 的客户端名和工具名。
