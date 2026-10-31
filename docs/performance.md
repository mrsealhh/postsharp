# 启动与内存基线

`scripts/perf-smoke.ps1` 启动一个自包含包，测量冷启动到 `/api/health` 就绪的时间和进程私有内存，用于发布前门禁。

## 使用

```powershell
# JIT Windows 包
.\scripts\perf-smoke.ps1 -Executable .\artifacts\PostSharp-1.11.1\PostSharp.exe

# NativeAOT 包
.\scripts\perf-smoke.ps1 -Executable .\artifacts\PostSharp-1.11.1-aot-win-x64\PostSharp.exe -Json
```

要求：

- 启动前关闭所有 PostSharp 实例；单实例锁会让新进程退出并连到旧进程。
- 脚本使用临时 `POSTSHARP_DATA_DIR`，不会读写本地工作区。
- 结束后自动关闭进程并删除临时目录；失败时保留目录和日志路径方便排查。

## 目标与历史基线

H1 目标：冷启动 < 1.5s，空闲内存 < 150MB。

| 通道 | 冷启动 | 空闲私有内存 | 说明 |
| --- | ---: | ---: | --- |
| JIT 自包含 | 约 1859 ms | 约 87 MB | 1.10.24 实测，受机器和磁盘影响 |
| NativeAOT | 约 812 ms | 约 78 MB | 1.10.24 实测；只支持 Windows |
| 1.11.1 复测（本机） | JIT 2328 ms / AOT 2285 ms | 87 / 66 MB | `perf-smoke` 到健康端点就绪；首次运行受杀毒/磁盘缓存影响 |

- JIT 的冷启动主要花在 .NET 运行时和 Avalonia/Skia 初始化；AOT 明显更快。
- 脚本只做本地烟雾，不作为 CI 硬门禁；发布前手工跑一次并记录结果。
- 内存取 `PrivateMemorySize64`，与任务管理器“专用工作集”不完全一致。
