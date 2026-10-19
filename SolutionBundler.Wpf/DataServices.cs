using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using StackExchange.Redis;

namespace SolutionBundler.Wpf;

internal static class SqlDataService
{
    public static async Task<SqlQuerySnapshot> ExecuteQueryAsync(string connectionString, string query, int limit,
        CancellationToken token)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(query, connection) { CommandTimeout = 120 };
        await using var reader = await command.ExecuteReaderAsync(token);

        while (reader.FieldCount == 0 && await reader.NextResultAsync(token)) { }

        var table = new DataTable();
        if (reader.FieldCount > 0)
        {
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var baseName = string.IsNullOrWhiteSpace(reader.GetName(i)) ? $"Column{i + 1}" : reader.GetName(i);
                var name = baseName;
                var suffix = 2;
                while (!usedNames.Add(name)) name = $"{baseName}_{suffix++}";
                table.Columns.Add(name, typeof(object));
            }

            while (table.Rows.Count < limit && await reader.ReadAsync(token))
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                table.Rows.Add(values);
            }
        }

        var truncated = reader.FieldCount > 0 && table.Rows.Count == limit && await reader.ReadAsync(token);
        return new SqlQuerySnapshot
        {
            Database = connection.Database,
            Query = query,
            RowLimit = limit,
            RowCount = table.Rows.Count,
            IsTruncated = truncated,
            Result = table
        };
    }

    public static string UseDatabase(string connectionString, string database)
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database };
        return builder.ConnectionString;
    }

    public static async Task<List<string>> LoadDatabasesAsync(string connectionString, CancellationToken token)
    {
        var result = new List<string>();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        const string sql = "SELECT name FROM sys.databases WHERE state = 0 AND database_id > 4 AND HAS_DBACCESS(name) = 1 ORDER BY name";
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) result.Add(reader.GetString(0));
        return result;
    }

    public static async Task<List<SqlObjectInfo>> LoadObjectsAsync(string connectionString, CancellationToken token)
    {
        var result = new List<SqlObjectInfo>();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        const string sql = """
            SELECT s.name, o.name, o.type
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.is_ms_shipped = 0
              AND o.type IN ('U', 'V', 'P', 'PC', 'FN', 'IF', 'TF', 'FS', 'FT')
            ORDER BY s.name, o.type, o.name
            """;
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var kind = reader.GetString(2).Trim() switch
            {
                "U" => SqlObjectKind.Table,
                "V" => SqlObjectKind.View,
                "P" or "PC" => SqlObjectKind.StoredProcedure,
                _ => SqlObjectKind.Function
            };
            result.Add(new SqlObjectInfo(reader.GetString(0), reader.GetString(1), kind));
        }
        return result;
    }

    public static async Task<(int tables, long rows)> WriteAsync(StreamWriter writer, string connectionString,
        IReadOnlyList<SqlObjectNode> objects, int limit, CancellationToken token)
    {
        await writer.WriteLineAsync("-- Solution Bundler SQL Server export");
        await writer.WriteLineAsync($"-- Row limit per table: {limit}");
        await writer.WriteLineAsync("SET NOCOUNT ON;");
        await writer.WriteLineAsync("GO");
        long rows = 0;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await writer.WriteLineAsync($"-- Database: [{connection.Database.Replace("]", "]]", StringComparison.Ordinal)}]");
        foreach (var item in objects)
        {
            token.ThrowIfCancellationRequested();
            var qualified = $"[{item.Schema.Replace("]", "]]", StringComparison.Ordinal)}].[{item.Name.Replace("]", "]]", StringComparison.Ordinal)}]";
            await writer.WriteLineAsync();
            if (item.Kind != SqlObjectKind.Table)
            {
                await writer.WriteLineAsync($"-- {item.KindLabel}: {qualified}");
                await using var definitionCommand = new SqlCommand("""
                    SELECT sm.definition
                    FROM sys.sql_modules sm
                    JOIN sys.objects o ON o.object_id = sm.object_id
                    JOIN sys.schemas s ON s.schema_id = o.schema_id
                    WHERE s.name = @schema AND o.name = @name
                    """, connection) { CommandTimeout = 30 };
                definitionCommand.Parameters.Add("@schema", SqlDbType.NVarChar, 128).Value = item.Schema;
                definitionCommand.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = item.Name;
                var definition = await definitionCommand.ExecuteScalarAsync(token) as string;
                await writer.WriteLineAsync(definition ?? $"-- Definition for {qualified} is unavailable or encrypted.");
                await writer.WriteLineAsync("GO");
                continue;
            }

            await writer.WriteLineAsync($"-- Table data: {qualified}");
            await using var command = new SqlCommand($"SELECT TOP (@limit) * FROM {qualified}", connection) { CommandTimeout = 60 };
            command.Parameters.Add("@limit", SqlDbType.Int).Value = limit;
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
            var columns = string.Join(", ", Enumerable.Range(0, reader.FieldCount)
                .Select(i => $"[{reader.GetName(i).Replace("]", "]]", StringComparison.Ordinal)}]"));
            while (await reader.ReadAsync(token))
            {
                var values = new string[reader.FieldCount];
                for (var i = 0; i < values.Length; i++) values[i] = ToSqlLiteral(reader.GetValue(i));
                await writer.WriteLineAsync($"INSERT INTO {qualified} ({columns}) VALUES ({string.Join(", ", values)});");
                rows++;
            }
            await writer.WriteLineAsync("GO");
        }
        return (objects.Count(item => item.Kind == SqlObjectKind.Table), rows);
    }

    public static async Task WriteQuerySnapshotsAsync(StreamWriter writer,
        IReadOnlyCollection<SqlQuerySnapshot> queries, CancellationToken token)
    {
        var index = 0;
        foreach (var snapshot in queries)
        {
            token.ThrowIfCancellationRequested();
            index++;
            await writer.WriteLineAsync();
            await writer.WriteLineAsync(new string('-', 100));
            await writer.WriteLineAsync($"-- Query #{index} | Rows: {snapshot.RowCount} | Limit: {snapshot.RowLimit}" +
                                        (snapshot.IsTruncated ? " | More rows available" : string.Empty));
            if (!string.IsNullOrWhiteSpace(snapshot.Database))
                await writer.WriteLineAsync($"-- Database: {snapshot.Database}");
            await writer.WriteLineAsync("-- Query:");
            foreach (var line in snapshot.Query.Trim().Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
                await writer.WriteLineAsync($"-- {line}");
            await writer.WriteLineAsync("-- Result:");
            if (snapshot.Result.Columns.Count == 0)
            {
                await writer.WriteLineAsync("-- (no result set)");
                continue;
            }

            await writer.WriteLineAsync("-- " + string.Join('\t', snapshot.Result.Columns.Cast<DataColumn>().Select(x => EscapeCell(x.ColumnName))));
            foreach (DataRow row in snapshot.Result.Rows)
            {
                token.ThrowIfCancellationRequested();
                await writer.WriteLineAsync("-- " + string.Join('\t', row.ItemArray.Select(FormatResultValue)));
            }
        }
    }

    private static string FormatResultValue(object? value) => value switch
    {
        null or DBNull => "NULL",
        byte[] bytes => $"0x{Convert.ToHexString(bytes)}",
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
        _ => EscapeCell(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)
    };

    private static string EscapeCell(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);

    private static string ToSqlLiteral(object value) => value switch
    {
        DBNull => "NULL",
        byte[] bytes => $"0x{Convert.ToHexString(bytes)}",
        bool boolean => boolean ? "1" : "0",
        DateTime date => $"'{date:O}'",
        DateTimeOffset date => $"'{date:O}'",
        TimeSpan time => $"'{time:c}'",
        Guid guid => $"'{guid:D}'",
        string text => $"N'{text.Replace("'", "''", StringComparison.Ordinal)}'",
        char character => $"N'{character.ToString().Replace("'", "''", StringComparison.Ordinal)}'",
        float or double or decimal or byte or sbyte or short or ushort or int or uint or long or ulong =>
            Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL",
        _ => $"N'{(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty).Replace("'", "''", StringComparison.Ordinal)}'"
    };
}

