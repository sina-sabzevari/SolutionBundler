using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
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
    private readonly HashSet<string> _hiddenPaths = new(StringComparer.OrdinalIgnoreCase);
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
        foreach (var path in _settings.HiddenPaths ?? []) _hiddenPaths.Add(path);
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
            var validPaths = CoreServices.Flatten(root).Select(item => item.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _hiddenPaths.RemoveWhere(hiddenPath => !validPaths.Contains(hiddenPath));
            _selectedPaths.Clear();
            RootPathBox.Text = path;
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            OutputPathBox.Text = Path.Combine(path, $"{name}.txt");
            SaveSettings();
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
        if (_hiddenPaths.Contains(source.Path) && !source.Path.Equals(_fullRoot?.Path, StringComparison.OrdinalIgnoreCase)) return null;
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

    private void ProjectNodeCheckBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DependencyObject current) return;
        while (current is not null)
        {
            if (current is TreeViewItem item)
            {
                item.IsSelected = true;
                item.Focus();
                return;
            }
            current = VisualTreeHelper.GetParent(current);
        }
    }

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
        SaveSettings();
    }

    private void HideFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_fullRoot is null) return;

        var pathsToHide = SelectedRoots()
            .Where(path => !path.Equals(_fullRoot.Path, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // A highlighted row without a checkbox selection is still a valid single-item action.
        if (pathsToHide.Count == 0 && ProjectTree.SelectedItem is ProjectNode selectedNode &&
            !selectedNode.Path.StartsWith("::", StringComparison.Ordinal) &&
            !selectedNode.Path.Equals(_fullRoot.Path, StringComparison.OrdinalIgnoreCase))
            pathsToHide.Add(selectedNode.Path);

        if (pathsToHide.Count == 0)
        {
            ShowError("یک یا چند پوشه یا پروژه را تیک بزنید.");
            return;
        }

        var allNodes = CoreServices.Flatten(_fullRoot)
            .ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        var hiddenCount = 0;
        foreach (var path in pathsToHide)
        {
            if (!allNodes.TryGetValue(path, out var source)) continue;
            if (_hiddenPaths.Add(source.Path)) hiddenCount++;
            foreach (var item in CoreServices.Flatten(source)) _selectedPaths.Remove(item.Path);
        }

        SaveSettings();
        RebuildProjectTree();
        Extensions.Clear();
        UpdateSelectionSummary();
        StatusText.Text = $"{hiddenCount:N0} پوشه یا پروژه پنهان شد.";
    }

    private void ShowHiddenFolders_Click(object sender, RoutedEventArgs e)
    {
        if (_fullRoot is null || _hiddenPaths.Count == 0)
        {
            ShowError("هیچ پوشهٔ پنهانی وجود ندارد.");
            return;
        }

        var root = BuildHiddenFolderTree(_fullRoot);
        if (root is null) return;
        var dialog = new HiddenFoldersDialog([root]) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedPaths.Count == 0) return;

        foreach (var path in dialog.SelectedPaths) _hiddenPaths.Remove(path);
        SaveSettings();
        RebuildProjectTree();
        StatusText.Text = $"{dialog.SelectedPaths.Count:N0} مورد دوباره نمایش داده شد.";
    }

    private HiddenFolderNode? BuildHiddenFolderTree(ProjectNode source)
    {
        var children = source.Children.Select(BuildHiddenFolderTree)
            .Where(node => node is not null).Cast<HiddenFolderNode>().ToList();
        var isHiddenEntry = _hiddenPaths.Contains(source.Path);
        if (!isHiddenEntry && children.Count == 0 && !source.Path.Equals(_fullRoot?.Path, StringComparison.OrdinalIgnoreCase))
            return null;

        var node = new HiddenFolderNode
        {
            Name = source.Name,
            Path = source.Path,
            Icon = source.Icon,
            IsHiddenEntry = isHiddenEntry
        };
        foreach (var child in children) node.Children.Add(child);
        return node;
    }

    private void SaveSettings()
    {
        _settings = new ModernSettings(RootPathBox.Text, _favorites.ToList(), _hiddenPaths.ToList());
        SettingsStore.Save(_settings);
    }

    private async void ScanExtensions_Click(object sender, RoutedEventArgs e)
    {
        var roots = SelectedRoots();
        var hiddenPaths = _hiddenPaths.ToArray();
        if (roots.Count == 0) { ShowError("حداقل یک پروژه یا پوشه را انتخاب کنید."); return; }
        await RunBusyAsync("در حال اسکن پسوندها...", async token =>
        {
            var counts = await Task.Run(() => CoreServices.ScanExtensions(roots, token, hiddenPaths), token);
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
        var hiddenPaths = _hiddenPaths.ToArray();
        if (roots.Count == 0 || ext.Count == 0) { EstimateText.Text = "Estimated output: —"; return; }
        try
        {
            var files = await Task.Run(() => CoreServices.FindFiles(roots, ext, outputPath, CancellationToken.None, hiddenPaths));
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

    private async void GenerateText_Click(object sender, RoutedEventArgs e) => await GenerateAsync(createText: true, createZip: false);

    private async void GenerateZip_Click(object sender, RoutedEventArgs e) => await GenerateAsync(createText: false, createZip: true);

    private async Task GenerateAsync(bool createText, bool createZip)
    {
        if (_fullRoot is null) { ShowError("Solution را انتخاب کنید."); return; }
        var roots = SelectedRoots(); var ext = Extensions.Where(x => x.IsChecked).Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (roots.Count == 0 || ext.Count == 0) { ShowError("پروژه‌ها و پسوندها را انتخاب کنید."); return; }
        var output = OutputPathBox.Text.Trim();
        var solutionRoot = _fullRoot.Path;
        var selectedTables = Schemas.SelectMany(x => x.Tables).Where(x => x.IsChecked).ToList();
        var rowLimit = 100;
        var includeSql = IncludeSqlCheck.IsChecked == true && selectedTables.Count > 0 &&
                         int.TryParse(SqlRowLimitBox.Text, out rowLimit);
        var sqlConnection = SqlConnectionBox.Text.Trim();
        var redisDb = 0;
        var redisKeyLimit = 100;
        var redisMemberLimit = 100;
        var includeRedis = IncludeRedisCheck.IsChecked == true && !string.IsNullOrWhiteSpace(RedisConnectionBox.Text) &&
                           int.TryParse(RedisDbBox.Text, out redisDb) &&
                           int.TryParse(RedisKeyLimitBox.Text, out redisKeyLimit) &&
                           int.TryParse(RedisMemberLimitBox.Text, out redisMemberLimit);
        var redisConnection = RedisConnectionBox.Text.Trim();
        var redisPattern = RedisPatternBox.Text.Trim();
        var hiddenPaths = _hiddenPaths.ToArray();
        await RunBusyAsync("در حال تولید خروجی...", async token =>
        {
            var zipPath = Path.ChangeExtension(output, ".zip");
            var files = await Task.Run(() => CoreServices.FindFiles(roots, ext, output, token, hiddenPaths), token);
            files.RemoveAll(file => string.Equals(Path.GetFullPath(file), Path.GetFullPath(zipPath), StringComparison.OrdinalIgnoreCase));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            var tempDirectory = Path.Combine(Path.GetTempPath(), $"SolutionBundler-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDirectory);
            var exports = new List<(string Path, string EntryName)>();
            try
            {
                if (includeSql)
                {
                    var sqlPath = Path.Combine(tempDirectory, "sql-export.sql");
                    await using var sqlWriter = new StreamWriter(sqlPath, false, new UTF8Encoding(true));
                    await SqlDataService.WriteAsync(sqlWriter, sqlConnection, selectedTables, Math.Clamp(rowLimit, 1, 100000), token);
                    await sqlWriter.FlushAsync(token);
                    exports.Add((sqlPath, "database/sql-export.sql"));
                }

                if (includeRedis)
                {
                    var redisPath = Path.Combine(tempDirectory, "redis-export.redis");
                    await using var redisWriter = new StreamWriter(redisPath, false, new UTF8Encoding(true));
                    await RedisDataService.WriteAsync(redisWriter, redisConnection, redisDb, redisPattern,
                        Math.Clamp(redisKeyLimit, 1, 100000), Math.Clamp(redisMemberLimit, 1, 100000), token);
                    await redisWriter.FlushAsync(token);
                    exports.Add((redisPath, "database/redis-export.redis"));
                }

                if (createText)
                {
                    await using var stream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.Read, 65536, true);
                    await using var writer = new StreamWriter(stream, new UTF8Encoding(true));
                    var progress = new Progress<(int current, int total)>(x => StatusText.Text = $"Files: {x.current:N0}/{x.total:N0}");
                    await CoreServices.WriteFilesAsync(writer, solutionRoot, files, progress, token);
                    foreach (var export in exports) await AppendExportAsync(writer, export.Path, token);
                    await writer.FlushAsync(token);
                    OpenOutputButton.IsEnabled = true;
                }

                if (createZip)
                {
                    StatusText.Text = "در حال ساخت فایل ZIP...";
                    await CreateZipAsync(zipPath, solutionRoot, files, exports, token);
                    OpenZipButton.IsEnabled = true;
                }

                var resultPath = createText ? output : zipPath;
                StatusText.Text = $"انجام شد — {files.Count:N0} فایل — {FormatSize(new FileInfo(resultPath).Length)}";
                OpenFolderButton.IsEnabled = true;
            }
            finally
            {
                try { Directory.Delete(tempDirectory, true); } catch { }
            }
        });
    }

    private static async Task AppendExportAsync(StreamWriter destination, string sourcePath, CancellationToken token)
    {
        await destination.WriteLineAsync();
        await destination.WriteLineAsync(new string('=', 120));
        await destination.WriteLineAsync($"DATA EXPORT: {Path.GetFileName(sourcePath)}");
        await destination.WriteLineAsync(new string('=', 120));
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        using var reader = new StreamReader(source, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[32768];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
            await destination.WriteAsync(buffer.AsMemory(0, read), token);
        await destination.WriteLineAsync();
    }

    private static async Task CreateZipAsync(string zipPath, string solutionRoot, IReadOnlyList<string> files,
        IReadOnlyList<(string Path, string EntryName)> exports, CancellationToken token)
    {
        await using var zipStream = new FileStream(zipPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 65536, true);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8);

        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(solutionRoot, file);
            if (relative.StartsWith("..", StringComparison.Ordinal))
                relative = Path.Combine("external", Path.GetFileName(file));
            await AddZipEntryAsync(archive, file, $"files/{relative.Replace('\\', '/')}", token);
        }

        foreach (var export in exports)
            await AddZipEntryAsync(archive, export.Path, export.EntryName, token);
    }

    private static async Task AddZipEntryAsync(ZipArchive archive, string sourcePath, string entryName, CancellationToken token)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        await using var output = entry.Open();
        await input.CopyToAsync(output, 65536, token);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private void OpenOutput_Click(object sender, RoutedEventArgs e) { if (File.Exists(OutputPathBox.Text)) Process.Start(new ProcessStartInfo(OutputPathBox.Text) { UseShellExecute = true }); }
    private void OpenZip_Click(object sender, RoutedEventArgs e)
    {
        var zipPath = Path.ChangeExtension(OutputPathBox.Text, ".zip");
        if (File.Exists(zipPath)) Process.Start(new ProcessStartInfo(zipPath) { UseShellExecute = true });
    }
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
        GenerateTextButton.IsEnabled = GenerateZipButton.IsEnabled = !busy; CancelButton.IsEnabled = busy;
    }

    private void ShowError(string message) => MessageBox.Show(this, message, "Solution Bundler", MessageBoxButton.OK, MessageBoxImage.Error);
    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"]; double value = bytes; var i = 0;
        while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
        return string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1}", value, units[i]);
    }
}
