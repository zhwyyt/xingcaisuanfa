using System;
using System.IO;
using Tekla.Structures.Model;

namespace TeklaSectionClassifier;

public sealed class ExtractionPipeline
{
    private readonly IMemberExtractor _memberExtractor;
    private readonly ICacheWriter _jsonCacheWriter;
    private readonly ISqliteCacheWriter _sqliteCacheWriter;
    private readonly IMemberClassifier? _memberClassifier;

    public ExtractionPipeline(
        IMemberExtractor memberExtractor,
        ICacheWriter jsonCacheWriter,
        ISqliteCacheWriter sqliteCacheWriter,
        IMemberClassifier? memberClassifier = null)
    {
        _memberExtractor = memberExtractor;
        _jsonCacheWriter = jsonCacheWriter;
        _sqliteCacheWriter = sqliteCacheWriter;
        _memberClassifier = memberClassifier;
    }

    public ExtractionBatch Run(string jsonOutputDirectory, string sqlitePath, Action<int, MemberSnapshot>? progress = null)
    {
        Directory.CreateDirectory(jsonOutputDirectory);
        if (!string.IsNullOrWhiteSpace(sqlitePath))
        {
            _sqliteCacheWriter.InitializeDatabase(sqlitePath);
        }

        var batch = new ExtractionBatch();
        var count = 0;
        foreach (var member in _memberExtractor.ExtractMembers())
        {
            if (_memberClassifier != null)
            {
                member.Classification = _memberClassifier.Classify(member);
            }

            batch.Members.Add(member);
            _jsonCacheWriter.WriteMemberJson(member, jsonOutputDirectory);
            if (!string.IsNullOrWhiteSpace(sqlitePath))
            {
                _sqliteCacheWriter.UpsertMember(member, sqlitePath);
            }

            count++;
            progress?.Invoke(count, member);
        }

        _jsonCacheWriter.WriteBatchSummary(batch, jsonOutputDirectory);
        return batch;
    }

    public static ExtractionPipeline CreateDefault(Model model, Func<string, System.Data.IDbConnection> sqliteConnectionFactory, ExtractionOptions extractionOptions, SamplingOptions samplingOptions)
    {
        var anomalyDetector = new DefaultAnomalyDetector(samplingOptions);
        var sectionIntersectionService = new ApproximateSectionIntersectionService();
        var sampler = new DefaultSectionSampler(samplingOptions, anomalyDetector, sectionIntersectionService);
        var extractor = new Tekla2017MemberExtractor(model, extractionOptions, sampler);
        var jsonWriter = new JsonCacheWriter();
        var sqliteWriter = new SqliteCacheWriter(sqliteConnectionFactory);
        var classifier = new FirstPassMemberClassifier(new FirstPassPartRoleScorer());
        return new ExtractionPipeline(extractor, jsonWriter, sqliteWriter, classifier);
    }

    public static ExtractionPipeline CreateJsonOnly(Model model, ExtractionOptions extractionOptions, SamplingOptions samplingOptions)
    {
        var anomalyDetector = new DefaultAnomalyDetector(samplingOptions);
        var sectionIntersectionService = new ApproximateSectionIntersectionService();
        var sampler = new DefaultSectionSampler(samplingOptions, anomalyDetector, sectionIntersectionService);
        var extractor = new Tekla2017MemberExtractor(model, extractionOptions, sampler);
        var jsonWriter = new JsonCacheWriter();
        var classifier = new FirstPassMemberClassifier(new FirstPassPartRoleScorer());
        return new ExtractionPipeline(extractor, jsonWriter, new NullSqliteCacheWriter(), classifier);
    }
}
