using System.Text.RegularExpressions;
using CEM.Utils;
using MNL;
using OpenTK;

namespace CEM.Client.ZoneExporter
{
    /// <summary>
    /// Ladders
    /// </summary>
    partial class Zone2Obj
    {
        private static readonly Regex[] LadderRegex = [new("^climb([0-9:])+")];

        private class LadderPart
        {
            public required string FullName { get; set; }
            public required string BaseName { get; set; }
            public int Index { get; set; }
            public Vector3 StartPoint { get; set; }
            public Vector3 EndPoint { get; set; }
        }

        /// <summary>
        /// Extracts ladder geometry from a single model instance. This method is self-contained
        /// and processes all ladder parts found within the given 'model', applying the 'worldMatrix'
        /// to place them correctly in the zone.
        /// </summary>
        private void ExtractLadder(NiFile model, Matrix4 worldMatrix)
        {
            if (model == null)
            {
                return;
            }

            List<LadderPart> ladderPartsInThisModel = new();

            // Find all nodes within this model that represent a ladder part.
            foreach (NiObject obj in model.ObjectsByRef.Values)
            {
                if (obj is not NiAVObject avNode)
                {
                    continue;
                }

                string climbName = FindMatchRegex(avNode, LadderRegex);
                if (string.IsNullOrEmpty(climbName))
                {
                    continue;
                }

                // Extract vertices from the geometry node, transformed by the world matrix.
                Vector3[] vertices = null;
                Triangle[] triangles = null;
                TryExtractTriShape(obj, worldMatrix, false, false, ref vertices, ref triangles);
                if (vertices == null)
                {
                    TryExtractTriStrips(obj, worldMatrix, false, false, ref vertices, ref triangles);
                }

                if (vertices == null || vertices.Length == 0)
                {
                    continue;
                }

                // Calculate the bottom and top points for this specific part.
                float minZ = vertices.Min(v => v.Z);
                float maxZ = vertices.Max(v => v.Z);
                float midZ = minZ + (maxZ - minZ) / 2;

                List<Vector3> minVecs = vertices.Where(v => v.Z < midZ).ToList();
                List<Vector3> maxVecs = vertices.Where(v => v.Z >= midZ).ToList();

                if (minVecs.Count == 0 || maxVecs.Count == 0)
                {
                    Log.Warn("Ladder part '{0}' in model is malformed (missing top or bottom vertices); ignoring.", climbName);
                    continue;
                }

                // Parse the name to get the base name and index for sorting.
                string baseName = climbName;
                int index = 0;
                int colonIndex = climbName.IndexOf(':');
                if (colonIndex != -1)
                {
                    baseName = climbName[..colonIndex];

                    if (!int.TryParse(climbName.AsSpan(colonIndex + 1), out index))
                    {
                        Log.Warn("Could not parse index for ladder part '{0}'; treating as index 0.", climbName);
                        index = 0;
                    }
                }

                // Add the processed part to our list for this model instance.
                ladderPartsInThisModel.Add(new LadderPart
                {
                    FullName = climbName,
                    BaseName = baseName,
                    Index = index,
                    StartPoint = Average(minVecs),
                    EndPoint = Average(maxVecs),
                });
            }

            if (ladderPartsInThisModel.Count == 0)
            {
                return;
            }

            IEnumerable<IGrouping<string, LadderPart>> groupedLadders = ladderPartsInThisModel.GroupBy(p => p.BaseName);

            foreach (IGrouping<string, LadderPart> group in groupedLadders)
            {
                List<LadderPart> sortedParts = group.OrderBy(p => p.Index).ToList();
                Vector3 representativePoint = sortedParts[0].StartPoint;

                Log.Debug($"Processed ladder '{group.Key}' ({sortedParts.Count} part(s)) at location {representativePoint}");

                // Create the chain of off-mesh connections for this ladder instance.
                for (int i = 0; i < sortedParts.Count; i++)
                {
                    LadderPart currentPart = sortedParts[i];

                    // Connection for climbing this specific part.
                    GeomSetWriter.WriteOffMeshConnection(currentPart.StartPoint, currentPart.EndPoint, true, GeomSetWriter.eAreas.Jump, GeomSetWriter.eFlags.Jump);

                    // If this isn't the last part, connect its top to the next part's bottom.
                    if (i < sortedParts.Count - 1)
                    {
                        LadderPart nextPart = sortedParts[i + 1];
                        GeomSetWriter.WriteOffMeshConnection(currentPart.EndPoint, nextPart.StartPoint, true, GeomSetWriter.eAreas.Jump, GeomSetWriter.eFlags.Jump);
                    }
                }
            }
        }
    }
}
