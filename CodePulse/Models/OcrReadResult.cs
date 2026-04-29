namespace CodePulse.Models;

public sealed class OcrReadResult
{
    public List<OcrPassResult> Passes { get; init; } = new();

    public double TotalElapsedMs { get; init; }

    public List<OcrStageMetric> StageMetrics { get; init; } = new();
}

public sealed class OcrPassResult
{
    public string PassName { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;

    public float Confidence { get; init; }
}

public sealed class OcrStageMetric
{
    public string StageName { get; init; } = string.Empty;

    public int VariantCount { get; init; }

    public int PageSegModeCount { get; init; }

    public int PassCount { get; init; }

    public double ElapsedMs { get; init; }

    public bool ReturnedEarly { get; init; }
}
