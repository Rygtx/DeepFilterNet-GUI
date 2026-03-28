namespace DeepFilterNetGui.Audio;

public enum AudioBackendType
{
    Wdm,
    Mme,
    Ks,
    Asio
}

public sealed class AudioBackendItem
{
    public AudioBackendItem(AudioBackendType backend, string name)
    {
        Backend = backend;
        Name = name;
    }

    public AudioBackendType Backend { get; }
    public string Name { get; }

    public override string ToString() => Name;
}

