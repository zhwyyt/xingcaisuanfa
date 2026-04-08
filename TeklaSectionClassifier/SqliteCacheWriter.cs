using System;
using System.Data;
using System.IO;

namespace TeklaSectionClassifier;

public sealed class SqliteCacheWriter : ISqliteCacheWriter
{
    private readonly Func<string, IDbConnection> _connectionFactory;

    public SqliteCacheWriter(Func<string, IDbConnection> connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public void InitializeDatabase(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = _connectionFactory(databasePath);
        connection.Open();

        var scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Sql", "001_init.sql");
        var sql = File.ReadAllText(scriptPath);
        ExecuteScript(connection, sql);
    }

    public void UpsertMember(MemberSnapshot member, string databasePath)
    {
        using var connection = _connectionFactory(databasePath);
        connection.Open();
        using var transaction = connection.BeginTransaction();

        UpsertMemberCore(connection, transaction, member);
        DeleteExistingChildren(connection, transaction, member.Member.MemberId);

        foreach (var part in member.Parts)
        {
            InsertPart(connection, transaction, member.Member.MemberId, part);
        }

        foreach (var sample in member.Samples)
        {
            InsertSample(connection, transaction, member.Member.MemberId, sample);
            foreach (var hit in sample.SectionParts)
            {
                InsertSamplePart(connection, transaction, sample.SampleId, hit);
            }
        }

        transaction.Commit();
    }

    private static void ExecuteScript(IDbConnection connection, string sql)
    {
        var chunks = sql.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var chunk in chunks)
        {
            using var command = connection.CreateCommand();
            command.CommandText = chunk;
            command.ExecuteNonQuery();
        }
    }

    private static void UpsertMemberCore(IDbConnection connection, IDbTransaction transaction, MemberSnapshot member)
    {
        using var delete = CreateCommand(connection, transaction, "DELETE FROM members WHERE member_id = @member_id;");
        AddParameter(delete, "@member_id", member.Member.MemberId);
        delete.ExecuteNonQuery();

        using var insert = CreateCommand(connection, transaction,
            @"INSERT INTO members (
                member_id, assembly_id, guid, name, profile_string, material,
                main_axis_origin_x, main_axis_origin_y, main_axis_origin_z,
                main_axis_dir_x, main_axis_dir_y, main_axis_dir_z, axis_length,
                bbox_min_x, bbox_min_y, bbox_min_z, bbox_max_x, bbox_max_y, bbox_max_z, extract_time
              ) VALUES (
                @member_id, @assembly_id, @guid, @name, @profile_string, @material,
                @origin_x, @origin_y, @origin_z,
                @dir_x, @dir_y, @dir_z, @axis_length,
                @bbox_min_x, @bbox_min_y, @bbox_min_z, @bbox_max_x, @bbox_max_y, @bbox_max_z, @extract_time
              );");

        AddParameter(insert, "@member_id", member.Member.MemberId);
        AddParameter(insert, "@assembly_id", member.Member.AssemblyId);
        AddParameter(insert, "@guid", member.Member.Guid);
        AddParameter(insert, "@name", member.Member.Name);
        AddParameter(insert, "@profile_string", member.Member.ProfileString);
        AddParameter(insert, "@material", member.Member.Material);
        AddParameter(insert, "@origin_x", member.Member.MainAxis.Origin.X);
        AddParameter(insert, "@origin_y", member.Member.MainAxis.Origin.Y);
        AddParameter(insert, "@origin_z", member.Member.MainAxis.Origin.Z);
        AddParameter(insert, "@dir_x", member.Member.MainAxis.Direction.X);
        AddParameter(insert, "@dir_y", member.Member.MainAxis.Direction.Y);
        AddParameter(insert, "@dir_z", member.Member.MainAxis.Direction.Z);
        AddParameter(insert, "@axis_length", member.Member.MainAxis.Length);
        AddParameter(insert, "@bbox_min_x", member.Member.BoundingBox.Min.X);
        AddParameter(insert, "@bbox_min_y", member.Member.BoundingBox.Min.Y);
        AddParameter(insert, "@bbox_min_z", member.Member.BoundingBox.Min.Z);
        AddParameter(insert, "@bbox_max_x", member.Member.BoundingBox.Max.X);
        AddParameter(insert, "@bbox_max_y", member.Member.BoundingBox.Max.Y);
        AddParameter(insert, "@bbox_max_z", member.Member.BoundingBox.Max.Z);
        AddParameter(insert, "@extract_time", DateTimeOffset.Now.ToString("O"));
        insert.ExecuteNonQuery();
    }

