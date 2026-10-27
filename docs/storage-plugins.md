# 远程存储插件开发指南

PostSharp 的远程存储不是写死在主程序里，而是通过插件扩展。主程序只内置本地 SQLite（离线兜底）；PostgreSQL、MySQL、SQL Server、MongoDB 以官方插件随安装包发布，ClickHouse、Doris、StarRocks 作为可选的 OLAP 插件提供；其它数据库（Oracle、Redis、Elasticsearch 等）按同一套契约实现即可。

| 场景 | 官方插件 | 安装方式 |
| --- | --- | --- |
| OLTP 关系型 | PostgreSQL、MySQL、SQL Server | 随安装包发布 |
| NoSQL / 文档 | MongoDB | 随安装包发布 |
| OLAP 分析型 | ClickHouse、Apache Doris、StarRocks | 可选插件，解压到 `plugins` 后重新扫描 |

| 场景 | 可自行扩展的示例 |
| --- | --- |
| OLTP 关系型 | Oracle、DB2、MariaDB、国产关系库 |
| OLAP 分析型 | GreenPlum、DuckDB、Hive、Trino |
| NoSQL / 键值 | Redis、Elasticsearch、Cassandra |
| 本地 | 嵌入式 DuckDB、SQLite 变体 |

## 1. 运行方式

插件目录：

- 用户插件：`<PostSharp 数据目录>/plugins/<插件名>/`
- 随程序发布的插件：`<安装目录>/plugins/<插件名>/`

数据目录在设置窗口「存储」里可以直接看到，点击「打开插件目录」即可定位。程序启动时扫描一次；放入新插件后，在设置窗口点击「重新扫描」即可使用，无需重启。

插件包必须包含：

```text
plugins/
  MyCompany.Storage.ClickHouse/
    plugin.json
    MyCompany.Storage.ClickHouse.dll
    ClickHouse.Client.dll
    ...其它依赖
```

`plugin.json`：

```json
{
    "assembly": "MyCompany.Storage.ClickHouse.dll"
}
```

同一个插件目录只能有一个入口程序集；入口程序集里可以声明多个 `IStorageProviderPlugin` 实现。插件 ID 重复时，用户目录中的插件覆盖同 ID 的随包插件（重新扫描后生效）。
> 插件以当前 Windows 用户权限运行，等同于本机程序。只安装可信来源的插件。

## 2. 契约

引用程序集：`PostSharp.Storage.Abstractions.dll`（随程序发布，也在 `artifacts/plugins/` 各插件目录中）。

```csharp
public interface IStorageProviderPlugin
{
    StorageProviderInfo Info { get; }
    IDocumentBackend Create(StorageBackendOptions options);
}

public sealed record StorageProviderInfo(
    string Id,
    string DisplayName,
    string Description,
    string ConnectionStringPlaceholder,
    string Version,
    string Author
);

public sealed record StorageBackendOptions(
    string ProviderId,
    string ProviderDisplayName,
    string Name,
    string ConnectionString
);

public interface IDocumentBackend
{
    Task<StorageTestResult> TestAsync(CancellationToken cancellationToken = default);

    Task<StorageDocument?> ReadAsync(
        string category,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<StorageDocument>> ReadAllAsync(
        CancellationToken cancellationToken = default
    );

    Task WriteAsync(
        string category,
        string payload,
        long revision,
        CancellationToken cancellationToken = default
    );
}
```

`StorageDocument` 是 `(Category, Payload, Revision, UpdatedAt)`，`Payload` 是整份 JSON 文档字符串。

## 3. 文档存储语义

PostSharp 只有三个逻辑分类：

| category | 内容 |
| --- | --- |
| `settings` | 系统配置 |
| `workspace` | 集合、请求、环境、历史、回收站 |
| `suites` | 自动化套件 |

实现要求：

- **按 category 唯一**，`WriteAsync` 必须是 insert-or-update，不能产生重复行。
- `Payload` 原样保存，不要改写 JSON；PostSharp 自己负责序列化。
- `Revision` 单调递增由调用方传入，插件只保存。
- `UpdatedAt` 使用 UTC，写库建议保存时间类型或 ISO-8601 字符串。
- `ReadAllAsync` 返回全部三类（有多少返回多少）。
- `ReadAsync` 找不到时返回 `null`，不要抛异常。
- `TestAsync` 不能抛异常：成功/失败都返回 `StorageTestResult`，失败消息面向用户，不能只有堆栈。
- 所有方法都应支持 `CancellationToken`，并设置合理的连接/命令超时。

## 4. 关系型数据库（OLTP）

ADO.NET 数据库直接继承 `SqlDocumentBackend`，只实现建表、连接和 upsert：

