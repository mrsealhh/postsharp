# PostSharp CLI

`postsharp-cli` 通过本机 MCP 端点调用正在运行的 PostSharp，适合终端、CI 和 AI 客户端封装。

## 桌面端入口

应用内「指南 → 命令行 CLI」卡片提供常用命令复制、MCP 地址复制、下载 CLI 和查看说明入口。CLI 是独立下载包，不随桌面安装包分发；桌面端开启「MCP 后台常驻」后，关闭主窗口仍会保持 MCP 端点可用。

## 前置条件

1. PostSharp 桌面端已启动，并开启设置中的「关闭窗口后保持 MCP 后台常驻」。
2. 默认 MCP 地址为 `http://127.0.0.1:5199/mcp`。
3. 自包含 CLI 包不需要额外运行时。
4. 如果设置中启用了「MCP 访问控制 → 要求本机 token」，需要加 `--token <token>` 或设置环境变量 `POSTSHARP_MCP_TOKEN`；只读模式下发送/修改类命令会被拒绝。

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

Windows 可把 `command` 改为 `postsharp-cli.exe` 的绝对路径。桥接本身不启动 PostSharp，桌面端仍然需要运行；启用 token 时在 `args` 中加 `"--token", "<token>"`（或给桥接进程设置 `POSTSHARP_MCP_TOKEN`）。

通用选项：

```text
--endpoint URL     默认 http://127.0.0.1:5199/mcp
--token TOKEN      MCP 访问控制要求的本机 token；也可用 POSTSHARP_MCP_TOKEN
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

CLI 从独立的 `cli-v1.1.0` Release 下载：Windows 解压后运行 `postsharp-cli.exe`，Linux/macOS 解压后运行 `bash run.sh`（启动脚本已带可执行位）。

`suite run` 使用 `start_suite_run`，随后轮询 `get_suite_run_job`；退出码根据最终状态返回。CLI 不开放删除、清空和切换存储等破坏性操作。

## 构建

```powershell
.\scripts\build-cli-release.ps1 -Version 1.1.0 -RuntimeIdentifier win-x64 -SelfContained
```

Linux/macOS 将 RID 换成 `linux-x64` 或 `osx-arm64`。发布包默认自包含，不需要预装 .NET。

CLI 不启动桌面窗口，也不直接操作工作区文件；它只调用 PostSharp 的 MCP 工具，因此审计仍会记录 CLI 的客户端名和工具名。
