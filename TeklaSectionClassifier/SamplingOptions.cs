using System.Collections.Generic;

namespace TeklaSectionClassifier;

public sealed class SamplingOptions
{
    public bool Enabled { get; set; } = true;
    public List<double> DefaultRatios { get; set; } = new List<double> { 0.1, 0.3, 0.5, 0.7, 0.9 };
    public double EndExclusionRatio { get; set; } = 0.05;
    public double ShiftStepRatio { get; set; } = 0.02;
    public int ShiftAttemptsPerSide { get; set; } = 3;
    public bool EnableAdaptiveDensify { get; set; } = true;
    public double DenseSampleStepRatio { get; set; } = 0.05;
}
