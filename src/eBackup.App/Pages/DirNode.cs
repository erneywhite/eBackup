namespace eBackup.App.Pages;

/// <summary>Узел дерева удалённых папок (текст узла = последний сегмент пути).</summary>
public sealed class DirNode(string path)
{
    public string Path { get; } = path;

    public override string ToString()
    {
        var trimmed = Path.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        var name = idx >= 0 ? trimmed[(idx + 1)..] : trimmed;
        return name.Length > 0 ? name : Path;
    }
}
