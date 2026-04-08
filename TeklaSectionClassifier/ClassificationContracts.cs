using System.Collections.Generic;

namespace TeklaSectionClassifier;

public interface IPartRoleScorer
{
    IReadOnlyList<PartRoleAssignment> ScoreRoles(MemberSnapshot member);
}

public interface IMemberClassifier
{
    ClassificationResult Classify(MemberSnapshot member);
}
