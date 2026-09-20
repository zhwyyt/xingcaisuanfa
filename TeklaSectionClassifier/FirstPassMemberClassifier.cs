using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TeklaSectionClassifier;

public sealed class FirstPassMemberClassifier : IMemberClassifier
{
    private readonly IPartRoleScorer _partRoleScorer;

    public FirstPassMemberClassifier(IPartRoleScorer partRoleScorer)
    {
        _partRoleScorer = partRoleScorer;
    }

    public ClassificationResult Classify(MemberSnapshot member)
    {
        var roles = _partRoleScorer.ScoreRoles(member);
        var roleMap = roles.ToDictionary(role => role.PartId, role => role);
        var stableSamples = member.Samples.Where(sample => !sample.IsAbnormal).ToList();
        var abnormalSamples = member.Samples.Where(sample => sample.IsAbnormal).ToList();
        var profile = NormalizeProfile(member.Member.ProfileString);
        var hasDirectBoxHint = LooksLikeBoxProfile(profile);
        var hasDirectCrossHint = LooksLikeCrossProfile(profile) || HasCrossNameHint(member.Member.Name);
        var hasDirectHHint = LooksLikeHProfile(profile);
        var hasDirectTHint = LooksLikeTProfile(profile);
        var hasDirectAngleHint = profile.StartsWith("L", StringComparison.OrdinalIgnoreCase);
        var hasDirectPipeHint = LooksLikePipeProfile(profile);
        var hasTrustedDirectProfile = hasDirectBoxHint || hasDirectHHint || hasDirectTHint || hasDirectAngleHint || hasDirectPipeHint || LooksLikeCrossProfile(profile);
        var scoreMap = new Dictionary<MemberClass, ScoreBreakdown>
        {
            [MemberClass.H] = NewScore(MemberClass.H),
            [MemberClass.Box] = NewScore(MemberClass.Box),
            [MemberClass.T] = NewScore(MemberClass.T),
            [MemberClass.Cross] = NewScore(MemberClass.Cross),
            [MemberClass.Angle] = NewScore(MemberClass.Angle),
            [MemberClass.Pipe] = NewScore(MemberClass.Pipe),
            [MemberClass.Irregular] = NewScore(MemberClass.Irregular)
        };

        ApplyProfileHints(member, scoreMap);

        foreach (var sample in stableSamples)
        {
            ScoreSample(sample, roleMap, scoreMap);
        }


        if (hasDirectHHint)
        {
            scoreMap[MemberClass.T].Score -= 70;
            scoreMap[MemberClass.T].Reasons.Add("direct H profile hint suppresses weak T fallback");
            scoreMap[MemberClass.Cross].Score -= 70;
            scoreMap[MemberClass.Cross].Reasons.Add("direct H profile hint suppresses weak cross fallback");
        }

        if (hasDirectTHint)
        {
            scoreMap[MemberClass.H].Score -= 60;
            scoreMap[MemberClass.H].Reasons.Add("direct T profile hint suppresses weak H fallback");
            scoreMap[MemberClass.Cross].Score -= 40;
            scoreMap[MemberClass.Cross].Reasons.Add("direct T profile hint suppresses weak cross fallback");
        }

        if (hasDirectAngleHint)
        {
            scoreMap[MemberClass.H].Score -= 80;
            scoreMap[MemberClass.H].Reasons.Add("direct angle profile hint suppresses weak H fallback");
            scoreMap[MemberClass.T].Score -= 60;
            scoreMap[MemberClass.T].Reasons.Add("direct angle profile hint suppresses weak T fallback");
            scoreMap[MemberClass.Cross].Score -= 60;
            scoreMap[MemberClass.Cross].Reasons.Add("direct angle profile hint suppresses weak cross fallback");
        }

        if (hasDirectCrossHint)
        {
            scoreMap[MemberClass.H].Score -= 80;
            scoreMap[MemberClass.H].Reasons.Add("direct cross hint suppresses H fallback");
            scoreMap[MemberClass.T].Score -= 80;
            scoreMap[MemberClass.T].Reasons.Add("direct cross hint suppresses T fallback");
        }

        if (hasDirectPipeHint)
        {
            scoreMap[MemberClass.H].Score -= 80;
            scoreMap[MemberClass.H].Reasons.Add("direct pipe profile hint suppresses H fallback");
            scoreMap[MemberClass.T].Score -= 80;
            scoreMap[MemberClass.T].Reasons.Add("direct pipe profile hint suppresses T fallback");
            scoreMap[MemberClass.Cross].Score -= 60;
            scoreMap[MemberClass.Cross].Reasons.Add("direct pipe profile hint suppresses weak cross fallback");
        }
        if (hasDirectBoxHint)
        {
            scoreMap[MemberClass.Box].Score += 20;
            scoreMap[MemberClass.Box].Reasons.Add("direct box profile hint is trusted over weak section slices");
            scoreMap[MemberClass.Irregular].Score -= 40;
            scoreMap[MemberClass.Irregular].Reasons.Add("direct box profile hint suppresses irregular fallback");
        }

        ApplyBoxRecoveryFromAbnormalSamples(stableSamples, abnormalSamples, scoreMap);

        if (stableSamples.Count == 0)
        {
            if (scoreMap[MemberClass.Box].Score >= 50)
            {
                scoreMap[MemberClass.Box].Score += 15;
                scoreMap[MemberClass.Box].Reasons.Add("abnormal-sample recovery compensates for missing stable slices");
            }
            else if (!hasTrustedDirectProfile)
            {
                scoreMap[MemberClass.Irregular].Score += 100;
                scoreMap[MemberClass.Irregular].Reasons.Add("no stable samples");
            }
        }

        var labels = BuildLabels(member, stableSamples, roles);
        var ordered = scoreMap.Values.OrderByDescending(item => item.Score).ToList();
        var winner = ordered[0];
        var runnerUp = ordered.Count > 1 ? ordered[1] : NewScore(MemberClass.Unknown);
        var confidence = ComputeConfidence(winner.ClassName, winner.Score, runnerUp.Score);
        var secondaryClass = BuildSecondaryClass(member, labels, winner.ClassName);
        var isTrustedDirectClassification = IsTrustedDirectClassification(profile, winner.ClassName);
        if (isTrustedDirectClassification && winner.ClassName == MemberClass.H)
        {
            secondaryClass = ClassifyTrustedDirectHSecondaryClass(member, labels);
        }

        if (winner.ClassName == MemberClass.Box)
        {
            secondaryClass = ClassifyBoxSecondaryClass(member, roleMap, labels, secondaryClass);
        }

        if (isTrustedDirectClassification)
        {
            confidence = 99;
        }

        return new ClassificationResult
        {
            MainClass = winner.ClassName,
            SecondaryClass = secondaryClass,
            Confidence = confidence,
            NeedsManualReview = !isTrustedDirectClassification && (confidence < 75 || winner.ClassName == MemberClass.Irregular || winner.ClassName == MemberClass.Cross),
            ReviewReason = isTrustedDirectClassification ? string.Empty : BuildReviewReason(winner, runnerUp, confidence),
            KeyDimensionsDisplay = BuildKeyDimensions(member, stableSamples, winner.ClassName, secondaryClass, roleMap),
            Labels = labels,
            Scores = ordered,
            PartRoles = roles.ToList()
        };
    }

    private static void ApplyProfileHints(MemberSnapshot member, IDictionary<MemberClass, ScoreBreakdown> scoreMap)
    {
        var profile = NormalizeProfile(member.Member.ProfileString);
        if (string.IsNullOrWhiteSpace(profile))
        {
            return;
        }

        if (profile.StartsWith("BOX", StringComparison.OrdinalIgnoreCase) ||
            profile.StartsWith("SHS", StringComparison.OrdinalIgnoreCase) ||
            profile.StartsWith("RHS", StringComparison.OrdinalIgnoreCase))
        {
            scoreMap[MemberClass.Box].Score += 140;
            scoreMap[MemberClass.Box].Reasons.Add($"profile hint: {member.Member.ProfileString}");
            return;
        }

        if (LooksLikePipeProfile(profile))
        {
            scoreMap[MemberClass.Pipe].Score += 140;
            scoreMap[MemberClass.Pipe].Reasons.Add($"profile hint: pipe profile {member.Member.ProfileString}");
            return;
        }

        if (LooksLikeCrossProfile(profile) || HasCrossNameHint(member.Member.Name))
        {
            scoreMap[MemberClass.Cross].Score += 140;
            scoreMap[MemberClass.Cross].Reasons.Add($"profile/name hint: {member.Member.Name} {member.Member.ProfileString}".Trim());
            return;
        }

        if (LooksLikeHProfile(profile))
        {
            scoreMap[MemberClass.H].Score += 120;
            scoreMap[MemberClass.H].Reasons.Add($"profile hint: {member.Member.ProfileString}");
            return;
        }

        if (LooksLikeTProfile(profile))
        {
            scoreMap[MemberClass.T].Score += 110;
            scoreMap[MemberClass.T].Reasons.Add($"profile hint: {member.Member.ProfileString}");
            return;
        }

        if (profile.StartsWith("L", StringComparison.OrdinalIgnoreCase))
        {
            scoreMap[MemberClass.Angle].Score += 140;
            scoreMap[MemberClass.Angle].Reasons.Add($"profile hint: angle profile {member.Member.ProfileString}");
            return;
        }
    }

