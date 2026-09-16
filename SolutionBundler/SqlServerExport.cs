using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;

namespace SolutionBundler;

internal sealed record ConnectionCandidate(string DisplayName, string ConnectionString)
{
    public override string ToString() => DisplayName;
}

internal sealed record SqlTableItem(string Schema, string Name)
{
    public string DisplayName => $"{Schema}.{Name}";
    public override string ToString() => DisplayName;
}

internal sealed record SqlExportOptions(
    string ConnectionName,
    string ConnectionString,
    IReadOnlyList<SqlTableItem> Tables,
    int RowLimit);

internal sealed record SqlExportResult(int TableCount, long RowCount, IReadOnlyList<string> Errors);

internal sealed class SqlServerDialog : Form
{
    private readonly ComboBox _connections = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _connectionString = new() { Dock = DockStyle.Fill, RightToLeft = RightToLeft.No };
    private readonly SqlTableTreeView _tables = new()
    {
        Dock = DockStyle.Fill,
        CheckBoxes = true,
        RightToLeft = RightToLeft.No,
        RightToLeftLayout = false,
        HideSelection = false,
        ShowNodeToolTips = true
    };
    private readonly NumericUpDown _rowLimit = new()
    {
        Minimum = 1,
        Maximum = 100_000,
        Value = 100,
        ThousandsSeparator = true,
        Width = 110
    };
    private readonly Label _status = new() { AutoSize = true, Text = "Connection String را انتخاب و جداول را دریافت کنید." };
    private readonly Button _loadTables = new() { Text = "اتصال و دریافت جداول", AutoSize = true };
    private readonly Button _ok = new() { Text = "افزودن به خروجی", AutoSize = true, Enabled = false };
    private readonly Button _cancel = new() { Text = "انصراف", AutoSize = true, DialogResult = DialogResult.Cancel };
    private readonly Button _selectAll = new() { Text = "انتخاب همه", AutoSize = true };
    private readonly Button _selectNone = new() { Text = "لغو همه", AutoSize = true };
    private readonly HashSet<string> _previouslySelectedTables;
    private bool _updatingTableChecks;

    public SqlExportOptions? Options { get; private set; }

    public SqlServerDialog(IReadOnlyList<ConnectionCandidate> candidates, SqlExportOptions? current)
    {
        Text = "SQL Server Data";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(720, 540);
        Size = new Size(850, 650);
        Font = new Font("Segoe UI", 10F);
        RightToLeft = RightToLeft.Yes;
        RightToLeftLayout = true;
        _previouslySelectedTables = current?.Tables
            .Select(x => x.DisplayName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
            _connections.Items.Add(candidate);

        BuildLayout();
        WireEvents();

        if (current is not null)
        {
            var match = candidates.FirstOrDefault(x =>
                string.Equals(x.ConnectionString, current.ConnectionString, StringComparison.Ordinal));
            if (match is not null)
                _connections.SelectedItem = match;
            else
                _connectionString.Text = current.ConnectionString;
            _rowLimit.Value = Math.Clamp(current.RowLimit, (int)_rowLimit.Minimum, (int)_rowLimit.Maximum);
        }
        else if (_connections.Items.Count > 0)
        {
            _connections.SelectedIndex = 0;
        }
        else
        {
            _status.Text = "Connection String پیدا نشد؛ می‌توانید آن را دستی وارد کنید.";
        }
    }

    private void BuildLayout()
    {
        var tableActions = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        tableActions.Controls.AddRange([_loadTables, _selectAll, _selectNone]);

        var limitRow = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        limitRow.Controls.Add(new Label { Text = "تعداد رکورد اول از هر جدول:", AutoSize = true, Margin = new Padding(8, 7, 0, 0) });
        limitRow.Controls.Add(_rowLimit);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        buttons.Controls.AddRange([_ok, _cancel]);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 1,
            RowCount = 9
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label { Text = "Connection String شناسایی‌شده:", AutoSize = true });
        layout.Controls.Add(_connections);
        layout.Controls.Add(new Label { Text = "Connection String (قابل ویرایش):", AutoSize = true, Margin = new Padding(0, 8, 0, 3) });
        layout.Controls.Add(_connectionString);
        layout.Controls.Add(tableActions);
        layout.Controls.Add(_tables);
        layout.Controls.Add(limitRow);
        layout.Controls.Add(_status);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        AcceptButton = _ok;
        CancelButton = _cancel;
    }

