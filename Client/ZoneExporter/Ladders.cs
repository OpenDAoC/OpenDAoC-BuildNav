using System.Text.RegularExpressions;
using CEM.Utils;
using CEM.World;
using MNL;
using OpenTK;

namespace CEM.Client.ZoneExporter
{
    /// <summary>
    /// Ladder geometry extraction. Off-mesh links are placed in a second pass after the navmesh exists.
    /// </summary>
    partial class Zone2Obj
    {
        private static readonly Regex[] LadderRegex = [new("^climb([0-9:])+")];

        private const float EXTREMITY_BAND_RATIO = 0.05f;
        private const float EXTREMITY_BAND_MIN = 32f;
        private const float INSTANCE_CLUSTER_RADIUS = 192f;

        private class LadderPart
        {
            public required string FullName { get; set; }
            public required string BaseName { get; set; }
            public required Vector3[] Vertices { get; set; }
            public int Index { get; set; }
            public Vector3 BottomAnchor { get; set; }
            public Vector3 TopAnchor { get; set; }
        }

        /// <summary>
        /// Extracts ladder geometry from a single model instance into LadderDefinitions.
        /// </summary>
        private void ExtractLadder(NiFile model, Matrix4 worldMatrix)
        {
            if (model == null)
                return;

            List<LadderPart> ladderPartsInThisModel = new();

            foreach (NiObject obj in model.ObjectsByRef.Values)
            {
                if (obj is not NiAVObject avNode)
                    continue;

                string climbName = FindMatchRegex(avNode, LadderRegex);
                if (string.IsNullOrEmpty(climbName))
                    continue;

                Vector3[] vertices = null;
                Triangle[] triangles = null;
                TryExtractTriShape(obj, worldMatrix, false, false, ref vertices, ref triangles);
                if (vertices == null)
                    TryExtractTriStrips(obj, worldMatrix, false, false, ref vertices, ref triangles);

                if (vertices == null || vertices.Length == 0)
                    continue;

                if (!TryComputeAnchors(vertices, out Vector3 bottomAnchor, out Vector3 topAnchor))
                {
                    Log.Warn("Ladder part '{0}' in model is malformed (missing top or bottom vertices); ignoring.", climbName);
                    continue;
                }

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

                ladderPartsInThisModel.Add(new()
                {
                    FullName = climbName,
                    BaseName = baseName,
                    Index = index,
                    Vertices = vertices,
                    BottomAnchor = bottomAnchor,
                    TopAnchor = topAnchor,
                });
            }

            if (ladderPartsInThisModel.Count == 0)
                return;

            foreach (IGrouping<string, LadderPart> nameGroup in ladderPartsInThisModel.GroupBy(p => p.BaseName))
            {
                foreach (List<LadderPart> instance in ClusterLadderInstances(nameGroup))
                    LadderDefinitions.Add(BuildLadderDefinition(nameGroup.Key, instance));
            }
        }

        private static LadderDefinition BuildLadderDefinition(string baseName, List<LadderPart> sortedParts)
        {
            Vector3[] allVertices = sortedParts.SelectMany(p => p.Vertices).ToArray();
            float minZ = allVertices.Min(v => v.Z);
            float maxZ = allVertices.Max(v => v.Z);
            float centerX = allVertices.Average(v => v.X);
            float centerY = allVertices.Average(v => v.Y);

            ComputeHorizontalFrame(allVertices, out Vector2 tangent, out Vector2 thinAxis);

            List<float> seedHeights = new();
            foreach (LadderPart part in sortedParts)
            {
                seedHeights.Add(part.BottomAnchor.Z);
                seedHeights.Add(part.TopAnchor.Z);
            }

            seedHeights = seedHeights.Distinct().OrderBy(z => z).ToList();

            Vector3 bottom = sortedParts.OrderBy(p => p.BottomAnchor.Z).First().BottomAnchor;
            Vector3 top = sortedParts.OrderByDescending(p => p.TopAnchor.Z).First().TopAnchor;

            string name = $"{baseName}@({bottom.X:F0},{bottom.Y:F0},{bottom.Z:F0})";
            Log.Debug($"Collected ladder '{name}' ({sortedParts.Count} part(s), Z {minZ:F0}..{maxZ:F0}) at {bottom}");

            return new()
            {
                Name = name,
                Bottom = bottom,
                Top = top,
                MinZ = minZ,
                MaxZ = maxZ,
                CenterXY = new(centerX, centerY),
                Tangent = tangent,
                ThinAxis = thinAxis,
                SeedHeights = seedHeights.ToArray(),
            };
        }

