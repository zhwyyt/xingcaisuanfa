using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using Tekla.Structures.Model;
using Tekla.Structures.Solid;

namespace TeklaSectionClassifier.Runner;

internal static class Web3DExporter
{
    public static Web3DExportResult Write(Model model, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        var selectedAssemblies = CollectSelectedAssemblies();
        if (selectedAssemblies.Count == 0)
        {
            throw new InvalidOperationException("没有从当前选择集中解析出可导出的 assembly。请先在 Tekla 中选择 assembly 或属于该 assembly 的零件。");
        }

        var components = new List<Web3DComponentMetadata>();
        var meshParts = new List<Web3DPartMesh>();
        var reference = BuildReferenceData(model, selectedAssemblies);

        foreach (var assembly in selectedAssemblies.OrderBy(item => item.Identifier.ID))
        {
            foreach (var part in GetAssemblyParts(assembly).OrderBy(item => item.Identifier.ID))
            {
                var mesh = TryBuildMesh(part);
                if (mesh == null)
                {
                    continue;
                }

                meshParts.Add(mesh);
                components.Add(new Web3DComponentMetadata
                {
                    TeklaGuid = part.Identifier.GUID.ToString(),
                    Name = part.Name ?? string.Empty,
                    Profile = part.Profile?.ProfileString ?? string.Empty,
                    Material = part.Material?.MaterialString ?? string.Empty,
                    PartMark = GetStringReportProperty(part, "PART_POS")
                });
            }
        }

        if (meshParts.Count == 0)
        {
            throw new InvalidOperationException("未能从当前选择中提取到可导出的实体网格。");
        }

        var modelPath = Path.Combine(outputDirectory, "web3d-model.gltf");
        var componentsPath = Path.Combine(outputDirectory, "web3d-components.json");
        var referencePath = Path.Combine(outputDirectory, "web3d-reference.json");
        var readmePath = Path.Combine(outputDirectory, "README-web3d-export.txt");

        File.WriteAllText(modelPath, BuildGltf(meshParts), new UTF8Encoding(false));
        WriteJson(componentsPath, components);
        WriteJson(referencePath, reference);

        var readme = new StringBuilder();
        readme.AppendLine("Tekla Web3D export completed.");
        readme.AppendLine("Model: web3d-model.gltf");
        readme.AppendLine("Components: web3d-components.json");
        readme.AppendLine("Reference: web3d-reference.json");
        readme.AppendLine("Upload the model file together with the component metadata to the Web3D viewer pipeline.");
        File.WriteAllText(readmePath, readme.ToString(), Encoding.UTF8);

        return new Web3DExportResult
        {
            ModelPath = modelPath,
            ComponentsPath = componentsPath,
            ReferencePath = referencePath,
            PartCount = meshParts.Count
        };
    }