    private void WireEvents()
    {
        _connections.SelectedIndexChanged += (_, _) =>
        {
            if (_connections.SelectedItem is ConnectionCandidate candidate)
                _connectionString.Text = candidate.ConnectionString;
        };
        _loadTables.Click += async (_, _) => await LoadTablesAsync();
        _selectAll.Click += (_, _) => SetAllTables(true);
        _selectNone.Click += (_, _) => SetAllTables(false);
        _tables.AfterCheck += TablesAfterCheck;
        _ok.Click += (_, _) => AcceptSelection();
    }

    private async Task LoadTablesAsync()
    {
        var connectionString = _connectionString.Text.Trim();
        if (connectionString.Length == 0)
        {
            ShowError("Connection String را وارد کنید.");
            return;
        }

        SetBusy(true);
        _status.Text = "در حال اتصال به SQL Server...";
        try
        {
            var tables = new List<SqlTableItem>();
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            const string sql = """
                SELECT s.name AS SchemaName, t.name AS TableName
                FROM sys.tables AS t
                INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
                WHERE t.is_ms_shipped = 0
                ORDER BY s.name, t.name;
                """;
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                tables.Add(new SqlTableItem(reader.GetString(0), reader.GetString(1)));

            _tables.BeginUpdate();
            _updatingTableChecks = true;
            try
            {
                _tables.Nodes.Clear();
                foreach (var schemaGroup in tables.GroupBy(x => x.Schema).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var schemaNode = new TreeNode(schemaGroup.Key)
                    {
                        ToolTipText = $"Schema: {schemaGroup.Key}"
                    };
                    foreach (var table in schemaGroup.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        var isChecked = _previouslySelectedTables.Contains(table.DisplayName);
                        schemaNode.Nodes.Add(new TreeNode(table.Name)
                        {
                            Tag = table,
                            Checked = isChecked,
                            ToolTipText = table.DisplayName
                        });
                    }
                    schemaNode.Checked = schemaNode.Nodes.Count > 0 &&
                                         schemaNode.Nodes.Cast<TreeNode>().All(x => x.Checked);
                    _tables.Nodes.Add(schemaNode);
                }
                _tables.ExpandAll();
            }
            finally
            {
                _updatingTableChecks = false;
                _tables.EndUpdate();
            }
            _status.Text = $"اتصال موفق بود؛ {tables.Count:N0} جدول پیدا شد.";
        }
        catch (Exception ex)
        {
            _tables.Nodes.Clear();
            _status.Text = "اتصال ناموفق بود.";
            ShowError(ex.Message);
        }
        finally
        {
            SetBusy(false);
            UpdateOkState();
        }
    }

    private void SetAllTables(bool isChecked)
    {
        _updatingTableChecks = true;
        try
        {
            foreach (TreeNode node in _tables.Nodes)
                SetNodeAndChildren(node, isChecked);
        }
        finally
        {
            _updatingTableChecks = false;
        }
        UpdateOkState();
    }

    private void TablesAfterCheck(object? sender, TreeViewEventArgs e)
    {
        if (_updatingTableChecks || e.Node is null)
            return;

        _updatingTableChecks = true;
        try
        {
            SetNodeAndChildren(e.Node, e.Node.Checked);
            if (e.Node.Parent is not null)
                e.Node.Parent.Checked = e.Node.Parent.Nodes.Cast<TreeNode>().All(x => x.Checked);
        }
        finally
        {
            _updatingTableChecks = false;
        }
        UpdateOkState();
    }

    private static void SetNodeAndChildren(TreeNode node, bool isChecked)
    {
        node.Checked = isChecked;
        foreach (TreeNode child in node.Nodes)
            SetNodeAndChildren(child, isChecked);
    }

