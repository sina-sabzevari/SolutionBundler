using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SolutionBundler.Wpf;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> DefaultExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".cs", ".razor", ".json", ".csproj", ".css", ".js", ".ts", ".tsx", ".sql", ".md", ".xml", ".yml", ".yaml", ".config", ".props", ".targets" };

    public ObservableCollection<ProjectNode> ProjectRoots { get; } = [];
    public ObservableCollection<ExtensionItem> Extensions { get; } = [];
    public ObservableCollection<SchemaNode> Schemas { get; } = [];

    private ProjectNode? _fullRoot;
    private readonly HashSet<string> _selectedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _favorites = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly DispatcherTimer _estimateTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private CancellationTokenSource? _operation;
    private ModernSettings _settings;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _settings = SettingsStore.Load();
        foreach (var path in _settings.Favorites ?? []) _favorites.Add(path);
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); RebuildProjectTree(); };
        _estimateTimer.Tick += async (_, _) => { _estimateTimer.Stop(); await EstimateAsync(); };
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_settings.LastRoot) && Directory.Exists(_settings.LastRoot))
            await LoadRootAsync(_settings.LastRoot);
    }

    private void Navigation_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out var index) || FilesPage is null) return;
        FilesPage.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        SqlPage.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        RedisPage.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select solution folder", InitialDirectory = Directory.Exists(RootPathBox.Text) ? RootPathBox.Text : null };
        if (dialog.ShowDialog(this) == true) await LoadRootAsync(dialog.FolderName);
    }

    private async Task LoadRootAsync(string path)
    {
        await RunBusyAsync("در حال خواندن ساختار Solution...", async token =>
        {
            var root = await Task.Run(() => CoreServices.BuildTree(path, token), token);
            _fullRoot = root;
            _selectedPaths.Clear();
            RootPathBox.Text = path;
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            OutputPathBox.Text = Path.Combine(path, $"{name}.txt");
            _settings = new ModernSettings(path, _favorites.ToList());
            SettingsStore.Save(_settings);
            RebuildProjectTree();
            Extensions.Clear();
            StatusText.Text = $"{CoreServices.Flatten(root).Count():N0} پوشه و پروژه پیدا شد.";
        });
    }

    private void RebuildProjectTree()
    {
        if (_fullRoot is null) return;
        var query = ProjectSearchBox.Text.Trim();
        ProjectRoots.Clear();
        var favoriteRoot = new ProjectNode { Name = "Favorites", Path = "::favorites", Icon = "\uE735" };
        foreach (var favorite in _favorites)
        {
            var source = CoreServices.Flatten(_fullRoot).FirstOrDefault(x => x.Path.Equals(favorite, StringComparison.OrdinalIgnoreCase));
            if (source is null) continue;
            var clone = CloneFiltered(source, query, forceRoot: true);
            if (clone is not null) favoriteRoot.Children.Add(clone);
        }
        if (favoriteRoot.Children.Count > 0) ProjectRoots.Add(favoriteRoot);
        var rootClone = CloneFiltered(_fullRoot, query, forceRoot: true);
        if (rootClone is not null) ProjectRoots.Add(rootClone);
    }

    private ProjectNode? CloneFiltered(ProjectNode source, string query, bool forceRoot = false)
    {
        var children = source.Children.Select(x => CloneFiltered(x, query)).Where(x => x is not null).Cast<ProjectNode>().ToList();
        var matches = query.Length == 0 || source.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        if (!forceRoot && !matches && children.Count == 0) return null;
        var clone = new ProjectNode { Name = source.Name, Path = source.Path, Icon = source.Icon, IsProject = source.IsProject };
        foreach (var child in children) clone.Children.Add(child);
        clone.CheckedChanged += DisplayNode_CheckedChanged;
        clone.IsChecked = _selectedPaths.Contains(source.Path);
        return clone;
    }

    private void DisplayNode_CheckedChanged(object? sender, EventArgs e)
    {
        if (sender is not ProjectNode node || node.Path.StartsWith("::")) return;
        var source = _fullRoot is null ? null : CoreServices.Flatten(_fullRoot).FirstOrDefault(x => x.Path.Equals(node.Path, StringComparison.OrdinalIgnoreCase));
        if (source is null) return;
        foreach (var item in CoreServices.Flatten(source))
        {
            if (node.IsChecked) _selectedPaths.Add(item.Path); else _selectedPaths.Remove(item.Path);
        }
        Extensions.Clear();
        EstimateText.Text = "Estimated output: —";
        UpdateSelectionSummary();
    }

    private List<string> SelectedRoots()
    {
        var result = new List<string>();
        if (_fullRoot is null) return result;
        Collect(_fullRoot, false);
        return result;
        void Collect(ProjectNode node, bool parentSelected)
        {
            var selected = _selectedPaths.Contains(node.Path);
            if (selected && !parentSelected) result.Add(node.Path);
            foreach (var child in node.Children) Collect(child, parentSelected || selected);
        }
    }

    private void UpdateSelectionSummary() => SidebarSelectionSummary.Text =
        $"{_selectedPaths.Count:N0} پوشه/پروژه • {Extensions.Count(x => x.IsChecked):N0} پسوند";

    private void ProjectSearchBox_TextChanged(object sender, TextChangedEventArgs e) { _searchTimer.Stop(); _searchTimer.Start(); }
    private void ProjectTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) { }

    private void NestedScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject source) return;

        var scrollViewer = FindScrollableDescendant(source) ?? FindScrollableAncestor(source);
        if (scrollViewer is null) return;

        // A predictable step is easier to control than WPF's default nested scrolling.
        var step = Math.Max(54, SystemParameters.WheelScrollLines * 18);
        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - Math.Sign(e.Delta) * step);
        e.Handled = true;
    }

    private static ScrollViewer? FindScrollableDescendant(DependencyObject root)
    {
        if (root is ScrollViewer viewer && viewer.ScrollableHeight > 0) return viewer;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var match = FindScrollableDescendant(VisualTreeHelper.GetChild(root, index));
            if (match is not null) return match;
        }
        return null;
    }

    private static ScrollViewer? FindScrollableAncestor(DependencyObject source)
    {
        for (var parent = VisualTreeHelper.GetParent(source); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer viewer && viewer.ScrollableHeight > 0) return viewer;
        return null;
    }

    private void AddFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is not ProjectNode node || node.Path.StartsWith("::")) return;
        _favorites.Add(node.Path); SaveFavorites(); RebuildProjectTree(); StatusText.Text = $"{node.Name} به Favorites اضافه شد.";
    }

    private void RemoveFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is not ProjectNode node) return;
        _favorites.Remove(node.Path); SaveFavorites(); RebuildProjectTree(); StatusText.Text = $"{node.Name} از Favorites حذف شد.";
    }

    private void SaveFavorites()
    {
        _settings = new ModernSettings(RootPathBox.Text, _favorites.ToList()); SettingsStore.Save(_settings);
    }

    private async void ScanExtensions_Click(object sender, RoutedEventArgs e)
    {
        var roots = SelectedRoots();
        if (roots.Count == 0) { ShowError("حداقل یک پروژه یا پوشه را انتخاب کنید."); return; }
        await RunBusyAsync("در حال اسکن پسوندها...", async token =>
        {
            var counts = await Task.Run(() => CoreServices.ScanExtensions(roots, token), token);
            Extensions.Clear();
            foreach (var pair in counts.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                var item = new ExtensionItem { Name = pair.Key, Count = pair.Value, IsChecked = DefaultExtensions.Contains(pair.Key) };
                item.CheckedChanged += (_, _) => { UpdateSelectionSummary(); _estimateTimer.Stop(); _estimateTimer.Start(); };
                Extensions.Add(item);
            }
            StatusText.Text = $"{counts.Count:N0} پسوند پیدا شد."; UpdateSelectionSummary();
        });
        _estimateTimer.Start();
    }

    private void SelectAllExtensions_Click(object sender, RoutedEventArgs e) { foreach (var item in Extensions) item.IsChecked = true; }
    private void ClearExtensions_Click(object sender, RoutedEventArgs e) { foreach (var item in Extensions) item.IsChecked = false; }

    private async Task EstimateAsync()
    {
        if (_operation is not null || _fullRoot is null || string.IsNullOrWhiteSpace(OutputPathBox.Text)) return;
        var roots = SelectedRoots(); var ext = Extensions.Where(x => x.IsChecked).Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var outputPath = OutputPathBox.Text;
        if (roots.Count == 0 || ext.Count == 0) { EstimateText.Text = "Estimated output: —"; return; }
        try
        {
            var files = await Task.Run(() => CoreServices.FindFiles(roots, ext, outputPath, CancellationToken.None));
            long size = files.Sum(x => { try { return new FileInfo(x).Length + 300; } catch { return 0; } });
            EstimateText.Text = $"Estimated files output: {FormatSize(size)} | {files.Count:N0} files";
        }
        catch { EstimateText.Text = "Estimated output: unavailable"; }
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Text file (*.txt)|*.txt", FileName = Path.GetFileName(OutputPathBox.Text), InitialDirectory = Path.GetDirectoryName(OutputPathBox.Text) };
        if (dialog.ShowDialog(this) == true) OutputPathBox.Text = dialog.FileName;
    }

    private async void DiscoverSql_Click(object sender, RoutedEventArgs e)
    {
        var rootPath = RootPathBox.Text;
        if (!Directory.Exists(rootPath)) return;
        await RunBusyAsync("در حال یافتن Connection Stringهای SQL...", async token =>
        {
            var items = await Task.Run(() => CoreServices.DiscoverSql(rootPath, token), token);
            SqlConnectionCombo.ItemsSource = items; if (items.Count > 0) SqlConnectionCombo.SelectedIndex = 0;
            StatusText.Text = $"{items.Count:N0} SQL Connection پیدا شد.";
        });
    }

    private void SqlConnectionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (SqlConnectionCombo.SelectedItem is ConnectionChoice item) SqlConnectionBox.Text = item.Value; }

    private async void LoadSqlTables_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SqlConnectionBox.Text)) { ShowError("Connection String را انتخاب کنید."); return; }
        await RunBusyAsync("در حال اتصال و دریافت جداول...", async token =>
        {
            var tables = await SqlDataService.LoadTablesAsync(SqlConnectionBox.Text.Trim(), token);
            Schemas.Clear();
            foreach (var group in tables.GroupBy(x => x.Schema))
            {
                var schema = new SchemaNode { Name = group.Key };
                foreach (var table in group)
                {
                    var item = new TableNode { Schema = table.Schema, Name = table.Name };
                    item.CheckedChanged += (_, _) => schema.RefreshFromChildren();
                    schema.Tables.Add(item);
                }
                Schemas.Add(schema);
            }
            StatusText.Text = $"{tables.Count:N0} جدول در {Schemas.Count:N0} Schema پیدا شد.";
        });
    }

    private void SelectAllTables_Click(object sender, RoutedEventArgs e) { foreach (var schema in Schemas) schema.IsChecked = true; }
    private void ClearTables_Click(object sender, RoutedEventArgs e) { foreach (var schema in Schemas) schema.IsChecked = false; }

    private async void DiscoverRedis_Click(object sender, RoutedEventArgs e)
    {
        var rootPath = RootPathBox.Text;
        if (!Directory.Exists(rootPath)) return;
        await RunBusyAsync("در حال یافتن Redis Connection...", async token =>
        {
            var items = await Task.Run(() => CoreServices.DiscoverRedis(rootPath, token), token);
            RedisConnectionCombo.ItemsSource = items; if (items.Count > 0) RedisConnectionCombo.SelectedIndex = 0;
            StatusText.Text = $"{items.Count:N0} Redis Connection پیدا شد.";
        });
    }

    private void RedisConnectionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (RedisConnectionCombo.SelectedItem is ConnectionChoice item) RedisConnectionBox.Text = item.Value; }

    private async void TestRedis_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RedisDbBox.Text, out var db)) { ShowError("Database نامعتبر است."); return; }
        await RunBusyAsync("در حال اتصال به Redis...", async _ =>
        {
            var ping = await RedisDataService.PingAsync(RedisConnectionBox.Text.Trim(), db);
            StatusText.Text = $"Redis connected — {ping.TotalMilliseconds:0.##} ms";
        });
    }

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (_fullRoot is null) { ShowError("Solution را انتخاب کنید."); return; }
        var roots = SelectedRoots(); var ext = Extensions.Where(x => x.IsChecked).Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (roots.Count == 0 || ext.Count == 0) { ShowError("پروژه‌ها و پسوندها را انتخاب کنید."); return; }
        var output = OutputPathBox.Text.Trim();
        await RunBusyAsync("در حال تولید خروجی...", async token =>
        {
            var files = await Task.Run(() => CoreServices.FindFiles(roots, ext, output, token), token);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            await using var stream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.Read, 65536, true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(true));
            var progress = new Progress<(int current, int total)>(x => StatusText.Text = $"Files: {x.current:N0}/{x.total:N0}");
            await CoreServices.WriteFilesAsync(writer, _fullRoot.Path, files, progress, token);

            if (IncludeSqlCheck.IsChecked == true && Schemas.Count > 0 && int.TryParse(SqlRowLimitBox.Text, out var rowLimit))
            {
                var tables = Schemas.SelectMany(x => x.Tables).Where(x => x.IsChecked).ToList();
                if (tables.Count > 0) await SqlDataService.WriteAsync(writer, SqlConnectionBox.Text.Trim(), tables, Math.Clamp(rowLimit, 1, 100000), token);
            }
            if (IncludeRedisCheck.IsChecked == true && !string.IsNullOrWhiteSpace(RedisConnectionBox.Text) &&
                int.TryParse(RedisDbBox.Text, out var db) && int.TryParse(RedisKeyLimitBox.Text, out var keyLimit) &&
                int.TryParse(RedisMemberLimitBox.Text, out var memberLimit))
                await RedisDataService.WriteAsync(writer, RedisConnectionBox.Text.Trim(), db, RedisPatternBox.Text.Trim(), Math.Clamp(keyLimit, 1, 100000), Math.Clamp(memberLimit, 1, 100000), token);

            await writer.FlushAsync(token);
            StatusText.Text = $"انجام شد — {files.Count:N0} فایل — {FormatSize(new FileInfo(output).Length)}";
            OpenOutputButton.IsEnabled = OpenFolderButton.IsEnabled = true;
        });
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private void OpenOutput_Click(object sender, RoutedEventArgs e) { if (File.Exists(OutputPathBox.Text)) Process.Start(new ProcessStartInfo(OutputPathBox.Text) { UseShellExecute = true }); }
    private void OpenFolder_Click(object sender, RoutedEventArgs e) { var dir = Path.GetDirectoryName(OutputPathBox.Text); if (Directory.Exists(dir)) Process.Start(new ProcessStartInfo(dir!) { UseShellExecute = true }); }

    private async Task RunBusyAsync(string message, Func<CancellationToken, Task> action)
    {
        if (_operation is not null) return;
        _operation = new CancellationTokenSource(); SetBusy(true, message);
        try { await action(_operation.Token); }
        catch (OperationCanceledException) { StatusText.Text = "عملیات لغو شد."; }
        catch (Exception ex) { StatusText.Text = "خطا"; ShowError(ex.Message); }
        finally { _operation.Dispose(); _operation = null; SetBusy(false, StatusText.Text); }
    }

    private void SetBusy(bool busy, string message)
    {
        StatusText.Text = message; BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        GenerateButton.IsEnabled = !busy; CancelButton.IsEnabled = busy;
    }

    private void ShowError(string message) => MessageBox.Show(this, message, "Solution Bundler", MessageBoxButton.OK, MessageBoxImage.Error);
    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"]; double value = bytes; var i = 0;
        while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
        return string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1}", value, units[i]);
    }
}