    private static void ScoreSample(SectionSample sample, IReadOnlyDictionary<string, PartRoleAssignment> roleMap, IDictionary<MemberClass, ScoreBreakdown> scoreMap)
    {
        var metrics = AnalyzeSample(sample, roleMap);
        var width = sample.SectionFeatures.OuterWidth;
        var height = sample.SectionFeatures.OuterHeight;
        var symmetry = (sample.SectionFeatures.HorizontalSymmetryScore + sample.SectionFeatures.VerticalSymmetryScore) / 2.0;
        var enclosure = sample.SectionFeatures.EnclosureScore;
        var hasStrongBoxCore = sample.SectionFeatures.CentralHorizontalPlateCount >= 4 && sample.SectionFeatures.CentralVerticalPlateCount >= 4 && sample.SectionFeatures.MajorPlateCount >= 8 && enclosure >= 0.35;
        var hasClosureEvidence = sample.SectionFeatures.ClosedLoops > 0 || sample.SectionFeatures.CavityCount > 0;
        var hasOpenSideWallsWithoutClosure = metrics.OuterVerticalBands >= 2 && metrics.CentralVerticalBands >= 1 && !hasStrongBoxCore && !hasClosureEvidence;
        var hasSimpleBoxShell = !hasClosureEvidence &&
            metrics.OuterHorizontalBands >= 2 &&
            metrics.OuterVerticalBands >= 2 &&
            metrics.CentralHorizontalBands == 0 &&
            metrics.CentralVerticalBands == 0 &&
            sample.SectionFeatures.MajorPlateCount >= 4 && sample.SectionFeatures.MajorPlateCount <= 6 &&
            enclosure >= 0.35;

        if (hasSimpleBoxShell)
        {
            scoreMap[MemberClass.Box].Score += 40;
            scoreMap[MemberClass.Box].Reasons.Add($"sample {sample.SampleId}: four outer wall shell forms a closed box");
            scoreMap[MemberClass.H].Score -= 30;
            scoreMap[MemberClass.H].Reasons.Add($"sample {sample.SampleId}: closed four-wall shell suppresses H interpretation");
        }

        if (metrics.HorizontalBands == 2 && metrics.VerticalBands == 1 && metrics.VerticalBandBetweenHorizontalBands && metrics.HasVerticalHitConnectingOuterHorizontals)
        {
            scoreMap[MemberClass.H].Score += 30;
            scoreMap[MemberClass.H].Reasons.Add($"sample {sample.SampleId}: two horizontal bands bridged by one web band");
        }

        if (metrics.OuterHorizontalBands >= 2 && metrics.CentralVerticalBands >= 1 && metrics.OuterVerticalBands <= 2 && metrics.CentralHorizontalBands == 0 && metrics.HasVerticalHitConnectingOuterHorizontals)
        {
            scoreMap[MemberClass.H].Score += 28;
            scoreMap[MemberClass.H].Reasons.Add($"sample {sample.SampleId}: two flange bands with central web band");
        }

        if (metrics.HorizontalBands == 2 && metrics.VerticalBands == 1 && metrics.HorizontalBandSpan >= height * 0.35 && metrics.HasVerticalHitConnectingOuterHorizontals)
        {
            scoreMap[MemberClass.H].Score += 12;
            scoreMap[MemberClass.H].Reasons.Add($"sample {sample.SampleId}: flange band span is large enough for H");
        }

        if (((metrics.HorizontalBands == 2 && metrics.VerticalBands == 1 && metrics.AttachmentCount >= 2) ||
             (metrics.OuterHorizontalBands >= 2 && metrics.CentralVerticalBands >= 1 && metrics.AttachmentCount >= 2)) && metrics.HasVerticalHitConnectingOuterHorizontals)
        {
            scoreMap[MemberClass.H].Score += 8;
            scoreMap[MemberClass.H].Reasons.Add($"sample {sample.SampleId}: H core remains stable with attachments");
        }

        if (!hasClosureEvidence &&
            metrics.HorizontalBands >= 3 && metrics.VerticalBands == 0 &&
            sample.SectionFeatures.CentralHorizontalPlateCount >= 3 &&
            sample.SectionFeatures.CentralVerticalPlateCount == 0 &&
            sample.SectionFeatures.MajorPlateCount >= 3 && sample.SectionFeatures.MajorPlateCount <= 5 &&
            enclosure <= 0.15 && symmetry >= 0.75)
        {
            scoreMap[MemberClass.H].Score += 24;
            scoreMap[MemberClass.H].Reasons.Add($"sample {sample.SampleId}: open built-up H slice with missing web hit");
        }

        if (hasClosureEvidence && metrics.OuterHorizontalBands >= 2 && metrics.OuterVerticalBands >= 2 && metrics.CentralHorizontalBands >= 1 && symmetry >= 0.85)
        {
            scoreMap[MemberClass.Box].Score += 26;
            scoreMap[MemberClass.Box].Reasons.Add($"sample {sample.SampleId}: closed box-like band layout");
        }

        if (hasStrongBoxCore)
        {
            scoreMap[MemberClass.Box].Score += 34;
            scoreMap[MemberClass.Box].Reasons.Add($"sample {sample.SampleId}: dense orthogonal inner walls indicate multi-cell box");
        }

        var hasModerateBoxCore = !hasStrongBoxCore &&
            metrics.OuterHorizontalBands >= 2 &&
            metrics.OuterVerticalBands >= 2 &&
            sample.SectionFeatures.CentralHorizontalPlateCount >= 4 &&
            sample.SectionFeatures.CentralVerticalPlateCount >= 2 &&
            sample.SectionFeatures.MajorPlateCount >= 8 &&
            enclosure >= 0.35;

        if (hasModerateBoxCore)
        {
            scoreMap[MemberClass.Box].Score += 22;
            scoreMap[MemberClass.Box].Reasons.Add($"sample {sample.SampleId}: moderate enclosed box core with diaphragms");
        }

        if (hasClosureEvidence && metrics.OuterVerticalBands >= 2 &&
            metrics.CentralVerticalBands >= 1 &&
            sample.SectionFeatures.CentralHorizontalPlateCount >= 2 &&
            enclosure >= 0.35)
        {
            scoreMap[MemberClass.Box].Score += 18;
            scoreMap[MemberClass.Box].Reasons.Add($"sample {sample.SampleId}: side walls plus internal diaphragms support box");
        }

        if (hasClosureEvidence)
        {
            scoreMap[MemberClass.Box].Score += 18;
            scoreMap[MemberClass.Box].Reasons.Add($"sample {sample.SampleId}: enclosure evidence for box section");
        }

        if (hasClosureEvidence &&
            metrics.OuterVerticalBands >= 2 &&
            metrics.OuterHorizontalBands >= 2 &&
            !metrics.HasVerticalHitConnectingOuterHorizontals)
        {
            scoreMap[MemberClass.H].Score -= 24;
            scoreMap[MemberClass.H].Reasons.Add($"sample {sample.SampleId}: enclosed side walls but no web truly bridges both flanges");
        }

        if (hasOpenSideWallsWithoutClosure)
        {
            scoreMap[MemberClass.Box].Score -= 36;
            scoreMap[MemberClass.Box].Reasons.Add($"sample {sample.SampleId}: outer side walls exist but no closed outer ring");
            scoreMap[MemberClass.H].Score += 20;
            scoreMap[MemberClass.H].Reasons.Add($"sample {sample.SampleId}: open side-wall layout fits built-up H better than box");
        }

        if (metrics.HorizontalBands == 1 && metrics.VerticalBands == 1 && metrics.MainBandCount <= 3)
        {
            scoreMap[MemberClass.T].Score += 24;
            scoreMap[MemberClass.T].Reasons.Add($"sample {sample.SampleId}: single flange band with one web band");
        }
        else if (metrics.HorizontalBands == 2 && metrics.VerticalBands == 1 && !metrics.VerticalBandBetweenHorizontalBands)
        {
            scoreMap[MemberClass.T].Score += 10;
            scoreMap[MemberClass.T].Reasons.Add($"sample {sample.SampleId}: two horizontal bands sit on one side of the web");
        }

        var hasCompactCrossCore = metrics.CentralVerticalBands >= 1 && metrics.CentralHorizontalBands >= 1 &&
            metrics.OuterHorizontalBands <= 1 && metrics.OuterVerticalBands <= 1 && symmetry >= 0.90;

        if (hasCompactCrossCore)
        {
            scoreMap[MemberClass.Cross].Score += 26;
            scoreMap[MemberClass.Cross].Reasons.Add($"sample {sample.SampleId}: open cross-like orthogonal core");
        }

        var hasExtendedCrossCore = !hasClosureEvidence &&
            metrics.TopOuterHorizontalBands >= 1 && metrics.BottomOuterHorizontalBands >= 1 &&
            metrics.LeftOuterVerticalBands >= 1 && metrics.RightOuterVerticalBands >= 1 &&
            metrics.CentralHorizontalBands >= 1 && metrics.CentralVerticalBands >= 1 &&
            metrics.HorizontalBands <= 3 && metrics.VerticalBands <= 3 &&
            sample.SectionFeatures.MajorPlateCount >= 6 && sample.SectionFeatures.MajorPlateCount <= 8 &&
            symmetry >= 0.90;

        if (hasExtendedCrossCore)
        {
            scoreMap[MemberClass.Cross].Score += 46;
            scoreMap[MemberClass.Cross].Reasons.Add($"sample {sample.SampleId}: four-arm cross column layout");
            scoreMap[MemberClass.H].Score -= 18;
            scoreMap[MemberClass.H].Reasons.Add($"sample {sample.SampleId}: balanced four-arm layout weakens H hypothesis");
        }

        if (metrics.MainBandCount <= 1 && !LooksLikeBoxSlice(sample))
        {
            scoreMap[MemberClass.Irregular].Score += 15;
            scoreMap[MemberClass.Irregular].Reasons.Add($"sample {sample.SampleId}: too few stable main bands");
        }

        if (width <= 0 || height <= 0)
        {
            scoreMap[MemberClass.Irregular].Score += 5;
            scoreMap[MemberClass.Irregular].Reasons.Add($"sample {sample.SampleId}: invalid dimensions");
        }

        if (metrics.CentralHorizontalBands >= 1 && metrics.OuterVerticalBands >= 2)
        {
            scoreMap[MemberClass.H].Score -= 8;
            scoreMap[MemberClass.H].Reasons.Add($"sample {sample.SampleId}: central horizontal wall weakens H hypothesis");
        }

        if (enclosure < 0.25 && metrics.OuterVerticalBands <= 1 && !LooksLikeBoxSlice(sample))
        {
            scoreMap[MemberClass.Box].Score -= 6;
            scoreMap[MemberClass.Box].Reasons.Add($"sample {sample.SampleId}: enclosure too weak for box");
        }
    }

