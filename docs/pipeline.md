# 流水线（YAML / JSON）

把「请求 A → 取响应字段 → 请求 B → 用 A、B 的结果发请求 C」写成一份文件，直接交给 CLI 或 MCP 执行。
适合放进新项目做自动化测试，也适合在 CI 里跑冒烟。

## 最小示例

```yaml
name: 订单流程
baseUrl: http://127.0.0.1:5199
steps:
  - id: respA
    method: POST
    url: /api/orders
    json:
      item: book
    expect:
      status: 201
      jsonPath:
        id: { exists: true }

  - id: respB
    method: GET
    url: /api/orders/${respA.id}
    expect:
      status: 200

  - method: PATCH
    url: /api/orders/${respA.id}
    json:
      param1: "${respB.id}"
    expect:
      status: 200
```

执行顺序：创建订单得到 `respA.id`，用它查询得到 `respB`，再用两者的字段发 PATCH。

## 运行

```bash
# 纯 CLI：不启动 PostSharp，直接在本机执行
postsharp-cli pipeline run docs/examples/order-flow.yaml --local --format junit

# 只校验（不发送请求）
postsharp-cli pipeline validate docs/examples/order-flow.yaml

# 输出只读流程图（Mermaid / DOT / JSON）
postsharp-cli pipeline graph docs/examples/parallel.yaml
postsharp-cli pipeline graph docs/examples/parallel.yaml --format dot

# 执行并打印每组数据的步骤结果
postsharp-cli pipeline run docs/examples/order-flow.yaml

# 输出 JSON（适合脚本解析），同时写 JUnit 报告
postsharp-cli pipeline run docs/examples/order-flow.yaml --json --format junit

# 把报告复制到 CI 工作区（服务端与 CLI 同一台机器时）
postsharp-cli pipeline run docs/examples/order-flow.yaml --format junit --copy-report test-results/postsharp.xml

# 用 CSV 数据矩阵跑多组
postsharp-cli pipeline run docs/examples/data-matrix.yaml --data docs/examples/order-cases.csv
```

CLI 通过本机 MCP 端点（默认 `http://127.0.0.1:5199/mcp`）调用 PostSharp，要求 PostSharp 正在运行；
无界面环境可用 `PostSharp.exe --headless` 或 Docker 镜像。

退出码：

| 退出码 | 含义 |
| ---: | --- |
| 0 | 全部通过，或校验通过 |
| 1 | 有步骤失败 / 校验有错误 |
| 2 | 参数错误、服务端拒绝（例如流水线校验失败） |
| 3 | 连接失败或取消 |

### MCP 工具

```jsonc
// 只校验
{ "name": "validate_pipeline", "arguments": { "document": "<YAML 文本>" } }

// 执行并写报告
{ "name": "run_pipeline", "arguments": { "document": "<YAML 文本>", "format": "junit", "vars": { "tenant": "demo" } } }

// 服务端已有的文件
{ "name": "run_pipeline", "arguments": { "path": "D:/ci/order-flow.yaml" } }
```

`document` 与 `path` 二选一；跨机器调用时用 `document`，并把数据文件内容通过 `dataContent` + `dataFormat` 一起传。

只读可视化用 `visualize_pipeline`：返回节点/边、层级、Mermaid 和 DOT，不发送请求。Mermaid 可直接贴进 Markdown 或 PR 描述。

## 可视化（只读）

```bash
postsharp-cli pipeline graph flow.yaml                 # Mermaid
postsharp-cli pipeline graph flow.yaml --format dot    # Graphviz DOT
postsharp-cli pipeline graph flow.yaml --format json   # 节点/边/图文本
```

自动化页「流水线」→「生成流程图」会用 Arc 工作流画布直接渲染节点卡片和依赖箭头（可缩放、平移、适应窗口），不再显示 Mermaid 文本。循环体按顺序串联，并发组从组节点扇出；画布只读。

MCP 对应 `visualize_pipeline { document | path }`。输出结构：

- `nodes`：`id`、`kind`（request / loop / parallel）、`label`、`method`、`url`、`parentId`、`order`
- `edges`：顺序依赖；并发组内不生成兄弟边
- `mermaid` / `dot`：可直接渲染的文本

可视化是只读的：不执行请求，也不替代 YAML。需要拖拽编辑时仍用测试工作流编排器（流水线转测试工作流的导入尚未实现）。

