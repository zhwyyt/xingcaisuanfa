# Tekla 构件截面缓存骨架

这个目录提供了第一版“Tekla 数据缓存器 + 截面采样数据结构”的代码骨架，目标是：

- 从 Tekla 2017 API 按单根构件编号提取数据
- 将原始信息缓存为 JSON 和 SQLite
- 在离线环境中反复调试 H / Box / T / 十字柱 / 异形 的规则引擎

## 当前包含

- `TeklaSectionClassifier/TeklaSectionClassifier.csproj`
- `TeklaSectionClassifier/Models.cs`
- `TeklaSectionClassifier/Geometry.cs`
- `TeklaSectionClassifier/Contracts.cs`
- `TeklaSectionClassifier/ExtractionOptions.cs`
- `TeklaSectionClassifier/SamplingOptions.cs`
- `TeklaSectionClassifier/DefaultAnomalyDetector.cs`
- `TeklaSectionClassifier/ApproximateSectionIntersectionService.cs`
- `TeklaSectionClassifier/DefaultSectionSampler.cs`
- `TeklaSectionClassifier/Tekla2017MemberExtractor.cs`
- `TeklaSectionClassifier/ExtractionPipeline.cs`
- `TeklaSectionClassifier/JsonCacheWriter.cs`
- `TeklaSectionClassifier/SqliteCacheWriter.cs`
- `TeklaSectionClassifier/NullSqliteCacheWriter.cs`
- `TeklaSectionClassifier/Sql/001_init.sql`
- `TeklaSectionClassifier.Runner/TeklaSectionClassifier.Runner.csproj`
- `TeklaSectionClassifier.Runner/Program.cs`

## 当前能力

- 默认只导出 Tekla 中当前选中的构件
- 支持直接选 assembly，也支持选中任意属于该构件的零件后自动归并到 assembly
- 提取零件基础属性、坐标系、包围盒、轴向投影、端部接近性
- 按 `0.1L / 0.3L / 0.5L / 0.7L / 0.9L` 默认采样
- 自动避开端部区和局部附件高密区
- 生成第一版截面命中结果和基础截面特征
- 每处理 10 根构件打印一次进度
- 可从 runner 直接导出 JSON
- 如提供 SQLite provider 路径，可同时写 SQLite

## 运行方式

先在 Tekla 里选中要导出的构件，再运行：

```powershell
dotnet run --project .\TeklaSectionClassifier.Runner\TeklaSectionClassifier.Runner.csproj -- --output I:\xingcaisuanfa\cache\run_selected_1
```

只导出某一类编号前缀时：

```powershell
dotnet run --project .\TeklaSectionClassifier.Runner\TeklaSectionClassifier.Runner.csproj -- --output I:\xingcaisuanfa\cache\run_selected_1 --prefix B
```

导出给 Web3D 查看器使用的模型和元数据时：

```powershell
dotnet run --project .\TeklaSectionClassifier.Runner\TeklaSectionClassifier.Runner.csproj -- --output I:\xingcaisuanfa\cache\run_web3d_1 --export-web3d
```

该命令会在输出目录中生成：

- `web3d-model.gltf`
- `web3d-components.json`
- `web3d-reference.json`
- `README-web3d-export.txt`

其中：

- `web3d-model.gltf` 是可直接给浏览器查看器加载的模型文件
- `web3d-components.json` 包含构件 `teklaGuid / name / profile / material / partMark`
- `web3d-reference.json` 包含轴网、标高线、轴网交点以及轴网+标高三维交点，可用于多模型重定位
- 当前模式同样默认只处理 Tekla 中已选中的 assembly 或其零件

## 离线重跑分类

当 `member_*.json` 已经导出完成后，可以不打开 Tekla，直接对缓存重算分类：

```powershell
dotnet run --project .\TeklaSectionClassifier.Runner\TeklaSectionClassifier.Runner.csproj -- --reclassify-cache I:\xingcaisuanfa\cache\run_selected_6
```

也可以直接把路径指到 `members` 目录：

```powershell
dotnet run --project .\TeklaSectionClassifier.Runner\TeklaSectionClassifier.Runner.csproj -- --reclassify-cache I:\xingcaisuanfa\cache\run_selected_6\members
```

重跑后会：

- 覆盖更新每个 `member_*.json` 中的 `Classification`
- 在同目录生成 `classification_summary.csv`
- 在同目录生成 `classification_summary.json`

## 2026-10-10 已验收识别基线

构件主轴使用 `AlignedSpanMemberAxis`，source=`assemblyParts.alignedSpanCentroid`；已验收源码保存于 `feat/accepted-recognition-axis`。见 [主轴与连接导出基线](docs/verification/2026-10-10-accepted-recognition-axis.md)。识别三类主线在 `I:\\zijinhuatekla`，零件出图仍独立开发。
