using System.Globalization;
using StackExchange.Redis;

namespace SolutionBundler;

internal sealed record RedisConnectionCandidate(string DisplayName, string ConnectionString)
{
    public override string ToString() => DisplayName;
}

internal sealed record RedisExportOptions(
    string ConnectionName,
    string ConnectionString,
    int Database,
    string KeyPattern,
    int KeyLimit,
    int MemberLimit);

internal sealed record RedisExportResult(int KeyCount, IReadOnlyList<string> Errors);

internal sealed class RedisDialog : Form
{
    private readonly ComboBox _connections = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _connectionString = new() { Dock = DockStyle.Fill, RightToLeft = RightToLeft.No };
    private readonly NumericUpDown _database = new() { Minimum = 0, Maximum = 1024, Value = 0, Width = 100 };
    private readonly TextBox _pattern = new() { Text = "*", Width = 180, RightToLeft = RightToLeft.No };
    private readonly NumericUpDown _keyLimit = new() { Minimum = 1, Maximum = 100_000, Value = 100, ThousandsSeparator = true, Width = 110 };
    private readonly NumericUpDown _memberLimit = new() { Minimum = 1, Maximum = 100_000, Value = 100, ThousandsSeparator = true, Width = 110 };
    private readonly Label _status = new() { AutoSize = true, Text = "تنظیمات Redis را بررسی و اتصال را آزمایش کنید." };
    private readonly Button _test = new() { Text = "Test Connection", AutoSize = true };
    private readonly Button _ok = new() { Text = "افزودن به خروجی", AutoSize = true };
    private readonly Button _cancel = new() { Text = "انصراف", AutoSize = true, DialogResult = DialogResult.Cancel };

    public RedisExportOptions? Options { get; private set; }

    public RedisDialog(IReadOnlyList<RedisConnectionCandidate> candidates, RedisExportOptions? current)
    {
        Text = "Redis Data";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(700, 430);
        Size = new Size(780, 500);
        Font = new Font("Segoe UI", 10F);
        RightToLeft = RightToLeft.Yes;
        RightToLeftLayout = true;

        foreach (var candidate in candidates)
            _connections.Items.Add(candidate);
        BuildLayout();
        WireEvents();

        if (current is not null)
        {
            _connectionString.Text = current.ConnectionString;
            _database.Value = Math.Clamp(current.Database, (int)_database.Minimum, (int)_database.Maximum);
            _pattern.Text = current.KeyPattern;
            _keyLimit.Value = Math.Clamp(current.KeyLimit, (int)_keyLimit.Minimum, (int)_keyLimit.Maximum);
            _memberLimit.Value = Math.Clamp(current.MemberLimit, (int)_memberLimit.Minimum, (int)_memberLimit.Maximum);
            var match = candidates.FirstOrDefault(x => x.ConnectionString == current.ConnectionString);
            if (match is not null)
                _connections.SelectedItem = match;
        }
        else if (_connections.Items.Count > 0)
        {
            _connections.SelectedIndex = 0;
        }
        else
        {
            _status.Text = "Redis Connection String پیدا نشد؛ امکان ورود دستی وجود دارد.";
        }
    }

