using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MCPHost.Tools
{
    [McpServerToolType]
    public class DatabaseTool
    {
        private readonly DatabaseService _database;

        // 数据查询默认返回行数上限（刻意设小，做任务时只取样本，避免消耗过多 token）。
        private const int DefaultMaxRows = 10;

        // 单次数据查询允许的最大行数（调用方最多能要这么多）。
        private const int HardMaxRows = 5000;

        // 元数据/结构类查询（表、列、外键等）的行数上限，通常不会超，给得宽松些。
        private const int MetadataMaxRows = 20000;

        // 序列化结果的最大字符数，超过则继续裁剪行，避免撑爆上下文。
        private const int MaxOutputChars = 60000;

        public DatabaseTool(DatabaseService database)
        {
            _database = database;
        }

        [McpServerTool]
        [Description("获取当前连接的数据库名称。只读操作，不会修改数据库。返回纯文本的数据库名。")]
        public async Task<string> GetDatabaseName()
        {
            try
            {
                var result = await _database.ExecuteScalarAsync("SELECT DB_NAME()");

                return result?.ToString() ?? "";
            }
            catch (Exception ex)
            {
                return Error(ex);
            }
        }

        [McpServerTool]
        [Description("列出数据库中所有的表（table），含所属架构 schema。当需要了解数据库有哪些表、探索整体结构、或不确定表名时使用。只读操作。返回 JSON，含 returned/truncated/rows 字段，每行有 schema、table。")]
        public async Task<string> ListTables()
        {
            try
            {
                var (table, hasMore) = await _database.ExecuteReadOnlyQueryAsync(@"
SELECT s.name AS [schema], t.name AS [table]
FROM sys.tables t
JOIN sys.schemas s ON t.schema_id = s.schema_id
ORDER BY s.name, t.name;", maxRows: MetadataMaxRows);

                return ToJson(table, hasMore);
            }
            catch (Exception ex)
            {
                return Error(ex);
            }
        }

        [McpServerTool]
        [Description("列出数据库中所有的视图（view），含所属架构 schema。当需要查找有哪些视图时使用；要看某个视图的定义源码请用 GetObjectDefinition。只读操作。返回 JSON，含 returned/truncated/rows 字段，每行有 schema、view。")]
        public async Task<string> ListViews()
        {
            try
            {
                var (table, hasMore) = await _database.ExecuteReadOnlyQueryAsync(@"
SELECT s.name AS [schema], v.name AS [view]
FROM sys.views v
JOIN sys.schemas s ON v.schema_id = s.schema_id
ORDER BY s.name, v.name;", maxRows: MetadataMaxRows);

                return ToJson(table, hasMore);
            }
            catch (Exception ex)
            {
                return Error(ex);
            }
        }

        [McpServerTool]
        [Description("列出数据库中所有的存储过程（stored procedure）和函数（function），含类型与所属架构 schema。当需要查找有哪些存储过程或函数时使用；要看具体某个的源码请用 GetObjectDefinition。只读操作。返回 JSON，含 returned/truncated/rows 字段，每行有 schema、name、type。")]
        public async Task<string> ListProceduresAndFunctions()
        {
            try
            {
                var (table, hasMore) = await _database.ExecuteReadOnlyQueryAsync(@"
SELECT s.name AS [schema], o.name AS [name],
       CASE o.type
            WHEN 'P'  THEN N'存储过程'
            WHEN 'FN' THEN N'标量函数'
            WHEN 'IF' THEN N'内联表值函数'
            WHEN 'TF' THEN N'表值函数'
            ELSE o.type_desc
       END AS [type]
FROM sys.objects o
JOIN sys.schemas s ON o.schema_id = s.schema_id
WHERE o.type IN ('P','FN','IF','TF')
ORDER BY s.name, o.name;", maxRows: MetadataMaxRows);

                return ToJson(table, hasMore);
            }
            catch (Exception ex)
            {
                return Error(ex);
            }
        }

        [McpServerTool]
        [Description("获取指定表的列结构（schema/字段定义），包括列名、数据类型、长度、精度、是否可空、是否自增、默认值、是否主键。当开发功能需要知道某张表有哪些字段及其类型时使用。只读操作。返回 JSON，含 returned/truncated/rows 字段。")]
        public async Task<string> DescribeTable(
            [Description("表名，可带架构，如 'dbo.Users' 或 'Users'。若省略架构且存在多个同名表，将返回所有匹配的表的列。")] string tableName)
        {
            try
            {
                var (schema, name) = SplitName(tableName);

                var (table, hasMore) = await _database.ExecuteReadOnlyQueryAsync(@"
SELECT
    c.column_id                                   AS [ordinal],
    c.name                                        AS [column],
    ty.name                                       AS [type],
    c.max_length                                  AS [max_length],
    c.precision                                   AS [precision],
    c.scale                                       AS [scale],
    c.is_nullable                                 AS [is_nullable],
    c.is_identity                                 AS [is_identity],
    dc.definition                                 AS [default],
    CASE WHEN pk.column_id IS NOT NULL THEN 1 ELSE 0 END AS [is_primary_key]
FROM sys.columns c
JOIN sys.tables t             ON c.object_id = t.object_id
JOIN sys.schemas s           ON t.schema_id = s.schema_id
JOIN sys.types ty            ON c.user_type_id = ty.user_type_id
LEFT JOIN sys.default_constraints dc ON c.default_object_id = dc.object_id
LEFT JOIN (
    SELECT ic.object_id, ic.column_id
    FROM sys.indexes i
    JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
    WHERE i.is_primary_key = 1
) pk ON c.object_id = pk.object_id AND c.column_id = pk.column_id
WHERE t.name = @table
  AND (@schema IS NULL OR s.name = @schema)
ORDER BY c.column_id;",
                    new (string, object?)[] { ("@table", name), ("@schema", schema) },
                    maxRows: MetadataMaxRows);

                if (table.Rows.Count == 0)
                    return $"未找到表: {tableName}";

                return ToJson(table, hasMore);
            }
            catch (Exception ex)
            {
                return Error(ex);
            }
        }

        [McpServerTool]
        [Description("获取指定表的外键关系（本表列 -> 所引用的表及列）。当需要了解表之间如何关联、做多表 JOIN 或分析数据模型时使用。只读操作。返回 JSON，含 returned/truncated/rows 字段，每行有 fk_name、column、referenced_table、referenced_column。")]
        public async Task<string> GetForeignKeys(
            [Description("表名，可带架构，如 'dbo.Orders' 或 'Orders'")] string tableName)
        {
            try
            {
                var (schema, name) = SplitName(tableName);

                var (table, hasMore) = await _database.ExecuteReadOnlyQueryAsync(@"
SELECT
    fk.name                    AS [fk_name],
    cpa.name                   AS [column],
    rs.name + '.' + rt.name    AS [referenced_table],
    cref.name                  AS [referenced_column]
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
JOIN sys.tables pt   ON fk.parent_object_id = pt.object_id
JOIN sys.schemas ps  ON pt.schema_id = ps.schema_id
JOIN sys.columns cpa ON fkc.parent_object_id = cpa.object_id AND fkc.parent_column_id = cpa.column_id
JOIN sys.tables rt   ON fk.referenced_object_id = rt.object_id
JOIN sys.schemas rs  ON rt.schema_id = rs.schema_id
JOIN sys.columns cref ON fkc.referenced_object_id = cref.object_id AND fkc.referenced_column_id = cref.column_id
WHERE pt.name = @table
  AND (@schema IS NULL OR ps.name = @schema)
ORDER BY fk.name;",
                    new (string, object?)[] { ("@table", name), ("@schema", schema) },
                    maxRows: MetadataMaxRows);

                return ToJson(table, hasMore);
            }
            catch (Exception ex)
            {
                return Error(ex);
            }
        }

        [McpServerTool]
        [Description("获取视图、存储过程或函数的完整定义脚本（源码/实现逻辑）。当需要查看某个存储过程/视图/函数具体是怎么写的、分析其内部 SQL 逻辑时使用。只读操作。返回该对象的 T-SQL 源码文本；若对象不存在或被加密则返回提示信息。")]
        public async Task<string> GetObjectDefinition(
            [Description("对象名，可带架构，如 'dbo.vw_Report' 或 'usp_GetUser'")] string objectName)
        {
            try
            {
                var (table, _) = await _database.ExecuteReadOnlyQueryAsync(
                    "SELECT OBJECT_DEFINITION(OBJECT_ID(@obj)) AS [definition];",
                    new (string, object?)[] { ("@obj", objectName) },
                    maxRows: 1);

                var def = table.Rows.Count > 0 ? table.Rows[0]["definition"] : null;

                if (def == null || def == DBNull.Value)
                    return $"未找到对象定义（可能不存在，或是加密对象）: {objectName}";

                return def.ToString() ?? "";
            }
            catch (Exception ex)
            {
                return Error(ex);
            }
        }

        [McpServerTool]
        [Description("执行自定义只读查询并返回结果（JSON）。当需要查看表中实际数据、验证数据、做聚合统计时使用。仅允许 SELECT / WITH 开头的单条只读语句，任何写操作（INSERT/UPDATE/DELETE/DROP/ALTER/EXEC 等）以及多语句都会被拒绝，且查询在只读事务中执行完毕后回滚。默认只取【10 行】样本以节省 token，数据库底层也只读取这么多行；确需更多时才显式加大 maxRows（最大 5000）。返回 JSON，含 returned/truncated/note/rows 字段。为避免拉取海量数据，请优先用 COUNT/聚合了解规模，或加 WHERE 过滤。")]
        public async Task<string> RunQuery(
            [Description("只读 SQL 查询语句，必须以 SELECT 或 WITH 开头，且只能是单条语句（不要用分号拼接多条）")] string sql,
            [Description("最多返回的行数，默认 10（做任务时取样本足够）。仅在确实需要时才调大，最大 5000")] int maxRows = DefaultMaxRows,
            [Description("查询超时秒数，默认取服务端配置（30 秒），最大 120 秒。查询较慢时可适当调大")] int? timeoutSeconds = null)
        {
            try
            {
                var guardError = ValidateReadOnly(sql);
                if (guardError != null)
                    return $"已拒绝执行（只读保护）: {guardError}";

                int limit = Math.Clamp(maxRows, 1, HardMaxRows);

                var (table, hasMore) = await _database.ExecuteReadOnlyQueryAsync(
                    sql, commandTimeoutSeconds: timeoutSeconds, maxRows: limit);

                return ToJson(table, hasMore);
            }
            catch (Exception ex)
            {
                return Error(ex);
            }
        }

        [McpServerTool]
        [Description("获取查询或存储过程语句的【预估执行计划】(Estimated Execution Plan, XML)，用于分析和优化慢查询/慢脚本。关键优势：数据库只编译不执行该语句，所以不会真正读数据、不会因数据量大而超时，特别适合优化时排查缺失索引、全表扫描等问题。仅接受 SELECT / WITH 开头的单条只读语句。返回执行计划 XML 文本。")]
        public async Task<string> GetQueryPlan(
            [Description("要分析的只读 SQL 语句，必须以 SELECT 或 WITH 开头，且只能是单条语句")] string sql,
            [Description("编译超时秒数，默认取服务端配置（30 秒），最大 120 秒")] int? timeoutSeconds = null)
        {
            try
            {
                var guardError = ValidateReadOnly(sql);
                if (guardError != null)
                    return $"已拒绝执行（只读保护）: {guardError}";

                var plan = await _database.GetEstimatedPlanAsync(sql, timeoutSeconds);

                if (string.IsNullOrEmpty(plan))
                    return "未能获取执行计划。";

                // 执行计划 XML 可能很大，做字符上限保护。
                if (plan.Length > MaxOutputChars)
                    return plan.Substring(0, MaxOutputChars) +
                           $"\n<!-- 执行计划过大，已截断，仅显示前 {MaxOutputChars} 个字符 -->";

                return plan;
            }
            catch (Exception ex)
            {
                return Error(ex);
            }
        }

        // ---------- 只读校验 ----------

        private static readonly string[] ForbiddenKeywords =
        {
            "insert", "update", "delete", "merge", "truncate", "drop", "alter",
            "create", "grant", "revoke", "deny", "exec", "execute", "backup",
            "restore", "shutdown", "reconfigure", "sp_", "xp_", "into",
            "waitfor", "openrowset", "opendatasource", "bulk"
        };

        /// <summary>
        /// 返回 null 表示通过校验；否则返回拒绝原因。
        /// </summary>
        private static string? ValidateReadOnly(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
                return "SQL 为空。";

            // 去掉行注释和块注释，防止用注释绕过关键字检测。
            var cleaned = Regex.Replace(sql, @"--[^\n]*", " ");
            cleaned = Regex.Replace(cleaned, @"/\*.*?\*/", " ", RegexOptions.Singleline);

            var trimmed = cleaned.Trim().TrimEnd(';').Trim();

            // 只允许单条语句：去掉结尾分号后，中间不应再出现分号。
            if (trimmed.Contains(';'))
                return "不允许多条语句。";

            var lower = trimmed.ToLowerInvariant();

            // 必须以 select 或 with 开头（with 用于 CTE）。
            if (!(lower.StartsWith("select") || lower.StartsWith("with")))
                return "只允许 SELECT / WITH 开头的查询。";

            // 按单词边界检查禁用关键字（sp_ / xp_ 特殊处理，因为下划线不是单词边界）。
            foreach (var kw in ForbiddenKeywords)
            {
                string pattern = kw.EndsWith("_")
                    ? $@"\b{Regex.Escape(kw)}"
                    : $@"\b{Regex.Escape(kw)}\b";

                if (Regex.IsMatch(lower, pattern))
                    return $"包含被禁止的关键字: {kw}";
            }

            return null;
        }

        // ---------- 辅助方法 ----------

        private static (string? schema, string name) SplitName(string fullName)
        {
            var parts = fullName.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                return (parts[0].Trim('[', ']', ' '), parts[1].Trim('[', ']', ' '));

            return (null, fullName.Trim('[', ']', ' '));
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            // 紧凑输出（不缩进），大幅减小体积，避免撑爆上下文。
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>
        /// 把（已在数据库层按行数截断的）结果序列化为紧凑 JSON。
        /// hasMore 表示数据库里还有未返回的更多行。若序列化后仍超过字符上限，则继续裁剪行。
        /// </summary>
        private static string ToJson(DataTable table, bool hasMore)
        {
            var rows = new List<Dictionary<string, object?>>();

            foreach (DataRow row in table.Rows)
            {
                var dict = new Dictionary<string, object?>();
                foreach (DataColumn col in table.Columns)
                {
                    var value = row[col];
                    dict[col.ColumnName] = value == DBNull.Value ? null : value;
                }
                rows.Add(dict);
            }

            string json = Serialize(rows, hasMore);

            // 字符上限兜底：即便行数不多，超宽的表也可能过大，继续按比例减少行数。
            while (json.Length > MaxOutputChars && rows.Count > 1)
            {
                int newCount = Math.Max(1, rows.Count / 2);
                rows = rows.GetRange(0, newCount);
                json = Serialize(rows, hasMore: true);
            }

            return json;
        }

        private static string Serialize(List<Dictionary<string, object?>> rows, bool hasMore)
        {
            var payload = new
            {
                returned = rows.Count,
                truncated = hasMore,
                note = hasMore
                    ? "还有更多行未返回（默认只取样本以节省 token）。如需更多请调大 maxRows，或用 COUNT/聚合/WHERE 缩小范围。"
                    : null,
                rows
            };

            return JsonSerializer.Serialize(payload, JsonOptions);
        }

        private static string Error(Exception ex) => $"执行失败: {ex.GetType().Name}: {ex.Message}";
    }
}
