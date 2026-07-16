using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MCPHost
{
    public class DatabaseService
    {
        private readonly string _connectionString;

        // 执行计划（SHOWPLAN）专用连接。若未单独配置，则回退到默认连接。
        private readonly string _planConnectionString;

        // 默认命令超时（秒），可在 appsettings.json 的 Database:CommandTimeoutSeconds 覆盖。
        public int DefaultCommandTimeoutSeconds { get; }

        // 允许的最大命令超时（秒），防止单条查询长时间占用连接。
        public int MaxCommandTimeoutSeconds { get; }

        public DatabaseService(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("Default")
                ?? throw new Exception("未找到数据库连接字符串。");

            // get_query_plan 需要 SHOWPLAN 权限，用独立账号；没配就沿用默认账号。
            _planConnectionString = configuration.GetConnectionString("Plan") ?? _connectionString;

            DefaultCommandTimeoutSeconds = configuration.GetValue<int?>("Database:CommandTimeoutSeconds") ?? 30;
            MaxCommandTimeoutSeconds = configuration.GetValue<int?>("Database:MaxCommandTimeoutSeconds") ?? 120;
        }

        private int ClampTimeout(int? seconds)
        {
            var value = seconds ?? DefaultCommandTimeoutSeconds;
            if (value <= 0) value = DefaultCommandTimeoutSeconds;
            return Math.Min(value, MaxCommandTimeoutSeconds);
        }

        public async Task<object?> ExecuteScalarAsync(string sql)
        {
            using var conn = new SqlConnection(_connectionString);

            await conn.OpenAsync();

            using var cmd = new SqlCommand(sql, conn);

            return await cmd.ExecuteScalarAsync();
        }
        public async Task<DataTable> ExecuteQueryAsync(string sql)
        {
            var table = new DataTable();

            using var conn = new SqlConnection(_connectionString);

            await conn.OpenAsync();

            using var cmd = new SqlCommand(sql, conn);

            using var reader = await cmd.ExecuteReaderAsync();

            table.Load(reader);

            return table;
        }

        /// <summary>
        /// 在一个只读事务中执行查询，执行完毕后回滚。
        /// 即使 SQL 里混入了写操作也不会真正落库，是代码层最后一道只读防线。
        /// 使用 ReadUncommitted 隔离级别做纯分析，避免被其它事务的锁阻塞而超时。
        ///
        /// 关键点：最多只从数据库读取 maxRows 行（额外多探一行用于判断是否还有更多），
        /// 不再把整表加载进内存，从源头减少数据传输、内存占用和 token 消耗。
        /// 返回 (数据表, 是否还有更多行)。
        /// </summary>
        public async Task<(DataTable Table, bool HasMore)> ExecuteReadOnlyQueryAsync(
            string sql,
            IEnumerable<(string Name, object? Value)>? parameters = null,
            int? commandTimeoutSeconds = null,
            int maxRows = int.MaxValue)
        {
            var table = new DataTable();
            bool hasMore = false;

            using var conn = new SqlConnection(_connectionString);

            await conn.OpenAsync();

            using var tran = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.ReadUncommitted);

            try
            {
                using var cmd = new SqlCommand(sql, conn, tran)
                {
                    CommandTimeout = ClampTimeout(commandTimeoutSeconds)
                };

                if (parameters != null)
                {
                    foreach (var (name, value) in parameters)
                    {
                        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
                    }
                }

                using var reader = await cmd.ExecuteReaderAsync();

                // 用 reader 的架构建列（处理空列名/重名，避免 DataTable 抛错）。
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    string name = reader.GetName(i);
                    if (string.IsNullOrWhiteSpace(name)) name = $"Column{i + 1}";

                    string unique = name;
                    int suffix = 1;
                    while (!usedNames.Add(unique))
                        unique = $"{name}_{suffix++}";

                    table.Columns.Add(unique, typeof(object));
                }

                // 只读取 maxRows 行；再多读一行就说明还有更多，随即停止。
                int read = 0;
                while (await reader.ReadAsync())
                {
                    if (read >= maxRows)
                    {
                        hasMore = true;
                        break;
                    }

                    var values = new object[reader.FieldCount];
                    reader.GetValues(values);
                    table.Rows.Add(values);
                    read++;
                }
            }
            finally
            {
                // 只读工具：无论如何都回滚，绝不提交任何变更。
                await tran.RollbackAsync();
            }

            return (table, hasMore);
        }

        /// <summary>
        /// 获取查询的“预估执行计划”（Estimated Execution Plan）。
        /// SET SHOWPLAN_XML ON 时数据库只编译不执行查询，因此不会真正读数据、也不会因数据量大而超时，
        /// 非常适合分析/优化慢查询、慢存储过程。返回执行计划的 XML 文本。
        /// </summary>
        public async Task<string?> GetEstimatedPlanAsync(string sql, int? commandTimeoutSeconds = null)
        {
            using var conn = new SqlConnection(_planConnectionString);

            await conn.OpenAsync();

            int timeout = ClampTimeout(commandTimeoutSeconds);

            // SET SHOWPLAN_XML 必须单独成批执行。
            using (var on = new SqlCommand("SET SHOWPLAN_XML ON;", conn) { CommandTimeout = timeout })
            {
                await on.ExecuteNonQueryAsync();
            }

            try
            {
                using var cmd = new SqlCommand(sql, conn) { CommandTimeout = timeout };

                // 计划开启时，执行返回的是计划 XML，而不是真正的查询结果。
                var result = await cmd.ExecuteScalarAsync();

                return result?.ToString();
            }
            finally
            {
                using var off = new SqlCommand("SET SHOWPLAN_XML OFF;", conn) { CommandTimeout = timeout };
                await off.ExecuteNonQueryAsync();
            }
        }
    }
}
