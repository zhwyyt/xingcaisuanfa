using System;
using System.Linq;

namespace TeklaSectionClassifier;

public sealed class DefaultAnomalyDetector : IAnomalyDetector
{
    private readonly SamplingOptions _options;

    public DefaultAnomalyDetector(SamplingOptions options)
    {
        _options = options;
    }

    public bool IsAbnormalStation(MemberSnapshot member, double station, out string reason)
    {
        var axisLength = LongitudinalAxisResolver.GetAxisLength(member);
        if (axisLength <= 0.0)
        {
            reason = "invalid_axis_length";
            return true;
        }

        if (station <= axisLength * _options.EndExclusionRatio)
        {
            reason = "near_start_end_zone";
            return true;
        }

        if (station >= axisLength * (1.0 - _options.EndExclusionRatio))
        {
            reason = "near_end_end_zone";
            return true;
        }

        var localParts = member.Parts.Where(part =>
            {
                var projection = LongitudinalAxisResolver.EstimatePartAxisProjection(member, part);
                return projection.Length > 0.0 &&
                       projection.Start <= station &&
                       projection.End >= station;
            })
            .ToList();

        var shortCoverageCount = localParts.Count(part => LongitudinalAxisResolver.EstimatePartAxisProjection(member, part).CoverageRatio < 0.20);
        if (shortCoverageCount >= 3)
        {
            reason = "dense_attachment_zone";
            return true;
        }

        var wideDisturbanceCount = localParts.Count(part =>
            LongitudinalAxisResolver.EstimatePartAxisProjection(member, part).CoverageRatio < 0.35 &&
            (part.GeometryHints.IsBentLike || part.GeometryHints.IsFoldedLike || part.Thickness > 0.0));
        if (wideDisturbanceCount >= 4)
        {
            reason = "local_geometry_disturbance";
            return true;
        }

        reason = string.Empty;
        return false;
    }
}
