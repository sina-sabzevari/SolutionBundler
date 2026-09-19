using System.Collections.ObjectModel;

namespace SolutionBundler.Wpf;

public partial class HiddenFoldersDialog : Window
{
    public ObservableCollection<HiddenFolderNode> Roots { get; }

    public IReadOnlyList<string> SelectedPaths => Roots
        .SelectMany(Flatten)
        .Where(node => node.IsHiddenEntry && node.IsChecked)
        .Select(node => node.Path)
        .ToList();

    public HiddenFoldersDialog(IEnumerable<HiddenFolderNode> roots)
    {
        Roots = new ObservableCollection<HiddenFolderNode>(roots);
        InitializeComponent();
        DataContext = this;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private static IEnumerable<HiddenFolderNode> Flatten(HiddenFolderNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in Flatten(child))
                yield return descendant;
    }
}