    private List<SqlTableItem> GetSelectedTables()
    {
        var result = new List<SqlTableItem>();
        foreach (TreeNode schemaNode in _tables.Nodes)
        {
            foreach (TreeNode tableNode in schemaNode.Nodes)
            {
                if (tableNode.Checked && tableNode.Tag is SqlTableItem table)
                    result.Add(table);
            }
        }
        return result;
    }

    private void UpdateOkState() => _ok.Enabled = GetSelectedTables().Count > 0;

    private void SetBusy(bool busy)
    {
        _loadTables.Enabled = !busy;
        _connections.Enabled = !busy;
        _connectionString.Enabled = !busy;
        _tables.Enabled = !busy;
        _ok.Enabled = !busy && GetSelectedTables().Count > 0;
        UseWaitCursor = busy;
    }

    private void AcceptSelection()
    {
        var tables = GetSelectedTables();
        if (tables.Count == 0)
        {
            ShowError("حداقل یک جدول را انتخاب کنید.");
            return;
        }

        var name = (_connections.SelectedItem as ConnectionCandidate)?.DisplayName ?? "Manual SQL Server";
        Options = new SqlExportOptions(name, _connectionString.Text.Trim(), tables, (int)_rowLimit.Value);
        DialogResult = DialogResult.OK;
        Close();
    }

    private void ShowError(string message) =>
        MessageBox.Show(this, message, "SQL Server", MessageBoxButtons.OK, MessageBoxIcon.Error);
}

internal sealed class SqlTableTreeView : TreeView
{
    private const int WsExRight = 0x00001000;
    private const int WsExRtlReading = 0x00002000;
    private const int WsExLeftScrollbar = 0x00004000;
    private const int WsExLayoutRtl = 0x00400000;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle &= ~(WsExRight | WsExRtlReading | WsExLeftScrollbar | WsExLayoutRtl);
            return parameters;
        }
    }
}

internal static class SqlServerExporter
{
    public static async Task<SqlExportResult> WriteAsync(
        StreamWriter writer,
        SqlExportOptions options,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        long totalRows = 0;

        await writer.WriteLineAsync();
        await writer.WriteLineAsync(new string('=', 120));
        await writer.WriteLineAsync($"SQL SERVER DATA: {options.ConnectionName}");
        await writer.WriteLineAsync($"ROW LIMIT PER TABLE: {options.RowLimit}");
        await writer.WriteLineAsync(new string('=', 120));

        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        foreach (var table in options.Tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync();
            await writer.WriteLineAsync(new string('-', 120));
            await writer.WriteLineAsync($"TABLE: {table.DisplayName}");
            await writer.WriteLineAsync(new string('-', 120));

            try
            {
                var qualifiedName = $"{Quote(table.Schema)}.{Quote(table.Name)}";
                await using var command = new SqlCommand($"SELECT TOP (@rowLimit) * FROM {qualifiedName};", connection)
                {
                    CommandTimeout = 60
                };
                command.Parameters.Add("@rowLimit", SqlDbType.Int).Value = options.RowLimit;
                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);

                var headers = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName);
                await writer.WriteLineAsync(string.Join('\t', headers));

                long tableRows = 0;
                while (await reader.ReadAsync(cancellationToken))
                {
                    var values = new string[reader.FieldCount];
                    for (var i = 0; i < reader.FieldCount; i++)
                        values[i] = FormatValue(reader.GetValue(i));
                    await writer.WriteLineAsync(string.Join('\t', values));
                    tableRows++;
                }
                totalRows += tableRows;
                await writer.WriteLineAsync($"ROWS EXPORTED: {tableRows}");
            }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException)
            {
                var error = $"{table.DisplayName}: {ex.Message}";
                errors.Add(error);
                await writer.WriteLineAsync($"[SQL ERROR: {ex.Message}]");
            }
        }

        return new SqlExportResult(options.Tables.Count, totalRows, errors);
    }

    private static string Quote(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string FormatValue(object value) => value switch
    {
        DBNull => "NULL",
        byte[] bytes => $"<binary:{bytes.Length} bytes>",
        DateTime dateTime => dateTime.ToString("O"),
        DateTimeOffset offset => offset.ToString("O"),
        bool boolean => boolean ? "true" : "false",
        _ => Escape(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)
    };

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal);
}