    private static Web3DReferenceData BuildReferenceData(Model model, IReadOnlyList<Assembly> selectedAssemblies)
    {
        var grids = CollectGrids(model);
        var bounds = BuildSelectionBounds(selectedAssemblies);
        var reference = new Web3DReferenceData
        {
            ModelOrigin = new Web3DVector3(),
            Bounds = new Web3DBounds
            {
                Min = ToVector3(bounds.minX, bounds.minY, bounds.minZ),
                Max = ToVector3(bounds.maxX, bounds.maxY, bounds.maxZ)
            }
        };

        foreach (var grid in grids)
        {
            var xOffsets = ParseCoordinateSeries(grid.CoordinateX ?? string.Empty, true);
            var yOffsets = ParseCoordinateSeries(grid.CoordinateY ?? string.Empty, true);
            var zOffsets = ParseCoordinateSeries(grid.CoordinateZ ?? string.Empty, false);
            var xCoords = xOffsets.ToList();
            var yCoords = yOffsets.ToList();
            var zCoords = zOffsets.ToList();
            var xLabels = ParseLabelSeries(grid.LabelX, xCoords.Count, "X");
            var yLabels = ParseLabelSeries(grid.LabelY, yCoords.Count, "Y");
            var zLabels = ParseLabelSeries(grid.LabelZ, zCoords.Count, "Z");

            var exportedGrid = new Web3DGridSystem
            {
                Name = grid.Name ?? string.Empty,
                CoordinateX = grid.CoordinateX ?? string.Empty,
                CoordinateY = grid.CoordinateY ?? string.Empty,
                CoordinateZ = grid.CoordinateZ ?? string.Empty
            };

            if (xCoords.Count == 0 || yCoords.Count == 0)
            {
                reference.Grids.Add(exportedGrid);
                continue;
            }

            var gridMinX = xCoords.Min();
            var gridMaxX = xCoords.Max();
            var gridMinY = yCoords.Min();
            var gridMaxY = yCoords.Max();
            var baseZ = zCoords.Count > 0 ? zCoords[0] : bounds.minZ;
            var levelAnchorY = gridMaxY + (grid.ExtensionRightY / 1000.0);

            foreach (var xLine in BuildGridLines("X", xCoords, xOffsets, xLabels, gridMinY, gridMaxY, baseZ, grid.ExtensionLeftX, grid.ExtensionRightX))
            {
                exportedGrid.AxisLines.Add(xLine);
                reference.AxisLines.Add(xLine);
            }

            foreach (var yLine in BuildGridLines("Y", yCoords, yOffsets, yLabels, gridMinX, gridMaxX, baseZ, grid.ExtensionLeftY, grid.ExtensionRightY))
            {
                exportedGrid.AxisLines.Add(yLine);
                reference.AxisLines.Add(yLine);
            }

            foreach (var levelLine in BuildLevelLines(zCoords, zOffsets, zLabels, gridMinX, gridMaxX, levelAnchorY, grid.ExtensionLeftZ, grid.ExtensionRightZ))
            {
                exportedGrid.LevelLines.Add(levelLine);
                reference.LevelLines.Add(levelLine);
            }

            foreach (var point in BuildReferencePoints(xCoords, xLabels, yCoords, yLabels, zCoords, zLabels))
            {
                exportedGrid.ReferencePoints.Add(point);
                reference.ReferencePoints.Add(point);
            }

            reference.Grids.Add(exportedGrid);
        }

        return reference;
    }

    private static List<Grid> CollectGrids(Model model)
    {
        var result = new List<Grid>();
        var selector = model.GetModelObjectSelector();
        var enumerator = selector.GetAllObjectsWithType(ModelObject.ModelObjectEnum.GRID);
        while (enumerator.MoveNext())
        {
            var grid = enumerator.Current as Grid;
            if (grid != null)
            {
                result.Add(grid);
            }
        }

        return result
            .OrderBy(item => item.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Identifier.ID)
            .ToList();
    }

    private static (double minX, double maxX, double minY, double maxY, double minZ, double maxZ) BuildSelectionBounds(IReadOnlyList<Assembly> assemblies)
    {
        var minX = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var minY = double.PositiveInfinity;
        var maxY = double.NegativeInfinity;
        var minZ = double.PositiveInfinity;
        var maxZ = double.NegativeInfinity;

        foreach (var assembly in assemblies)
        {
            foreach (var part in GetAssemblyParts(assembly))
            {
                Solid solid;
                try
                {
                    solid = part.GetSolid();
                }
                catch
                {
                    continue;
                }

                if (solid == null)
                {
                    continue;
                }

                minX = Math.Min(minX, solid.MinimumPoint.X);
                minY = Math.Min(minY, solid.MinimumPoint.Y);
                minZ = Math.Min(minZ, solid.MinimumPoint.Z);
                maxX = Math.Max(maxX, solid.MaximumPoint.X);
                maxY = Math.Max(maxY, solid.MaximumPoint.Y);
                maxZ = Math.Max(maxZ, solid.MaximumPoint.Z);
            }
        }

        if (double.IsInfinity(minX))
        {
            minX = minY = minZ = -1000.0;
            maxX = maxY = maxZ = 1000.0;
        }

        return (minX / 1000.0, maxX / 1000.0, minY / 1000.0, maxY / 1000.0, minZ / 1000.0, maxZ / 1000.0);
    }

