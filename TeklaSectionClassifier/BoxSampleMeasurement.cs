using System.Collections.Generic;

namespace TeklaSectionClassifier;

internal readonly struct BoxSampleMeasurement
{
    public BoxSampleMeasurement(double widthEstimate, double heightEstimate, IReadOnlyList<double> horizontalThicknesses, IReadOnlyList<double> verticalThicknesses)
    {
        WidthEstimate = widthEstimate;
        HeightEstimate = heightEstimate;
        HorizontalThicknesses = horizontalThicknesses;
        VerticalThicknesses = verticalThicknesses;
    }

    public double WidthEstimate { get; }
    public double HeightEstimate { get; }
    public IReadOnlyList<double> HorizontalThicknesses { get; }
    public IReadOnlyList<double> VerticalThicknesses { get; }
}
