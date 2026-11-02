# 无头模式与 CI

`--headless` 只启动 MCP 端点和内置 Mock 服务，不创建主窗口、托盘图标和单实例桌面进程。适合 Docker、Linux CI 和无人值守运行。

## 能力与限制

- MCP 端点仍为 `http://127.0.0.1:5199/mcp`，健康检查为 `http://127.0.0.1:5199/api/health`。
- 沿用当前数据目录中的集合、环境、套件和设置，通过 `POSTSHARP_DATA_DIR` 指定。
- 只监听回环地址；容器内不要用 `-p 5199:5199` 暴露端口，CI 步骤用 `docker exec` 在容器内调用。
- 无头模式不显示窗口，不能做人工调试；需要界面时启动桌面端。
- Linux 需要 Xvfb 和 Avalonia 的 X11 依赖；Windows 可直接运行，macOS 未在 CI 验证。

## 本地启动

Windows：

```powershell
$env:POSTSHARP_DATA_DIR = "C:\ci\postsharp-data"
.\PostSharp.exe --headless
```

Linux：

```bash
export POSTSHARP_DATA_DIR="$PWD/ci-data"
xvfb-run -a ./PostSharp --headless
```

就绪判断：

```bash
curl -fsS http://127.0.0.1:5199/api/health
```

MCP 调用需要先 `initialize`：

```bash
cat > initialize.json <<'JSON'
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"ci","version":"1.0"}}}
JSON
curl -fsS -H 'Content-Type: application/json' \
  --data-binary @initialize.json http://127.0.0.1:5199/mcp > /dev/null
```

## Docker

仓库根目录提供 `Dockerfile`：多阶段构建 Linux x64 自包含包，运行镜像只安装 Xvfb、字体和 X11 依赖，入口脚本先启动 Xvfb，再运行 `./PostSharp --headless`。

```bash
docker build -t postsharp-headless:1.11.19 .
docker run -d --name postsharp-ci -v "$PWD/ci-data:/data" postsharp-headless:1.11.19
docker exec postsharp-ci curl -fsS http://127.0.0.1:5199/api/health
```

镜像带 `HEALTHCHECK`，`docker ps` 显示 `healthy` 后再执行 CI 步骤。数据目录挂载到 `/data`，请求和套件从这里读取。

调用 MCP：

```bash
docker exec -i postsharp-ci curl -fsS \
  -H 'Content-Type: application/json' \
  --data-binary @tools-request.json http://127.0.0.1:5199/mcp > tools.json
```

## GitHub Actions 示例

下面示例在 Ubuntu runner 上启动官方发布包，再用 `postsharp-cli` 运行套件并上传 JUnit、Markdown 和 HTML 报告。CLI 自 v1.11.19 起随主 Release 一起发布（`PostSharp.Cli-<平台>.zip`），Linux 也可以直接用一体化无头包 `PostSharp-headless-linux-x64.zip`。

```yaml
name: API regression

on:
  push:
    branches: [main]
  workflow_dispatch:

jobs:
  suite:
    runs-on: ubuntu-latest
    timeout-minutes: 30

    steps:
      - uses: actions/checkout@v5

      - name: Install Xvfb
        run: |
          sudo apt-get update
          sudo apt-get install -y --no-install-recommends xvfb xauth curl unzip

      - name: Download PostSharp headless bundle
        run: |
          curl -fsSL -o postsharp.zip \
            https://github.com/mrsealhh/postsharp/releases/download/v1.11.19/PostSharp-headless-linux-x64.zip
          unzip -q postsharp.zip -d postsharp
          chmod +x postsharp/PostSharp postsharp/cli/postsharp-cli

      - name: Start headless MCP
        run: |
          mkdir -p ci-data
          export POSTSHARP_DATA_DIR="$PWD/ci-data"
          nohup xvfb-run -a ./postsharp/PostSharp --headless > postsharp.log 2>&1 &
          for i in $(seq 1 60); do
            if curl -fsS http://127.0.0.1:5199/api/health > /dev/null 2>&1; then
              exit 0
            fi
            sleep 1
          done
          cat postsharp.log
          exit 1

      - name: Run pipeline
        run: |
          ./postsharp/cli/postsharp-cli pipeline run qa/order-flow.yaml \
            --data qa/cases.csv \
            --format junit \
            --copy-report test-results/postsharp.xml

      - name: Upload reports
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: postsharp-reports
          path: |
            test-results/
            postsharp.log
```