## 图像编辑

自动化页 →「流水线」→「图像编辑」提供树形编辑器：

- 节点类型：请求、循环、并发组；分组可以继续添加子步骤/子循环/子并发。
- 请求字段：名称、别名、方法、URL、请求头、查询参数、请求体（JSON/原始）、期望状态、`bodyContains`、JSONPath、重试、失败策略。
- 分组字段：循环的 `over/as/index/max`，并发组的 `max`。
- 支持添加、删除、上移/下移；树形结构直接对应 `steps` 的嵌套。
- 保存时只改写 `steps` 与 `name`/`baseUrl`，保留 `data`、`vars`、`retry`、`defaults` 等未编辑字段，并用同一套解析器校验。
- 编辑器保存后把 YAML 回填到流水线页，再点「保存」写入 `pipelines` 文件。

图像编辑目前是「结构化树 + 明细面板」，不是自由拖拽画布：流水线的顺序/循环/并发是树形语义，用树编辑比任意 DAG 更直观，也不会产生无法序列化的连线。

## 字段参考

### 顶层

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `name` | string | 报告和结果里的名称，默认「未命名流水线」 |
| `baseUrl` | string | 相对地址的前缀；同时以 `${baseUrl}` 暴露给模板 |
| `timeoutSeconds` / `timeoutMs` | int | 单次请求超时，默认 60 秒 |
| `maxParallelism` | int | 并发组默认并行数，默认 4，范围 1-64 |
| `stopOnError` | bool | 失败后是否停止后续步骤，默认 `true` |
| `retry` | object/int | 默认重试策略，见下文 |
| `vars` | object | 自定义变量，模板里写 `${名称}` |
| `defaults.headers` | object | 所有请求共享的请求头 |
| `data` | object/array | 数据驱动矩阵，见下文 |
| `steps` | array | 步骤或循环节点 |

### 步骤

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` / `as` / `saveAs` | string | 步骤别名；成功后响应 JSON 注册为它 |
| `name` | string | 展示名称 |
| `method` | string | 默认 `GET` |
| `url` | string | 绝对地址或相对 `baseUrl` 的路径，可含 `${...}` |
| `headers` | object | 该步骤的请求头，同名覆盖 `defaults.headers` |
| `json` | any | 结构化请求体，字符串值可含 `${...}`；整串占位符保留原类型 |
| `body` | string | 原始请求体，与 `json` 二选一 |
| `form` | object | 表单字段；写 `form` 时请求体为 `application/x-www-form-urlencoded` |
| `query` | object | 查询参数，自动追加到 URL（含中文与特殊字符时按 URL 编码） |
| `headers` | object | 该步骤的请求头，同名覆盖 `defaults.headers` |
| `contentType` | string | 默认 `application/json`（有 `json` 时）或 `application/x-www-form-urlencoded`（有 `form` 时） |
| `redirects` | bool | 是否跟随 3xx，默认 `true` |
| `timeoutSeconds` | int | 该步骤的超时秒数；省略时跟随流水线 |
| `auth` | object | `{ username, password }`，自动生成 `Authorization: Basic ...`；显式 `headers.Authorization` 优先 |
| `expect` | int/string/object | 断言，见下文 |
| `retry` | object/int | 该步骤的重试策略，覆盖顶层 |
| `onFailure` | `stop` / `continue` | 该步骤失败后是否继续 |

`id` 只能由字母、数字、下划线等组成（不要带点或空格），后续用 `${id.字段}` 引用。

### 断言 `expect`

```yaml
expect:
  status: 201               # 也可以是 [200, 201]，或 "2xx"
  bodyContains: ["success"] # 单个字符串也可以
  jsonPath:
    data.id: { exists: true }
    data.status: { equals: "ok" }
    data.name: { contains: "book" }
```

- 不写 `status` 时接受任意 2xx。
- `jsonPath` 路径支持 `data.token`、`items[0].id`，也可写 `$.data.token`。
- 响应不是 JSON 时，`jsonPath` 断言直接失败。

### 重试

```yaml
retry:
  count: 2          # 额外重试次数，最多 10
  delayMs: 500      # 每次重试前等待
  on: [network, 500, 5xx]   # 触发重试的状态码；network 表示连接失败/超时
  onNetworkError: true      # 等价于 on 里包含 network
