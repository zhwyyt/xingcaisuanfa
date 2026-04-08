using System;
using System.Collections.Generic;

namespace TeklaSectionClassifier;

public sealed class DefaultSectionSampler : ISectionSampler
{
    private readonly SamplingOptions _options;
    private readonly IAnomalyDetector _anomalyDetector;
    private readonly ISectionIntersectionService _sectionIntersectionService;

    public DefaultSectionSampler(
        SamplingOptions options,
        IAnomalyDetector anomalyDetector,
        ISectionIntersectionService sectionIntersectionService)
    {
        _options = options;
        _anomalyDetector = anomalyDetector;
        _sectionIntersectionService = sectionIntersectionService;
    }

    public IReadOnlyList<SectionSample> BuildSamples(MemberSnapshot member)
    {
        var samples = new List<SectionSample>();
        for (var index = 0; index < _options.DefaultRatios.Count; index++)
        {
            var ratio = ClampRatio(_options.DefaultRatios[index]);
            var resolved = ResolveStation(member, ratio);
            var sample = new SectionSample
            {
                SampleId = $"S{index + 1:00}",
                Ratio = resolved.Ratio,
                Station = resolved.Station,
                IsAbnormal = resolved.IsAbnormal,
                AbnormalReason = resolved.Reason,
                SectionParts = new List<SectionPartHit>(),
                SectionFeatures = new SectionFeatures()
            };

            samples.Add(_sectionIntersectionService.PopulateSample(member, sample));
        }

        return samples;
    }

    private ResolvedStation ResolveStation(MemberSnapshot member, double ratio)
    {
        var axisLength = member.Member.MainAxis.Length;
        var station = axisLength * ratio;
        if (!_anomalyDetector.IsAbnormalStation(member, station, out var reason))
        {
            return new ResolvedStation(ratio, station, false, string.Empty);
        }

        for (var offsetIndex = 1; offsetIndex <= _options.ShiftAttemptsPerSide; offsetIndex++)
        {
            var shift = axisLength * _options.ShiftStepRatio * offsetIndex;
            var leftStation = station - shift;
            if (leftStation > axisLength * _options.EndExclusionRatio &&
                !_anomalyDetector.IsAbnormalStation(member, leftStation, out _))
            {
                return new ResolvedStation(leftStation / axisLength, leftStation, false, string.Empty);
            }

            var rightStation = station + shift;
            if (rightStation < axisLength * (1 - _options.EndExclusionRatio) &&
                !_anomalyDetector.IsAbnormalStation(member, rightStation, out _))
            {
                return new ResolvedStation(rightStation / axisLength, rightStation, false, string.Empty);
            }
        }

        return new ResolvedStation(ratio, station, true, reason);
    }

    private static double ClampRatio(double ratio)
    {
        if (ratio < 0.0)
        {
            return 0.0;
        }

        if (ratio > 1.0)
        {
            return 1.0;
        }

        return ratio;
    }

    private readonly struct ResolvedStation
    {
        public ResolvedStation(double ratio, double station, bool isAbnormal, string reason)
        {
            Ratio = ratio;
            Station = station;
            IsAbnormal = isAbnormal;
            Reason = reason;
        }

        public double Ratio { get; }
        public double Station { get; }
        public bool IsAbnormal { get; }
        public string Reason { get; }
    }
}