internal static class RedisDataService
{
    public static async Task<TimeSpan> PingAsync(string connectionString, int database)
    {
        var config = ConfigurationOptions.Parse(connectionString); config.AbortOnConnectFail = false;
        using var connection = await ConnectionMultiplexer.ConnectAsync(config);
        return await connection.GetDatabase(database).PingAsync();
    }

    public static async Task<int> WriteAsync(StreamWriter writer, string connectionString, int databaseNumber,
        string pattern, int keyLimit, int memberLimit, CancellationToken token)
    {
        var config = ConfigurationOptions.Parse(connectionString); config.AbortOnConnectFail = false;
        using var connection = await ConnectionMultiplexer.ConnectAsync(config);
        var database = connection.GetDatabase(databaseNumber);
        var keys = await Task.Run(() =>
        {
            var result = new List<RedisKey>(); var seen = new HashSet<string>();
            foreach (var endpoint in connection.GetEndPoints())
            {
                var server = connection.GetServer(endpoint);
                if (!server.IsConnected || server.IsReplica) continue;
                foreach (var key in server.Keys(databaseNumber, pattern, pageSize: 250))
                {
                    token.ThrowIfCancellationRequested();
                    if (seen.Add(key.ToString())) result.Add(key);
                    if (result.Count >= keyLimit) return result;
                }
            }
            return result;
        }, token);

        await writer.WriteLineAsync();
        await writer.WriteLineAsync(new string('=', 120));
        await writer.WriteLineAsync($"REDIS DATA | DB: {databaseNumber} | PATTERN: {pattern} | KEY LIMIT: {keyLimit}");
        await writer.WriteLineAsync(new string('=', 120));
        foreach (var key in keys)
        {
            var type = await database.KeyTypeAsync(key);
            var ttl = await database.KeyTimeToLiveAsync(key);
            await writer.WriteLineAsync($"\nKEY: {Escape(key.ToString())}\nTYPE: {type} | TTL: {(ttl.HasValue ? ttl.Value : "none")}");
            switch (type)
            {
                case RedisType.String:
                    await writer.WriteLineAsync($"VALUE: {Escape((await database.StringGetAsync(key)).ToString())}"); break;
                case RedisType.List:
                    foreach (var value in await database.ListRangeAsync(key, 0, memberLimit - 1)) await writer.WriteLineAsync(Escape(value.ToString())); break;
                case RedisType.Hash:
                    foreach (var entry in database.HashScan(key).Take(memberLimit)) await writer.WriteLineAsync($"{Escape(entry.Name.ToString())}\t{Escape(entry.Value.ToString())}"); break;
                case RedisType.Set:
                    foreach (var value in database.SetScan(key).Take(memberLimit)) await writer.WriteLineAsync(Escape(value.ToString())); break;
                case RedisType.SortedSet:
                    foreach (var value in await database.SortedSetRangeByRankWithScoresAsync(key, 0, memberLimit - 1)) await writer.WriteLineAsync($"{value.Score}\t{Escape(value.Element.ToString())}"); break;
                default: await writer.WriteLineAsync($"<{type} is not expanded>"); break;
            }
        }
        return keys.Count;
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
}
