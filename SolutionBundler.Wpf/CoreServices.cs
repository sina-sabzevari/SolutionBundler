using System.Text;
using System.Text.Json;

namespace SolutionBundler.Wpf;

internal static class CoreServices
{
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    { "bin", "obj", "node_modules", ".git", ".vs", ".idea", ".vscode", "packages" };

    public static ProjectNode BuildTree(string path, CancellationToken token, Action<ProjectNode>? checkedChanged = null)
    {
        token.ThrowIfCancellationRequested();
        var isProject = false;
        string[] directories;
        try
        {
            isProject = Directory.EnumerateFiles(path, "*.csproj", SearchOption.TopDirectoryOnly).Any();
            directories = Directory.EnumerateDirectories(path)
                .Where(x => !Ignored.Contains(Path.GetFileName(x)))
                .Where(x => (File.GetAttributes(x) & FileAttributes.ReparsePoint) == 0)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            directories = [];
        }

        var node = new ProjectNode
        {
            Name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path)),
            Path = Path.GetFullPath(path),
            IsProject = isProject,
            // Segoe Fluent Icons: project/document and folder glyphs.
            Icon = isProject ? "\uE943" : "\uE8B7"
        };
        if (checkedChanged is not null)
            node.CheckedChanged += (_, _) => checkedChanged(node);
        foreach (var directory in directories)
            node.Children.Add(BuildTree(directory, token, checkedChanged));
        return node;
    }

    public static IEnumerable<ProjectNode> Flatten(ProjectNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var item in Flatten(child)) yield return item;
    }

    public static List<string> SelectedRoots(ProjectNode root)
    {
        var result = new List<string>();
        Collect(root, false, result);
        return result;
        static void Collect(ProjectNode node, bool parentSelected, List<string> output)
        {
            if (node.IsChecked && !parentSelected) output.Add(node.Path);
            foreach (var child in node.Children) Collect(child, parentSelected || node.IsChecked, output);
        }
    }

    public static IEnumerable<string> EnumerateFiles(IEnumerable<string> roots, CancellationToken token)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                string[] files;
                string[] children;
                try
                {
                    files = Directory.GetFiles(directory);
                    children = Directory.GetDirectories(directory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { continue; }
                foreach (var file in files)
                    if (seen.Add(file)) yield return file;
                foreach (var child in children)
                {
                    if (Ignored.Contains(Path.GetFileName(child))) continue;
                    try { if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
        }
    }

    public static Dictionary<string, int> ScanExtensions(IEnumerable<string> roots, CancellationToken token) =>
        EnumerateFiles(roots, token)
            .Select(Path.GetExtension).Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);

    public static List<string> FindFiles(IEnumerable<string> roots, HashSet<string> extensions, string output, CancellationToken token)
    {
        var outputFull = Path.GetFullPath(output);
        return EnumerateFiles(roots, token)
            .Where(x => extensions.Contains(Path.GetExtension(x)))
            .Where(x => !string.Equals(Path.GetFullPath(x), outputFull, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static async Task WriteFilesAsync(StreamWriter writer, string root, IReadOnlyList<string> files,
        IProgress<(int current, int total)> progress, CancellationToken token)
    {
        for (var index = 0; index < files.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var file = files[index];
            await writer.WriteLineAsync();
            await writer.WriteLineAsync(new string('=', 120));
            await writer.WriteLineAsync($"FILE: {Path.GetRelativePath(root, file)}");
            await writer.WriteLineAsync(new string('=', 120));
            try
            {
                using var reader = new StreamReader(file, detectEncodingFromByteOrderMarks: true);
                var buffer = new char[32768];
                int read;
                while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
                    await writer.WriteAsync(buffer.AsMemory(0, read), token);
                await writer.WriteLineAsync();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { await writer.WriteLineAsync($"[ERROR READING FILE: {ex.Message}]"); }
            progress.Report((index + 1, files.Count));
        }
    }

    public static List<ConnectionChoice> DiscoverSql(string root, CancellationToken token) =>
        Discover(root, token, IsSql);

    public static List<ConnectionChoice> DiscoverRedis(string root, CancellationToken token) =>
        Discover(root, token, IsRedis);

    private static List<ConnectionChoice> Discover(string root, CancellationToken token, Func<string, string, bool> accept)
    {
        var output = new List<ConnectionChoice>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in EnumerateFiles([root], token))
        {
            var name = Path.GetFileName(file);
            if (!name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                Visit(doc.RootElement, string.Empty, Path.GetRelativePath(root, file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return output.OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();

        void Visit(JsonElement element, string path, string file)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    var next = path.Length == 0 ? property.Name : $"{path}:{property.Name}";
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        var value = property.Value.GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(value) && accept(property.Name, value) && seen.Add(value))
                            output.Add(new ConnectionChoice($"{next} — {file}", value));
                    }
                    else Visit(property.Value, next, file);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var child in element.EnumerateArray()) Visit(child, path, file);
        }
    }

    private static bool IsSql(string _, string value)
    {
        var server = value.Contains("Server=", StringComparison.OrdinalIgnoreCase) || value.Contains("Data Source=", StringComparison.OrdinalIgnoreCase);
        var db = value.Contains("Database=", StringComparison.OrdinalIgnoreCase) || value.Contains("Initial Catalog=", StringComparison.OrdinalIgnoreCase);
        return server && db;
    }

    private static bool IsRedis(string name, string value) =>
        (name.Contains("Cache", StringComparison.OrdinalIgnoreCase) || name.Contains("Redis", StringComparison.OrdinalIgnoreCase)) &&
        !value.Contains("http", StringComparison.OrdinalIgnoreCase) && value.Contains(':');
}

internal sealed record ModernSettings(string? LastRoot, List<string>? Favorites);

internal static class SettingsStore
{
    private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolutionBundler", "modern-settings.json");
    public static ModernSettings Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<ModernSettings>(File.ReadAllText(FilePath)) ?? new(null, []) : new(null, []); }
        catch { return new(null, []); }
    }
    public static void Save(ModernSettings settings)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }
}