    private static SampleMetrics AnalyzeSample(SectionSample sample, IReadOnlyDictionary<string, PartRoleAssignment> roleMap)
    {
        var width = Math.Max(sample.SectionFeatures.OuterWidth, 1.0);
        var height = Math.Max(sample.SectionFeatures.OuterHeight, 1.0);
        var horizontalTolerance = Math.Min(120.0, Math.Max(12.0, height * 0.08));
        var verticalTolerance = Math.Min(120.0, Math.Max(12.0, width * 0.08));

        var coreHits = new List<SectionPartHit>();
        var attachmentCount = 0;

        foreach (var hit in sample.SectionParts)
        {
            var role = ResolveEffectiveRole(hit, roleMap);
            if (IsAttachmentRole(role))
            {
                attachmentCount++;
                continue;
            }

            if (hit.TotalCutLength <= 0)
            {
                continue;
            }

            coreHits.Add(hit);
        }

        var maxHorizontalLength = coreHits
            .Where(hit => Math.Abs(hit.Direction2D.X) >= Math.Abs(hit.Direction2D.Y))
            .Select(hit => hit.TotalCutLength)
            .DefaultIfEmpty(0)
            .Max();
        var maxVerticalLength = coreHits
            .Where(hit => Math.Abs(hit.Direction2D.Y) > Math.Abs(hit.Direction2D.X))
            .Select(hit => hit.TotalCutLength)
            .DefaultIfEmpty(0)
            .Max();

        var horizontalHits = coreHits
            .Where(hit => Math.Abs(hit.Direction2D.X) >= Math.Abs(hit.Direction2D.Y))
            .Where(hit => hit.TotalCutLength >= Math.Max(width * 0.18, maxHorizontalLength * 0.35))
            .ToList();
        var verticalHits = coreHits
            .Where(hit => Math.Abs(hit.Direction2D.Y) > Math.Abs(hit.Direction2D.X))
            .Where(hit => hit.TotalCutLength >= Math.Max(height * 0.18, maxVerticalLength * 0.35))
            .ToList();

        var horizontalBands = BuildBands(horizontalHits, hit => hit.Center2D.Y, horizontalTolerance);
        var verticalBands = BuildBands(verticalHits, hit => hit.Center2D.X, verticalTolerance);

        var horizontalCenter = horizontalBands.Count > 0
            ? (horizontalBands.Min(band => band.Position) + horizontalBands.Max(band => band.Position)) / 2.0
            : 0.0;
        var verticalCenter = verticalBands.Count > 0
            ? (verticalBands.Min(band => band.Position) + verticalBands.Max(band => band.Position)) / 2.0
            : 0.0;

        var outerHorizontalBands = horizontalBands.Count(band => Math.Abs(band.Position - horizontalCenter) >= height * 0.18);
        var topOuterHorizontalBands = horizontalBands.Count(band => band.Position <= horizontalCenter - (height * 0.18));
        var bottomOuterHorizontalBands = horizontalBands.Count(band => band.Position >= horizontalCenter + (height * 0.18));
        var centralHorizontalBands = horizontalBands.Count(band => Math.Abs(band.Position - horizontalCenter) < height * 0.18);
        var outerVerticalBands = verticalBands.Count(band => Math.Abs(band.Position - verticalCenter) >= width * 0.18);
        var leftOuterVerticalBands = verticalBands.Count(band => band.Position <= verticalCenter - (width * 0.18));
        var rightOuterVerticalBands = verticalBands.Count(band => band.Position >= verticalCenter + (width * 0.18));
        var centralVerticalBands = verticalBands.Count(band => Math.Abs(band.Position - verticalCenter) < width * 0.18);
        var outerHorizontalPositions = horizontalBands.Where(band => Math.Abs(band.Position - horizontalCenter) >= height * 0.18).Select(band => band.Position).ToList();
        var outerVerticalPositions = verticalBands.Where(band => Math.Abs(band.Position - verticalCenter) >= width * 0.18).Select(band => band.Position).ToList();
        var outerHorizontalBandSpan = outerHorizontalPositions.Count >= 2 ? outerHorizontalPositions.Max() - outerHorizontalPositions.Min() : 0.0;
        var outerVerticalBandSpan = outerVerticalPositions.Count >= 2 ? outerVerticalPositions.Max() - outerVerticalPositions.Min() : 0.0;
        var horizontalBandSpan = horizontalBands.Count >= 2 ? horizontalBands.Max(band => band.Position) - horizontalBands.Min(band => band.Position) : 0.0;
        var verticalBandBetweenHorizontalBands = verticalBands.Any(band => Math.Abs(band.Position - verticalCenter) < width * 0.18);
        var topOuterHorizontalPosition = outerHorizontalPositions.Count > 0 ? outerHorizontalPositions.Min() : 0.0;
        var bottomOuterHorizontalPosition = outerHorizontalPositions.Count > 0 ? outerHorizontalPositions.Max() : 0.0;
        var hasVerticalHitConnectingOuterHorizontals = verticalHits.Any(hit =>
        {
            var spanY = hit.CutSpanY > 0 ? hit.CutSpanY : hit.TotalCutLength;
            var minY = hit.Center2D.Y - (spanY / 2.0);
            var maxY = hit.Center2D.Y + (spanY / 2.0);
            var isCentralWebLikeHit = Math.Abs(hit.Center2D.X - verticalCenter) < width * 0.18;
            return isCentralWebLikeHit &&
                   outerHorizontalPositions.Count >= 2 &&
                   minY <= topOuterHorizontalPosition + horizontalTolerance &&
                   maxY >= bottomOuterHorizontalPosition - horizontalTolerance;
        });

        return new SampleMetrics(
            horizontalBands.Count,
            verticalBands.Count,
            outerHorizontalBands,
            topOuterHorizontalBands,
            bottomOuterHorizontalBands,
            centralHorizontalBands,
            outerVerticalBands,
            leftOuterVerticalBands,
            rightOuterVerticalBands,
            centralVerticalBands,
            outerHorizontalBandSpan,
            outerVerticalBandSpan,
            horizontalBands.Count + verticalBands.Count,
            attachmentCount,
            horizontalBandSpan,
            hasVerticalHitConnectingOuterHorizontals,
            verticalBandBetweenHorizontalBands);
    }

    private static List<BandInfo> BuildBands(IReadOnlyList<SectionPartHit> hits, Func<SectionPartHit, double> selector, double tolerance)
    {
        var bands = new List<BandInfo>();
        foreach (var hit in hits.OrderBy(selector))
        {
            var position = selector(hit);
            var existing = bands.FirstOrDefault(band => Math.Abs(band.Position - position) <= tolerance);
            if (existing == null)
            {
                bands.Add(new BandInfo(position, hit.TotalCutLength, 1));
                continue;
            }

            var mergedLength = Math.Max(existing.TotalLength, hit.TotalCutLength);
            var mergedCount = existing.HitCount + 1;
            var mergedPosition = ((existing.Position * existing.HitCount) + position) / mergedCount;
            bands.Remove(existing);
            bands.Add(new BandInfo(mergedPosition, mergedLength, mergedCount));
        }

        return bands.OrderBy(band => band.Position).ToList();
    }

    private static bool IsAttachmentRole(string role)
    {
        return role == "stiffener_candidate" || role == "endplate_candidate" || role == "bracket_candidate";
    }

    private static string ResolveEffectiveRole(SectionPartHit hit, IReadOnlyDictionary<string, PartRoleAssignment> roleMap)
    {
        var sampleRole = hit.RoleHint ?? string.Empty;
        var globalRole = roleMap.TryGetValue(hit.PartId, out var assignment) ? assignment.Role : string.Empty;

        if (IsAttachmentRole(globalRole))
        {
            return globalRole;
        }

        if (sampleRole == "direct_profile_body")
        {
            return string.IsNullOrWhiteSpace(globalRole) ? "other" : globalRole;
        }

        if (!string.IsNullOrWhiteSpace(sampleRole))
        {
            return sampleRole;
        }

        if (!string.IsNullOrWhiteSpace(globalRole))
        {
            return globalRole;
        }

        return "other";
    }

