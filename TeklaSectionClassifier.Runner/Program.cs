using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using Tekla.Structures.Model;
using ReflectionAssembly = System.Reflection.Assembly;

namespace TeklaSectionClassifier.Runner;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var options = RunnerOptions.Parse(args);
            if (!string.IsNullOrWhiteSpace(options.ReclassifyCacheDirectory))
            {
                return RunOfflineReclassification(options);
            }

            EnsureTeklaSessionName();
            var model = new Model();
            if (!model.GetConnectionStatus())
            {
                Console.Error.WriteLine("未连接到 Tekla 模型。请先打开 Tekla 2017 模型后再运行。");
                return 2;
            }

            if (options.ExportWeb3D)
            {
                Directory.CreateDirectory(options.OutputDirectory);
                var result = Web3DExporter.Write(model, options.OutputDirectory);
                Console.WriteLine($"Web3D 导出完成: {result.PartCount} 个零件");
                Console.WriteLine($"模型文件: {result.ModelPath}");
                Console.WriteLine($"元数据文件: {result.ComponentsPath}");
                return 0;
            }

            Directory.CreateDirectory(options.OutputDirectory);
            var extractionOptions = new TeklaSectionClassifier.ExtractionOptions
            {
                MemberPrefixFilter = options.MemberPrefixFilter,
                SelectedOnly = true
            };
            var samplingOptions = new TeklaSectionClassifier.SamplingOptions
            {
                Enabled = false
            };

            Console.WriteLine("当前模式: 仅导出 Tekla 中已选中的构件。可选中 assembly 或任意属于该构件的零件。");
            Console.WriteLine("识别导出关闭截面采样，选择集只遍历一次。");

            var assemblies = BodyBracketBundleExporter.CollectSelectedAssemblies();
            if (assemblies.Count == 0)
            {
                throw new InvalidOperationException("没有从当前选择集中解析出可导出的 assembly。请先在 Tekla 中选择 assembly 或属于该 assembly 的零件。");
            }

            var jsonDir = Path.Combine(options.OutputDirectory, "members");
            Directory.CreateDirectory(jsonDir);
            var sqlitePath = options.EnableSqlite ? Path.Combine(options.OutputDirectory, "tekla_members.db") : string.Empty;
            TeklaSectionClassifier.ISqliteCacheWriter sqliteWriter;
            string sqliteMessage;
            TryCreateSqliteWriter(options, out sqliteWriter, out sqliteMessage);
            Console.WriteLine(sqliteMessage);
            if (!string.IsNullOrWhiteSpace(sqlitePath))
            {
                sqliteWriter.InitializeDatabase(sqlitePath);
            }

            var sampler = new TeklaSectionClassifier.DefaultSectionSampler(
                samplingOptions,
                new TeklaSectionClassifier.DefaultAnomalyDetector(samplingOptions),
                new TeklaSectionClassifier.ApproximateSectionIntersectionService());
            var extractor = new TeklaSectionClassifier.Tekla2017MemberExtractor(model, extractionOptions, sampler);
            var classifier = new TeklaSectionClassifier.FirstPassMemberClassifier(new TeklaSectionClassifier.FirstPassPartRoleScorer());
            var jsonWriter = new TeklaSectionClassifier.JsonCacheWriter();
            var batch = new TeklaSectionClassifier.ExtractionBatch();

            Console.WriteLine("开始单次导出: " + assemblies.Count.ToString(CultureInfo.InvariantCulture) + " 根装配");
            Console.Out.Flush();

            var indexWatch = Stopwatch.StartNew();
            var bevelByFather = BodyBracketBundleExporter.IndexBevelObjectsByFather(model);
            indexWatch.Stop();
            Console.WriteLine(
                "剖口对象索引完成: " +
                bevelByFather.Count.ToString(CultureInfo.InvariantCulture) +
                " 个父零件，耗时 " +
                indexWatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) +
                "s");
            Console.Out.Flush();

            var ordered = assemblies.OrderBy(assembly => assembly.Identifier.ID).ToList();
            var exported = new List<ExportedAssembly>();
            var exportWatch = Stopwatch.StartNew();
            for (var i = 0; i < ordered.Count; i++)
            {
                var assembly = ordered[i];
                var member = extractor.ExtractAssembly(assembly);
                var item = BodyBracketBundleExporter.BuildAssemblyExport(model, assembly, bevelByFather);
                if (item != null)
                {
                    exported.Add(item);
                }

                if (member != null)
                {
                    member.Classification = classifier.Classify(member);
                    ApplyBundleBodyHint(member, item);
                    batch.Members.Add(member);
                    jsonWriter.WriteMemberJson(member, jsonDir);
                    if (!string.IsNullOrWhiteSpace(sqlitePath))
                    {
                        sqliteWriter.UpsertMember(member, sqlitePath);
                    }
                }

                var count = i + 1;
                if (count == 1 || count % 10 == 0 || count == ordered.Count)
                {
                    var label = member != null && member.Member != null && !string.IsNullOrWhiteSpace(member.Member.MemberId)
                        ? member.Member.MemberId
                        : item != null && item.Metadata != null && !string.IsNullOrWhiteSpace(item.Metadata.AssemblyPosition)
                            ? item.Metadata.AssemblyPosition
                            : assembly.Identifier.ID.ToString(CultureInfo.InvariantCulture);
                    Console.WriteLine(
                        "进度: 已处理 " +
                        count.ToString(CultureInfo.InvariantCulture) +
                        "/" +
                        ordered.Count.ToString(CultureInfo.InvariantCulture) +
                        " 根装配，当前 " +
                        label);
                    Console.Out.Flush();
                }
            }

            jsonWriter.WriteBatchSummary(batch, jsonDir);
            var bundlePath = BodyBracketBundleExporter.WriteBundleFile(
                model,
                options.OutputDirectory,
                exported,
                indexWatch.Elapsed.TotalSeconds,
                exportWatch.Elapsed.TotalSeconds);
            exportWatch.Stop();

            Console.WriteLine($"导出完成: {batch.Members.Count} 根构件");
            Console.WriteLine($"JSON目录: {jsonDir}");
            Console.WriteLine($"Body/Bracket Bundle: {bundlePath}");
            Console.WriteLine(
                "单次导出耗时 " +
                exportWatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) +
                "s");
            Console.Out.Flush();
            if (options.EnableSqlite && !string.IsNullOrWhiteSpace(sqlitePath) && File.Exists(sqlitePath))
            {
                Console.WriteLine($"SQLite: {sqlitePath}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static void EnsureTeklaSessionName()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SESSIONNAME")))
        {
            return;
        }

        Environment.SetEnvironmentVariable("SESSIONNAME", "Console", EnvironmentVariableTarget.Process);
    }

    private static int RunOfflineReclassification(RunnerOptions options)
    {
        var membersDirectory = ResolveMembersDirectory(options.ReclassifyCacheDirectory);
        if (string.IsNullOrWhiteSpace(membersDirectory) || !Directory.Exists(membersDirectory))
        {
            Console.Error.WriteLine($"未找到可用的 members 目录: {options.ReclassifyCacheDirectory}");
            return 3;
        }

        var files = Directory
            .GetFiles(membersDirectory, "member_*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
        {
            Console.Error.WriteLine($"目录中没有找到 member_*.json: {membersDirectory}");
            return 4;
        }

        var samplingOptions = new TeklaSectionClassifier.SamplingOptions();
        var anomalyDetector = new TeklaSectionClassifier.DefaultAnomalyDetector(samplingOptions);
        var intersectionService = new TeklaSectionClassifier.ApproximateSectionIntersectionService();
        var sampler = new TeklaSectionClassifier.DefaultSectionSampler(samplingOptions, anomalyDetector, intersectionService);
        var classifier = new TeklaSectionClassifier.FirstPassMemberClassifier(new TeklaSectionClassifier.FirstPassPartRoleScorer());
        var summaries = new List<MemberClassificationSummary>();
        var count = 0;

        foreach (var file in files)
        {
            var member = ReadJson<TeklaSectionClassifier.MemberSnapshot>(file);
            member.Samples = sampler.BuildSamples(member).ToList();
            member.Classification = classifier.Classify(member);
            WriteJson(file, member);

            count++;
            ReportProgress(count, member);
            summaries.Add(new MemberClassificationSummary
            {
                FileName = Path.GetFileName(file),
                MemberId = member.Member.MemberId,
                MainClass = ToChineseMainClass(member.Classification.MainClass),
                SecondaryClass = ToChineseSecondaryClass(member.Classification.SecondaryClass),
                Confidence = member.Classification.Confidence,
                NeedsManualReview = member.Classification.NeedsManualReview,
                KeyDimensions = member.Classification.KeyDimensionsDisplay,
                Labels = string.Join(" | ", (member.Classification.Labels ?? new List<string>()).Select(ToChineseLabel)),
                ReviewReason = ToChineseReviewReason(member.Classification.ReviewReason)
            });
        }

        WriteOfflineSummary(membersDirectory, summaries);
        Console.WriteLine($"离线重算完成: {count} 根构件");
        Console.WriteLine($"缓存目录: {membersDirectory}");
        Console.WriteLine($"汇总文件: {Path.Combine(membersDirectory, "classification_summary.csv")}");
        Console.WriteLine($"人工确认清单: {Path.Combine(membersDirectory, "manual_review_checklist.xlsx")}");
        return 0;
    }

    private static void ReportProgress(int count, TeklaSectionClassifier.MemberSnapshot member)
    {
        if (count == 1 || count % 10 == 0)
        {
            Console.WriteLine($"进度: 已处理 {count} 根构件，当前 {member.Member.MemberId}");
        }
    }

    private static bool TryCreateSqliteWriter(
        RunnerOptions options,
        out TeklaSectionClassifier.ISqliteCacheWriter writer,
        out string message)
    {
        if (!options.EnableSqlite)
        {
            writer = new TeklaSectionClassifier.NullSqliteCacheWriter();
            message = "SQLite 已关闭，仅导出 JSON。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(options.SqliteProviderPath) || !File.Exists(options.SqliteProviderPath))
        {
            writer = new TeklaSectionClassifier.NullSqliteCacheWriter();
            message = "未提供可用的 SQLite provider，已回退为仅导出 JSON。";
            return false;
        }

        var assembly = ReflectionAssembly.LoadFrom(options.SqliteProviderPath);
        var connectionType = assembly.GetTypes().FirstOrDefault(type => typeof(IDbConnection).IsAssignableFrom(type) && !type.IsAbstract);
        if (connectionType == null)
        {
            writer = new TeklaSectionClassifier.NullSqliteCacheWriter();
            message = "SQLite provider 中未找到可用的 IDbConnection 实现，已回退为仅导出 JSON。";
            return false;
        }

        IDbConnection ConnectionFactory(string dbPath)
        {
            var connection = (IDbConnection)Activator.CreateInstance(connectionType)!;
            var connectionStringProperty = connectionType.GetProperty("ConnectionString");
            connectionStringProperty!.SetValue(connection, $"Data Source={dbPath};Version=3;");
            return connection;
        }

        writer = new TeklaSectionClassifier.SqliteCacheWriter(ConnectionFactory);
        message = $"SQLite 已启用: {options.SqliteProviderPath}";
        return true;
    }

    private static void ApplyBundleBodyHint(
        TeklaSectionClassifier.MemberSnapshot member,
        ExportedAssembly? item)
    {
        if (member?.Classification == null || item?.Metadata == null)
        {
            return;
        }

        var classification = member.Classification;
        if (classification.MainClass != TeklaSectionClassifier.MemberClass.Irregular &&
            classification.MainClass != TeklaSectionClassifier.MemberClass.Unknown)
        {
            return;
        }

        var boxLoops = item.Metadata.BoxSectionEvidence != null
            ? item.Metadata.BoxSectionEvidence.StationLoops
            : null;
        var hasClosedBox = boxLoops != null && boxLoops.Any(loop => loop != null && loop.ClosedLoopCount > 0);
        var hFrames = item.Metadata.HBeamSectionEvidence != null
            ? item.Metadata.HBeamSectionEvidence.StationFrames
            : null;
        var hasHFrames = hFrames != null && hFrames.Count > 0;
        if (!hasClosedBox && !hasHFrames)
        {
            return;
        }

        if (classification.Labels == null)
        {
            classification.Labels = new List<string>();
        }

        if (hasClosedBox)
        {
            classification.MainClass = TeklaSectionClassifier.MemberClass.Box;
            if (!StartsWithToken(classification.KeyDimensionsDisplay, "BOX"))
            {
                classification.KeyDimensionsDisplay = string.IsNullOrWhiteSpace(classification.KeyDimensionsDisplay)
                    ? "BOX"
                    : "BOX " + classification.KeyDimensionsDisplay;
            }

            if (!classification.Labels.Contains("bundle-box-closed-loop-hint"))
            {
                classification.Labels.Add("bundle-box-closed-loop-hint");
            }

            return;
        }

        classification.MainClass = TeklaSectionClassifier.MemberClass.H;
        if (string.IsNullOrWhiteSpace(classification.KeyDimensionsDisplay))
        {
            classification.KeyDimensionsDisplay = "H";
        }

        if (!classification.Labels.Contains("bundle-h-station-frame-hint"))
        {
            classification.Labels.Add("bundle-h-station-frame-hint");
        }
    }

    private static bool StartsWithToken(string value, string token)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.TrimStart().StartsWith(token, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveMembersDirectory(string inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            return string.Empty;
        }

        if (Directory.Exists(inputPath))
        {
            var directMembers = Directory.GetFiles(inputPath, "member_*.json", SearchOption.TopDirectoryOnly);
            if (directMembers.Length > 0)
            {
                return inputPath;
            }

            var nestedMembers = Path.Combine(inputPath, "members");
            if (Directory.Exists(nestedMembers))
            {
                return nestedMembers;
            }
        }

        return inputPath;
    }

    private static T ReadJson<T>(string path)
    {
        using var stream = File.OpenRead(path);
        var serializer = new DataContractJsonSerializer(typeof(T));
        return (T)serializer.ReadObject(stream);
    }

    private static void WriteJson<T>(string path, T value)
    {
        using var stream = File.Create(path);
        var serializer = new DataContractJsonSerializer(typeof(T));
        serializer.WriteObject(stream, value);
    }

    private static void WriteOfflineSummary(string membersDirectory, IReadOnlyList<MemberClassificationSummary> summaries)
    {
        var csvPath = Path.Combine(membersDirectory, "classification_summary.csv");
        var jsonPath = Path.Combine(membersDirectory, "classification_summary.json");

        var csv = new StringBuilder();
        csv.AppendLine("文件名,构件编号,主类,二级分类,置信度,需人工确认,关键尺寸,标签,复核原因");
        foreach (var item in summaries)
        {
            csv.AppendLine(string.Join(",",
                EscapeCsv(item.FileName),
                EscapeCsv(item.MemberId),
                EscapeCsv(item.MainClass),
                EscapeCsv(item.SecondaryClass),
                item.Confidence.ToString("0.##"),
                item.NeedsManualReview ? "是" : "否",
                EscapeCsv(item.KeyDimensions),
                EscapeCsv(item.Labels),
                EscapeCsv(item.ReviewReason)));
        }

        File.WriteAllText(csvPath, csv.ToString(), Encoding.UTF8);

        WriteManualReviewExcel(Path.Combine(membersDirectory, "manual_review_checklist.xlsx"), summaries);

        var report = new OfflineClassificationReport
        {
            GeneratedAt = DateTimeOffset.Now,
            TotalMembers = summaries.Count,
            ManualReviewCount = summaries.Count(item => item.NeedsManualReview),
            ClassCounts = summaries
                .GroupBy(item => item.MainClass)
                .OrderByDescending(group => group.Count())
                .Select(group => new ClassCountItem
                {
                    MainClass = group.Key,
                    Count = group.Count()
                })
                .ToList(),
            Members = summaries.ToList()
        };

        WriteJson(jsonPath, report);
    }

    private static string EscapeCsv(string value)
    {
        var text = value ?? string.Empty;
        if (text.Contains("\""))
        {
            text = text.Replace("\"", "\"\"");
        }

        if (text.IndexOfAny(new[] { ',', '\"', '\r', '\n' }) >= 0)
        {
            return $"\"{text}\"";
        }

        return text;
    }

    private static void WriteManualReviewExcel(string path, IReadOnlyList<MemberClassificationSummary> summaries)
    {
        var manualRows = summaries
            .Where(ShouldIncludeInPriorityReview)
            .OrderByDescending(IsLowConfidenceBoxReview)
            .ThenByDescending(item => item.NeedsManualReview)
            .ThenBy(item => item.Confidence)
            .ThenBy(item => item.MainClass, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.MemberId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var allRows = summaries
            .OrderByDescending(ShouldIncludeInPriorityReview)
            .ThenByDescending(IsLowConfidenceBoxReview)
            .ThenByDescending(item => item.NeedsManualReview)
            .ThenBy(item => item.Confidence)
            .ThenBy(item => item.MainClass, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.MemberId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var headers = new[]
        {
            "文件名",
            "构件编号",
            "主类",
            "二级分类",
            "置信度",
            "关键尺寸",
            "标签",
            "复核原因",
            "人工确认结果",
            "备注"
        };

        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        WriteZipEntry(archive, "[Content_Types].xml", @"<?xml version=""1.0"" encoding=""UTF-8""?>
<Types xmlns=""http://schemas.openxmlformats.org/package/2006/content-types"">
  <Default Extension=""rels"" ContentType=""application/vnd.openxmlformats-package.relationships+xml""/>
  <Default Extension=""xml"" ContentType=""application/xml""/>
  <Override PartName=""/xl/workbook.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml""/>
  <Override PartName=""/xl/worksheets/sheet1.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml""/>
  <Override PartName=""/xl/worksheets/sheet2.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml""/>
  <Override PartName=""/xl/styles.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml""/>
</Types>");

        WriteZipEntry(archive, "_rels/.rels", @"<?xml version=""1.0"" encoding=""UTF-8""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
  <Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"" Target=""xl/workbook.xml""/>
</Relationships>");

        WriteZipEntry(archive, "xl/workbook.xml", @"<?xml version=""1.0"" encoding=""UTF-8""?>
<workbook xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"" xmlns:r=""http://schemas.openxmlformats.org/officeDocument/2006/relationships"">
  <sheets>
    <sheet name=""人工复核"" sheetId=""1"" r:id=""rId1""/>
    <sheet name=""全部结果"" sheetId=""2"" r:id=""rId2""/>
  </sheets>
</workbook>");

        WriteZipEntry(archive, "xl/_rels/workbook.xml.rels", @"<?xml version=""1.0"" encoding=""UTF-8""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
  <Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"" Target=""worksheets/sheet1.xml""/>
  <Relationship Id=""rId2"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"" Target=""worksheets/sheet2.xml""/>
  <Relationship Id=""rId3"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"" Target=""styles.xml""/>
</Relationships>");

        WriteZipEntry(archive, "xl/styles.xml", @"<?xml version=""1.0"" encoding=""UTF-8""?>
<styleSheet xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"">
  <fonts count=""2"">
    <font><sz val=""11""/><name val=""Calibri""/></font>
    <font><b/><sz val=""11""/><name val=""Calibri""/></font>
  </fonts>
  <fills count=""2"">
    <fill><patternFill patternType=""none""/></fill>
    <fill><patternFill patternType=""gray125""/></fill>
  </fills>
  <borders count=""1"">
    <border><left/><right/><top/><bottom/><diagonal/></border>
  </borders>
  <cellStyleXfs count=""1"">
    <xf numFmtId=""0"" fontId=""0"" fillId=""0"" borderId=""0""/>
  </cellStyleXfs>
  <cellXfs count=""3"">
    <xf numFmtId=""0"" fontId=""0"" fillId=""0"" borderId=""0"" xfId=""0""/>
    <xf numFmtId=""0"" fontId=""1"" fillId=""0"" borderId=""0"" xfId=""0"" applyFont=""1""/>
    <xf numFmtId=""2"" fontId=""0"" fillId=""0"" borderId=""0"" xfId=""0"" applyNumberFormat=""1""/>
  </cellXfs>
  <cellStyles count=""1"">
    <cellStyle name=""Normal"" xfId=""0"" builtinId=""0""/>
  </cellStyles>
</styleSheet>");

        WriteZipEntry(archive, "xl/worksheets/sheet1.xml", BuildSummaryWorksheetXml(headers, manualRows));
        WriteZipEntry(archive, "xl/worksheets/sheet2.xml", BuildSummaryWorksheetXml(headers, allRows));
    }

    private static string BuildSummaryWorksheetXml(IReadOnlyList<string> headers, IReadOnlyList<MemberClassificationSummary> rows)
    {
        var sheet = new StringBuilder();
        sheet.Append(@"<?xml version=""1.0"" encoding=""UTF-8""?>");
        sheet.Append(@"<worksheet xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main""><sheetViews><sheetView workbookViewId=""0""/></sheetViews>");
        sheet.Append(@"<sheetFormatPr defaultRowHeight=""15""/>");
        sheet.Append(@"<cols>");
        sheet.Append(@"<col min=""1"" max=""1"" width=""24"" customWidth=""1""/>");
        sheet.Append(@"<col min=""2"" max=""2"" width=""18"" customWidth=""1""/>");
        sheet.Append(@"<col min=""3"" max=""3"" width=""12"" customWidth=""1""/>");
        sheet.Append(@"<col min=""4"" max=""4"" width=""16"" customWidth=""1""/>");
        sheet.Append(@"<col min=""5"" max=""5"" width=""12"" customWidth=""1""/>");
        sheet.Append(@"<col min=""6"" max=""6"" width=""24"" customWidth=""1""/>");
        sheet.Append(@"<col min=""7"" max=""7"" width=""42"" customWidth=""1""/>");
        sheet.Append(@"<col min=""8"" max=""8"" width=""56"" customWidth=""1""/>");
        sheet.Append(@"<col min=""9"" max=""9"" width=""18"" customWidth=""1""/>");
        sheet.Append(@"<col min=""10"" max=""10"" width=""24"" customWidth=""1""/>");
        sheet.Append(@"</cols>");
        sheet.Append(@"<sheetData>");
        sheet.Append(@"<row r=""1"">");
        for (var i = 0; i < headers.Count; i++)
        {
            sheet.Append(BuildInlineStringCell(GetExcelColumnName(i + 1), 1, headers[i], 1));
        }
        sheet.Append(@"</row>");

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            AppendSummaryRow(sheet, rowIndex + 2, rows[rowIndex]);
        }

        sheet.Append("</sheetData></worksheet>");
        return sheet.ToString();
    }

    private static void AppendSummaryRow(StringBuilder sheet, int rowNumber, MemberClassificationSummary item)
    {
        sheet.Append($"<row r=\"{rowNumber}\">");
        sheet.Append(BuildInlineStringCell("A", rowNumber, item.FileName));
        sheet.Append(BuildInlineStringCell("B", rowNumber, item.MemberId));
        sheet.Append(BuildInlineStringCell("C", rowNumber, item.MainClass));
        sheet.Append(BuildInlineStringCell("D", rowNumber, item.SecondaryClass));
        sheet.Append(BuildNumberCell("E", rowNumber, item.Confidence, 2));
        sheet.Append(BuildInlineStringCell("F", rowNumber, item.KeyDimensions));
        sheet.Append(BuildInlineStringCell("G", rowNumber, item.Labels));
        sheet.Append(BuildInlineStringCell("H", rowNumber, item.ReviewReason));
        sheet.Append(BuildInlineStringCell("I", rowNumber, string.Empty));
        sheet.Append(BuildInlineStringCell("J", rowNumber, string.Empty));
        sheet.Append("</row>");
    }

    private static bool ShouldIncludeInPriorityReview(MemberClassificationSummary item)
    {
        return item.NeedsManualReview || IsLowConfidenceBoxReview(item);
    }

    private static bool IsLowConfidenceBoxReview(MemberClassificationSummary item)
    {
        return string.Equals(item.MainClass, "箱型", StringComparison.OrdinalIgnoreCase) && item.Confidence < 90;
    }

    private static void WriteZipEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string BuildInlineStringCell(string columnName, int rowNumber, string value, int styleIndex = 0)
    {
        return $"<c r=\"{columnName}{rowNumber}\" t=\"inlineStr\" s=\"{styleIndex}\"><is><t xml:space=\"preserve\">{EscapeXml(value)}</t></is></c>";
    }

    private static string BuildNumberCell(string columnName, int rowNumber, double value, int styleIndex = 0)
    {
        return $"<c r=\"{columnName}{rowNumber}\" s=\"{styleIndex}\"><v>{value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}</v></c>";
    }

    private static string GetExcelColumnName(int index)
    {
        var name = string.Empty;
        var value = index;
        while (value > 0)
        {
            value--;
            name = (char)('A' + (value % 26)) + name;
            value /= 26;
        }

        return name;
    }

    private static string EscapeXml(string value)
    {
        return (value ?? string.Empty)
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }

    private static string ToChineseMainClass(TeklaSectionClassifier.MemberClass memberClass)
    {
        return memberClass switch
        {
            TeklaSectionClassifier.MemberClass.H => "H钢",
            TeklaSectionClassifier.MemberClass.Box => "箱型",
            TeklaSectionClassifier.MemberClass.T => "T型",
            TeklaSectionClassifier.MemberClass.Cross => "十字柱",
            TeklaSectionClassifier.MemberClass.Angle => "角钢",
            TeklaSectionClassifier.MemberClass.Pipe => "圆管",
            TeklaSectionClassifier.MemberClass.Irregular => "异形",
            _ => "未知"
        };
    }

    private static string ToChineseSecondaryClass(TeklaSectionClassifier.SecondaryClass secondaryClass)
    {
        return secondaryClass switch
        {
            TeklaSectionClassifier.SecondaryClass.Regular => "规则",
            TeklaSectionClassifier.SecondaryClass.Irregular => "不规则",
            TeklaSectionClassifier.SecondaryClass.HRegular => "规则H",
            TeklaSectionClassifier.SecondaryClass.HTapered => "变截面H",
            TeklaSectionClassifier.SecondaryClass.HIrregular => "异形H",
            TeklaSectionClassifier.SecondaryClass.HCurved => "弧形H",
            TeklaSectionClassifier.SecondaryClass.HFolded => "折型H",
            TeklaSectionClassifier.SecondaryClass.BoxCurved => "弧形箱型",
            TeklaSectionClassifier.SecondaryClass.BoxFolded => "折型箱型",
            _ => "未知"
        };
    }

    private static string ToChineseLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return string.Empty;
        }

        return label.Trim() switch
        {
            "section-width-varies" => "截面宽度变化",
            "section-height-varies" => "截面高度变化",
            "profile-direct-hint" => "型材属性直读",
            "profile-angle" => "角钢属性",
            "folded-flange" => "折板翼缘",
            "h-irregular-end-taper-mid-constant" => "中段恒定、两端收",
            "h-irregular-single-end-taper" => "中段恒定、单端收",
            "h-irregular-folded-flange" => "局部折板翼缘",
            "h-curved-polybeam" => "弧形H",
            "h-folded-polybeam" => "折型H",
            "h-irregular-nonparallel-flange" => "局部非平行翼缘",
            "box-curved-polybeam" => "弧形箱型",
            "box-folded-polybeam" => "折型箱型",
            "many-local-stiffeners" => "局部加劲较多",
            "endplates-present" => "存在端板",
            _ => label
        };
    }

    private static string ToChineseReviewReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return string.Empty;
        }

        var text = reason.Trim();
        text = text.Replace("first-pass rules could not map the member to a stable standard section", "首轮规则无法将该构件稳定映射到标准截面");
        text = text.Replace("top two class scores are close:", "前两类得分接近：");
        text = text.Replace("manual review required", "需要人工复核");
        text = text.Replace("profile direct hint", "型材属性直读");
        text = text.Replace("Unknown", "未知");
        text = text.Replace("Box", "箱型");
        text = text.Replace("Cross", "十字柱");
        text = text.Replace("Angle", "角钢");
        text = text.Replace("Pipe", "圆管");
        text = text.Replace("Irregular", "异形");
        text = text.Replace("H=", "H钢=");
        text = text.Replace("T=", "T型=");
        return text;
    }
}

internal sealed class RunnerOptions
{
    public string OutputDirectory { get; set; } = string.Empty;
    public string MemberPrefixFilter { get; set; } = string.Empty;
    public bool EnableSqlite { get; set; }
    public string SqliteProviderPath { get; set; } = string.Empty;
    public string ReclassifyCacheDirectory { get; set; } = string.Empty;
    public bool ExportWeb3D { get; set; }

    public static RunnerOptions Parse(IReadOnlyList<string> args)
    {
        var options = new RunnerOptions
        {
            OutputDirectory = Path.Combine(Environment.CurrentDirectory, "cache", DateTime.Now.ToString("yyyyMMdd_HHmmss")),
            MemberPrefixFilter = string.Empty,
            EnableSqlite = false,
            SqliteProviderPath = string.Empty,
            ReclassifyCacheDirectory = string.Empty,
            ExportWeb3D = false
        };

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Equals("--output", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                options.OutputDirectory = args[++i];
                continue;
            }

            if (arg.Equals("--prefix", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                options.MemberPrefixFilter = args[++i];
                continue;
            }

            if (arg.Equals("--sqlite", StringComparison.OrdinalIgnoreCase))
            {
                options.EnableSqlite = true;
                continue;
            }

            if (arg.Equals("--sqlite-provider", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                options.SqliteProviderPath = args[++i];
                options.EnableSqlite = true;
                continue;
            }

            if (arg.Equals("--reclassify-cache", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                options.ReclassifyCacheDirectory = args[++i];
                continue;
            }

            if (arg.Equals("--export-web3d", StringComparison.OrdinalIgnoreCase))
            {
                options.ExportWeb3D = true;
                continue;
            }
        }

        return options;
    }
}

[DataContract]
internal sealed class MemberClassificationSummary
{
    [DataMember] public string FileName { get; set; } = string.Empty;
    [DataMember] public string MemberId { get; set; } = string.Empty;
    [DataMember] public string MainClass { get; set; } = string.Empty;
    [DataMember] public string SecondaryClass { get; set; } = string.Empty;
    [DataMember] public double Confidence { get; set; }
    [DataMember] public bool NeedsManualReview { get; set; }
    [DataMember] public string KeyDimensions { get; set; } = string.Empty;
    [DataMember] public string Labels { get; set; } = string.Empty;
    [DataMember] public string ReviewReason { get; set; } = string.Empty;
}

[DataContract]
internal sealed class OfflineClassificationReport
{
    [DataMember] public DateTimeOffset GeneratedAt { get; set; }
    [DataMember] public int TotalMembers { get; set; }
    [DataMember] public int ManualReviewCount { get; set; }
    [DataMember] public List<ClassCountItem> ClassCounts { get; set; } = new List<ClassCountItem>();
    [DataMember] public List<MemberClassificationSummary> Members { get; set; } = new List<MemberClassificationSummary>();
}

[DataContract]
internal sealed class ClassCountItem
{
    [DataMember] public string MainClass { get; set; } = string.Empty;
    [DataMember] public int Count { get; set; }
}