        /// <summary>
        /// Splits parts sharing a base name into separate physical ladder instances by XY proximity.
        /// </summary>
        private static IEnumerable<List<LadderPart>> ClusterLadderInstances(IEnumerable<LadderPart> parts)
        {
            float clusterRadiusSq = INSTANCE_CLUSTER_RADIUS * INSTANCE_CLUSTER_RADIUS;
            List<LadderPart> remaining = parts.OrderBy(p => p.Index).ToList();

            while (remaining.Count > 0)
            {
                LadderPart seed = remaining[0];
                remaining.RemoveAt(0);
                List<LadderPart> cluster = [seed];

                for (int i = remaining.Count - 1; i >= 0; i--)
                {
                    LadderPart candidate = remaining[i];
                    bool nearCluster = cluster.Any(part =>
                        HorizontalDistanceSquared(part.BottomAnchor, candidate.BottomAnchor) <= clusterRadiusSq ||
                        HorizontalDistanceSquared(part.TopAnchor, candidate.TopAnchor) <= clusterRadiusSq ||
                        HorizontalDistanceSquared(part.TopAnchor, candidate.BottomAnchor) <= clusterRadiusSq ||
                        HorizontalDistanceSquared(part.BottomAnchor, candidate.TopAnchor) <= clusterRadiusSq);

                    if (nearCluster)
                    {
                        cluster.Add(candidate);
                        remaining.RemoveAt(i);
                    }
                }

                yield return cluster.OrderBy(p => p.Index).ToList();
            }
        }

        private static void ComputeHorizontalFrame(Vector3[] vertices, out Vector2 tangent, out Vector2 thinAxis)
        {
            float minX = vertices.Min(v => v.X);
            float maxX = vertices.Max(v => v.X);
            float minY = vertices.Min(v => v.Y);
            float maxY = vertices.Max(v => v.Y);

            float extentX = maxX - minX;
            float extentY = maxY - minY;

            // Approximate: longer horizontal AABB side is tangent (along wall), shorter is thin axis (into wall).
            if (extentX >= extentY)
            {
                tangent = Vector2.UnitX;
                thinAxis = Vector2.UnitY;
            }
            else
            {
                tangent = Vector2.UnitY;
                thinAxis = Vector2.UnitX;
            }

            // Refine with covariance PCA on XY for better orientation on rotated ladders.
            float cx = vertices.Average(v => v.X);
            float cy = vertices.Average(v => v.Y);
            float xx = 0, xy = 0, yy = 0;
            foreach (Vector3 v in vertices)
            {
                float dx = v.X - cx;
                float dy = v.Y - cy;
                xx += dx * dx;
                xy += dx * dy;
                yy += dy * dy;
            }

            // Dominant eigenvector of [[xx,xy],[xy,yy]]
            float trace = xx + yy;
            float det = xx * yy - xy * xy;
            float disc = Math.Max(0f, trace * trace * 0.25f - det);
            float lambda1 = trace * 0.5f + MathF.Sqrt(disc);

            Vector2 major;
            if (Math.Abs(xy) > 1e-3f)
                major = new(lambda1 - yy, xy);
            else if (xx >= yy)
                major = Vector2.UnitX;
            else
                major = Vector2.UnitY;

            float majorLen = major.Length;
            if (majorLen > 1e-6f)
            {
                tangent = major / majorLen;
                thinAxis = new(-tangent.Y, tangent.X);
            }
        }

        private static bool TryComputeAnchors(Vector3[] vertices, out Vector3 bottomAnchor, out Vector3 topAnchor)
        {
            float minZ = vertices.Min(v => v.Z);
            float maxZ = vertices.Max(v => v.Z);
            float zRange = maxZ - minZ;
            if (zRange < 1f)
            {
                bottomAnchor = default;
                topAnchor = default;
                return false;
            }

            float bandHeight = Math.Max(zRange * EXTREMITY_BAND_RATIO, EXTREMITY_BAND_MIN);
            List<Vector3> bottomVerts = vertices.Where(v => v.Z <= minZ + bandHeight).ToList();
            List<Vector3> topVerts = vertices.Where(v => v.Z >= maxZ - bandHeight).ToList();

            if (bottomVerts.Count == 0 || topVerts.Count == 0)
            {
                bottomAnchor = default;
                topAnchor = default;
                return false;
            }

            bottomAnchor = AverageVectors(bottomVerts);
            topAnchor = AverageVectors(topVerts);
            return true;
        }

        private static Vector3 AverageVectors(IEnumerable<Vector3> vecs)
        {
            Vector3 sum = Vector3.Zero;
            int cnt = 0;
            foreach (Vector3 v in vecs)
            {
                cnt++;
                sum += v;
            }

            return sum / (cnt == 0 ? 1f : cnt);
        }

        private static float HorizontalDistanceSquared(Vector3 a, Vector3 b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }
    }
}