    private void BuildLayout()
    {
        var options = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        options.Controls.AddRange([
            Labeled("Database:", _database),
            Labeled("Key pattern:", _pattern),
            Labeled("حداکثر کلید:", _keyLimit),
            Labeled("حداکثر عضو هر مقدار:", _memberLimit)
        ]);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange([_test, _ok, _cancel]);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 8 };
        for (var i = 0; i < 8; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "Redis Connection شناسایی‌شده:", AutoSize = true });
        layout.Controls.Add(_connections);
        layout.Controls.Add(new Label { Text = "Connection String (قابل ویرایش):", AutoSize = true, Margin = new Padding(0, 10, 0, 3) });
        layout.Controls.Add(_connectionString);
        layout.Controls.Add(options);
        layout.Controls.Add(_status);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = _ok;
        CancelButton = _cancel;
    }

    private static Control Labeled(string text, Control control)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(8) };
        panel.Controls.Add(new Label { Text = text, AutoSize = true, Margin = new Padding(4, 7, 0, 0) });
        panel.Controls.Add(control);
        return panel;
    }

    private void WireEvents()
    {
        _connections.SelectedIndexChanged += (_, _) =>
        {
            if (_connections.SelectedItem is RedisConnectionCandidate candidate)
                _connectionString.Text = candidate.ConnectionString;
        };
        _test.Click += async (_, _) => await TestConnectionAsync();
        _ok.Click += (_, _) => AcceptSelection();
    }

    private async Task TestConnectionAsync()
    {
        if (_connectionString.Text.Trim().Length == 0)
        {
            ShowError("Redis Connection String را وارد کنید.");
            return;
        }

        SetBusy(true);
        _status.Text = "Connecting to Redis...";
        try
        {
            var configuration = ConfigurationOptions.Parse(_connectionString.Text.Trim());
            configuration.AbortOnConnectFail = false;
            using var connection = await ConnectionMultiplexer.ConnectAsync(configuration);
            var latency = await connection.GetDatabase((int)_database.Value).PingAsync();
            _status.Text = $"Connected successfully — ping {latency.TotalMilliseconds:0.##} ms";
        }
        catch (Exception ex)
        {
            _status.Text = "Redis connection failed.";
            ShowError(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void AcceptSelection()
    {
        var connectionString = _connectionString.Text.Trim();
        if (connectionString.Length == 0 || _pattern.Text.Trim().Length == 0)
        {
            ShowError("Connection String و Key pattern را وارد کنید.");
            return;
        }
        var name = (_connections.SelectedItem as RedisConnectionCandidate)?.DisplayName ?? "Manual Redis";
        Options = new RedisExportOptions(name, connectionString, (int)_database.Value, _pattern.Text.Trim(),
            (int)_keyLimit.Value, (int)_memberLimit.Value);
        DialogResult = DialogResult.OK;
        Close();
    }

    private void SetBusy(bool busy)
    {
        _test.Enabled = !busy;
        _ok.Enabled = !busy;
        UseWaitCursor = busy;
    }

    private void ShowError(string message) =>
        MessageBox.Show(this, message, "Redis", MessageBoxButtons.OK, MessageBoxIcon.Error);
}

internal static class RedisExporter
{
    public static async Task<RedisExportResult> WriteAsync(StreamWriter writer, RedisExportOptions options, CancellationToken token)
    {
        var errors = new List<string>();
        var configuration = ConfigurationOptions.Parse(options.ConnectionString);
        configuration.AbortOnConnectFail = false;
        using var connection = await ConnectionMultiplexer.ConnectAsync(configuration);
        var database = connection.GetDatabase(options.Database);

        var keys = await Task.Run(() => ScanKeys(connection, options, token, errors), token);
        await writer.WriteLineAsync();
        await writer.WriteLineAsync(new string('=', 120));
        await writer.WriteLineAsync($"REDIS DATA: {options.ConnectionName}");
        await writer.WriteLineAsync($"DATABASE: {options.Database} | PATTERN: {options.KeyPattern} | KEY LIMIT: {options.KeyLimit}");
        await writer.WriteLineAsync(new string('=', 120));

        foreach (var key in keys)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var type = await database.KeyTypeAsync(key);
                var ttl = await database.KeyTimeToLiveAsync(key);
                await writer.WriteLineAsync();
                await writer.WriteLineAsync($"KEY: {Escape(key.ToString())}");
                await writer.WriteLineAsync($"TYPE: {type} | TTL: {(ttl.HasValue ? ttl.Value.ToString() : "none")}");
                await WriteValueAsync(writer, database, key, type, options.MemberLimit, token);
            }
            catch (Exception ex) when (ex is RedisException or InvalidOperationException)
            {
                errors.Add($"{key}: {ex.Message}");
                await writer.WriteLineAsync($"[REDIS ERROR: {ex.Message}]");
            }
        }
        return new RedisExportResult(keys.Count, errors);
    }

    private static List<RedisKey> ScanKeys(ConnectionMultiplexer connection, RedisExportOptions options,
        CancellationToken token, List<string> errors)
    {
        var result = new List<RedisKey>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in connection.GetEndPoints())
        {
            if (result.Count >= options.KeyLimit)
                break;
            try
            {
                var server = connection.GetServer(endpoint);
                if (!server.IsConnected || server.IsReplica)
                    continue;
                foreach (var key in server.Keys(options.Database, options.KeyPattern, pageSize: 250))
                {
                    token.ThrowIfCancellationRequested();
                    if (seen.Add(key.ToString()))
                        result.Add(key);
                    if (result.Count >= options.KeyLimit)
                        break;
                }
            }
            catch (Exception ex) when (ex is RedisException or InvalidOperationException)
            {
                errors.Add($"{endpoint}: {ex.Message}");
            }
        }
        return result;
    }

    private static async Task WriteValueAsync(StreamWriter writer, IDatabase database, RedisKey key,
        RedisType type, int limit, CancellationToken token)
    {
        switch (type)
        {
            case RedisType.String:
                await writer.WriteLineAsync($"VALUE: {Escape((await database.StringGetAsync(key)).ToString())}");
                break;
            case RedisType.List:
                var list = await database.ListRangeAsync(key, 0, limit - 1);
                for (var i = 0; i < list.Length; i++)
                    await writer.WriteLineAsync($"[{i}] {Escape(list[i].ToString())}");
                break;
            case RedisType.Hash:
                var hashCount = 0;
                foreach (var entry in database.HashScan(key, pageSize: Math.Min(limit, 250)))
                {
                    token.ThrowIfCancellationRequested();
                    await writer.WriteLineAsync($"{Escape(entry.Name.ToString())}\t{Escape(entry.Value.ToString())}");
                    if (++hashCount >= limit) break;
                }
                break;
            case RedisType.Set:
                var setCount = 0;
                foreach (var value in database.SetScan(key, pageSize: Math.Min(limit, 250)))
                {
                    token.ThrowIfCancellationRequested();
                    await writer.WriteLineAsync(Escape(value.ToString()));
                    if (++setCount >= limit) break;
                }
                break;
            case RedisType.SortedSet:
                var sorted = await database.SortedSetRangeByRankWithScoresAsync(key, 0, limit - 1);
                foreach (var value in sorted)
                    await writer.WriteLineAsync($"{value.Score.ToString(CultureInfo.InvariantCulture)}\t{Escape(value.Element.ToString())}");
                break;
            default:
                await writer.WriteLineAsync($"VALUE: <{type} is not expanded>");
                break;
        }
    }

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal);
}
