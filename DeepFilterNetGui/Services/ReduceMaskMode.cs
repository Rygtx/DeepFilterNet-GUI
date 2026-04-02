namespace DeepFilterNetGui.Services;

public enum ReduceMaskMode
{
    Independent = 0,
    Maximum = 1,
    Mean = 2
}

public static class ReduceMaskModeExtensions
{
    public static string ToDisplayName(this ReduceMaskMode mode)
    {
        return mode switch
        {
            ReduceMaskMode.Independent => "Independent (NONE)",
            ReduceMaskMode.Maximum => "Maximum (MAX)",
            ReduceMaskMode.Mean => "Mean (MEAN)",
            _ => "Independent (NONE)"
        };
    }
}
