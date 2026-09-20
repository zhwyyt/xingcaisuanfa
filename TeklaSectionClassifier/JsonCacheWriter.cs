using System.IO;
using System.Runtime.Serialization.Json;

namespace TeklaSectionClassifier;

public sealed class JsonCacheWriter : ICacheWriter
{
    public void WriteMemberJson(MemberSnapshot member, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var safeMemberId = Sanitize(member.Member.MemberId);
        var path = Path.Combine(outputDirectory, $"member_{safeMemberId}.json");
        WriteJson(path, member);

        var draftPath = Path.Combine(outputDirectory, $"member_{safeMemberId}.input-layer-draft.json");
        WriteJson(draftPath, InputLayerDraftExportBuilder.BuildMember(member));
    }

    public void WriteBatchSummary(ExtractionBatch batch, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, "batch_summary.json");
        WriteJson(path, batch);

        var draftSummaryPath = Path.Combine(outputDirectory, "input_layer_draft_summary.json");
        WriteJson(draftSummaryPath, InputLayerDraftExportBuilder.BuildSummary(batch));
    }

    private static void WriteJson<T>(string path, T value)
    {
        using var stream = File.Create(path);
        var serializer = new DataContractJsonSerializer(typeof(T));
        serializer.WriteObject(stream, value);
    }

    private static string Sanitize(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }

        return value;
    }
}
