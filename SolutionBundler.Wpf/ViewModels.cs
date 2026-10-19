using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Runtime.CompilerServices;

namespace SolutionBundler.Wpf;

public sealed class ProjectNode : INotifyPropertyChanged
{
    private bool _isChecked;
    private bool _updating;

    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Icon { get; init; }
    public bool IsProject { get; init; }
    public ObservableCollection<ProjectNode> Children { get; } = [];

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged();
            if (_updating) return;
            _updating = true;
            foreach (var child in Children)
                child.IsChecked = value;
            _updating = false;
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? CheckedChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ExtensionItem : INotifyPropertyChanged
{
    private bool _isChecked;
    public required string Name { get; init; }
    public int Count { get; init; }
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public event EventHandler? CheckedChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
}

public enum SqlObjectKind
{
    Table,
    View,
    Function,
    StoredProcedure
}

public sealed record SqlObjectInfo(string Schema, string Name, SqlObjectKind Kind);

public sealed class SqlObjectNode : INotifyPropertyChanged
{
    private bool _isChecked;
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required SqlObjectKind Kind { get; init; }
    public string KindLabel => Kind switch
    {
        SqlObjectKind.Table => "Table",
        SqlObjectKind.View => "View",
        SqlObjectKind.Function => "Function",
        SqlObjectKind.StoredProcedure => "SP",
        _ => Kind.ToString()
    };
    public string Icon => Kind switch
    {
        SqlObjectKind.Table => "▤",
        SqlObjectKind.View => "◫",
        SqlObjectKind.Function => "ƒ",
        SqlObjectKind.StoredProcedure => "⚙",
        _ => "•"
    };
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public event EventHandler? CheckedChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class SqlObjectGroupNode : INotifyPropertyChanged
{
    private bool _isChecked;
    private bool _updating;
    public required SqlObjectKind Kind { get; init; }
    public ObservableCollection<SqlObjectNode> Objects { get; } = [];
    public string Name => Kind switch
    {
        SqlObjectKind.Table => "Table",
        SqlObjectKind.View => "View",
        SqlObjectKind.Function => "Function",
        SqlObjectKind.StoredProcedure => "SP",
        _ => Kind.ToString()
    };
    public string Icon => Kind switch
    {
        SqlObjectKind.Table => "▤",
        SqlObjectKind.View => "◫",
        SqlObjectKind.Function => "ƒ",
        SqlObjectKind.StoredProcedure => "⚙",
        _ => "•"
    };
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            if (_updating) return;
            _updating = true;
            foreach (var item in Objects) item.IsChecked = value;
            _updating = false;
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public void RefreshFromChildren()
    {
        if (_updating) return;
        _updating = true;
        _isChecked = Objects.Count > 0 && Objects.All(item => item.IsChecked);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        _updating = false;
        CheckedChanged?.Invoke(this, EventArgs.Empty);
    }
    public event EventHandler? CheckedChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class SchemaNode : INotifyPropertyChanged
{
    private bool _isChecked;
    private bool _updating;
    public required string Name { get; init; }
    public ObservableCollection<SqlObjectGroupNode> Groups { get; } = [];
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            if (_updating) return;
            _updating = true;
            foreach (var group in Groups) group.IsChecked = value;
            _updating = false;
        }
    }
    public void RefreshFromChildren()
    {
        if (_updating) return;
        _updating = true;
        _isChecked = Groups.Count > 0 && Groups.All(group => group.IsChecked);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        _updating = false;
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record ConnectionChoice(string DisplayName, string Value);

public sealed class SqlQuerySnapshot
{
    public string Database { get; init; } = string.Empty;
    public required string Query { get; init; }
    public required int RowLimit { get; init; }
    public required int RowCount { get; init; }
    public required bool IsTruncated { get; init; }
    public required DataTable Result { get; init; }
    public string DisplayName => $"Query {RowCount:N0} ردیف" + (IsTruncated ? "+" : string.Empty) +
                                 (string.IsNullOrWhiteSpace(Database) ? string.Empty : $" — {Database}");
}

public sealed class HiddenFolderNode
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Icon { get; init; }
    public bool IsHiddenEntry { get; init; }
    public bool IsChecked { get; set; }
    public ObservableCollection<HiddenFolderNode> Children { get; } = [];
}
