namespace DeepFilterNetGui.Audio;

public sealed class Metrics
{
    public double InferMs { get; set; }
    public double FrameMs { get; set; }
    public double AvgMs { get; set; }
    public double Rtf { get; set; }
    public double LatencyMs { get; set; }
    public double InRms { get; set; }
    public double OutRms { get; set; }
    public double Fps { get; set; }
}