```

默认触发：网络错误与 408/429/500/502/503/504。重试后仍不满足断言则记为失败。

### 数据驱动

```yaml
data:
  rows:
    - { user: alice, amount: 12 }
    - { user: bob, amount: 20 }
```

或指向数据文件：

```yaml
data:
  file: cases.csv
```

CSV 首行是表头，其余每行一组；JSON 是对象数组。每行跑一遍整条流水线：

- `${row.user}` 或直接 `${user}`
- 行里的数组字段可用于循环，例如 `ids: [1, 2]` → `over: ${ids}`

跨机器执行时，用 CLI `--data <文件>`（会读本地文件并内联传给服务端）或 MCP `dataContent` + `dataFormat`。

### 循环

```yaml
steps:
  - id: list
    method: GET
    url: /api/items

  - loop:
      over: ${list}        # 必须是数组
      as: item             # 循环项变量
      index: i             # 可选：下标变量
      max: 1000            # 可选：安全上限，超过直接失败
      steps:
        - method: GET
          url: /api/items/${item.id}?index=${i}
```

循环可以嵌套；内层循环覆盖同名变量。循环体内步骤失败时按 `stopOnError` / `onFailure` 处理。

### 并发

```yaml
maxParallelism: 4        # 并发组默认并行数，1-64

steps:
  - id: login
    method: POST
    url: /api/login
    json: { user: demo }

  - parallel:
      max: 3             # 本组最多 3 个同时执行
      steps:
        - id: health
          method: GET
          url: /api/health
        - id: users
          method: GET
          url: /api/users
        - id: detail
          method: GET
          url: /api/users/1
```

- 组内步骤同时发起，`max` 控制并发度；省略时用流水线的 `maxParallelism`。
- 组内步骤**互不可见**：不能引用同组其他步骤的别名；整组结束后，它们的别名才对后续步骤可见。
- 组内某步失败且 `stopOnError: true` 时，会取消尚未完成的同组请求；已完成的步骤结果仍保留。
- 并发组可以嵌套在循环里，也可以包含循环。

## 结果与报告

`run_pipeline` 返回：

```jsonc
{
  "name": "订单流程",
  "passed": true,
  "elapsedMs": 165,
  "cases": [
    {
      "index": 0,
      "label": "第 1 组（user=\"alice\"）",
      "passed": true,
      "elapsedMs": 160,
      "steps": [
        {
          "id": "respA",
          "name": "创建订单",
          "method": "POST",
          "url": "http://127.0.0.1:5199/api/orders",
          "statusCode": 201,
          "passed": true,
          "attempts": 1,
          "elapsedMs": 42,
          "error": null,
          "assertions": ["状态码 201", "jsonPath id 存在"]
        }
      ]
    }
  ],
  "notes": [],
  "reportFileName": "pipeline-...-20261102-153941.xml",
  "reportPath": "F:/.../.postsharp/reports/pipeline-...xml"
}
```

报告写到数据目录 `reports/`，同时通过 MCP `resources/read` 的 `postsharp://reports/{文件名}` 暴露给 AI。
`format` 支持 `markdown` / `junit` / `json` / `none`。

## 与套件的区别

| | 套件 | 流水线 |
| --- | --- | --- |
| 编辑 | 三栏可视化编排器 | YAML/JSON 文本 |
| 存储 | 写入工作区，可定时 | 文件，随项目走 |
| 分支 | DAG、条件边、All/Any 汇合 | 顺序 + 循环 |
| 断言 | 状态码、bodyContains、DAG 条件 | 状态码、bodyContains、JSONPath |
| 适用 | 长期维护、需要回看历史 | CI、脚本化冒烟、AI 一次调用 |

需要分支/汇合或可视化维护时仍用套件；需要「一份文件跑完整条业务链」时用流水线。

## 当前限制

- 不支持 multipart 上传、SOAP / gRPC、SSE 聚合；这些仍用套件或单请求。
- 并发只支持显式 `parallel` 组，暂不支持「步骤级 parallel 标记自动分组」。
- 流水线不写入工作区，因此不会出现在自动化页和运行历史里。
- `${...}` 引用只支持 `a.b` 与 `a[0].b`，不支持函数或表达式运算。
