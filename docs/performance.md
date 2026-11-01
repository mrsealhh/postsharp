# 启动与内存基线

`scripts/perf-smoke.ps1` 启动一个自包含包，测量健康端点、MCP 就绪和空闲私有内存，用于发布前门禁。

## 使用

```powershell
# JIT Windows 包
.\scripts\perf-smoke.ps1 -Executable .\artifacts\PostSharp-1.11.7\PostSharp.exe

# NativeAOT 包
.\scripts\perf-smoke.ps1 -Executable .\artifacts\PostSharp-1.11.7-aot-win-x64\PostSharp.exe -Json
```

要求：

- 启动前关闭所有 PostSharp 实例；单实例锁会让新进程退出并连到旧进程。
- 脚本使用临时 `POSTSHARP_DATA_DIR`，不会读写本地工作区。
- 结束后自动关闭进程并删除临时目录；失败时保留目录和日志路径方便排查。
- 端口固定探测 `5199`；被其它进程占用时直接失败，避免误测到别的实例。

## 测量方式

- `ColdStartMs`：从 `Start-Process` 到 `/api/health` 返回 200，直接 HTTP 轮询，间隔 50ms，单次超时 500ms。
- `McpReadyMs`：健康后发送一次 MCP `initialize`，成功返回 `result` 的时间。
- `IdleMemoryMb`：健康后等待 `-IdleSeconds`（默认 10 秒）再取 `PrivateMemorySize64`。

1.11.3 之前脚本会先对 `5199..5210` 做 TCP 探测，每个端口最多等待 150ms；在端口未就绪时最多引入约 1.8 秒额外开销，导致历史数字偏高。现在改为直连 5199 的 HTTP 轮询，并新增 `McpReadyMs`。

## 目标与历史基线

H1 目标：MCP 就绪 < 1.5s，空闲内存 < 150MB。

| 通道 | 健康就绪 | MCP 就绪 | 空闲私有内存 | 说明 |
| --- | ---: | ---: | ---: | --- |
| 1.11.3 JIT | 588–593 ms | 664–690 ms | 100–108 MB | 本机连续 3 次，二进制已被系统和杀毒软件缓存 |
| 1.11.3 NativeAOT | 579–589 ms | 581–590 ms | 约 81 MB | 同一机器；AOT 只支持 Windows |
| 1.11.1（旧脚本） | 2328 ms | 未测 | 约 87 MB | 旧端口扫描最多引入约 1.8s 探测开销，不能与新数字直接比较 |

- 首次运行刚发布的二进制仍受杀毒扫描和磁盘缓存影响：1.11.3 JIT 首次约 2.8s，AOT 首次约 1.3–1.7s；第二次起稳定在 0.6s 左右。
- MCP 就绪早于桌面窗口创建；创建窗口、应用主题和托盘后进程还会继续占用内存，本指标只代表服务已经可以被 CI/Agent 调用。
- 脚本只做本地烟雾，不作为 CI 硬门禁；发布前手工跑一次并记录结果。
- 内存取 `PrivateMemorySize64`，与任务管理器“专用工作集”不完全一致。