    private static void DeleteExistingChildren(IDbConnection connection, IDbTransaction transaction, string memberId)
    {
        using var deleteParts = CreateCommand(connection, transaction, "DELETE FROM parts WHERE member_id = @member_id;");
        AddParameter(deleteParts, "@member_id", memberId);
        deleteParts.ExecuteNonQuery();

        using var deleteSampleParts = CreateCommand(connection, transaction,
            "DELETE FROM section_sample_parts WHERE sample_id IN (SELECT sample_id FROM section_samples WHERE member_id = @member_id);");
        AddParameter(deleteSampleParts, "@member_id", memberId);
        deleteSampleParts.ExecuteNonQuery();

        using var deleteSamples = CreateCommand(connection, transaction, "DELETE FROM section_samples WHERE member_id = @member_id;");
        AddParameter(deleteSamples, "@member_id", memberId);
        deleteSamples.ExecuteNonQuery();
    }

    private static void InsertPart(IDbConnection connection, IDbTransaction transaction, string memberId, PartSnapshot part)
    {
        using var insert = CreateCommand(connection, transaction,
            @"INSERT INTO parts (
                part_id, member_id, guid, name, part_type, profile_string, material,
                thickness, area, volume, centroid_x, centroid_y, centroid_z,
                axis_proj_start, axis_proj_end, axis_proj_length, coverage_ratio,
                near_start, near_end, is_bent_like, is_folded_like, outer_side_candidate
              ) VALUES (
                @part_id, @member_id, @guid, @name, @part_type, @profile_string, @material,
                @thickness, @area, @volume, @centroid_x, @centroid_y, @centroid_z,
                @axis_proj_start, @axis_proj_end, @axis_proj_length, @coverage_ratio,
                @near_start, @near_end, @is_bent_like, @is_folded_like, @outer_side_candidate
              );");

        AddParameter(insert, "@part_id", part.PartId);
        AddParameter(insert, "@member_id", memberId);
        AddParameter(insert, "@guid", part.Guid);
        AddParameter(insert, "@name", part.Name);
        AddParameter(insert, "@part_type", part.PartType);
        AddParameter(insert, "@profile_string", part.ProfileString);
        AddParameter(insert, "@material", part.Material);
        AddParameter(insert, "@thickness", part.Thickness);
        AddParameter(insert, "@area", part.Area);
        AddParameter(insert, "@volume", part.Volume);
        AddParameter(insert, "@centroid_x", part.Centroid.X);
        AddParameter(insert, "@centroid_y", part.Centroid.Y);
        AddParameter(insert, "@centroid_z", part.Centroid.Z);
        AddParameter(insert, "@axis_proj_start", part.AxisProjection.Start);
        AddParameter(insert, "@axis_proj_end", part.AxisProjection.End);
        AddParameter(insert, "@axis_proj_length", part.AxisProjection.Length);
        AddParameter(insert, "@coverage_ratio", part.AxisProjection.CoverageRatio);
        AddParameter(insert, "@near_start", part.EndProximity.NearStart ? 1 : 0);
        AddParameter(insert, "@near_end", part.EndProximity.NearEnd ? 1 : 0);
        AddParameter(insert, "@is_bent_like", part.GeometryHints.IsBentLike ? 1 : 0);
        AddParameter(insert, "@is_folded_like", part.GeometryHints.IsFoldedLike ? 1 : 0);
        AddParameter(insert, "@outer_side_candidate", part.GeometryHints.OuterSideCandidate ? 1 : 0);
        insert.ExecuteNonQuery();
    }