    private static List<double> ParseCoordinateSeries(string text, bool cumulativeMode)
    {
        var result = new List<double>();
        var cumulative = 0.0;
        var tokens = (text ?? string.Empty)
            .Split(new[] { ' ', '\t', ';', ',' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var token in tokens)
        {
            var trimmed = token.Trim();
            var starIndex = trimmed.IndexOf('*');
            if (starIndex > 0 &&
                int.TryParse(trimmed.Substring(0, starIndex), NumberStyles.Integer, CultureInfo.InvariantCulture, out var repeat) &&
                TryParseDistance(trimmed.Substring(starIndex + 1), out var step))
            {
                for (var i = 0; i < repeat; i++)
                {
                    if (cumulativeMode)
                    {
                        cumulative += step;
                        result.Add(cumulative / 1000.0);
                    }
                    else
                    {
                        result.Add(step / 1000.0);
                    }
                }

                continue;
            }

            if (TryParseDistance(trimmed, out var distance))
            {
                if (cumulativeMode)
                {
                    cumulative += distance;
                    result.Add(cumulative / 1000.0);
                }
                else
                {
                    result.Add(distance / 1000.0);
                }
            }
        }

        return result;
    }

    private static bool TryParseDistance(string text, out double value)
    {
        var normalized = (text ?? string.Empty).Trim().Replace("+", string.Empty);
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static List<string> ParseLabelSeries(string text, int expectedCount, string prefix)
    {
        var labels = (text ?? string.Empty)
            .Split(new[] { ' ', '\t', ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        while (labels.Count < expectedCount)
        {
            labels.Add(prefix + (labels.Count + 1).ToString(CultureInfo.InvariantCulture));
        }

        if (labels.Count > expectedCount)
        {
            labels = labels.Take(expectedCount).ToList();
        }

        return labels;
    }

    private static IEnumerable<Web3DReferenceLine> BuildGridLines(
        string axis,
        IReadOnlyList<double> coordinates,
        IReadOnlyList<double> offsets,
        IReadOnlyList<string> labels,
        double spanMin,
        double spanMax,
        double anchor,
        double extensionLeftMm,
        double extensionRightMm)
    {
        var start = spanMin - (extensionLeftMm / 1000.0);
        var end = spanMax + (extensionRightMm / 1000.0);

        for (var i = 0; i < coordinates.Count; i++)
        {
            var coord = coordinates[i];
            var label = i < labels.Count ? labels[i] : axis + (i + 1).ToString(CultureInfo.InvariantCulture);
            var value = i < offsets.Count ? offsets[i] : coord;
            yield return axis.Equals("X", StringComparison.OrdinalIgnoreCase)
                ? new Web3DReferenceLine
                {
                    Id = axis + ":" + label,
                    Kind = "axis",
                    Axis = axis,
                    Label = label,
                    Value = value,
                    Start = ToVector3(coord, start, anchor),
                    End = ToVector3(coord, end, anchor)
                }
                : new Web3DReferenceLine
                {
                    Id = axis + ":" + label,
                    Kind = "axis",
                    Axis = axis,
                    Label = label,
                    Value = value,
                    Start = ToVector3(start, coord, anchor),
                    End = ToVector3(end, coord, anchor)
                };
        }
    }

    private static IEnumerable<Web3DReferenceLine> BuildLevelLines(
        IReadOnlyList<double> coordinates,
        IReadOnlyList<double> offsets,
        IReadOnlyList<string> labels,
        double minX,
        double maxX,
        double anchorY,
        double extensionLeftMm,
        double extensionRightMm)
    {
        var start = minX - (extensionLeftMm / 1000.0);
        var end = maxX + (extensionRightMm / 1000.0);
        for (var i = 0; i < coordinates.Count; i++)
        {
            var coord = coordinates[i];
            var label = i < labels.Count ? labels[i] : "Z" + (i + 1).ToString(CultureInfo.InvariantCulture);
            var value = i < offsets.Count ? offsets[i] : coord;
            yield return new Web3DReferenceLine
            {
                Id = "Z:" + label,
                Kind = "level",
                Axis = "Z",
                Label = label,
                Value = value,
                Start = ToVector3(start, anchorY, coord),
                End = ToVector3(end, anchorY, coord)
            };
        }
    }

    private static IEnumerable<Web3DReferencePoint> BuildReferencePoints(
        IReadOnlyList<double> xCoords,
        IReadOnlyList<string> xLabels,
        IReadOnlyList<double> yCoords,
        IReadOnlyList<string> yLabels,
        IReadOnlyList<double> zCoords,
        IReadOnlyList<string> zLabels)
    {
        for (var ix = 0; ix < xCoords.Count; ix++)
        {
            for (var iy = 0; iy < yCoords.Count; iy++)
            {
                yield return new Web3DReferencePoint
                {
                    Id = xLabels[ix] + "-" + yLabels[iy],
                    Type = "grid-intersection",
                    XLabel = xLabels[ix],
                    YLabel = yLabels[iy],
                    Position = ToVector3(xCoords[ix], yCoords[iy], zCoords.Count > 0 ? zCoords[0] : 0.0)
                };
            }
        }

        for (var ix = 0; ix < xCoords.Count; ix++)
        {
            for (var iy = 0; iy < yCoords.Count; iy++)
            {
                for (var iz = 0; iz < zCoords.Count; iz++)
                {
                    yield return new Web3DReferencePoint
                    {
                        Id = xLabels[ix] + "-" + yLabels[iy] + "-" + zLabels[iz],
                        Type = "grid-level-intersection",
                        XLabel = xLabels[ix],
                        YLabel = yLabels[iy],
                        ZLabel = zLabels[iz],
                        Position = ToVector3(xCoords[ix], yCoords[iy], zCoords[iz])
                    };
                }
            }
        }
    }

    private static List<Assembly> CollectSelectedAssemblies()
    {
        var selector = new Tekla.Structures.Model.UI.ModelObjectSelector();
        var selected = selector.GetSelectedObjects();
        var assemblies = new Dictionary<int, Assembly>();

        while (selected.MoveNext())
        {
            var assembly = ResolveAssembly(selected.Current as ModelObject);
            if (assembly == null)
            {
                continue;
            }

            assemblies[assembly.Identifier.ID] = assembly;
        }

        return assemblies.Values.ToList();
    }

    private static Assembly? ResolveAssembly(ModelObject? modelObject)
    {
        if (modelObject is Assembly assembly)
        {
            return assembly;
        }

        if (modelObject is Part part)
        {
            return part.GetAssembly();
        }

        return null;
    }

    private static IEnumerable<Part> GetAssemblyParts(Assembly assembly)
    {
        var mainPart = assembly.GetMainPart() as Part;
        if (mainPart != null)
        {
            yield return mainPart;
        }

        var secondaries = assembly.GetSecondaries();
        foreach (var secondary in secondaries.OfType<Part>())
        {
            yield return secondary;
        }
    }

    private static Web3DPartMesh? TryBuildMesh(Part part)
    {
        Solid solid;
        try
        {
            solid = part.GetSolid();
        }
        catch
        {
            return null;
        }

        if (solid == null)
        {
            return null;
        }

        var positions = new List<float>();
        var normals = new List<float>();
        var indices = new List<uint>();
        var vertexIndex = 0u;

        var faceEnumerator = solid.GetFaceEnumerator();
        while (faceEnumerator.MoveNext())
        {
            var face = faceEnumerator.Current as Face;
            if (face == null)
            {
                continue;
            }

            var normal = face.Normal;
            var nx = normal != null ? (float)normal.X : 0f;
            var ny = normal != null ? (float)normal.Y : 0f;
            var nz = normal != null ? (float)normal.Z : 1f;

            var loopEnumerator = face.GetLoopEnumerator();
            while (loopEnumerator.MoveNext())
            {
                var loop = loopEnumerator.Current as Loop;
                if (loop == null)
                {
                    continue;
                }

                var loopPoints = new List<Tekla.Structures.Geometry3d.Point>();
                var vertexEnumerator = loop.GetVertexEnumerator();
                while (vertexEnumerator.MoveNext())
                {
                    var point = vertexEnumerator.Current as Tekla.Structures.Geometry3d.Point;
                    if (point != null)
                    {
                        loopPoints.Add(point);
                    }
                }

                if (loopPoints.Count < 3)
                {
                    continue;
                }

                for (var i = 1; i < loopPoints.Count - 1; i++)
                {
                    AppendTriangle(loopPoints[0], loopPoints[i], loopPoints[i + 1], nx, ny, nz, positions, normals, indices, ref vertexIndex);
                }
            }
        }

        if (positions.Count == 0 || indices.Count == 0)
        {
            return null;
        }

        return new Web3DPartMesh
        {
            TeklaGuid = part.Identifier.GUID.ToString(),
            Name = part.Name ?? string.Empty,
            Positions = positions,
            Normals = normals,
            Indices = indices
        };
    }

    private static void AppendTriangle(
        Tekla.Structures.Geometry3d.Point p0,
        Tekla.Structures.Geometry3d.Point p1,
        Tekla.Structures.Geometry3d.Point p2,
        float nx,
        float ny,
        float nz,
        List<float> positions,
        List<float> normals,
        List<uint> indices,
        ref uint vertexIndex)
    {
        AppendVertex(p0, nx, ny, nz, positions, normals);
        indices.Add(vertexIndex++);
        AppendVertex(p1, nx, ny, nz, positions, normals);
        indices.Add(vertexIndex++);
        AppendVertex(p2, nx, ny, nz, positions, normals);
        indices.Add(vertexIndex++);
    }

    private static void AppendVertex(
        Tekla.Structures.Geometry3d.Point point,
        float nx,
        float ny,
        float nz,
        List<float> positions,
        List<float> normals)
    {
        positions.Add((float)(point.X / 1000.0));
        positions.Add((float)(point.Y / 1000.0));
        positions.Add((float)(point.Z / 1000.0));
        normals.Add(nx);
        normals.Add(ny);
        normals.Add(nz);
    }

    private static string BuildGltf(IReadOnlyList<Web3DPartMesh> meshParts)
    {
        var buffer = new MemoryStream();
        var bufferViews = new List<string>();
        var accessors = new List<string>();
        var meshes = new List<string>();
        var nodes = new List<string>();

        foreach (var part in meshParts)
        {
            var positionBytes = FloatArrayToBytes(part.Positions);
            var normalBytes = FloatArrayToBytes(part.Normals);
            var useUInt32 = part.Indices.Count > ushort.MaxValue;
            var indexBytes = useUInt32 ? UIntArrayToBytes(part.Indices) : UShortArrayToBytes(part.Indices);

            var positionOffset = AppendChunk(buffer, positionBytes);
            var normalOffset = AppendChunk(buffer, normalBytes);
            var indexOffset = AppendChunk(buffer, indexBytes);

            var positionBufferViewIndex = bufferViews.Count;
            bufferViews.Add(BuildBufferViewJson(positionOffset, positionBytes.Length, 34962));
            var normalBufferViewIndex = bufferViews.Count;
            bufferViews.Add(BuildBufferViewJson(normalOffset, normalBytes.Length, 34962));
            var indexBufferViewIndex = bufferViews.Count;
            bufferViews.Add(BuildBufferViewJson(indexOffset, indexBytes.Length, 34963));

            var positionAccessorIndex = accessors.Count;
            accessors.Add(BuildPositionAccessorJson(positionBufferViewIndex, part.Positions));
            var normalAccessorIndex = accessors.Count;
            accessors.Add(BuildVectorAccessorJson(normalBufferViewIndex, part.Normals));
            var indexAccessorIndex = accessors.Count;
            accessors.Add(BuildIndexAccessorJson(indexBufferViewIndex, part.Indices.Count, useUInt32));

            var meshIndex = meshes.Count;
            meshes.Add(BuildMeshJson(part.Name, positionAccessorIndex, normalAccessorIndex, indexAccessorIndex));
            nodes.Add(BuildNodeJson(part.TeklaGuid, meshIndex));
        }

        var base64Buffer = Convert.ToBase64String(buffer.ToArray());
        var sceneNodeIndices = string.Join(", ", Enumerable.Range(0, nodes.Count).Select(value => value.ToString(CultureInfo.InvariantCulture)));

        return "{\n"
            + "  \"asset\": { \"version\": \"2.0\", \"generator\": \"TeklaSectionClassifier.Runner.Web3DExporter\" },\n"
            + "  \"scene\": 0,\n"
            + "  \"scenes\": [{ \"nodes\": [" + sceneNodeIndices + "] }],\n"
            + "  \"nodes\": [\n    " + string.Join(",\n    ", nodes) + "\n  ],\n"
            + "  \"meshes\": [\n    " + string.Join(",\n    ", meshes) + "\n  ],\n"
            + "  \"materials\": [\n    {\n"
            + "      \"name\": \"default\",\n"
            + "      \"doubleSided\": true,\n"
            + "      \"pbrMetallicRoughness\": {\n"
            + "        \"baseColorFactor\": [0.7, 0.7, 0.75, 1.0],\n"
            + "        \"metallicFactor\": 0.0,\n"
            + "        \"roughnessFactor\": 0.6\n"
            + "      }\n"
            + "    }\n  ],\n"
            + "  \"buffers\": [\n    {\n"
            + "      \"byteLength\": " + buffer.Length.ToString(CultureInfo.InvariantCulture) + ",\n"
            + "      \"uri\": \"data:application/octet-stream;base64," + base64Buffer + "\"\n"
            + "    }\n  ],\n"
            + "  \"bufferViews\": [\n    " + string.Join(",\n    ", bufferViews) + "\n  ],\n"
            + "  \"accessors\": [\n    " + string.Join(",\n    ", accessors) + "\n  ]\n"
            + "}\n";
    }

    private static int AppendChunk(MemoryStream stream, byte[] bytes)
    {
        var offset = (int)stream.Position;
        stream.Write(bytes, 0, bytes.Length);
        while (stream.Length % 4 != 0)
        {
            stream.WriteByte(0);
        }

        return offset;
    }

    private static byte[] FloatArrayToBytes(IReadOnlyList<float> values)
    {
        var bytes = new byte[values.Count * sizeof(float)];
        Buffer.BlockCopy(values.ToArray(), 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static byte[] UIntArrayToBytes(IReadOnlyList<uint> values)
    {
        var bytes = new byte[values.Count * sizeof(uint)];
        Buffer.BlockCopy(values.ToArray(), 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static byte[] UShortArrayToBytes(IReadOnlyList<uint> values)
    {
        var array = values.Select(value => checked((ushort)value)).ToArray();
        var bytes = new byte[array.Length * sizeof(ushort)];
        Buffer.BlockCopy(array, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static string BuildBufferViewJson(int offset, int length, int target)
    {
        return "{ \"buffer\": 0, \"byteOffset\": "
            + offset.ToString(CultureInfo.InvariantCulture)
            + ", \"byteLength\": "
            + length.ToString(CultureInfo.InvariantCulture)
            + ", \"target\": "
            + target.ToString(CultureInfo.InvariantCulture)
            + " }";
    }

    private static string BuildPositionAccessorJson(int bufferViewIndex, IReadOnlyList<float> positions)
    {
        var count = positions.Count / 3;
        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var minZ = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        var maxZ = double.NegativeInfinity;

        for (var i = 0; i < positions.Count; i += 3)
        {
            minX = Math.Min(minX, positions[i]);
            minY = Math.Min(minY, positions[i + 1]);
            minZ = Math.Min(minZ, positions[i + 2]);
            maxX = Math.Max(maxX, positions[i]);
            maxY = Math.Max(maxY, positions[i + 1]);
            maxZ = Math.Max(maxZ, positions[i + 2]);
        }

        return "{ \"bufferView\": "
            + bufferViewIndex.ToString(CultureInfo.InvariantCulture)
            + ", \"componentType\": 5126, \"count\": "
            + count.ToString(CultureInfo.InvariantCulture)
            + ", \"type\": \"VEC3\", \"min\": ["
            + FormatDouble(minX) + ", " + FormatDouble(minY) + ", " + FormatDouble(minZ)
            + "], \"max\": ["
            + FormatDouble(maxX) + ", " + FormatDouble(maxY) + ", " + FormatDouble(maxZ)
            + "] }";
    }

    private static string BuildVectorAccessorJson(int bufferViewIndex, IReadOnlyList<float> values)
    {
        var count = values.Count / 3;
        return "{ \"bufferView\": "
            + bufferViewIndex.ToString(CultureInfo.InvariantCulture)
            + ", \"componentType\": 5126, \"count\": "
            + count.ToString(CultureInfo.InvariantCulture)
            + ", \"type\": \"VEC3\" }";
    }

    private static string BuildIndexAccessorJson(int bufferViewIndex, int indexCount, bool useUInt32)
    {
        return "{ \"bufferView\": "
            + bufferViewIndex.ToString(CultureInfo.InvariantCulture)
            + ", \"componentType\": "
            + (useUInt32 ? "5125" : "5123")
            + ", \"count\": "
            + indexCount.ToString(CultureInfo.InvariantCulture)
            + ", \"type\": \"SCALAR\" }";
    }

    private static string BuildMeshJson(string name, int positionAccessorIndex, int normalAccessorIndex, int indexAccessorIndex)
    {
        return "{ \"name\": " + Quote(name) + ", \"primitives\": [{ \"attributes\": { \"POSITION\": "
            + positionAccessorIndex.ToString(CultureInfo.InvariantCulture)
            + ", \"NORMAL\": "
            + normalAccessorIndex.ToString(CultureInfo.InvariantCulture)
            + " }, \"indices\": "
            + indexAccessorIndex.ToString(CultureInfo.InvariantCulture)
            + ", \"material\": 0 }] }";
    }

    private static string BuildNodeJson(string teklaGuid, int meshIndex)
    {
        return "{ \"name\": " + Quote(teklaGuid) + ", \"mesh\": " + meshIndex.ToString(CultureInfo.InvariantCulture) + " }";
    }

    private static string Quote(string value)
    {
        return "\"" + EscapeJson(value) + "\"";
    }

    private static string EscapeJson(string value)
    {
        return (value ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"");
    }

    private static string FormatDouble(double value)
    {
        return value.ToString("0.########", CultureInfo.InvariantCulture);
    }

    private static Web3DVector3 ToVector3(double x, double y, double z)
    {
        return new Web3DVector3
        {
            X = x,
            Y = y,
            Z = z
        };
    }

    private static string GetStringReportProperty(Part part, string propertyName)
    {
        var value = string.Empty;
        part.GetReportProperty(propertyName, ref value);
        return value ?? string.Empty;
    }

    private static void WriteJson<T>(string path, T value)
    {
        using (var stream = File.Create(path))
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            serializer.WriteObject(stream, value);
        }
    }
}

[DataContract]
internal sealed class Web3DComponentMetadata
{
    [DataMember(Name = "teklaGuid", Order = 1)]
    public string TeklaGuid { get; set; } = string.Empty;

    [DataMember(Name = "name", Order = 2)]
    public string Name { get; set; } = string.Empty;

    [DataMember(Name = "profile", Order = 3)]
    public string Profile { get; set; } = string.Empty;

    [DataMember(Name = "material", Order = 4)]
    public string Material { get; set; } = string.Empty;

    [DataMember(Name = "partMark", Order = 5)]
    public string PartMark { get; set; } = string.Empty;
}

internal sealed class Web3DPartMesh
{
    public string TeklaGuid { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<float> Positions { get; set; } = new List<float>();
    public List<float> Normals { get; set; } = new List<float>();
    public List<uint> Indices { get; set; } = new List<uint>();
}

internal sealed class Web3DExportResult
{
    public string ModelPath { get; set; } = string.Empty;
    public string ComponentsPath { get; set; } = string.Empty;
    public string ReferencePath { get; set; } = string.Empty;
    public int PartCount { get; set; }
}

[DataContract]
internal sealed class Web3DReferenceData
{
    [DataMember(Name = "modelOrigin", Order = 1)]
    public Web3DVector3 ModelOrigin { get; set; } = new Web3DVector3();

    [DataMember(Name = "bounds", Order = 2)]
    public Web3DBounds Bounds { get; set; } = new Web3DBounds();

    [DataMember(Name = "grids", Order = 3)]
    public List<Web3DGridSystem> Grids { get; set; } = new List<Web3DGridSystem>();

    [DataMember(Name = "axisLines", Order = 4)]
    public List<Web3DReferenceLine> AxisLines { get; set; } = new List<Web3DReferenceLine>();

    [DataMember(Name = "levelLines", Order = 5)]
    public List<Web3DReferenceLine> LevelLines { get; set; } = new List<Web3DReferenceLine>();

    [DataMember(Name = "referencePoints", Order = 6)]
    public List<Web3DReferencePoint> ReferencePoints { get; set; } = new List<Web3DReferencePoint>();
}

[DataContract]
internal sealed class Web3DGridSystem
{
    [DataMember(Name = "name", Order = 1)]
    public string Name { get; set; } = string.Empty;

    [DataMember(Name = "coordinateX", Order = 2)]
    public string CoordinateX { get; set; } = string.Empty;

    [DataMember(Name = "coordinateY", Order = 3)]
    public string CoordinateY { get; set; } = string.Empty;

    [DataMember(Name = "coordinateZ", Order = 4)]
    public string CoordinateZ { get; set; } = string.Empty;

    [DataMember(Name = "axisLines", Order = 5)]
    public List<Web3DReferenceLine> AxisLines { get; set; } = new List<Web3DReferenceLine>();

    [DataMember(Name = "levelLines", Order = 6)]
    public List<Web3DReferenceLine> LevelLines { get; set; } = new List<Web3DReferenceLine>();

    [DataMember(Name = "referencePoints", Order = 7)]
    public List<Web3DReferencePoint> ReferencePoints { get; set; } = new List<Web3DReferencePoint>();
}

[DataContract]
internal sealed class Web3DReferenceLine
{
    [DataMember(Name = "id", Order = 1)]
    public string Id { get; set; } = string.Empty;

    [DataMember(Name = "kind", Order = 2)]
    public string Kind { get; set; } = string.Empty;

    [DataMember(Name = "axis", Order = 3)]
    public string Axis { get; set; } = string.Empty;

    [DataMember(Name = "label", Order = 4)]
    public string Label { get; set; } = string.Empty;

    [DataMember(Name = "value", Order = 5)]
    public double Value { get; set; }

    [DataMember(Name = "start", Order = 6)]
    public Web3DVector3 Start { get; set; } = new Web3DVector3();

    [DataMember(Name = "end", Order = 7)]
    public Web3DVector3 End { get; set; } = new Web3DVector3();
}

[DataContract]
internal sealed class Web3DReferencePoint
{
    [DataMember(Name = "id", Order = 1)]
    public string Id { get; set; } = string.Empty;

    [DataMember(Name = "type", Order = 2)]
    public string Type { get; set; } = string.Empty;

    [DataMember(Name = "xLabel", EmitDefaultValue = false, Order = 3)]
    public string XLabel { get; set; } = string.Empty;

    [DataMember(Name = "yLabel", EmitDefaultValue = false, Order = 4)]
    public string YLabel { get; set; } = string.Empty;

    [DataMember(Name = "zLabel", EmitDefaultValue = false, Order = 5)]
    public string ZLabel { get; set; } = string.Empty;

    [DataMember(Name = "position", Order = 6)]
    public Web3DVector3 Position { get; set; } = new Web3DVector3();
}

[DataContract]
internal sealed class Web3DBounds
{
    [DataMember(Name = "min", Order = 1)]
    public Web3DVector3 Min { get; set; } = new Web3DVector3();

    [DataMember(Name = "max", Order = 2)]
    public Web3DVector3 Max { get; set; } = new Web3DVector3();
}

[DataContract]
internal sealed class Web3DVector3
{
    [DataMember(Name = "x", Order = 1)]
    public double X { get; set; }

    [DataMember(Name = "y", Order = 2)]
    public double Y { get; set; }

    [DataMember(Name = "z", Order = 3)]
    public double Z { get; set; }
}
