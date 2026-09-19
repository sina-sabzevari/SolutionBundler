using System.Collections.ObjectModel;
using System.ComponentModel;
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

public sealed class TableNode : INotifyPropertyChanged
{
    private bool _isChecked;
    public required string Schema { get; init; }
    public required string Name { get; init; }
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

public sealed class SchemaNode : INotifyPropertyChanged
{
    private bool _isChecked;
    private bool _updating;
    public required string Name { get; init; }
    public ObservableCollection<TableNode> Tables { get; } = [];
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
            foreach (var table in Tables) table.IsChecked = value;
            _updating = false;
        }
    }
    public void RefreshFromChildren()
    {
        _updating = true;
        _isChecked = Tables.Count > 0 && Tables.All(x => x.IsChecked);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        _updating = false;
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record ConnectionChoice(string DisplayName, string Value);

public sealed class HiddenFolderNode
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Icon { get; init; }
    public bool IsHiddenEntry { get; init; }
    public bool IsChecked { get; set; }
    public ObservableCollection<HiddenFolderNode> Children { get; } = [];
}