    private static List<string> BuildLabels(MemberSnapshot member, IReadOnlyList<SectionSample> stableSamples, IReadOnlyList<PartRoleAssignment> roles)
    {
        var labels = new List<string>();
        if (stableSamples.Count >= 2)
        {
            var orderedSamples = stableSamples
                .Where(sample => sample.SectionFeatures.OuterWidth > 0 || sample.SectionFeatures.OuterHeight > 0)
                .OrderBy(sample => sample.Ratio)
                .ToList();
            var widths = orderedSamples.Select(sample => sample.SectionFeatures.OuterWidth).Where(value => value > 0).ToList();
            var heights = orderedSamples.Select(sample => sample.SectionFeatures.OuterHeight).Where(value => value > 0).ToList();
            var widthVaries = widths.Count >= 2 && (widths.Max() - widths.Min()) > 40;
            var heightVaries = heights.Count >= 2 && (heights.Max() - heights.Min()) > 40;
            if (widthVaries)
            {
                labels.Add("section-width-varies");
            }

            if (heightVaries)
            {
                labels.Add("section-height-varies");
            }

            if (widthVaries || heightVaries)
            {
                var widthUniform = !widthVaries || IsUniformSectionChange(orderedSamples, sample => sample.SectionFeatures.OuterWidth);
                var heightUniform = !heightVaries || IsUniformSectionChange(orderedSamples, sample => sample.SectionFeatures.OuterHeight);
                var hasEndMiddleConstant =
                    IsEndMiddleConstantTaperPattern(orderedSamples, sample => sample.SectionFeatures.OuterWidth) ||
                    IsEndMiddleConstantTaperPattern(orderedSamples, sample => sample.SectionFeatures.OuterHeight);
                var hasSingleEndTaper =
                    IsSingleEndTaperPattern(orderedSamples, sample => sample.SectionFeatures.OuterWidth) ||
                    IsSingleEndTaperPattern(orderedSamples, sample => sample.SectionFeatures.OuterHeight);
                var hasNonParallelFlangeLikeVariation =
                    (widthVaries && !widthUniform && !hasEndMiddleConstant && !hasSingleEndTaper) ||
                    (heightVaries && !heightUniform && !hasEndMiddleConstant && !hasSingleEndTaper);

                if (hasEndMiddleConstant || hasSingleEndTaper || hasNonParallelFlangeLikeVariation)
                {
                    labels.Add("section-variation-irregular");
                    if (hasEndMiddleConstant)
                    {
                        labels.Add("h-irregular-end-taper-mid-constant");
                    }
                    if (hasSingleEndTaper)
                    {
                        labels.Add("h-irregular-single-end-taper");
                    }
                    if (hasNonParallelFlangeLikeVariation)
                    {
                        labels.Add("h-irregular-nonparallel-flange");
                    }
                }
                else if (widthUniform && heightUniform)
                {
                    labels.Add("section-tapered-uniform");
                }
                else
                {
                    labels.Add("section-variation-irregular");
                }
            }
        }

        var profile = NormalizeProfile(member.Member.ProfileString);
        if (LooksLikeHProfile(profile) || profile.StartsWith("BOX", StringComparison.OrdinalIgnoreCase) || profile.StartsWith("SHS", StringComparison.OrdinalIgnoreCase) || profile.StartsWith("RHS", StringComparison.OrdinalIgnoreCase) || LooksLikePipeProfile(profile) || LooksLikeTProfile(profile) || LooksLikeCrossProfile(profile) || HasCrossNameHint(member.Member.Name))
        {
            labels.Add("profile-direct-hint");
        }
        else if (profile.StartsWith("L", StringComparison.OrdinalIgnoreCase))
        {
            labels.Add("profile-angle");
        }

        if (roles.Any(role => role.Role == "folded_flange_candidate"))
        {
            labels.Add("folded-flange");
            labels.Add("h-irregular-folded-flange");
        }

        if (roles.Count(role => role.Role == "stiffener_candidate") >= 4)
        {
            labels.Add("many-local-stiffeners");
        }

        if (roles.Count(role => role.Role == "endplate_candidate") >= 2)
        {
            labels.Add("endplates-present");
        }

        return labels.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static SecondaryClass BuildSecondaryClass(MemberSnapshot member, IReadOnlyList<string> labels, MemberClass winnerClass)
    {
        if (winnerClass == MemberClass.H)
        {
            if (labels.Contains("folded-flange") || labels.Contains("section-variation-irregular"))
            {
                return SecondaryClass.HIrregular;
            }

            if (labels.Contains("section-tapered-uniform"))
            {
                return SecondaryClass.HTapered;
            }

            return SecondaryClass.HRegular;
        }

        if (winnerClass == MemberClass.Irregular || winnerClass == MemberClass.Cross)
        {
            return SecondaryClass.Irregular;
        }

        var profile = NormalizeProfile(member.Member.ProfileString);
        if (labels.Contains("profile-direct-hint") || profile.StartsWith("L", StringComparison.OrdinalIgnoreCase))
        {
            return SecondaryClass.Regular;
        }

        return SecondaryClass.Irregular;
    }

    private static SecondaryClass ClassifyTrustedDirectHSecondaryClass(MemberSnapshot member, List<string> labels)
    {
        var mainHPart = member.Parts
            .Where(part => LooksLikeHProfile(NormalizeProfile(part.ProfileString)))
            .OrderByDescending(part => part.AxisProjection.CoverageRatio)
            .ThenByDescending(part => part.AxisProjection.Length)
            .FirstOrDefault();

        if (mainHPart == null)
        {
            return SecondaryClass.HRegular;
        }

        if (!string.Equals(mainHPart.PartType, "PolyBeam", StringComparison.OrdinalIgnoreCase))
        {
            return SecondaryClass.HRegular;
        }

        var polyBeamClass = ClassifyPolyBeamH(mainHPart);
        if (polyBeamClass == SecondaryClass.HCurved)
        {
            labels.Add("h-curved-polybeam");
        }
        else if (polyBeamClass == SecondaryClass.HFolded)
        {
            labels.Add("h-folded-polybeam");
        }

        return polyBeamClass;
    }

    private static SecondaryClass ClassifyPolyBeamH(PartSnapshot part)
    {
        var directions = ExtractDominantPolyBeamDirections(part);
        if (directions.Count == 0)
        {
            return SecondaryClass.HIrregular;
        }

        if (directions.Count >= 5 && IsSmoothPolylineDirectionChange(directions))
        {
            return SecondaryClass.HCurved;
        }

        if (directions.Count >= 2)
        {
            return SecondaryClass.HFolded;
        }

        return SecondaryClass.HRegular;
    }

    private static List<double> ExtractDominantPolyBeamDirections(PartSnapshot part)
    {
        var planarEdges = (part.SolidEdges ?? new List<LineSegment3D>())
            .Select(edge => new
            {
                Edge = edge,
                Dx = edge.End.X - edge.Start.X,
                Dy = edge.End.Y - edge.Start.Y,
                Dz = Math.Abs(edge.End.Z - edge.Start.Z)
            })
            .Select(item => new
            {
                item.Edge,
                item.Dx,
                item.Dy,
                item.Dz,
                PlanLength = Math.Sqrt((item.Dx * item.Dx) + (item.Dy * item.Dy))
            })
            .Where(item => item.PlanLength > 80.0 && item.Dz <= 5.0)
            .ToList();

        if (planarEdges.Count == 0)
        {
            return new List<double>();
        }

        var maxPlanLength = planarEdges.Max(item => item.PlanLength);
        var candidateEdges = planarEdges
            .Where(item => item.PlanLength >= Math.Max(120.0, maxPlanLength * 0.15))
            .ToList();

        var directions = new List<double>();
        foreach (var edge in candidateEdges)
        {
            var angle = Math.Atan2(edge.Dy, edge.Dx) * 180.0 / Math.PI;
            if (angle < 0.0)
            {
                angle += 180.0;
            }
            else if (angle >= 180.0)
            {
                angle -= 180.0;
            }

            if (!directions.Any(existing => Math.Abs(existing - angle) <= 1.5))
            {
                directions.Add(angle);
            }
        }

        directions.Sort();
        return directions;
    }

    private static bool IsSmoothPolylineDirectionChange(IReadOnlyList<double> directions)
    {
        if (directions.Count < 3)
        {
            return false;
        }

        var deltas = new List<double>();
        for (var i = 1; i < directions.Count; i++)
        {
            deltas.Add(directions[i] - directions[i - 1]);
        }

        if (deltas.Count == 0)
        {
            return false;
        }

        var maxDelta = deltas.Max();
        var minDelta = deltas.Min();
        var avgDelta = deltas.Average();
        return maxDelta <= 4.5 && avgDelta <= 3.0 && (maxDelta - minDelta) <= 2.5;
    }

    private static SecondaryClass ClassifyBoxSecondaryClass(
        MemberSnapshot member,
        IReadOnlyDictionary<string, PartRoleAssignment> roleMap,
        List<string> labels,
        SecondaryClass fallback)
    {
        var polyBeamParts = member.Parts
            .Where(part => string.Equals(part.PartType, "PolyBeam", StringComparison.OrdinalIgnoreCase))
            .Where(part => part.AxisProjection.CoverageRatio >= 0.75)
            .Where(part => IsPrimaryBoxPolyBeamCandidate(part, roleMap))
            .OrderByDescending(part => part.AxisProjection.CoverageRatio)
            .ThenByDescending(part => part.AxisProjection.Length)
            .ToList();

        if (polyBeamParts.Count == 0)
        {
            return fallback;
        }

        var representative = polyBeamParts[0];
        var directions = ExtractDominantPolyBeamDirections(representative);
        if (directions.Count >= 5 && IsSmoothPolylineDirectionChange(directions))
        {
            labels.Add("box-curved-polybeam");
            return SecondaryClass.BoxCurved;
        }

        if (directions.Count >= 2)
        {
            labels.Add("box-folded-polybeam");
            return SecondaryClass.BoxFolded;
        }

        return fallback;
    }

    private static bool IsPrimaryBoxPolyBeamCandidate(
        PartSnapshot part,
        IReadOnlyDictionary<string, PartRoleAssignment> roleMap)
    {
        var profile = NormalizeProfile(part.ProfileString);
        if (LooksLikeBoxProfile(profile))
        {
            return true;
        }

        if (roleMap.TryGetValue(part.PartId, out var role))
        {
            if (role.Role == "flange_candidate" || role.Role == "web_candidate" || role.Role == "wall_candidate")
            {
                return true;
            }
        }

        return part.AxisProjection.CoverageRatio >= 0.90;
    }

    private static bool IsUniformSectionChange(
        IReadOnlyList<SectionSample> orderedSamples,
        Func<SectionSample, double> selector)
    {
        var values = orderedSamples
            .Select(selector)
            .Where(value => value > 0)
            .ToList();
        if (values.Count < 3)
        {
            return values.Count >= 2;
        }

        var diffs = new List<double>();
        for (var i = 1; i < values.Count; i++)
        {
            diffs.Add(values[i] - values[i - 1]);
        }

        var significantDiffs = diffs
            .Where(diff => Math.Abs(diff) > 8.0)
            .ToList();
        if (significantDiffs.Count == 0)
        {
            return true;
        }

        var allPositive = significantDiffs.All(diff => diff > 0);
        var allNegative = significantDiffs.All(diff => diff < 0);
        if (!allPositive && !allNegative)
        {
            return false;
        }

        var absDiffs = significantDiffs.Select(Math.Abs).ToList();
        var maxDiff = absDiffs.Max();
        var minDiff = absDiffs.Min();
        var averageDiff = absDiffs.Average();
        var tolerance = Math.Max(15.0, averageDiff * 0.45);
        return (maxDiff - minDiff) <= tolerance;
    }

    private static bool IsEndMiddleConstantTaperPattern(
        IReadOnlyList<SectionSample> orderedSamples,
        Func<SectionSample, double> selector)
    {
        var values = orderedSamples
            .Select(selector)
            .Where(value => value > 0)
            .ToList();
        if (values.Count < 5)
        {
            return false;
        }

        var first = values[0];
        var second = values[1];
        var middle = values[2];
        var fourth = values[3];
        var last = values[4];
        var endTolerance = Math.Max(20.0, middle * 0.06);
        var middleTolerance = Math.Max(15.0, middle * 0.04);
        var endMatch = Math.Abs(first - last) <= endTolerance;
        var middlePlateau = Math.Abs(second - middle) <= middleTolerance && Math.Abs(middle - fourth) <= middleTolerance;
        var endDifference = Math.Abs(middle - ((first + last) / 2.0)) > Math.Max(30.0, middle * 0.08);
        return endMatch && middlePlateau && endDifference;
    }

    private static bool IsSingleEndTaperPattern(
        IReadOnlyList<SectionSample> orderedSamples,
        Func<SectionSample, double> selector)
    {
        var values = orderedSamples
            .Select(selector)
            .Where(value => value > 0)
            .ToList();
        if (values.Count < 5)
        {
            return false;
        }

        var first = values[0];
        var second = values[1];
        var middle = values[2];
        var fourth = values[3];
        var last = values[4];
        var plateauTolerance = Math.Max(15.0, middle * 0.04);
        var endTolerance = Math.Max(20.0, middle * 0.06);
        var leftPlateau = Math.Abs(first - second) <= plateauTolerance && Math.Abs(second - middle) <= plateauTolerance && Math.Abs(middle - fourth) <= plateauTolerance && Math.Abs(fourth - last) > endTolerance;
        var rightPlateau = Math.Abs(first - second) > endTolerance && Math.Abs(second - middle) <= plateauTolerance && Math.Abs(middle - fourth) <= plateauTolerance && Math.Abs(fourth - last) <= plateauTolerance;
        return leftPlateau || rightPlateau;
    }

    private static string BuildReviewReason(ScoreBreakdown winner, ScoreBreakdown runnerUp, double confidence)
    {
        if (winner.ClassName == MemberClass.Irregular)
        {
            return "first-pass rules could not map the member to a stable standard section";
        }

        if (winner.ClassName == MemberClass.Angle)
        {
            return string.Empty;
        }

        if (winner.ClassName == MemberClass.Pipe)
        {
            return string.Empty;
        }

        if (confidence < 75)
        {
            return $"top two class scores are close: {winner.ClassName}={winner.Score:0.0}, {runnerUp.ClassName}={runnerUp.Score:0.0}";
        }

        return string.Empty;
    }

    private static string BuildKeyDimensions(MemberSnapshot member, IReadOnlyList<SectionSample> stableSamples, MemberClass winnerClass, SecondaryClass secondaryClass, IReadOnlyDictionary<string, PartRoleAssignment> roleMap)
    {
        var profile = NormalizeProfile(member.Member.ProfileString);
        if (winnerClass == MemberClass.H)
        {
            if (secondaryClass != SecondaryClass.HRegular)
            {
                return "异形";
            }

            if (LooksLikeHProfile(profile))
            {
                return member.Member.ProfileString;
            }
        }


        if (winnerClass == MemberClass.Box && (profile.StartsWith("BOX", StringComparison.OrdinalIgnoreCase) || profile.StartsWith("SHS", StringComparison.OrdinalIgnoreCase) || profile.StartsWith("RHS", StringComparison.OrdinalIgnoreCase)))
        {
            return member.Member.ProfileString;
        }

        if (winnerClass == MemberClass.T && LooksLikeTProfile(profile))
        {
            return member.Member.ProfileString;
        }

        if (winnerClass == MemberClass.Angle && profile.StartsWith("L", StringComparison.OrdinalIgnoreCase))
        {
            return member.Member.ProfileString;
        }

        if (winnerClass == MemberClass.Pipe && LooksLikePipeProfile(profile))
        {
            return member.Member.ProfileString;
        }

        var representative = stableSamples
            .Where(sample => sample.SectionFeatures.OuterHeight > 0 && sample.SectionFeatures.OuterWidth > 0)
            .OrderBy(sample => Math.Abs(sample.Ratio - 0.5))
            .FirstOrDefault();

        if (representative == null)
        {
            return string.Empty;
        }

        var height = representative.SectionFeatures.OuterHeight;
        var width = representative.SectionFeatures.OuterWidth;
        var flangeThickness = representative.SectionParts
            .Where(hit => hit.RoleHint == "flange_candidate" || hit.RoleHint == "folded_flange_candidate")
            .Select(hit => hit.Thickness)
            .Where(value => value > 0)
            .DefaultIfEmpty(0)
            .Max();
        var webThickness = representative.SectionParts
            .Where(hit => hit.RoleHint == "web_candidate")
            .Select(hit => hit.Thickness)
            .Where(value => value > 0)
            .DefaultIfEmpty(0)
            .Max();
        var wallThickness = representative.SectionParts
            .Select(hit => hit.Thickness)
            .Where(value => value > 0)
            .DefaultIfEmpty(0)
            .Min();

        if (winnerClass == MemberClass.H)
        {
            var builtUpHDimensions = TryBuildBuiltUpHKeyDimensions(member, stableSamples, representative, roleMap);
            if (!string.IsNullOrEmpty(builtUpHDimensions))
            {
                return builtUpHDimensions;
            }

            return $"H{height:0}x{width:0}x{webThickness:0}x{flangeThickness:0}";
        }

        if (winnerClass == MemberClass.T)
        {
            return $"T{height:0}x{width:0}x{webThickness:0}x{flangeThickness:0}";
        }

        if (winnerClass == MemberClass.Cross)
        {
            return $"CROSS {height:0}x{width:0}";
        }

        if (winnerClass == MemberClass.Angle)
        {
            return profile.StartsWith("L", StringComparison.OrdinalIgnoreCase) ? member.Member.ProfileString : $"ANGLE {height:0}x{width:0}";
        }

        if (winnerClass == MemberClass.Pipe)
        {
            return LooksLikePipeProfile(profile) ? member.Member.ProfileString : $"PIPE {height:0}x{width:0}";
        }

        if (winnerClass == MemberClass.Box)
        {
            var builtBoxDimensions = TryBuildBoxKeyDimensions(member, stableSamples, representative, roleMap);
            if (!string.IsNullOrEmpty(builtBoxDimensions))
            {
                return builtBoxDimensions;
            }

            var horizontalThicknessText = FormatThicknessPair(representative.SectionParts
                .Where(hit => Math.Abs(hit.Direction2D.X) >= Math.Abs(hit.Direction2D.Y))
                .Select(hit => hit.Thickness));
            var verticalThicknessText = FormatThicknessPair(representative.SectionParts
                .Where(hit => Math.Abs(hit.Direction2D.Y) > Math.Abs(hit.Direction2D.X))
                .Select(hit => hit.Thickness));

            if (string.IsNullOrEmpty(verticalThicknessText))
            {
                verticalThicknessText = wallThickness > 0 ? wallThickness.ToString("0") : "0";
            }

            if (string.IsNullOrEmpty(horizontalThicknessText))
            {
                horizontalThicknessText = flangeThickness > 0 ? flangeThickness.ToString("0") : verticalThicknessText;
            }

            return $"BOX {height:0}x{width:0}x{verticalThicknessText}x{horizontalThicknessText}";
        }

        if (profile.StartsWith("L", StringComparison.OrdinalIgnoreCase))
        {
            return member.Member.ProfileString;
        }

        return $"{height:0}x{width:0}";
    }



    private static string TryBuildBoxKeyDimensions(
        MemberSnapshot member,
        IReadOnlyList<SectionSample> stableSamples,
        SectionSample representative,
        IReadOnlyDictionary<string, PartRoleAssignment> roleMap)
    {
        var longParts = member.Parts
            .Where(part => part.AxisProjection.CoverageRatio >= 0.60)
            .ToList();

        var roleFlangeParts = longParts
            .Where(part => roleMap.TryGetValue(part.PartId, out var role) && (role.Role == "flange_candidate" || role.Role == "folded_flange_candidate"))
            .ToList();

        var roleWebParts = longParts
            .Where(part => roleMap.TryGetValue(part.PartId, out var role) && (role.Role == "web_candidate" || role.Role == "wall_candidate"))
            .ToList();

        var directFromRoles = TryFormatBoxDimensionsFromParts(roleFlangeParts, roleWebParts, representative);
        if (!string.IsNullOrEmpty(directFromRoles))
        {
            return directFromRoles;
        }

        var sectionFlangeIds = stableSamples
            .SelectMany(sample => sample.SectionParts)
            .Where(hit => IsBoxPartGeometryCandidate(hit, roleMap, horizontal: true))
            .Select(hit => hit.PartId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sectionWebIds = stableSamples
            .SelectMany(sample => sample.SectionParts)
            .Where(hit => IsBoxPartGeometryCandidate(hit, roleMap, horizontal: false))
            .Select(hit => hit.PartId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sectionFlangeParts = longParts
            .Where(part => sectionFlangeIds.Contains(part.PartId))
            .ToList();

        var sectionWebParts = longParts
            .Where(part => sectionWebIds.Contains(part.PartId))
            .ToList();

        var directFromSections = TryFormatBoxDimensionsFromParts(sectionFlangeParts, sectionWebParts, representative);
        if (!string.IsNullOrEmpty(directFromSections))
        {
            return directFromSections;
        }

        var measurements = stableSamples
            .Select(sample => TryMeasureBoxSample(sample, roleMap))
            .Where(measurement => measurement != null)
            .Cast<BoxSampleMeasurement>()
            .ToList();

        if (measurements.Count == 0)
        {
            return string.Empty;
        }

        var width = GetRobustBoxDimension(measurements.Select(item => item.WidthEstimate), representative.SectionFeatures.OuterWidth);
        var height = GetRobustBoxDimension(measurements.Select(item => item.HeightEstimate), representative.SectionFeatures.OuterHeight);
        if (width <= 0 || height <= 0)
        {
            return string.Empty;
        }

        var horizontalThicknessText = FormatThicknessPair(measurements.SelectMany(item => item.HorizontalThicknesses));
        var verticalThicknessText = FormatThicknessPair(measurements.SelectMany(item => item.VerticalThicknesses));
        if (string.IsNullOrEmpty(verticalThicknessText) || string.IsNullOrEmpty(horizontalThicknessText))
        {
            return string.Empty;
        }

        return $"BOX {height:0}x{width:0}x{verticalThicknessText}x{horizontalThicknessText}";
    }

    private static string TryFormatBoxDimensionsFromParts(
        IReadOnlyList<PartSnapshot> flangeParts,
        IReadOnlyList<PartSnapshot> webParts,
        SectionSample representative)
    {
        if (flangeParts.Count < 2 || webParts.Count < 2)
        {
            return string.Empty;
        }

        var flangeWidths = flangeParts
            .Select(part => ParsePlateProfile(part.ProfileString))
            .Where(parsed => parsed.HasValue && parsed.Value.Width > 0)
            .Select(parsed => parsed.Value.Width)
            .ToList();

        var webWidths = webParts
            .Select(part => ParsePlateProfile(part.ProfileString))
            .Where(parsed => parsed.HasValue && parsed.Value.Width > 0)
            .Select(parsed => parsed.Value.Width)
            .ToList();

        var horizontalThicknessText = FormatThicknessPair(flangeParts.Select(GetPlateThickness));
        var verticalThicknessText = FormatThicknessPair(webParts.Select(GetPlateThickness));
        var representativeHorizontalThickness = GetRepresentativeThickness(flangeParts.Select(GetPlateThickness).Where(value => value > 0).ToList());
        if (flangeWidths.Count == 0 ||
            webWidths.Count == 0 ||
            string.IsNullOrEmpty(horizontalThicknessText) ||
            string.IsNullOrEmpty(verticalThicknessText) ||
            representativeHorizontalThickness <= 0)
        {
            return string.Empty;
        }

        var widthFromParts = GetClosestBoxPartDimension(flangeWidths, representative.SectionFeatures.OuterWidth);
        var clearHeightTarget = representative.SectionFeatures.OuterHeight > 0
            ? Math.Max(0.0, representative.SectionFeatures.OuterHeight - (representativeHorizontalThickness * 2.0))
            : 0.0;
        var webHeightFromParts = GetClosestBoxPartDimension(webWidths, clearHeightTarget > 0 ? clearHeightTarget : representative.SectionFeatures.OuterHeight);
        var heightFromParts = webHeightFromParts + (representativeHorizontalThickness * 2.0);
        var widthTolerance = Math.Max(120.0, representative.SectionFeatures.OuterWidth * 0.20);
        var heightTolerance = Math.Max(160.0, representative.SectionFeatures.OuterHeight * 0.20);
        var matchesRepresentativeOrientation = representative.SectionFeatures.OuterWidth <= 0 || representative.SectionFeatures.OuterHeight <= 0 ||
            (Math.Abs(widthFromParts - representative.SectionFeatures.OuterWidth) <= widthTolerance &&
             Math.Abs(heightFromParts - representative.SectionFeatures.OuterHeight) <= heightTolerance);
        if (widthFromParts <= 0 || heightFromParts <= 0 || !matchesRepresentativeOrientation)
        {
            return string.Empty;
        }

        return $"BOX {heightFromParts:0}x{widthFromParts:0}x{verticalThicknessText}x{horizontalThicknessText}";
    }

    private static double GetClosestBoxPartDimension(IEnumerable<double> values, double target)
    {
        var candidates = values
            .Where(value => value > 0)
            .Select(value => Math.Round(value / 10.0) * 10.0)
            .Distinct()
            .ToList();
        if (candidates.Count == 0)
        {
            return 0;
        }

        if (target <= 0)
        {
            return candidates.Max();
        }

        return candidates
            .OrderBy(value => Math.Abs(value - target))
            .ThenByDescending(value => value)
            .First();
    }

    private static BoxSampleMeasurement? TryMeasureBoxSample(
        SectionSample sample,
        IReadOnlyDictionary<string, PartRoleAssignment> roleMap)
    {
        var horizontalHits = sample.SectionParts
            .Where(hit => IsPrimaryBoxWallHit(hit, roleMap, horizontal: true))
            .OrderBy(hit => hit.Center2D.Y)
            .ToList();
        var verticalHits = sample.SectionParts
            .Where(hit => IsPrimaryBoxWallHit(hit, roleMap, horizontal: false))
            .OrderBy(hit => hit.Center2D.X)
            .ToList();

        if (horizontalHits.Count < 2 || verticalHits.Count < 2)
        {
            return null;
        }

        var outerHorizontalHits = PickOuterHits(horizontalHits, hit => hit.Center2D.Y);
        var outerVerticalHits = PickOuterHits(verticalHits, hit => hit.Center2D.X);
        if (outerHorizontalHits.Count < 2 || outerVerticalHits.Count < 2)
        {
            return null;
        }

        var horizontalSpan = outerHorizontalHits.Max(GetSpanX);
        var verticalSpan = outerVerticalHits.Max(GetSpanY);
        var horizontalThicknesses = outerHorizontalHits.Select(hit => hit.Thickness).Where(value => value > 0).ToList();
        var verticalThicknesses = outerVerticalHits.Select(hit => hit.Thickness).Where(value => value > 0).ToList();
        if (horizontalSpan <= 0 || verticalSpan <= 0 || horizontalThicknesses.Count == 0 || verticalThicknesses.Count == 0)
        {
            return null;
        }

        var representativeHorizontalThickness = GetRepresentativeThickness(horizontalThicknesses);
        var representativeVerticalThickness = GetRepresentativeThickness(verticalThicknesses);
        if (representativeHorizontalThickness <= 0 || representativeVerticalThickness <= 0)
        {
            return null;
        }

        var widthEstimate = ChooseCloser(
            sample.SectionFeatures.OuterWidth,
            horizontalSpan,
            horizontalSpan + (representativeVerticalThickness * 2.0));
        var heightEstimate = ChooseCloser(
            sample.SectionFeatures.OuterHeight,
            verticalSpan,
            verticalSpan + (representativeHorizontalThickness * 2.0));
        if (widthEstimate <= 0 || heightEstimate <= 0)
        {
            return null;
        }

        return new BoxSampleMeasurement(widthEstimate, heightEstimate, horizontalThicknesses, verticalThicknesses);
    }

    private static double GetRepresentativeThickness(IReadOnlyList<double> values)
    {
        return values
            .Where(value => value > 0)
            .GroupBy(value => Math.Round(value, 1))
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .Select(group => group.Key)
            .FirstOrDefault();
    }

    private static double ChooseCloser(double target, double a, double b)
    {
        if (target <= 0)
        {
            return Math.Max(a, b);
        }

        return Math.Abs(a - target) <= Math.Abs(b - target) ? a : b;
    }

    private static double GetRobustBoxDimension(IEnumerable<double> values, double fallback)
    {
        var candidates = values
            .Where(value => value > 0)
            .Select(value => Math.Round(value / 10.0) * 10.0)
            .ToList();
        if (candidates.Count == 0)
        {
            return fallback;
        }

        var best = candidates
            .GroupBy(value => value)
            .Select(group => new
            {
                Value = group.Key,
                Count = group.Count(),
                DistanceToFallback = Math.Abs(group.Key - fallback)
            })
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.DistanceToFallback)
            .First();

        return best.Value;
    }

    private static bool IsBoxPartGeometryCandidate(
        SectionPartHit hit,
        IReadOnlyDictionary<string, PartRoleAssignment> roleMap,
        bool horizontal)
    {
        var sampleRole = hit.RoleHint ?? string.Empty;
        var globalRole = roleMap.TryGetValue(hit.PartId, out var assignment) ? assignment.Role : string.Empty;
        if (IsAttachmentRole(sampleRole) || IsAttachmentRole(globalRole))
        {
            return false;
        }

        var spanX = GetSpanX(hit);
        var spanY = GetSpanY(hit);
        var thickness = hit.Thickness;
        if (thickness <= 0)
        {
            return false;
        }

        var maxWallThickness = Math.Max(80.0, Math.Min(spanX, spanY) * 0.8);
        if (thickness > maxWallThickness)
        {
            return false;
        }

        if (horizontal)
        {
            return spanX >= 300.0 && spanX >= spanY * 4.0 && spanY <= maxWallThickness;
        }

        return spanY >= 300.0 && spanY >= spanX * 4.0 && spanX <= maxWallThickness;
    }

    private static bool IsPrimaryBoxWallHit(
        SectionPartHit hit,
        IReadOnlyDictionary<string, PartRoleAssignment> roleMap,
        bool horizontal)
    {
        var sampleRole = hit.RoleHint ?? string.Empty;
        var globalRole = roleMap.TryGetValue(hit.PartId, out var assignment) ? assignment.Role : string.Empty;
        var role = sampleRole == "flange_candidate" || sampleRole == "folded_flange_candidate" || sampleRole == "web_candidate" || sampleRole == "wall_candidate"
            ? sampleRole
            : ResolveEffectiveRole(hit, roleMap);
        if ((role == "other" || role == "direct_profile_body") && IsAttachmentRole(globalRole))
        {
            return false;
        }
        if (role == "other" || role == "direct_profile_body")
        {
            return false;
        }

        var spanX = GetSpanX(hit);
        var spanY = GetSpanY(hit);
        var thickness = hit.Thickness;
        if (thickness <= 0)
        {
            return false;
        }

        var maxWallThickness = Math.Max(80.0, Math.Min(spanX, spanY) * 0.8);
        if (thickness > maxWallThickness)
        {
            return false;
        }

        if (horizontal)
        {
            if (role != "flange_candidate" && role != "folded_flange_candidate" && role != "wall_candidate")
            {
                return false;
            }

            return spanX >= 300.0 && spanX >= spanY * 4.0 && spanY <= maxWallThickness;
        }

        if (role != "web_candidate" && role != "wall_candidate")
        {
            return false;
        }

        return spanY >= 300.0 && spanY >= spanX * 4.0 && spanX <= maxWallThickness;
    }

    private static List<SectionPartHit> PickOuterHits(
        IReadOnlyList<SectionPartHit> hits,
        Func<SectionPartHit, double> selector)
    {
        if (hits.Count <= 2)
        {
            return hits.ToList();
        }

        var ordered = hits.OrderBy(selector).ToList();
        return new List<SectionPartHit>
        {
            ordered.First(),
            ordered.Last()
        };
    }

    private static double GetSpanX(SectionPartHit hit)
    {
        if (hit.CutSpanX > 0)
        {
            return hit.CutSpanX;
        }

        return Math.Abs(hit.Direction2D.X) >= Math.Abs(hit.Direction2D.Y)
            ? hit.TotalCutLength
            : hit.Thickness;
    }

    private static double GetSpanY(SectionPartHit hit)
    {
        if (hit.CutSpanY > 0)
        {
            return hit.CutSpanY;
        }

        return Math.Abs(hit.Direction2D.Y) > Math.Abs(hit.Direction2D.X)
            ? hit.TotalCutLength
            : hit.Thickness;
    }
    private static string TryBuildBuiltUpHKeyDimensions(
        MemberSnapshot member,
        IReadOnlyList<SectionSample> stableSamples,
        SectionSample representative,
        IReadOnlyDictionary<string, PartRoleAssignment> roleMap)
    {
        var longParts = member.Parts
            .Where(part => part.AxisProjection.CoverageRatio >= 0.60)
            .ToList();

        var flangeParts = longParts
            .Where(part =>
            {
                if (!roleMap.TryGetValue(part.PartId, out var role))
                {
                    return false;
                }

                return role.Role == "flange_candidate" || role.Role == "folded_flange_candidate";
            })
            .ToList();

        var webParts = longParts
            .Where(part =>
            {
                if (!roleMap.TryGetValue(part.PartId, out var role))
                {
                    return false;
                }

                return role.Role == "web_candidate" || role.Role == "wall_candidate";
            })
            .ToList();

        var flangeWidths = flangeParts
            .Select(part => ParsePlateProfile(part.ProfileString))
            .Where(parsed => parsed.HasValue && parsed.Value.Width > 0)
            .Select(parsed => parsed.Value.Width)
            .ToList();

        var flangeThicknesses = flangeParts
            .Select(GetPlateThickness)
            .Where(value => value > 0)
            .ToList();

        var webThicknesses = webParts
            .Select(GetPlateThickness)
            .Where(value => value > 0)
            .ToList();

        var inferredHeight = representative.SectionFeatures.OuterHeight > 0
            ? representative.SectionFeatures.OuterHeight
            : stableSamples
                .Select(sample => sample.SectionFeatures.OuterHeight)
                .Where(value => value > 0)
                .DefaultIfEmpty(0)
                .Max();

        if (inferredHeight <= 0 || flangeWidths.Count == 0 || flangeThicknesses.Count == 0 || webThicknesses.Count == 0)
        {
            return string.Empty;
        }

        var widthText = FormatDimensionPair(flangeWidths);
        var flangeThicknessText = FormatDimensionPair(flangeThicknesses);
        var webThicknessText = FormatDimensionPair(webThicknesses);
        if (string.IsNullOrEmpty(widthText) || string.IsNullOrEmpty(flangeThicknessText) || string.IsNullOrEmpty(webThicknessText))
        {
            return string.Empty;
        }

        return $"H{inferredHeight:0}x{widthText}x{webThicknessText}x{flangeThicknessText}";
    }
    private static string FormatThicknessPair(IEnumerable<double> thicknesses)
    {
        var roundedValues = thicknesses
            .Where(value => value > 0)
            .Select(value => Math.Round(value, 3))
            .ToList();
        if (roundedValues.Count == 0)
        {
            return string.Empty;
        }

        var values = roundedValues
            .Distinct()
            .OrderBy(value => value)
            .ToList();
        if (values.Count == 1)
        {
            return values[0].ToString("0");
        }

        var minValue = values[0];
        var maxValue = values[values.Count - 1];
        if (values.Count == 2 && minValue <= 10 && maxValue >= 12)
        {
            var minCount = roundedValues.Count(value => Math.Abs(value - minValue) <= 0.5);
            var maxCount = roundedValues.Count(value => Math.Abs(value - maxValue) <= 0.5);
            if (maxCount >= minCount && minValue / maxValue <= 0.6)
            {
                return maxValue.ToString("0");
            }
        }

        return $"{minValue:0}/{maxValue:0}";
    }


    private static string FormatDimensionPair(IEnumerable<double> dimensions)
    {
        var values = dimensions
            .Where(value => value > 0)
            .Select(value => Math.Round(value, 3))
            .Distinct()
            .OrderBy(value => value)
            .ToList();

        if (values.Count == 0)
        {
            return string.Empty;
        }

        if (values.Count == 1)
        {
            return values[0].ToString("0");
        }

        return $"{values[0]:0}/{values[values.Count - 1]:0}";
    }

    private static double GetPlateThickness(PartSnapshot part)
    {
        if (part.Thickness > 0)
        {
            return part.Thickness;
        }

        var parsed = ParsePlateProfile(part.ProfileString);
        return parsed?.Thickness ?? 0;
    }

    private static PlateProfileDimensions? ParsePlateProfile(string profile)
    {
        var normalized = NormalizeProfile(profile);
        var match = Regex.Match(normalized, @"^PL(?<thickness>\d+(\.\d+)?)(?:[X\*](?<width>\d+(\.\d+)?))?$");
        if (!match.Success)
        {
            return null;
        }

        var thickness = ParseInvariantDouble(match.Groups["thickness"].Value);
        var width = ParseInvariantDouble(match.Groups["width"].Value);
        return new PlateProfileDimensions(thickness, width);
    }

    private static double ParseInvariantDouble(string text)
    {
        if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return 0;
    }

    private readonly struct PlateProfileDimensions
    {
        public PlateProfileDimensions(double thickness, double width)
        {
            Thickness = thickness;
            Width = width;
        }

        public double Thickness { get; }
        public double Width { get; }
    }
    private static string NormalizeProfile(string profile)
    {
        return (profile ?? string.Empty).Trim().Replace(" ", string.Empty).ToUpperInvariant();
    }

    private static bool LooksLikeHProfile(string profile)
    {
        return Regex.IsMatch(profile, @"^(H|HN|HM|HW|I)\d+([X\*]\d+){2,}$") ||
               profile.StartsWith("BH", StringComparison.OrdinalIgnoreCase) ||
               profile.StartsWith("HI", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeBoxProfile(string profile)
    {
        return profile.StartsWith("BOX", StringComparison.OrdinalIgnoreCase) ||
               profile.StartsWith("SHS", StringComparison.OrdinalIgnoreCase) ||
               profile.StartsWith("RHS", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeTProfile(string profile)
    {
        return Regex.IsMatch(profile, @"^T\d+([X\*]\d+){1,}$");
    }

    private static bool LooksLikeCrossProfile(string profile)
    {
        return profile.StartsWith("[]", StringComparison.OrdinalIgnoreCase) ||
               profile.StartsWith("CROSS", StringComparison.OrdinalIgnoreCase) ||
               profile.StartsWith("CR", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasCrossNameHint(string name)
    {
        return !string.IsNullOrWhiteSpace(name) && name.IndexOf("十字", StringComparison.Ordinal) >= 0;
    }


    private static bool IsTrustedDirectClassification(string profile, MemberClass winnerClass)
    {
        return winnerClass switch
        {
            MemberClass.H => LooksLikeHProfile(profile),
            MemberClass.Box => LooksLikeBoxProfile(profile),
            MemberClass.T => LooksLikeTProfile(profile),
            MemberClass.Angle => profile.StartsWith("L", StringComparison.OrdinalIgnoreCase),
            MemberClass.Pipe => LooksLikePipeProfile(profile),
            MemberClass.Cross => LooksLikeCrossProfile(profile),
            _ => false
        };
    }
    private static bool LooksLikePipeProfile(string profile)
    {
        return profile.StartsWith("CHS", StringComparison.OrdinalIgnoreCase) ||
               profile.StartsWith("PIPE", StringComparison.OrdinalIgnoreCase) ||
               profile.StartsWith("TUBE", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeBoxSlice(SectionSample sample)
    {
        return sample.SectionFeatures.MajorPlateCount <= 2 &&
               sample.SectionFeatures.CentralHorizontalPlateCount <= 1 &&
               sample.SectionFeatures.CentralVerticalPlateCount == 0 &&
               sample.SectionFeatures.OuterHeight > 0 &&
               sample.SectionFeatures.OuterWidth > 0 &&
               Math.Abs(sample.SectionFeatures.OuterHeight - sample.SectionFeatures.OuterWidth) <= Math.Max(30.0, Math.Max(sample.SectionFeatures.OuterHeight, sample.SectionFeatures.OuterWidth) * 0.12);
    }

    private static void ApplyBoxRecoveryFromAbnormalSamples(
        IReadOnlyList<SectionSample> stableSamples,
        IReadOnlyList<SectionSample> abnormalSamples,
        IDictionary<MemberClass, ScoreBreakdown> scoreMap)
    {
        if (stableSamples.Count > 1 || abnormalSamples.Count == 0)
        {
            return;
        }

        var recoveryCount = abnormalSamples.Count(IsAbnormalSampleStrongBoxCandidate);
        if (recoveryCount < 2)
        {
            return;
        }

        scoreMap[MemberClass.Box].Score += 60;
        scoreMap[MemberClass.Box].Reasons.Add($"{recoveryCount} abnormal samples still show repeatable box evidence");
        scoreMap[MemberClass.Irregular].Score -= 20;
        scoreMap[MemberClass.Irregular].Reasons.Add("repeatable abnormal box evidence weakens irregular fallback");
    }

    private static bool IsAbnormalSampleStrongBoxCandidate(SectionSample sample)
    {
        var width = sample.SectionFeatures.OuterWidth;
        var height = sample.SectionFeatures.OuterHeight;
        return sample.SectionFeatures.EnclosureScore >= 0.35 &&
               sample.SectionFeatures.CentralHorizontalPlateCount >= 4 &&
               sample.SectionFeatures.CentralVerticalPlateCount >= 2 &&
               sample.SectionFeatures.MajorPlateCount >= 8 &&
               width > 0 &&
               height > 0 &&
               Math.Abs(width - height) <= Math.Max(180.0, Math.Max(width, height) * 0.30);
    }
    private static double ComputeConfidence(MemberClass winnerClass, double winnerScore, double runnerUpScore)
    {
        if (winnerScore <= 0)
        {
            return 0;
        }

        if (winnerClass == MemberClass.Irregular)
        {
            var irregularRaw = 35 + (winnerScore - runnerUpScore) * 0.25;
            return Math.Max(15, Math.Min(60, irregularRaw));
        }

        if (winnerClass == MemberClass.Angle)
        {
            var angleRaw = 85 + (winnerScore - runnerUpScore) * 0.10;
            return Math.Max(85, Math.Min(99, angleRaw));
        }

        if (winnerClass == MemberClass.Pipe)
        {
            var pipeRaw = 85 + (winnerScore - runnerUpScore) * 0.10;
            return Math.Max(85, Math.Min(99, pipeRaw));
        }

        var raw = 50 + (winnerScore - runnerUpScore);
        return Math.Max(5, Math.Min(99, raw));
    }

    private static ScoreBreakdown NewScore(MemberClass className)
    {
        return new ScoreBreakdown
        {
            ClassName = className,
            Score = 0,
            Reasons = new List<string>()
        };
    }

    private sealed class BandInfo
    {
        public BandInfo(double position, double totalLength, int hitCount)
        {
            Position = position;
            TotalLength = totalLength;
            HitCount = hitCount;
        }

        public double Position { get; }
        public double TotalLength { get; }
        public int HitCount { get; }
    }

    private sealed class SampleMetrics
    {
        public SampleMetrics(
            int horizontalBands,
            int verticalBands,
            int outerHorizontalBands,
            int topOuterHorizontalBands,
            int bottomOuterHorizontalBands,
            int centralHorizontalBands,
            int outerVerticalBands,
            int leftOuterVerticalBands,
            int rightOuterVerticalBands,
            int centralVerticalBands,
            double outerHorizontalBandSpan,
            double outerVerticalBandSpan,
            int mainBandCount,
            int attachmentCount,
            double horizontalBandSpan,
            bool hasVerticalHitConnectingOuterHorizontals,
            bool verticalBandBetweenHorizontalBands)
        {
            HorizontalBands = horizontalBands;
            VerticalBands = verticalBands;
            OuterHorizontalBands = outerHorizontalBands;
            TopOuterHorizontalBands = topOuterHorizontalBands;
            BottomOuterHorizontalBands = bottomOuterHorizontalBands;
            CentralHorizontalBands = centralHorizontalBands;
            OuterVerticalBands = outerVerticalBands;
            LeftOuterVerticalBands = leftOuterVerticalBands;
            RightOuterVerticalBands = rightOuterVerticalBands;
            CentralVerticalBands = centralVerticalBands;
            OuterHorizontalBandSpan = outerHorizontalBandSpan;
            OuterVerticalBandSpan = outerVerticalBandSpan;
            MainBandCount = mainBandCount;
            AttachmentCount = attachmentCount;
            HorizontalBandSpan = horizontalBandSpan;
            HasVerticalHitConnectingOuterHorizontals = hasVerticalHitConnectingOuterHorizontals;
            VerticalBandBetweenHorizontalBands = verticalBandBetweenHorizontalBands;
        }

        public int HorizontalBands { get; }
        public int VerticalBands { get; }
        public int OuterHorizontalBands { get; }
        public int TopOuterHorizontalBands { get; }
        public int BottomOuterHorizontalBands { get; }
        public int CentralHorizontalBands { get; }
        public int OuterVerticalBands { get; }
        public int LeftOuterVerticalBands { get; }
        public int RightOuterVerticalBands { get; }
        public int CentralVerticalBands { get; }
        public double OuterHorizontalBandSpan { get; }
        public double OuterVerticalBandSpan { get; }
        public int MainBandCount { get; }
        public int AttachmentCount { get; }
        public double HorizontalBandSpan { get; }
        public bool VerticalBandBetweenHorizontalBands { get; }
        public bool HasVerticalHitConnectingOuterHorizontals { get; }
    }
}



