    private static void InsertSample(IDbConnection connection, IDbTransaction transaction, string memberId, SectionSample sample)
    {
        using var insert = CreateCommand(connection, transaction,
            @"INSERT INTO section_samples (
                sample_id, member_id, ratio, station, is_abnormal, abnormal_reason,
                closed_loops, major_plate_count, outer_width, outer_height, cavity_count,
                horizontal_symmetry_score, vertical_symmetry_score,
                central_vertical_plate_count, central_horizontal_plate_count, enclosure_score
              ) VALUES (
                @sample_id, @member_id, @ratio, @station, @is_abnormal, @abnormal_reason,
                @closed_loops, @major_plate_count, @outer_width, @outer_height, @cavity_count,
                @horizontal_symmetry_score, @vertical_symmetry_score,
                @central_vertical_plate_count, @central_horizontal_plate_count, @enclosure_score
              );");

        AddParameter(insert, "@sample_id", sample.SampleId);
        AddParameter(insert, "@member_id", memberId);
        AddParameter(insert, "@ratio", sample.Ratio);
        AddParameter(insert, "@station", sample.Station);
        AddParameter(insert, "@is_abnormal", sample.IsAbnormal ? 1 : 0);
        AddParameter(insert, "@abnormal_reason", sample.AbnormalReason);
        AddParameter(insert, "@closed_loops", sample.SectionFeatures.ClosedLoops);
        AddParameter(insert, "@major_plate_count", sample.SectionFeatures.MajorPlateCount);
        AddParameter(insert, "@outer_width", sample.SectionFeatures.OuterWidth);
        AddParameter(insert, "@outer_height", sample.SectionFeatures.OuterHeight);
        AddParameter(insert, "@cavity_count", sample.SectionFeatures.CavityCount);
        AddParameter(insert, "@horizontal_symmetry_score", sample.SectionFeatures.HorizontalSymmetryScore);
        AddParameter(insert, "@vertical_symmetry_score", sample.SectionFeatures.VerticalSymmetryScore);
        AddParameter(insert, "@central_vertical_plate_count", sample.SectionFeatures.CentralVerticalPlateCount);
        AddParameter(insert, "@central_horizontal_plate_count", sample.SectionFeatures.CentralHorizontalPlateCount);
        AddParameter(insert, "@enclosure_score", sample.SectionFeatures.EnclosureScore);
        insert.ExecuteNonQuery();
    }

    private static void InsertSamplePart(IDbConnection connection, IDbTransaction transaction, string sampleId, SectionPartHit hit)
    {
        using var insert = CreateCommand(connection, transaction,
            @"INSERT INTO section_sample_parts (
                sample_id, part_id, role_hint, cut_length, thickness, local_pos_1, local_pos_2
              ) VALUES (
                @sample_id, @part_id, @role_hint, @cut_length, @thickness, @local_pos_1, @local_pos_2
              );");

        AddParameter(insert, "@sample_id", sampleId);
        AddParameter(insert, "@part_id", hit.PartId);
        AddParameter(insert, "@role_hint", hit.RoleHint);
        AddParameter(insert, "@cut_length", hit.TotalCutLength);
        AddParameter(insert, "@thickness", hit.Thickness);
        AddParameter(insert, "@local_pos_1", hit.Center2D.X);
        AddParameter(insert, "@local_pos_2", hit.Center2D.Y);
        insert.ExecuteNonQuery();
    }

    private static IDbCommand CreateCommand(IDbConnection connection, IDbTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void AddParameter(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
