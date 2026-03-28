namespace DeepFilterNetGui.Audio;

public sealed class AudioDeviceItem
{
    public AudioDeviceItem(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }
    public string Name { get; }

    public override string ToString() => Name;
}

