# MCPHost — SQL Server 只读分析 MCP 服务

面向 Cursor 的 SQL Server **只读** MCP（Model Context Protocol）服务。在开发功能时，可直接让 Cursor 查询库结构、查看存储过程源码、跑样本数据、分析执行计划——**不提供任何写/改权限**。

## 功能一览

| 工具 | 说明 |
|------|------|
| `hello` | 连通性自检 |
| `get_database_name` | 当前数据库名 |
| `list_tables` | 列出所有表（含 schema） |
| `list_views` | 列出所有视图 |
| `list_procedures_and_functions` | 列出存储过程与函数 |
| `describe_table` | 表结构（列、类型、主键、默认值等） |
| `get_foreign_keys` | 表外键关系 |
| `get_object_definition` | 视图 / 存储过程 / 函数的完整 T-SQL 源码 |
| `run_query` | 执行只读 `SELECT` / `WITH` 查询（默认只取 **10 行**样本） |
| `get_query_plan` | 预估执行计划（`SHOWPLAN_XML`，只编译不执行） |

## 技术栈

- .NET 8
- [ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol)（stdio 传输）
- Microsoft.Data.SqlClient
- Microsoft.Extensions.Hosting

## 目录结构

```
MCPServiceSolution/
├── MCPServiceSolution.sln
├── README.md
└── MCPHost/
    ├── Program.cs              # 主机入口、配置与 MCP 注册
    ├── DatabaseService.cs      # 数据库访问（只读事务、限行、执行计划）
    ├── appsettings.json        # 连接串与超时配置
    └── Tools/
        ├── HelloTool.cs
        └── DatabaseTool.cs     # 全部数据库分析工具
```

## 快速开始

### 1. 环境要求

- .NET 8 SDK
- 可访问的 SQL Server
- Cursor（或其它 MCP 客户端）

### 2. 配置连接

编辑 `MCPHost/appsettings.json`（**不要把真实密码提交到公开仓库**）：

```json
{
  "ConnectionStrings": {
    "Default": "Server=你的服务器;Database=你的库;User Id=只读账号;Password=***;TrustServerCertificate=True;Connect Timeout=15;",
    "Plan": "Server=你的服务器;Database=你的库;User Id=执行计划专用账号;Password=***;TrustServerCertificate=True;Connect Timeout=15;"
  },
  "Database": {
    "CommandTimeoutSeconds": 30,
    "MaxCommandTimeoutSeconds": 120
  }
}
```

| 配置项 | 用途 |
|--------|------|
| `Default` | 普通查询 / 元数据工具 |
| `Plan` | 仅 `get_query_plan` 使用；未配置时回退到 `Default` |
| `CommandTimeoutSeconds` | 默认命令超时（秒） |
| `MaxCommandTimeoutSeconds` | 单次调用允许的最大超时上限 |

配置从 **exe 所在目录** 加载（`AppContext.BaseDirectory`），不依赖 MCP 客户端的工作目录。

### 3. 编译

```bash
dotnet build MCPHost/MCPHost.csproj -c Release
```

输出目录示例：`MCPHost/bin/Release/net8.0/MCPHost.dll`

### 4. 在 Cursor 中注册 MCP

在 Cursor 的 MCP 配置中增加类似项（路径按本机调整）：

```json
{
  "mcpServers": {
    "sqlserver": {
      "command": "dotnet",
      "args": [
        "D:/MCP/MCPServiceSolution/MCPHost/bin/Release/net8.0/MCPHost.dll"
      ]
    }
  }
}
```

也可直接指向已发布的可执行文件。修改代码后需重新编译，并在 Cursor 中重启该 MCP 服务器。

## 只读安全设计

代码层三重防护（即使登录账号本身有写权限，也尽量挡在应用侧）：

1. **关键字白名单**：`run_query` / `get_query_plan` 仅允许以 `SELECT` 或 `WITH` 开头的单条语句；命中 `INSERT` / `UPDATE` / `DELETE` / `DROP` / `EXEC` 等即拒绝。
2. **禁止多语句**：去掉注释后不允许中间出现分号，防止拼接攻击。
3. **只读事务兜底**：查询在 `ReadUncommitted` 事务中执行，结束后强制 `Rollback`，写操作不会落库。

建议在 SQL Server 侧再给 `Default` 账号只授 `db_datareader`，形成纵深防御。

## Token 与性能控制

| 机制 | 说明 |
|------|------|
| 默认 10 行样本 | `run_query` 默认 `maxRows=10`，做任务时只看样本 |
| 底层只读 N 行 | 不再 `Load` 整表，读够即停，减少网络与内存 |
| 紧凑 JSON | 无缩进序列化，并有约 6 万字符上限兜底 |
| 超时可调 | 默认 30 秒，单次最高 120 秒；`run_query` / `get_query_plan` 支持 `timeoutSeconds` |

需要更多行时显式传 `maxRows`（最大 5000），或在 SQL 中用 `WHERE` / `COUNT` / 聚合缩小范围。

## 执行计划专用账号（可选）

`get_query_plan` 依赖 `SHOWPLAN` 权限。可单独配置 `ConnectionStrings:Plan`，与日常查询账号隔离。

用管理员在目标库执行（按需改登录名与密码）：

```sql
USE master;
GO
CREATE LOGIN mcp_plan WITH PASSWORD = '你的强密码';
GO

USE YourDatabase;
GO
CREATE USER mcp_plan FOR LOGIN mcp_plan;
GO
GRANT SHOWPLAN TO mcp_plan;
GO
-- 可选：若该账号也要能读数据
ALTER ROLE db_datareader ADD MEMBER mcp_plan;
GO
```

然后将 `appsettings.json` 中 `Plan` 的 `User Id` / `Password` 改成该账号。

## 典型用法（给 Cursor 的提示）

- 「列出数据库有哪些表」→ `list_tables`
- 「看一下 `EMS_PollWorker` 的字段」→ `describe_table`
- 「这个存储过程源码是啥」→ `get_object_definition`
- 「抽几条样本看看数据」→ `run_query`（默认 10 行）
- 「分析这段 SQL 为什么慢」→ `get_query_plan`

## 注意事项

- `appsettings.json` 含数据库凭据，请妥善保管，勿提交到公开仓库。
- MCP 使用 **stdio** 传输，进程内不要向控制台写日志（本项目已 `ClearProviders()`）。
- 修改工具后务必 **重新编译并重启 MCP**，否则 Cursor 仍在跑旧进程。
