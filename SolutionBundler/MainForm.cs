using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace SolutionBundler;

public sealed class MainForm : Form
{
    private static readonly HashSet<string> DefaultExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".css", ".razor", ".json", ".ipynb", ".csproj", ".conf",
        ".config", ".xml", ".yml", ".yaml", ".js", ".ts", ".tsx", ".jsx",
        ".html", ".htm", ".scss", ".sql", ".md", ".sln", ".props", ".targets"
    };

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".idea", ".vscode", "packages"
    };

    private readonly TextBox _rootPath = new() { Dock = DockStyle.Fill };
    private readonly TextBox _outputPath = new() { Dock = DockStyle.Fill };
    private readonly LtrTreeView _folders = new()
    {
        Dock = DockStyle.Fill,
        CheckBoxes = true,
        HideSelection = false,
        RightToLeft = RightToLeft.No,
        RightToLeftLayout = false,
        Scrollable = true,
        ShowNodeToolTips = true
    };
    private readonly TextBox _folderSearch = new()
    {
        Dock = DockStyle.Fill,
        PlaceholderText = "جست‌وجوی پروژه یا پوشه..."
    };
    private readonly CheckedListBox _extensions = new()
    {
        Dock = DockStyle.Fill,
        CheckOnClick = true,
        MultiColumn = true,
        ColumnWidth = 125,
        IntegralHeight = false
    };
    private readonly Label _summary = new() { AutoSize = true, Text = "ابتدا پوشه‌ی Solution را انتخاب کنید." };
    private readonly Label _estimatedSize = new()
    {
        AutoSize = true,
        Text = "Estimated output: —",
        RightToLeft = RightToLeft.No,
        ForeColor = Color.FromArgb(45, 90, 140)
    };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Continuous };
    private readonly Button _browseRoot = new() { Text = "انتخاب پوشه...", AutoSize = true };
    private readonly Button _browseOutput = new() { Text = "مسیر خروجی...", AutoSize = true };
    private readonly Button _scan = new() { Text = "اسکن پسوندها", AutoSize = true };
    private readonly Button _addFavorite = new() { Text = "★ افزودن Favorite", AutoSize = true, Enabled = false };
    private readonly Button _removeFavorite = new() { Text = "حذف Favorite", AutoSize = true, Enabled = false };
    private readonly Button _generate = new() { Text = "تولید فایل متنی", AutoSize = true, Height = 38 };
    private readonly Button _cancel = new() { Text = "لغو", AutoSize = true, Enabled = false, Height = 38 };
    private readonly Button _openOutput = new() { Text = "باز کردن خروجی", AutoSize = true, Enabled = false, Height = 38 };
    private readonly Button _openFolder = new() { Text = "باز کردن پوشه", AutoSize = true, Enabled = false, Height = 38 };
    private readonly Button _configureSql = new() { Text = "SQL Server...", AutoSize = true };
    private readonly Button _clearSql = new() { Text = "حذف SQL", AutoSize = true, Enabled = false };
    private readonly Label _sqlSummary = new() { Text = "SQL data: not configured", AutoSize = true, RightToLeft = RightToLeft.No };
    private readonly Button _configureRedis = new() { Text = "Redis...", AutoSize = true };
    private readonly Button _clearRedis = new() { Text = "حذف Redis", AutoSize = true, Enabled = false };
    private readonly Label _redisSummary = new() { Text = "Redis: not configured", AutoSize = true, RightToLeft = RightToLeft.No };
    private readonly CheckBox _relativePaths = new() { Text = "نمایش مسیر نسبی فایل‌ها در خروجی", AutoSize = true, Checked = true };
    private CancellationTokenSource? _operationCancellation;
    private bool _updatingTreeChecks;
    private DirectoryTreeItem? _directoryTree;
    private readonly HashSet<string> _selectedDirectoryPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _favoriteDirectoryPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Forms.Timer _searchTimer = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer _sizeEstimateTimer = new() { Interval = 350 };
    private CancellationTokenSource? _estimateCancellation;
    private SqlExportOptions? _sqlExportOptions;
    private RedisExportOptions? _redisExportOptions;
    private static readonly string FavoritesFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SolutionBundler", "favorites.json");
    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SolutionBundler", "settings.json");
    private const int WmHScroll = 0x0114;
    private const int SbLeft = 6;

    public MainForm()
    {
        Text = "Solution Bundler — تجمیع فایل‌های پروژه";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 620);
        Size = new Size(1080, 760);
        Font = new Font("Segoe UI", 10F);
        RightToLeft = RightToLeft.Yes;
        RightToLeftLayout = true;
        _folders.ImageList = CreateTreeImageList();
        LoadFavorites();

        BuildLayout();
        WireEvents();
        RestoreLastRoot();
    }

    private void BuildLayout()
    {
        var rootRow = CreatePathRow(_rootPath, _browseRoot);
        var outputRow = CreatePathRow(_outputPath, _browseOutput);

        var selectAll = new Button { Text = "انتخاب همه پسوندها", AutoSize = true };
        var selectNone = new Button { Text = "لغو همه پسوندها", AutoSize = true };
        selectAll.Click += (_, _) => SetAllExtensions(true);
        selectNone.Click += (_, _) => SetAllExtensions(false);

        var extensionActions = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        extensionActions.Controls.AddRange([selectAll, selectNone]);

        var selectAllFolders = new Button { Text = "انتخاب همه", AutoSize = true };
        var selectNoFolders = new Button { Text = "لغو همه", AutoSize = true };
        selectAllFolders.Click += (_, _) => SetAllFolders(true);
        selectNoFolders.Click += (_, _) => SetAllFolders(false);
        _addFavorite.Click += (_, _) => AddSelectedFavorite();
        _removeFavorite.Click += (_, _) => RemoveSelectedFavorite();

        var folderActions = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        folderActions.Controls.AddRange([selectAllFolders, selectNoFolders, _addFavorite, _removeFavorite, _scan]);

        var favoriteMenu = new ContextMenuStrip();
        var addFavoriteMenuItem = new ToolStripMenuItem("★ افزودن به علاقه‌مندی‌ها");
        var removeFavoriteMenuItem = new ToolStripMenuItem("حذف از علاقه‌مندی‌ها");
        addFavoriteMenuItem.Click += (_, _) => AddSelectedFavorite();
        removeFavoriteMenuItem.Click += (_, _) => RemoveSelectedFavorite();
        favoriteMenu.Items.AddRange([addFavoriteMenuItem, removeFavoriteMenuItem]);
        favoriteMenu.Opening += (_, _) =>
        {
            UpdateFavoriteButtons();
            addFavoriteMenuItem.Enabled = _addFavorite.Enabled;
            removeFavoriteMenuItem.Enabled = _removeFavorite.Enabled;
        };
        _folders.ContextMenuStrip = favoriteMenu;

        var clearSearch = new Button
        {
            Text = "×",
            AutoSize = true,
            AccessibleName = "پاک کردن جست‌وجو"
        };
        clearSearch.Click += (_, _) => _folderSearch.Clear();
        var searchRow = CreatePathRow(_folderSearch, clearSearch);

        var folderGroup = new GroupBox
        {
            Text = "۱) پروژه‌ها و پوشه‌ها را انتخاب کنید",
            Dock = DockStyle.Fill,
            Padding = new Padding(8)
        };
        var folderLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        folderLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        folderLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        folderLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        folderLayout.Controls.Add(searchRow, 0, 0);
        folderLayout.Controls.Add(folderActions, 0, 1);
        folderLayout.Controls.Add(_folders, 0, 2);
        folderGroup.Controls.Add(folderLayout);

        var extensionGroup = new GroupBox
        {
            Text = "۲) پسوندها را انتخاب کنید",
            Dock = DockStyle.Fill,
            Padding = new Padding(8)
        };
        var extensionLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        extensionLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        extensionLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        extensionLayout.Controls.Add(extensionActions, 0, 0);
        extensionLayout.Controls.Add(_extensions, 0, 1);
        extensionGroup.Controls.Add(extensionLayout);

        var selectionGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1
        };
        selectionGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        selectionGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        selectionGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        folderGroup.Margin = new Padding(0, 0, 0, 0);
        extensionGroup.Margin = new Padding(8, 0, 0, 0);
        selectionGrid.Controls.Add(folderGroup, 0, 0);
        selectionGrid.Controls.Add(extensionGroup, 1, 0);

        var actionRow = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 0)
        };
        actionRow.Controls.AddRange([_generate, _cancel, _openOutput, _openFolder]);

        var sqlRow = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = true
        };
        sqlRow.Controls.AddRange([_configureSql, _clearSql, _sqlSummary, _configureRedis, _clearRedis, _redisSummary]);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 12
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            Text = "تجمیع فایل‌های Solution در یک فایل متنی",
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 12)
        };

        layout.Controls.Add(title);
        layout.Controls.Add(CreateLabel("پوشه‌ی اصلی Solution:"));
        layout.Controls.Add(rootRow);
        layout.Controls.Add(selectionGrid);
        layout.Controls.Add(_relativePaths);
        layout.Controls.Add(CreateLabel("فایل خروجی:"));
        layout.Controls.Add(outputRow);
        layout.Controls.Add(sqlRow);
        layout.Controls.Add(_estimatedSize);
        layout.Controls.Add(_summary);
        layout.Controls.Add(_progress);
        layout.Controls.Add(actionRow);

        Controls.Add(layout);
        AcceptButton = _generate;
    }

    private static Label CreateLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(0, 8, 0, 4)
    };

    private static Control CreatePathRow(TextBox textBox, Button button)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        textBox.Margin = new Padding(0, 3, 6, 3);
        row.Controls.Add(textBox, 0, 0);
        row.Controls.Add(button, 1, 0);
        return row;
    }

    private void WireEvents()
    {
        _browseRoot.Click += async (_, _) => await ChooseRootAsync();
        _browseOutput.Click += (_, _) => ChooseOutput();
        _scan.Click += async (_, _) => await ScanExtensionsAsync();
        _generate.Click += async (_, _) => await GenerateAsync();
        _cancel.Click += (_, _) => _operationCancellation?.Cancel();
        _openOutput.Click += (_, _) => OpenOutput();
        _openFolder.Click += (_, _) => OpenOutputFolder();
        _configureSql.Click += async (_, _) => await ConfigureSqlServerAsync();
        _clearSql.Click += (_, _) => ClearSqlConfiguration();
        _configureRedis.Click += async (_, _) => await ConfigureRedisAsync();
        _clearRedis.Click += (_, _) => ClearRedisConfiguration();
        _outputPath.TextChanged += (_, _) =>
        {
            _openOutput.Enabled = false;
            _openFolder.Enabled = false;
        };
        _rootPath.Leave += (_, _) => SetDefaultOutputPath();
        _folders.AfterCheck += FoldersAfterCheck;
        _folders.AfterSelect += (_, _) => UpdateFavoriteButtons();
        _folders.NodeMouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
                _folders.SelectedNode = e.Node;
        };
        _folders.Resize += (_, _) => ResetTreeHorizontalScrollSoon();
        _folderSearch.TextChanged += (_, _) =>
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            RebuildVisibleTree();
        };
        _extensions.ItemCheck += (_, _) =>
        {
            _sizeEstimateTimer.Stop();
            _sizeEstimateTimer.Start();
        };
        _relativePaths.CheckedChanged += (_, _) => ScheduleSizeEstimate();
        _sizeEstimateTimer.Tick += async (_, _) =>
        {
            _sizeEstimateTimer.Stop();
            await EstimateOutputSizeAsync();
        };
    }

    private async Task ChooseRootAsync()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "پوشه‌ی اصلی Solution را انتخاب کنید",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
            SelectedPath = Directory.Exists(_rootPath.Text) ? _rootPath.Text : string.Empty
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        _rootPath.Text = dialog.SelectedPath;
        SetDefaultOutputPath(force: true);
        SaveLastRoot(dialog.SelectedPath);
        await LoadFolderTreeAsync();
    }

    private void ChooseOutput()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "انتخاب فایل خروجی",
            Filter = "Text file (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = "txt",
            AddExtension = true,
            FileName = Path.GetFileName(_outputPath.Text)
        };

        var directory = Path.GetDirectoryName(_outputPath.Text);
        if (Directory.Exists(directory))
            dialog.InitialDirectory = directory;

        if (dialog.ShowDialog(this) == DialogResult.OK)
            _outputPath.Text = dialog.FileName;
    }

    private void SetDefaultOutputPath(bool force = false)
    {
        var root = _rootPath.Text.Trim();
        if (!Directory.Exists(root) || (!force && !string.IsNullOrWhiteSpace(_outputPath.Text)))
            return;

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var rootName = Path.GetFileName(normalizedRoot);
        _outputPath.Text = Path.Combine(normalizedRoot, $"{rootName}.txt");
    }

    private async Task LoadFolderTreeAsync()
    {
        var root = _rootPath.Text.Trim();
        if (!Directory.Exists(root))
        {
            ShowError("پوشه‌ی انتخاب‌شده وجود ندارد.");
            return;
        }

        await RunOperationAsync(async token =>
        {
            SetStatus("در حال خواندن پروژه‌ها و پوشه‌ها...", marquee: true);
            var tree = await Task.Run(() => BuildDirectoryTree(Path.GetFullPath(root), token), token);

            _directoryTree = tree;
            _selectedDirectoryPaths.Clear();
            _folderSearch.Clear();
            RebuildVisibleTree();

            _extensions.Items.Clear();
            _summary.Text = $"{tree.DirectoryCount:N0} پوشه پیدا شد. پروژه‌ها یا پوشه‌های موردنظر را تیک بزنید و «اسکن پسوندها» را بزنید.";
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = 0;
        });
    }

    private void FoldersAfterCheck(object? sender, TreeViewEventArgs e)
    {
        if (_updatingTreeChecks || e.Node is null)
            return;

        _updatingTreeChecks = true;
        try
        {
            _folders.SelectedNode = e.Node;
            if (_directoryTree is null)
                return;

            if (e.Node.Tag is string path)
            {
                var item = FindDirectoryItem(_directoryTree, path);
                if (item is null)
                    return;

                SetModelSelection(item, e.Node.Checked);
                if (!e.Node.Checked)
                {
                    var parent = e.Node.Parent;
                    while (parent is not null)
                    {
                        parent.Checked = false;
                        if (parent.Tag is string parentPath)
                            _selectedDirectoryPaths.Remove(parentPath);
                        parent = parent.Parent;
                    }
                }
            }
            else if (e.Node.Text == "★ Favorites")
            {
                foreach (var favorite in GetTopLevelFavoriteItems())
                    SetModelSelection(favorite, e.Node.Checked);
            }

            SyncVisibleChecks();
        }
        finally
        {
            _updatingTreeChecks = false;
        }

        _extensions.Items.Clear();
        _estimatedSize.Text = "Estimated output: —";
        _summary.Text = "انتخاب پوشه‌ها تغییر کرد؛ برای به‌روزرسانی فهرست، «اسکن پسوندها» را بزنید.";
    }

    private void SetAllFolders(bool isChecked)
    {
        if (_directoryTree is null)
            return;

        _updatingTreeChecks = true;
        try
        {
            _selectedDirectoryPaths.Clear();
            if (isChecked)
                SetModelSelection(_directoryTree, true);
            foreach (TreeNode node in _folders.Nodes)
                SetVisibleNodeAndChildren(node, isChecked);
        }
        finally
        {
            _updatingTreeChecks = false;
        }

        _extensions.Items.Clear();
        _estimatedSize.Text = "Estimated output: —";
        _summary.Text = isChecked
            ? "همه‌ی پوشه‌ها انتخاب شدند؛ اکنون «اسکن پسوندها» را بزنید."
            : "همه‌ی انتخاب‌ها لغو شدند.";
    }

    private void SetModelSelection(DirectoryTreeItem item, bool isChecked)
    {
        if (isChecked)
            _selectedDirectoryPaths.Add(item.Path);
        else
            _selectedDirectoryPaths.Remove(item.Path);

        foreach (var child in item.Children)
            SetModelSelection(child, isChecked);
    }

    private static void SetVisibleNodeAndChildren(TreeNode node, bool isChecked)
    {
        node.Checked = isChecked;
        foreach (TreeNode child in node.Nodes)
            SetVisibleNodeAndChildren(child, isChecked);
    }

    private void SyncVisibleChecks()
    {
        foreach (TreeNode node in _folders.Nodes)
            SyncVisibleChecks(node);
    }

    private void SyncVisibleChecks(TreeNode node)
    {
        if (node.Tag is string path)
            node.Checked = _selectedDirectoryPaths.Contains(path);
        foreach (TreeNode child in node.Nodes)
            SyncVisibleChecks(child);
    }

    private List<string> GetSelectedDirectories()
    {
        var result = new List<string>();
        if (_directoryTree is not null)
            CollectSelectedRoots(_directoryTree, ancestorSelected: false, result);
        return result;
    }

    private void CollectSelectedRoots(DirectoryTreeItem item, bool ancestorSelected, List<string> result)
    {
        var isSelected = _selectedDirectoryPaths.Contains(item.Path);
        if (isSelected && !ancestorSelected)
            result.Add(item.Path);

        foreach (var child in item.Children)
            CollectSelectedRoots(child, ancestorSelected || isSelected, result);
    }

    private void RebuildVisibleTree()
    {
        if (_directoryTree is null)
            return;

        var query = _folderSearch.Text.Trim();
        _updatingTreeChecks = true;
        _folders.BeginUpdate();
        try
        {
            _folders.Nodes.Clear();

            var favoriteNodes = GetTopLevelFavoriteItems()
                .Select(item => CreateFilteredTreeNode(item, query))
                .Where(node => node is not null)
                .Cast<TreeNode>()
                .ToArray();
            if (favoriteNodes.Length > 0)
            {
                var favoritesNode = new TreeNode("★ Favorites")
                {
                    ImageKey = "favorite",
                    SelectedImageKey = "favorite",
                    ToolTipText = "Favorite projects and folders"
                };
                favoritesNode.Nodes.AddRange(favoriteNodes);
                _folders.Nodes.Add(favoritesNode);
                favoritesNode.Expand();
            }

            var rootNode = CreateFilteredTreeNode(_directoryTree, query, isRoot: true);
            if (rootNode is not null)
            {
                _folders.Nodes.Add(rootNode);
                if (query.Length > 0)
                    rootNode.ExpandAll();
                else
                    rootNode.Expand();
            }
        }
        finally
        {
            _folders.EndUpdate();
            _updatingTreeChecks = false;
        }
        UpdateFavoriteButtons();
        ResetTreeHorizontalScrollSoon();
    }

    private TreeNode? CreateFilteredTreeNode(DirectoryTreeItem item, string query, bool isRoot = false)
    {
        var visibleChildren = item.Children
            .Select(child => CreateFilteredTreeNode(child, query))
            .Where(node => node is not null)
            .Cast<TreeNode>()
            .ToArray();

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(item.Path));
        var matches = query.Length == 0 || name.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        if (!isRoot && !matches && visibleChildren.Length == 0)
            return null;

        var label = name;
        var node = new TreeNode(label)
        {
            Tag = item.Path,
            Checked = _selectedDirectoryPaths.Contains(item.Path),
            ImageKey = item.IsProject ? "project" : "folder",
            SelectedImageKey = item.IsProject ? "project" : "folder",
            ToolTipText = item.Path
        };
        node.Nodes.AddRange(visibleChildren);
        return node;
    }

    private static DirectoryTreeItem? FindDirectoryItem(DirectoryTreeItem item, string path)
    {
        if (string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))
            return item;
        foreach (var child in item.Children)
        {
            var found = FindDirectoryItem(child, path);
            if (found is not null)
                return found;
        }
        return null;
    }

    private IEnumerable<DirectoryTreeItem> GetTopLevelFavoriteItems()
    {
        if (_directoryTree is null)
            return [];

        var items = _favoriteDirectoryPaths
            .Select(path => FindDirectoryItem(_directoryTree, path))
            .Where(item => item is not null)
            .Cast<DirectoryTreeItem>()
            .OrderBy(item => Path.GetFileName(item.Path), StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return items.Where(item => !items.Any(other =>
            !ReferenceEquals(item, other) && IsPathInside(item.Path, other.Path)));
    }

    private static bool IsPathInside(string path, string possibleParent)
    {
        var parent = Path.TrimEndingDirectorySeparator(possibleParent) + Path.DirectorySeparatorChar;
        return path.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateFavoriteButtons()
    {
        var path = _folders.SelectedNode?.Tag as string;
        var isSelectable = path is not null && _directoryTree is not null &&
            !string.Equals(path, _directoryTree.Path, StringComparison.OrdinalIgnoreCase);
        var isFavorite = isSelectable && _favoriteDirectoryPaths.Contains(path!);
        _addFavorite.Enabled = isSelectable && !isFavorite;
        _removeFavorite.Enabled = isFavorite;
    }

    private void AddSelectedFavorite()
    {
        if (_folders.SelectedNode?.Tag is not string path || _directoryTree is null ||
            string.Equals(path, _directoryTree.Path, StringComparison.OrdinalIgnoreCase))
        {
            ShowError("ابتدا یک پروژه یا پوشه را در درخت انتخاب کنید.");
            return;
        }

        if (!_favoriteDirectoryPaths.Add(path))
        {
            _summary.Text = "این مورد از قبل در علاقه‌مندی‌ها قرار دارد.";
            return;
        }

        SaveFavorites();
        RebuildVisibleTree();
        _summary.Text = $"«{Path.GetFileName(path)}» به علاقه‌مندی‌ها اضافه شد.";
    }

    private void RemoveSelectedFavorite()
    {
        if (_folders.SelectedNode?.Tag is not string path || !_favoriteDirectoryPaths.Contains(path))
        {
            ShowError("یک مورد را از بخش علاقه‌مندی‌ها انتخاب کنید.");
            return;
        }

        _favoriteDirectoryPaths.Remove(path);
        SaveFavorites();
        RebuildVisibleTree();
        _summary.Text = $"«{Path.GetFileName(path)}» از علاقه‌مندی‌ها حذف شد.";
    }

    private void LoadFavorites()
    {
        try
        {
            if (!File.Exists(FavoritesFile))
                return;
            var paths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FavoritesFile));
            if (paths is null)
                return;
            foreach (var path in paths.Where(Directory.Exists))
                _favoriteDirectoryPaths.Add(Path.GetFullPath(path));
        }
        catch
        {
            // A damaged preferences file should not prevent the app from starting.
        }
    }

    private void SaveFavorites()
    {
        try
        {
            var directory = Path.GetDirectoryName(FavoritesFile)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(FavoritesFile,
                JsonSerializer.Serialize(_favoriteDirectoryPaths.OrderBy(x => x), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            ShowError($"ذخیره‌ی علاقه‌مندی‌ها انجام نشد:\n{ex.Message}");
        }
    }

    private void RestoreLastRoot()
    {
        try
        {
            if (!File.Exists(SettingsFile))
                return;
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile));
            if (settings is null || !Directory.Exists(settings.LastRootPath))
                return;

            _rootPath.Text = Path.GetFullPath(settings.LastRootPath);
            SetDefaultOutputPath(force: true);
            Shown += LoadLastRootWhenShown;
        }
        catch
        {
            // Invalid settings should not prevent the app from starting.
        }
    }

    private async void LoadLastRootWhenShown(object? sender, EventArgs e)
    {
        Shown -= LoadLastRootWhenShown;
        await LoadFolderTreeAsync();
    }

    private static void SaveLastRoot(string rootPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            var settings = new AppSettings(Path.GetFullPath(rootPath));
            File.WriteAllText(SettingsFile,
                JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Remembering the last path is helpful but not required for generation.
        }
    }

    private void ResetTreeHorizontalScrollSoon()
    {
        if (!_folders.IsHandleCreated || _folders.IsDisposed)
            return;

        BeginInvoke(() =>
        {
            if (_folders.IsHandleCreated && !_folders.IsDisposed)
                SendMessage(_folders.Handle, WmHScroll, (nint)SbLeft, 0);
        });
    }

    private async Task ConfigureSqlServerAsync()
    {
        var root = _rootPath.Text.Trim();
        if (!Directory.Exists(root))
        {
            ShowError("ابتدا پوشه‌ی Solution را انتخاب کنید.");
            return;
        }

        List<ConnectionCandidate> candidates = [];
        await RunOperationAsync(async token =>
        {
            SetStatus("در حال جست‌وجوی Connection Stringها در کل Solution...", marquee: true);
            candidates = await Task.Run(() => DiscoverConnectionStrings(root, [root], token), token);
            _summary.Text = candidates.Count > 0
                ? $"{candidates.Count:N0} Connection String پیدا شد."
                : "Connection String پیدا نشد؛ امکان ورود دستی وجود دارد.";
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = 0;
        });

        using var dialog = new SqlServerDialog(candidates, _sqlExportOptions);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Options is null)
            return;

        _sqlExportOptions = dialog.Options;
        _clearSql.Enabled = true;
        _sqlSummary.Text = $"SQL data: {_sqlExportOptions.Tables.Count:N0} tables | top {_sqlExportOptions.RowLimit:N0} rows each";
        _summary.Text = "داده‌های SQL Server به خروجی اضافه خواهند شد.";
    }

    private void ClearSqlConfiguration()
    {
        _sqlExportOptions = null;
        _clearSql.Enabled = false;
        _sqlSummary.Text = "SQL data: not configured";
        _summary.Text = "داده‌های SQL Server از خروجی حذف شدند.";
    }

    private async Task ConfigureRedisAsync()
    {
        var root = _rootPath.Text.Trim();
        if (!Directory.Exists(root))
        {
            ShowError("ابتدا پوشه‌ی Solution را انتخاب کنید.");
            return;
        }

        List<RedisConnectionCandidate> candidates = [];
        await RunOperationAsync(async token =>
        {
            SetStatus("در حال جست‌وجوی Redis Connectionها در کل Solution...", marquee: true);
            candidates = await Task.Run(() => DiscoverRedisConnections(root, token), token);
            _summary.Text = candidates.Count > 0
                ? $"{candidates.Count:N0} Redis Connection پیدا شد."
                : "Redis Connection پیدا نشد؛ امکان ورود دستی وجود دارد.";
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = 0;
        });

        using var dialog = new RedisDialog(candidates, _redisExportOptions);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Options is null)
            return;
        _redisExportOptions = dialog.Options;
        _clearRedis.Enabled = true;
        _redisSummary.Text = $"Redis: DB {_redisExportOptions.Database} | top {_redisExportOptions.KeyLimit:N0} keys";
        _summary.Text = "داده‌های Redis به خروجی اضافه خواهند شد.";
    }

    private void ClearRedisConfiguration()
    {
        _redisExportOptions = null;
        _clearRedis.Enabled = false;
        _redisSummary.Text = "Redis: not configured";
        _summary.Text = "داده‌های Redis از خروجی حذف شدند.";
    }

    private static List<RedisConnectionCandidate> DiscoverRedisConnections(string root, CancellationToken token)
    {
        var results = new List<RedisConnectionCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in EnumerateFilesSafely(root, token))
        {
            var name = Path.GetFileName(file);
            if (!name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
                FindRedisConnectionsInJson(document.RootElement, string.Empty, Path.GetRelativePath(root, file), seen, results);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Ignore invalid environment-specific configuration files.
            }
        }
        return results.OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void FindRedisConnectionsInJson(JsonElement element, string path, string relativeFile,
        HashSet<string> seen, List<RedisConnectionCandidate> results)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var propertyPath = path.Length == 0 ? property.Name : $"{path}:{property.Name}";
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString()?.Trim();
                    var nameMatches = property.Name.Contains("Redis", StringComparison.OrdinalIgnoreCase) ||
                                      property.Name.Contains("Cache", StringComparison.OrdinalIgnoreCase);
                    if (nameMatches && LooksLikeRedisConnection(value) && seen.Add(value!))
                        results.Add(new RedisConnectionCandidate($"{propertyPath} — {relativeFile}", value!));
                }
                else
                {
                    FindRedisConnectionsInJson(property.Value, propertyPath, relativeFile, seen, results);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var child in element.EnumerateArray())
                FindRedisConnectionsInJson(child, $"{path}[{index++}]", relativeFile, seen, results);
        }
    }

    private static bool LooksLikeRedisConnection(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("https://", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            return StackExchange.Redis.ConfigurationOptions.Parse(value).EndPoints.Count > 0;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return false;
        }
    }

    private static List<ConnectionCandidate> DiscoverConnectionStrings(
        string root,
        IEnumerable<string> selectedDirectories,
        CancellationToken token)
    {
        var results = new List<ConnectionCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in EnumerateSelectedFiles(selectedDirectories, token))
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            if (!name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
                var relativeFile = Path.GetRelativePath(root, file);
                FindConnectionStringsInJson(
                    document.RootElement,
                    jsonPath: string.Empty,
                    insideConnectionSection: false,
                    relativeFile,
                    seen,
                    results);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Ignore unreadable or invalid environment-specific appsettings files.
            }
        }

        return results.OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void FindConnectionStringsInJson(
        JsonElement element,
        string jsonPath,
        bool insideConnectionSection,
        string relativeFile,
        HashSet<string> seen,
        List<ConnectionCandidate> results)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var propertyPath = jsonPath.Length == 0 ? property.Name : $"{jsonPath}:{property.Name}";
                var nameLooksLikeConnection =
                    property.Name.Contains("ConnectionString", StringComparison.OrdinalIgnoreCase);
                var isConnectionContainer = insideConnectionSection || nameLooksLikeConnection;

                if (property.Value.ValueKind == JsonValueKind.String &&
                    LooksLikeSqlServerConnection(property.Value.GetString()))
                {
                    var value = property.Value.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(value) && seen.Add(value))
                        results.Add(new ConnectionCandidate($"{propertyPath} — {relativeFile}", value));
                }
                else
                {
                    FindConnectionStringsInJson(
                        property.Value,
                        propertyPath,
                        isConnectionContainer,
                        relativeFile,
                        seen,
                        results);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var child in element.EnumerateArray())
            {
                FindConnectionStringsInJson(
                    child,
                    $"{jsonPath}[{index++}]",
                    insideConnectionSection,
                    relativeFile,
                    seen,
                    results);
            }
        }
    }

    private static bool LooksLikeSqlServerConnection(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var hasServer = value.Contains("Server=", StringComparison.OrdinalIgnoreCase) ||
                        value.Contains("Data Source=", StringComparison.OrdinalIgnoreCase) ||
                        value.Contains("Address=", StringComparison.OrdinalIgnoreCase) ||
                        value.Contains("Addr=", StringComparison.OrdinalIgnoreCase) ||
                        value.Contains("Network Address=", StringComparison.OrdinalIgnoreCase);
        var hasDatabase = value.Contains("Database=", StringComparison.OrdinalIgnoreCase) ||
                          value.Contains("Initial Catalog=", StringComparison.OrdinalIgnoreCase) ||
                          value.Contains("AttachDbFilename=", StringComparison.OrdinalIgnoreCase);
        return hasServer && hasDatabase;
    }

    private async Task ScanExtensionsAsync()
    {
        var root = _rootPath.Text.Trim();
        if (!Directory.Exists(root))
        {
            ShowError("پوشه‌ی انتخاب‌شده وجود ندارد.");
            return;
        }

        var selectedDirectories = GetSelectedDirectories();
        if (selectedDirectories.Count == 0)
        {
            ShowError("حداقل یک پروژه یا پوشه را از درخت انتخاب کنید.");
            return;
        }

        var previouslyChecked = _extensions.CheckedItems.Cast<string>()
            .Select(ParseExtension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        await RunOperationAsync(async token =>
        {
            SetStatus("در حال اسکن فایل‌های پوشه‌های انتخاب‌شده...", marquee: true);
            var scan = await Task.Run(() => Scan(selectedDirectories, token), token);

            _extensions.BeginUpdate();
            try
            {
                _extensions.Items.Clear();
                foreach (var item in scan.Extensions.OrderBy(x => x.Extension, StringComparer.OrdinalIgnoreCase))
                {
                    var shouldCheck = previouslyChecked.Count > 0
                        ? previouslyChecked.Contains(item.Extension)
                        : DefaultExtensions.Contains(item.Extension);
                    _extensions.Items.Add($"{item.Extension}  ({item.Count:N0})", shouldCheck);
                }
            }
            finally
            {
                _extensions.EndUpdate();
            }

            _summary.Text = $"در {selectedDirectories.Count:N0} شاخه‌ی انتخابی، {scan.FileCount:N0} فایل و {scan.Extensions.Count:N0} پسوند پیدا شد.";
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = 0;
        });
        ScheduleSizeEstimate();
    }

    private void ScheduleSizeEstimate()
    {
        _sizeEstimateTimer.Stop();
        _sizeEstimateTimer.Start();
    }

    private async Task EstimateOutputSizeAsync()
    {
        if (_operationCancellation is not null)
            return;

        var root = _rootPath.Text.Trim();
        var output = _outputPath.Text.Trim();
        var selectedDirectories = GetSelectedDirectories();
        var selectedExtensions = _extensions.CheckedItems.Cast<string>()
            .Select(ParseExtension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var useRelativePaths = _relativePaths.Checked;

        if (!Directory.Exists(root) || selectedDirectories.Count == 0 || selectedExtensions.Count == 0)
        {
            _estimatedSize.Text = "Estimated output: —";
            return;
        }

        _estimateCancellation?.Cancel();
        _estimateCancellation?.Dispose();
        _estimateCancellation = new CancellationTokenSource();
        var token = _estimateCancellation.Token;
        _estimatedSize.Text = "Calculating estimated output size...";

        try
        {
            var estimate = await Task.Run(() =>
            {
                var files = FindFiles(selectedDirectories, selectedExtensions, output, token);
                long bytes = 3; // UTF-8 BOM
                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var path = useRelativePaths ? Path.GetRelativePath(root, file) : file;
                        bytes += new FileInfo(file).Length;
                        bytes += Encoding.UTF8.GetByteCount(path) + 260; // FILE header and separators
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Ignore files that cannot be measured; generation handles them separately.
                    }
                }
                return new OutputEstimate(files.Count, bytes);
            }, token);

            if (!token.IsCancellationRequested)
                _estimatedSize.Text = $"Estimated output: {FormatSize(estimate.Bytes)} | {estimate.FileCount.ToString("N0", CultureInfo.InvariantCulture)} files";
        }
        catch (OperationCanceledException)
        {
            // A newer selection superseded this estimate.
        }
        catch (Exception ex)
        {
            _estimatedSize.Text = $"Size calculation failed: {ex.Message}";
        }
    }

    private async Task GenerateAsync()
    {
        var root = _rootPath.Text.Trim();
        var output = _outputPath.Text.Trim();
        var selectedDirectories = GetSelectedDirectories();
        var selected = _extensions.CheckedItems.Cast<string>()
            .Select(ParseExtension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(root))
        {
            ShowError("پوشه‌ی انتخاب‌شده وجود ندارد.");
            return;
        }
        if (string.IsNullOrWhiteSpace(output))
        {
            ShowError("مسیر فایل خروجی را مشخص کنید.");
            return;
        }
        if (selectedDirectories.Count == 0)
        {
            ShowError("حداقل یک پروژه یا پوشه را انتخاب کنید.");
            return;
        }
        if (selected.Count == 0)
        {
            ShowError("حداقل یک پسوند را انتخاب کنید.");
            return;
        }

        await RunOperationAsync(async token =>
        {
            SetStatus("در حال آماده‌سازی فهرست فایل‌ها...", marquee: true);
            var files = await Task.Run(() => FindFiles(selectedDirectories, selected, output, token), token);
            if (files.Count == 0)
            {
                _summary.Text = "فایلی با پسوندهای انتخاب‌شده پیدا نشد.";
                _progress.Style = ProgressBarStyle.Continuous;
                return;
            }

            var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(output));
            if (!string.IsNullOrEmpty(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Minimum = 0;
            _progress.Maximum = files.Count;
            _progress.Value = 0;

            await using var stream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            for (var i = 0; i < files.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var file = files[i];
                var displayPath = _relativePaths.Checked ? Path.GetRelativePath(root, file) : file;
                await writer.WriteLineAsync();
                await writer.WriteLineAsync(new string('=', 120));
                await writer.WriteLineAsync($"FILE: {displayPath}");
                await writer.WriteLineAsync(new string('=', 120));

                try
                {
                    using var reader = new StreamReader(file, detectEncodingFromByteOrderMarks: true);
                    var buffer = new char[32 * 1024];
                    int read;
                    while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
                        await writer.WriteAsync(buffer.AsMemory(0, read), token);
                    await writer.WriteLineAsync();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await writer.WriteLineAsync($"[ERROR READING FILE: {ex.Message}]");
                }

                _progress.Value = i + 1;
                _summary.Text = $"در حال پردازش {i + 1:N0} از {files.Count:N0} فایل...";
            }

            SqlExportResult? sqlResult = null;
            if (_sqlExportOptions is not null)
            {
                _summary.Text = $"در حال دریافت حداکثر {_sqlExportOptions.RowLimit:N0} رکورد از هر جدول SQL Server...";
                try
                {
                    sqlResult = await SqlServerExporter.WriteAsync(writer, _sqlExportOptions, token);
                }
                catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
                {
                    await writer.WriteLineAsync();
                    await writer.WriteLineAsync(new string('=', 120));
                    await writer.WriteLineAsync($"SQL SERVER CONNECTION ERROR: {ex.Message}");
                    await writer.WriteLineAsync(new string('=', 120));
                }
            }

            RedisExportResult? redisResult = null;
            if (_redisExportOptions is not null)
            {
                _summary.Text = $"در حال دریافت حداکثر {_redisExportOptions.KeyLimit:N0} کلید از Redis...";
                try
                {
                    redisResult = await RedisExporter.WriteAsync(writer, _redisExportOptions, token);
                }
                catch (Exception ex) when (ex is StackExchange.Redis.RedisException or InvalidOperationException or ArgumentException)
                {
                    await writer.WriteLineAsync();
                    await writer.WriteLineAsync(new string('=', 120));
                    await writer.WriteLineAsync($"REDIS CONNECTION ERROR: {ex.Message}");
                    await writer.WriteLineAsync(new string('=', 120));
                }
            }

            await writer.FlushAsync(token);
            var size = new FileInfo(output).Length;
            var sqlSummary = sqlResult is null
                ? string.Empty
                : $"، {sqlResult.RowCount:N0} رکورد از {sqlResult.TableCount:N0} جدول";
            var redisSummary = redisResult is null ? string.Empty : $"، {redisResult.KeyCount:N0} کلید Redis";
            _summary.Text = $"انجام شد: {files.Count:N0} فایل{sqlSummary}{redisSummary} — حجم خروجی {FormatSize(size)}";
            _estimatedSize.Text = $"Output size: {FormatSize(size)} | {files.Count.ToString("N0", CultureInfo.InvariantCulture)} files";
            _openOutput.Enabled = true;
            _openFolder.Enabled = true;
            MessageBox.Show(this, $"فایل خروجی با موفقیت ساخته شد:\n{output}", "انجام شد", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }, deletePartialOutputOnCancel: output);
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> action, string? deletePartialOutputOnCancel = null)
    {
        if (_operationCancellation is not null)
            return;

        _operationCancellation = new CancellationTokenSource();
        SetControlsEnabled(false);
        try
        {
            await action(_operationCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            if (!string.IsNullOrWhiteSpace(deletePartialOutputOnCancel) && File.Exists(deletePartialOutputOnCancel))
            {
                try { File.Delete(deletePartialOutputOnCancel); } catch { /* Best effort cleanup. */ }
            }
            _summary.Text = "عملیات لغو شد.";
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = 0;
        }
        catch (Exception ex)
        {
            ShowError($"عملیات انجام نشد:\n{ex.Message}");
            _summary.Text = "خطا در انجام عملیات.";
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = 0;
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
            SetControlsEnabled(true);
        }
    }

    private void SetControlsEnabled(bool enabled)
    {
        _browseRoot.Enabled = enabled;
        _browseOutput.Enabled = enabled;
        _scan.Enabled = enabled;
        _configureSql.Enabled = enabled;
        _clearSql.Enabled = enabled && _sqlExportOptions is not null;
        _configureRedis.Enabled = enabled;
        _clearRedis.Enabled = enabled && _redisExportOptions is not null;
        _generate.Enabled = enabled;
        _rootPath.Enabled = enabled;
        _outputPath.Enabled = enabled;
        _extensions.Enabled = enabled;
        _folders.Enabled = enabled;
        _cancel.Enabled = !enabled;
        if (enabled)
            UpdateFavoriteButtons();
        else
        {
            _addFavorite.Enabled = false;
            _removeFavorite.Enabled = false;
        }
    }

    private void SetStatus(string message, bool marquee)
    {
        _summary.Text = message;
        _progress.Style = marquee ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
    }

    private void SetAllExtensions(bool isChecked)
    {
        for (var i = 0; i < _extensions.Items.Count; i++)
            _extensions.SetItemChecked(i, isChecked);
    }

    private void OpenOutput()
    {
        if (!File.Exists(_outputPath.Text))
        {
            ShowError("فایل خروجی وجود ندارد.");
            return;
        }

        Process.Start(new ProcessStartInfo(_outputPath.Text) { UseShellExecute = true });
    }

    private void OpenOutputFolder()
    {
        var fullPath = Path.GetFullPath(_outputPath.Text);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            ShowError("پوشه‌ی خروجی وجود ندارد.");
            return;
        }

        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

    private void ShowError(string message) =>
        MessageBox.Show(this, message, "خطا", MessageBoxButtons.OK, MessageBoxIcon.Error);

    private static ScanResult Scan(IEnumerable<string> selectedDirectories, CancellationToken token)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var fileCount = 0;
        foreach (var file in EnumerateSelectedFiles(selectedDirectories, token))
        {
            fileCount++;
            var extension = Path.GetExtension(file);
            if (string.IsNullOrWhiteSpace(extension))
                continue;
            counts[extension] = counts.GetValueOrDefault(extension) + 1;
        }

        return new ScanResult(fileCount, counts.Select(x => new ExtensionCount(x.Key, x.Value)).ToList());
    }

    private static List<string> FindFiles(IEnumerable<string> selectedDirectories, HashSet<string> selected, string output, CancellationToken token)
    {
        var normalizedOutput = Path.GetFullPath(output);
        return EnumerateSelectedFiles(selectedDirectories, token)
            .Where(file => selected.Contains(Path.GetExtension(file)))
            .Where(file => !string.Equals(Path.GetFullPath(file), normalizedOutput, StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> EnumerateSelectedFiles(IEnumerable<string> selectedDirectories, CancellationToken token)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in selectedDirectories)
        {
            foreach (var file in EnumerateFilesSafely(directory, token))
            {
                if (seen.Add(file))
                    yield return file;
            }
        }
    }

    private static DirectoryTreeItem BuildDirectoryTree(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var children = new List<DirectoryTreeItem>();
        var isProject = false;
        IEnumerable<string> directories;
        try
        {
            isProject = Directory.EnumerateFiles(path, "*.csproj", SearchOption.TopDirectoryOnly).Any();
            directories = Directory.EnumerateDirectories(path)
                .Where(child => !IgnoredDirectories.Contains(Path.GetFileName(child)))
                .OrderBy(child => child, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            directories = [];
        }

        foreach (var child in directories)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    continue;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }
            children.Add(BuildDirectoryTree(child, token));
        }

        return new DirectoryTreeItem(path, isProject, children, 1 + children.Sum(x => x.DirectoryCount));
    }

    private static ImageList CreateTreeImageList()
    {
        var images = new ImageList
        {
            ColorDepth = ColorDepth.Depth32Bit,
            ImageSize = new Size(20, 20),
            TransparentColor = Color.Transparent
        };
        images.Images.Add("folder", DrawFolderIcon());
        images.Images.Add("project", DrawProjectIcon());
        images.Images.Add("favorite", DrawFavoriteIcon());
        return images;
    }

    private static Bitmap DrawFolderIcon()
    {
        var bitmap = new Bitmap(20, 20);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var outline = new Pen(Color.FromArgb(184, 126, 26), 1.2F);
        using var fill = new SolidBrush(Color.FromArgb(248, 193, 68));
        var shape = new PointF[]
        {
            new(1.5F, 5.5F), new(7.5F, 5.5F), new(9.2F, 7.4F),
            new(18.2F, 7.4F), new(17.2F, 17.2F), new(1.5F, 17.2F)
        };
        graphics.FillPolygon(fill, shape);
        graphics.DrawPolygon(outline, shape);
        return bitmap;
    }

    private static Bitmap DrawProjectIcon()
    {
        var bitmap = new Bitmap(20, 20);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var back = new SolidBrush(Color.FromArgb(47, 116, 192));
        using var front = new SolidBrush(Color.FromArgb(92, 161, 230));
        using var white = new Pen(Color.White, 1.3F);
        graphics.FillRectangle(back, 2, 3, 13, 13);
        graphics.FillRectangle(front, 6, 6, 12, 12);
        graphics.DrawRectangle(white, 8.5F, 8.5F, 6.5F, 6.5F);
        return bitmap;
    }

    private static Bitmap DrawFavoriteIcon()
    {
        var bitmap = new Bitmap(20, 20);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var fill = new SolidBrush(Color.FromArgb(242, 177, 32));
        using var outline = new Pen(Color.FromArgb(172, 112, 10), 1F);
        var center = new PointF(10, 10);
        var points = new PointF[10];
        for (var i = 0; i < points.Length; i++)
        {
            var radius = i % 2 == 0 ? 8F : 3.6F;
            var angle = -Math.PI / 2 + i * Math.PI / 5;
            points[i] = new PointF(
                center.X + (float)Math.Cos(angle) * radius,
                center.Y + (float)Math.Sin(angle) * radius);
        }
        graphics.FillPolygon(fill, points);
        graphics.DrawPolygon(outline, points);
        return bitmap;
    }

    private static IEnumerable<string> EnumerateFilesSafely(string root, CancellationToken token)
    {
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));

        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            IEnumerable<string> files;
            IEnumerable<string> directories;
            try
            {
                files = Directory.EnumerateFiles(directory).ToArray();
                directories = Directory.EnumerateDirectories(directory).ToArray();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                yield return file;
            }

            foreach (var child in directories)
            {
                token.ThrowIfCancellationRequested();
                if (IgnoredDirectories.Contains(Path.GetFileName(child)))
                    continue;
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                        continue;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    continue;
                }
                pending.Push(child);
            }
        }
    }

    private static string ParseExtension(string displayText)
    {
        var separator = displayText.IndexOf(' ');
        return separator < 0 ? displayText : displayText[..separator];
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1}", value, units[unit]);
    }

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hWnd, int message, nint wParam, nint lParam);

    private sealed record ExtensionCount(string Extension, int Count);
    private sealed record ScanResult(int FileCount, List<ExtensionCount> Extensions);
    private sealed record OutputEstimate(int FileCount, long Bytes);
    private sealed record DirectoryTreeItem(string Path, bool IsProject, List<DirectoryTreeItem> Children, int DirectoryCount);
    private sealed record AppSettings(string LastRootPath);

    private sealed class LtrTreeView : TreeView
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
}
