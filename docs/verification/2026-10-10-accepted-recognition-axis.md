# 已验收识别主轴与连接导出基线

2026-10-10 用户确认最后实验成果已人工验收，构件主轴必须使用实验算法。此分支 `feat/accepted-recognition-axis` 保存既有源代码，不重新修改判定逻辑。

## 已保存范围

- `TeklaSectionClassifier/AlignedSpanMemberAxis.cs`：5° 方向分组、最大合并跨度、贴合零件面积加权中心、主段覆盖与折线链。
- `TeklaSectionClassifier/TeklaPartAxisSampler.cs`：供主材和 bundle 共用的实体测量。
- `TeklaSectionClassifier/Tekla2017MemberExtractor.cs`：将主轴应用到 MainAxis、AxisSegments、GuidePolyline 与零件投影。
- `TeklaSectionClassifier.Runner/BodyBracketBundleExporter.cs`、`BodyBracketExportDtos.cs`：分组跨度主轴及跨构件 Weld/Bolt 连接证据。

`memberAxisEvidence.source` 与 `LongitudinalAxisSource` 为 `assemblyParts.alignedSpanCentroid`。主轴不读取构件号或 Tekla 主零件；身份选择仍独立保留。

## 验收与验证

人工验收来源：2026-10-10 用户明确确认既有实验成果。详细规则和已保存案例位于 `I:\zijinhuatekla\docs\design\2026-09-29-aligned-span-member-axis.md` 和角色断言设计。

本轮构建：`dotnet build TeklaSectionClassifier.Runner/TeklaSectionClassifier.Runner.csproj --no-restore -v:q`，0 警告、0 错误。识别侧正式集成在 `I:\zijinhuatekla` 的 `feat/recognition-mainline`。

本轮未重新连接真实 Tekla，也未重新导出选择集；历史 cache 仍为旧主轴，需要以后使用此 Runner 重导。编译产物、其它业务文件和缓存不随本次源码提交。

## 可迁移检查

共同算法不等于共同数据版本。跨语言导出/识别交付必须同时检查：源码版本、bundle 主轴 source、member 主轴 source、主段与整条折线的语义、缓存生成时间。这样可防止新算法接入后仍消费旧缓存，或把折线主段误当整条构件轴线。
