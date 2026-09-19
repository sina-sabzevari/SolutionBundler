using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using StackExchange.Redis;

namespace SolutionBundler.Wpf;

internal static class SqlDataService
{
    public static async Task<List<(string Schema, string Name)>> LoadTablesAsync(string connectionString, CancellationToken token)
    {
        var result = new List<(string, string)>();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        const string sql = "SELECT s.name,t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE t.is_ms_shipped=0 ORDER BY s.name,t.name";
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) result.Add((reader.GetString(0), reader.GetString(1)));
        return result;
    }

    public static async Task<(int tables, long rows)> WriteAsync(StreamWriter writer, string connectionString,
        IReadOnlyList<TableNode> tables, int limit, CancellationToken token)
    {
        await writer.WriteLineAsync("-- Solution Bundler SQL Server export");
        await writer.WriteLineAsync($"-- Row limit per table: {limit}");
        await writer.WriteLineAsync("SET NOCOUNT ON;");
        await writer.WriteLineAsync("GO");
        long rows = 0;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        foreach (var table in tables)
        {
            var qualified = $"[{table.Schema.Replace("]", "]]", StringComparison.Ordinal)}].[{table.Name.Replace("]", "]]", StringComparison.Ordinal)}]";
            await writer.WriteLineAsync();
            await writer.WriteLineAsync($"-- Table: {qualified}");
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
        return (tables.Count, rows);
    }

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