要点：

- 用 tag 固定 PostSharp 版本，避免 runner 上版本漂移。
- `--junit` / `--copy-report` 可被大多数测试报告面板直接读取；退出码 `0` 表示通过，`1` 表示套件/流水线失败，`2` 表示参数或预检错误，`3` 表示无法连接。
- 失败时保留 `postsharp.log` 和报告，便于定位启动或断言问题。
- 需要套件（UI 里维护的 DAG）时，用 `suite list` 取 id，再 `suite run <id> --junit ...`。

## 可复用流水线 Workflow

仓库提供 `.github/workflows/postsharp-pipeline.yml`：下载无头包、启动服务、执行调用方仓库里的 YAML 流水线、上传 JUnit，一条 `uses` 就能接入产品线测试。

```yaml
name: Product line smoke

on:
  push:
    branches: [main]

jobs:
  api:
    uses: mrsealhh/postsharp/.github/workflows/postsharp-pipeline.yml@v1.11.19
    with:
      version: 1.11.19
      pipeline: qa/order-flow.yaml
      data: qa/cases.csv
      base-url: https://qa.example.com
      format: junit
```

流水线文件和数据文件都放在调用方仓库；`base-url` 会覆盖 YAML 里的 `baseUrl`，方便同一份流水线跑多个环境。

## 可复用 Action

仓库内置复合 Action `.github/actions/postsharp-mcp`，把「安装 Xvfb → 下载 Linux 包 → 无头启动 → 等待健康端点」封装为一步，省掉手写启动脚本。

```yaml
      - name: Start PostSharp MCP
        id: postsharp
        uses: mrsealhh/postsharp/.github/actions/postsharp-mcp@v1.11.19
        with:
          version: 1.11.19
          data-dir: ci-data

      - name: Run suite
        run: |
          postsharp-cli suite run "$SUITE_ID" --junit test-results/postsharp.xml

      - name: Stop PostSharp
        if: always()
        run: pkill -f "$RUNNER_TEMP/postsharp-" 2>/dev/null || true
```

输入：

| 输入 | 必填 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `version` | 是 |  | PostSharp 版本，不带 `v` |
| `data-dir` | 否 | `.postsharp-ci` | 工作区、设置和日志目录 |
| `wait-seconds` | 否 | `60` | 等待健康端点的最长时间 |
| `token` | 否 |  | 可选 MCP token，导出为 `POSTSHARP_MCP_TOKEN` 供后续步骤使用 |

输出：`endpoint`（`http://127.0.0.1:5199`）和 `log`（无头进程日志路径）。

Action 下载的运行时放在 `$RUNNER_TEMP`，启动后会先等待健康端点，再发送一次 MCP `initialize`，确认 MCP 真正可用后才返回；进程会常驻到 job 结束，runner 会自动清理，但建议保留上面的显式停止步骤，便于在同一 job 内重启服务。

## 准备 CI 数据目录

无头模式不创建新套件，CI 使用的数据目录必须先有集合、环境和套件。两种做法：

1. **在 CI 里通过 MCP 播种**：用 `import_content` 导入 Postman/OpenAPI，用 `create_suite_from_collections` 生成套件，再用 `validate_suite` 预检。适合完全从代码和接口描述生成测试。
2. **提交或缓存数据目录**：在本地准备工作区，把 `POSTSHARP_DATA_DIR` 目录作为私有 artifact 或加密缓存。不要把带生产账号和 token 的目录提交到公开仓库；Windows 上用 DPAPI 加密的秘密在 Linux 上无法解密。

`settings.json` 中的 MCP token、只读和细粒度权限同样生效。CI 建议保持默认的本机访问和全权限，或在私有数据目录中设置只读以外的合适权限。

## 当前限制

- 无头模式仍会初始化 Avalonia 基础设施，因此 Linux 必须有可用的 X 服务（Xvfb）。
- 端口固定为 5199，同一容器或 runner 上只能运行一个实例。
- 没有内置的定时器；定时触发交给 GitHub Actions、cron 或外部调度器。
- 报告写入路径由 CLI 参数决定，CI 需要显式上传。
