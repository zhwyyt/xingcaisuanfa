using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace TeklaSectionClassifier;

public sealed class FirstPassPartRoleScorer : IPartRoleScorer
{
    public IReadOnlyList<PartRoleAssignment> ScoreRoles(MemberSnapshot member)
    {
        var assignments = new List<PartRoleAssignment>();
        foreach (var part in member.Parts)
        {
            assignments.Add(ScoreSingle(part));
        }

        return assignments;
    }

    private static PartRoleAssignment ScoreSingle(PartSnapshot part)
    {
        var bestRole = "other";
        var bestScore = 0.0;
        var bestReason = "default";

        void Consider(string role, double score, string reason)
        {
            if (score > bestScore)
            {
                bestScore = score;
                bestRole = role;
                bestReason = reason;
            }
        }

        var dx = Math.Abs(part.BoundingBox.Max.X - part.BoundingBox.Min.X);
        var dy = Math.Abs(part.BoundingBox.Max.Y - part.BoundingBox.Min.Y);
        var dz = Math.Abs(part.BoundingBox.Max.Z - part.BoundingBox.Min.Z);
        var dims = new[] { dx, dy, dz }.OrderByDescending(value => value).ToArray();
        var primary = dims[0];
        var secondary = dims[1];
        var tertiary = dims[2];
        var coverage = part.AxisProjection.CoverageRatio;
        var thickness = ResolveEffectiveThickness(part);

        if (coverage >= 0.70)
        {
            if (secondary > 0.0 && primary / secondary >= 10.0)
            {
                Consider("web_candidate", 0.82, "long continuous slender main plate");
            }

            if (secondary > 0.0 && tertiary > 0.0 && secondary / Math.Max(tertiary, 1.0) >= 6.0)
            {
                Consider("flange_candidate", 0.80, "long continuous wide main plate");
            }

            if (thickness > 0 && secondary > 0.0 && secondary / thickness >= 4.0 && primary / secondary < 10.0)
            {
                Consider("wall_candidate", 0.78, "continuous wall-like plate");
            }
        }

        if (coverage < 0.25)
        {
            Consider("stiffener_candidate", 0.88, "short local attachment");
        }

        if ((part.EndProximity.NearStart || part.EndProximity.NearEnd) && coverage < 0.20)
        {
            Consider("endplate_candidate", 0.90, "short local part near member end");
        }

        if (part.GeometryHints.IsFoldedLike)
        {
            Consider("folded_flange_candidate", 0.92, "folded geometry hint");
        }

        var name = part.Name ?? string.Empty;
        if (name.IndexOf("牛腿", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            Consider("bracket_candidate", 0.95, "part name contains bracket keyword");
        }

        if (name.IndexOf("加劲", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("stiff", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            Consider("stiffener_candidate", 0.94, "part name contains stiffener keyword");
        }

        if (bestRole == "other" && coverage >= 0.60 && thickness > 0)
        {
            Consider("wall_candidate", 0.65, "continuous plate fallback");
        }

        return new PartRoleAssignment
        {
            PartId = part.PartId,
            Role = bestRole,
            Score = bestScore,
            Reason = bestReason
        };
    }

    private static double ResolveEffectiveThickness(PartSnapshot part)
    {
        if (part.Thickness > 0)
        {
            return part.Thickness;
        }

        return TryParseThicknessFromProfile(part.ProfileString, out var profileThickness)
            ? profileThickness
            : 0.0;
    }

    private static bool TryParseThicknessFromProfile(string profile, out double thickness)
    {
        thickness = 0.0;
        if (string.IsNullOrWhiteSpace(profile))
        {
            return false;
        }

        var normalized = profile.Replace(" ", string.Empty).ToUpperInvariant();
        var numbers = ExtractNumbers(normalized);
        if (numbers.Count == 0)
        {
            return false;
        }

        if (normalized.StartsWith("PL", StringComparison.OrdinalIgnoreCase))
        {
            thickness = numbers[0];
            return thickness > 0;
        }

        if (normalized.StartsWith("RHS", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("SHS", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("BOX", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("PIPE", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("CHS", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("ROD", StringComparison.OrdinalIgnoreCase))
        {
            thickness = numbers[numbers.Count - 1];
            return thickness > 0;
        }

        return false;
    }

    private static List<double> ExtractNumbers(string text)
    {
        var numbers = new List<double>();
        var buffer = new StringBuilder();
        foreach (var ch in text)
        {
            if ((ch >= '0' && ch <= '9') || ch == '.')
            {
                buffer.Append(ch);
                continue;
            }

            FlushNumber(buffer, numbers);
        }

        FlushNumber(buffer, numbers);
        return numbers;
    }

    private static void FlushNumber(StringBuilder buffer, List<double> numbers)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        if (double.TryParse(buffer.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            numbers.Add(value);
        }

        buffer.Clear();
    }
}
