using System.Collections.Generic;

namespace TeklaSectionClassifier;

public interface IMemberExtractor
{
    IEnumerable<MemberSnapshot> ExtractMembers();
}

public interface ISectionSampler
{
    IReadOnlyList<SectionSample> BuildSamples(MemberSnapshot member);
}

public interface ISectionIntersectionService
{
    SectionSample PopulateSample(MemberSnapshot member, SectionSample sample);
}

public interface ICacheWriter
{
    void WriteMemberJson(MemberSnapshot member, string outputDirectory);
    void WriteBatchSummary(ExtractionBatch batch, string outputDirectory);
}

public interface ISqliteCacheWriter
{
    void InitializeDatabase(string databasePath);
    void UpsertMember(MemberSnapshot member, string databasePath);
}

public interface IAnomalyDetector
{
    bool IsAbnormalStation(MemberSnapshot member, double station, out string reason);
}