```csharp
using System.Data.Common;
using PostSharp.Storage.Abstractions;

public sealed class ClickHouseStoragePlugin : IStorageProviderPlugin
{
    public StorageProviderInfo Info { get; } = new(
        "ClickHouse",
        "ClickHouse",
        "远程 ClickHouse 文档库",
        "Host=127.0.0.1;Port=8123;Database=postsharp;Username=default;Password=***",
        "1.0.0",
        "MyCompany"
    );

    public IDocumentBackend Create(StorageBackendOptions options) => new Backend(options);

    private sealed class Backend(StorageBackendOptions options) : SqlDocumentBackend(options)
    {
        protected override DbConnection CreateConnection() =>
            new ClickHouseConnection(Options.ConnectionString);

        protected override string CreateTableSql => """
            CREATE TABLE IF NOT EXISTS PostSharpDocuments (
                Category String,
                Payload String,
                Revision Int64,
                UpdatedAt String
            ) ENGINE = ReplacingMergeTree()
            ORDER BY Category;
            """;

        // ClickHouse 没有通用 UPDATE：按你的版本使用 ReplacingMergeTree + FINAL，
        // 或改成“先删后插”。关键是同一 category 只能读到最新一条。
        protected override string UpsertSql => """
            INSERT INTO PostSharpDocuments (Category, Payload, Revision, UpdatedAt)
            VALUES (@category, @payload, @revision, @updatedAt);
            """;
    }
}
```

OLAP 库（ClickHouse、Doris、StarRocks、GreenPlum）通常没有传统 upsert，实现 `IDocumentBackend` 时按“同 category 只保留最新版本”实现读语义即可，不要求 SQL 形状一致。

## 5. NoSQL / OLAP / 自定义协议

不继承 `SqlDocumentBackend`，直接实现 `IDocumentBackend`。例如 Redis 用 Hash，MongoDB 用集合文档（官方插件就是这种实现）：

```csharp
public sealed class RedisStoragePlugin : IStorageProviderPlugin
{
    public StorageProviderInfo Info { get; } = new(
        "Redis",
        "Redis",
        "Redis 文档存储（Hash）",
        "host:6379,password=***,ssl=false",
        "1.0.0",
        "MyCompany"
    );

    public IDocumentBackend Create(StorageBackendOptions options) => new Backend(options);

    private sealed class Backend(StorageBackendOptions options) : IDocumentBackend
    {
        // 实现 TestAsync / ReadAsync / ReadAllAsync / WriteAsync。
        // 推荐结构：Hash key = "postsharp:documents:<category>"，
        // 字段为 Payload / Revision / UpdatedAt；ReadAll 扫描前缀。
    }
}
```

同样的方式可以接 Oracle、DB2、SQLite 变体、DuckDB、Hive、Trino、Elasticsearch 等。只要满足三个分类的整份文档读写语义即可。

## 6. 官方 OLAP 插件

ClickHouse、Doris、StarRocks 插件源码在 `src/Plugins/PostSharp.Storage.ClickHouse`、`...Doris`、`...StarRocks`，构建脚本会同时生成 zip。

- **ClickHouse**：`ReplacingMergeTree(Revision) ORDER BY Category`；读取使用 `FINAL` 或 `argMax` 取最新 Revision；适合列式分析库。
- **Doris / StarRocks**：通过 MySQL 协议连接，使用 `UNIQUE KEY(Category)` 表；重复写入同一 Category 即覆盖，读取按 Category 唯一。
- 三者都要求连接串指向一个可创建表的 database；首次 `WriteAsync` 会自动建表。
- ClickHouse 默认 HTTP 端口 8123；Doris/StarRocks 默认 MySQL 端口 9030。
- 可选插件不随安装包默认复制，下载 `artifacts/plugins/PostSharp.Storage.ClickHouse.zip` 等解压到 `plugins/` 后点「重新扫描」即可。

## 7. 打包与安装

1. 新建 `net10.0` 类库，引用 `PostSharp.Storage.Abstractions`（不要引用 `PostSharp` 主程序）。
2. 实现 `IStorageProviderPlugin`，提供公共无参构造函数。
3. `plugin.json` 指向入口 DLL。
4. `dotnet publish -c Release -o <输出目录>`，把输出目录（DLL + 依赖 + plugin.json）放进插件目录。
5. 设置窗口点击「重新扫描」，选择新出现的数据库类型，填连接串，点「测试连接」。
6. 绑定「系统配置 / 集合与接口记录 / 套件自动化」三个分类。绑定远程库后，本地 SQLite 仍是离线缓存。

官方插件的构建脚本：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build-storage-plugins.ps1
```

输出在 `artifacts/plugins/<插件名>/` 和同名 `.zip`，可直接分发给用户解压到插件目录。脚本会构建默认四类以及 ClickHouse、Doris、StarRocks 三个可选 OLAP 插件。

## 8. 失败与安全规则

- **连接失败不写远程**：PostSharp 只有在 `TestAsync`、同步或切换成功后才开启该分类的远程回写；连接失败/写入失败会立即暂停远程回写，后续保存只落本地 SQLite。
- 插件加载失败只影响对应插件，主程序继续以本地 SQLite 启动；问题显示在设置窗口的插件状态区。
- 不要吞掉取消/超时；不要返回 `null` 的 `StorageBackendOptions` 或 `IDocumentBackend`。
- 连接串由 PostSharp 用 Windows DPAPI 加密后写入 `storage.json`，插件收到的是明文，不要自行持久化连接串。
- 插件不要访问 UI 线程，不要引用 Avalonia；所有方法应可在后台线程调用。
