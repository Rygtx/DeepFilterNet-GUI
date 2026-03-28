namespace DeepFilterNetGui.ViewModels;

public sealed class ModelItem
{
    public ModelItem(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
    }

    public string Name { get; }
    public string Path { get; }

    public override string ToString() => Name;
}

