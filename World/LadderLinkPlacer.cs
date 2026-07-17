using CEM.Utils;
using OpenDAoC.Pathing;
using Numerics = System.Numerics;

namespace CEM.World
{
    /// <summary>
    /// Second-pass ladder off-mesh placement: free-side half-space snaps + sparse left/center/right columns.
    /// </summary>
    internal static class LadderLinkPlacer
    {
        // Tunables (game units).

        // Distance outward from the ladder's center plane to start probing for a walkable surface.
        // Prevents snapping to the wall the ladder is attached to.
        private const float LANDING_OFFSET = 24f;

        // Distance left/right from the center column to cast secondary probes.
        // Ensures wide ladders allow AI to mount from the sides.
        private const float COLUMN_OFFSET = 32f;

        // The dimensions of the bounding box given to Detour's FindClosestPointInBox.
        // Depth: How far "forward/backward" to search around the probe point.
        private const float BOX_DEPTH = 36f;
        // Half-Width: How far left/right to search.
        private const float BOX_HALF_WIDTH = 20f;
        // Half-Height: How far up/down Detour is allowed to look for a poly.
        private const float BOX_HALF_HEIGHT = 24f;

        // Vertical distance between test probes when scanning a ladder's height.
        private const float Z_SAMPLE_STEP = 48f;

        // If two generated "floors" (landings) are within this vertical distance, they are merged into a single floor.
        // Prevents generating micro-hops.
        private const float Z_MERGE_DISTANCE = 80f;

        // Distance behind the ladder plane that a snapped navmesh point is allowed to be.
        // Uses a half-space check to strictly prevent generating links through solid walls.
        private const float HALF_SPACE_TOLERANCE = 4f;

        // Absolute maximum 3D distance between our ideal probe point and Detour's snapped point.
        // If the closest navmesh poly is further than this, the probe is discarded.
        private const float MAX_SNAP_DISTANCE = 64f;

        // If a direct vertical column connection fails, allow a diagonal jump to another column up to this horizontal distance.
        private const float CROSS_COLUMN_FALLBACK_MAX_HORIZ = 48f;

        private static readonly float[] ColumnOffsets = [-COLUMN_OFFSET, 0f, COLUMN_OFFSET];

        private static readonly EDtPolyFlags[] WalkFilters =
        [
            EDtPolyFlags.All ^ EDtPolyFlags.Disabled ^ EDtPolyFlags.Jump,
            0
        ];

        public static List<OffMeshLink> Place(string navMeshPath, IEnumerable<LadderDefinition> ladders)
        {
            List<OffMeshLink> links = new();

            if (!DetourNavMesh.TryProbeNativeLibrary(out Exception? probeError))
            {
                Log.Error($"Detour library not available for ladder placement: {probeError?.Message}");
                return links;
            }

            using DetourNavMesh? mesh = DetourNavMesh.TryLoad(navMeshPath);
            if (mesh == null)
            {
                Log.Error($"Failed to load navmesh for ladder placement: {navMeshPath}");
                return links;
            }

            using DetourNavMeshQuery query = mesh.CreateQuery();

            foreach (LadderDefinition ladder in ladders)
            {
                int before = links.Count;
                PlaceLadder(query, ladder, links);
                Log.Normal($"Ladder '{ladder.Name}': emitted {links.Count - before} off-mesh link(s)");
            }

            return links;
        }

        private static void PlaceLadder(DetourNavMeshQuery query, LadderDefinition ladder, List<OffMeshLink> links)
        {
            List<Numerics.Vector2> freeSides = ResolveFreeSides(query, ladder);
            if (freeSides.Count == 0)
            {
                Log.Warn($"Ladder '{ladder.Name}': no free-side walkable mesh found; skipping");
                return;
            }

            foreach (Numerics.Vector2 outward in freeSides)
            {
                PlaceLadderSide(query, ladder, outward, links);
            }
        }

        private static List<Numerics.Vector2> ResolveFreeSides(DetourNavMeshQuery query, LadderDefinition ladder)
        {
            Numerics.Vector2 thin = ToNum(ladder.ThinAxis);
            if (thin.LengthSquared() < 1e-8f)
                thin = Numerics.Vector2.UnitX;
            else
                thin = Numerics.Vector2.Normalize(thin);

            Numerics.Vector2 center = ToNum(ladder.CenterXY);

            // Probe bottom and top to figure out which side is open.
            bool pos = ProbeSide(query, center, thin, ladder.Bottom.Z) || ProbeSide(query, center, thin, ladder.Top.Z);
            bool neg = ProbeSide(query, center, -thin, ladder.Bottom.Z) || ProbeSide(query, center, -thin, ladder.Top.Z);

            List<Numerics.Vector2> sides = new();
            if (pos)
                sides.Add(thin);
            if (neg)
                sides.Add(-thin);

            if (sides.Count == 0)
            {
                // Still try geometric thin axis (may fail later at snap).
                sides.Add(thin);
                Log.Warn($"Ladder '{ladder.Name}': free-side probe failed both sides; trying +thinAxis");
            }

            return sides;
        }

        private static bool ProbeSide(DetourNavMeshQuery query, Numerics.Vector2 center, Numerics.Vector2 outward, float z)
        {
            Numerics.Vector3 sample = new(
                center.X + outward.X * LANDING_OFFSET,
                center.Y + outward.Y * LANDING_OFFSET,
                z);

            Numerics.Vector3? hit = SnapFreeSide(
                query,
                sample,
                center,
                outward,
                sample);

            return hit.HasValue;
        }

        private static void PlaceLadderSide(
            DetourNavMeshQuery query,
            LadderDefinition ladder,
            Numerics.Vector2 outward,
            List<OffMeshLink> links)
        {
            Numerics.Vector2 tangent = ToNum(ladder.Tangent);
            if (tangent.LengthSquared() < 1e-8f)
                tangent = new(-outward.Y, outward.X);
            else
                tangent = Numerics.Vector2.Normalize(tangent);

            // Ensure tangent is orthogonal-ish to outward.
            float proj = Numerics.Vector2.Dot(tangent, outward);
            tangent -= outward * proj;
            if (tangent.LengthSquared() > 1e-8f)
                tangent = Numerics.Vector2.Normalize(tangent);
            else
                tangent = new(-outward.Y, outward.X);

            Numerics.Vector2 center = ToNum(ladder.CenterXY);
            List<float> sampleZs = BuildSampleHeights(ladder);

            // floors[floorIndex][columnIndex] = optional landing
            List<Numerics.Vector3?[]> floors = new();

            foreach (float z in sampleZs)
            {
                Numerics.Vector3?[] columns = new Numerics.Vector3?[ColumnOffsets.Length];
                bool any = false;

                for (int c = 0; c < ColumnOffsets.Length; c++)
                {
                    float s = ColumnOffsets[c];
                    Numerics.Vector3 sample = new(
                        center.X + outward.X * LANDING_OFFSET + tangent.X * s,
                        center.Y + outward.Y * LANDING_OFFSET + tangent.Y * s,
                        z);

                    Numerics.Vector3? hit = SnapFreeSide(query, sample, center, outward, sample);
                    if (hit.HasValue)
                    {
                        columns[c] = hit.Value;
                        any = true;
                    }
                }

                if (any)
                    floors.Add(columns);
            }

            // Merge floors that are close in Z (per-column average height).
            floors = MergeFloors(floors);

            if (floors.Count < 2)
            {
                Log.Debug($"Ladder '{ladder.Name}' side ({outward.X:F2},{outward.Y:F2}): only {floors.Count} landing floor(s); need >= 2");
                return;
            }

            int emitted = 0;
            for (int i = 0; i < floors.Count - 1; i++)
            {
                Numerics.Vector3?[] lower = floors[i];
                Numerics.Vector3?[] upper = floors[i + 1];

                bool hopEmitted = false;
                for (int c = 0; c < ColumnOffsets.Length; c++)
                {
                    if (lower[c].HasValue && upper[c].HasValue)
                    {
                        links.Add(new(ToOpenTK(lower[c]!.Value), ToOpenTK(upper[c]!.Value)));
                        emitted++;
                        hopEmitted = true;
                    }
                }

                // Pair any free-side landings if no matching column pair.
                if (!hopEmitted)
                {
                    if (TryCrossColumnFallback(lower, upper, out Numerics.Vector3 a, out Numerics.Vector3 b))
                    {
                        links.Add(new(ToOpenTK(a), ToOpenTK(b)));
                        emitted++;
                    }
                }
            }

            Log.Debug($"Ladder '{ladder.Name}' side ({outward.X:F2},{outward.Y:F2}): {floors.Count} floor(s), {emitted} link(s)");
        }

        private static bool TryCrossColumnFallback(
            Numerics.Vector3?[] lower,
            Numerics.Vector3?[] upper,
            out Numerics.Vector3 a,
            out Numerics.Vector3 b)
        {
            a = default;
            b = default;
            float best = float.MaxValue;
            bool found = false;

            for (int i = 0; i < lower.Length; i++)
            {
                if (!lower[i].HasValue)
                    continue;

                for (int j = 0; j < upper.Length; j++)
                {
                    if (!upper[j].HasValue)
                        continue;

                    Numerics.Vector3 p = lower[i]!.Value;
                    Numerics.Vector3 q = upper[j]!.Value;
                    float dx = p.X - q.X;
                    float dy = p.Y - q.Y;
                    float dist = MathF.Sqrt(dx * dx + dy * dy);
                    if (dist <= CROSS_COLUMN_FALLBACK_MAX_HORIZ && dist < best)
                    {
                        best = dist;
                        a = p;
                        b = q;
                        found = true;
                    }
                }
            }

            return found;
        }

        private static List<float> BuildSampleHeights(LadderDefinition ladder)
        {
            HashSet<float> heights = [ladder.MinZ, ladder.MaxZ, ladder.Bottom.Z, ladder.Top.Z, .. ladder.SeedHeights];

            for (float z = ladder.MinZ; z <= ladder.MaxZ; z += Z_SAMPLE_STEP)
                heights.Add(z);

            return heights.OrderBy(z => z).ToList();
        }

        private static Numerics.Vector3? SnapFreeSide(
            DetourNavMeshQuery query,
            Numerics.Vector3 sample,
            Numerics.Vector2 ladderCenter,
            Numerics.Vector2 outward,
            Numerics.Vector3 reference)
        {
            // Box entirely on free side of ladder plane.
            Numerics.Vector3 boxCenter = new(
                sample.X + outward.X * (BOX_DEPTH * 0.5f),
                sample.Y + outward.Y * (BOX_DEPTH * 0.5f),
                sample.Z);

            // Axis-aligned extents that cover the free-side slab. Slightly larger than ideal oriented box
            // but combined with half-space reject this avoids behind-wall snaps.
            Numerics.Vector3 extents = new(
                Math.Max(BOX_HALF_WIDTH, BOX_DEPTH * 0.5f + 4f),
                Math.Max(BOX_HALF_WIDTH, BOX_DEPTH * 0.5f + 4f),
                BOX_HALF_HEIGHT);

            Numerics.Vector3? hit = query.FindClosestPointInBox(boxCenter, extents, reference, WalkFilters);
            if (!hit.HasValue)
                return null;

            Numerics.Vector3 p = hit.Value;
            float side = (p.X - ladderCenter.X) * outward.X + (p.Y - ladderCenter.Y) * outward.Y;
            if (side < -HALF_SPACE_TOLERANCE)
                return null;

            float dx = p.X - sample.X;
            float dy = p.Y - sample.Y;
            float dz = p.Z - sample.Z;
            if (dx * dx + dy * dy + dz * dz > MAX_SNAP_DISTANCE * MAX_SNAP_DISTANCE)
                return null;

            return p;
        }

        private static List<Numerics.Vector3?[]> MergeFloors(List<Numerics.Vector3?[]> floors)
        {
            if (floors.Count == 0)
                return floors;

            // Sort by average Z of present columns.
            floors = floors.OrderBy(AverageZ).ToList();

            List<Numerics.Vector3?[]> merged = new();
            Numerics.Vector3?[] current = (Numerics.Vector3?[])floors[0].Clone();
            float currentZ = AverageZ(current);

            for (int i = 1; i < floors.Count; i++)
            {
                Numerics.Vector3?[] next = floors[i];
                float nextZ = AverageZ(next);

                if (Math.Abs(nextZ - currentZ) <= Z_MERGE_DISTANCE)
                {
                    // Prefer non-null; if both set, keep closer-to-center of cluster (first wins).
                    for (int c = 0; c < current.Length; c++)
                    {
                        if (!current[c].HasValue && next[c].HasValue)
                            current[c] = next[c];
                    }

                    // DO NOT update currentZ here. Keep it anchored to the first floor in this cluster.
                }
                else
                {
                    merged.Add(current);
                    current = (Numerics.Vector3?[])next.Clone();
                    currentZ = nextZ;
                }
            }

            merged.Add(current);
            return merged;
        }

        private static float AverageZ(Numerics.Vector3?[] columns)
        {
            float sum = 0;
            int n = 0;
            foreach (Numerics.Vector3? c in columns)
            {
                if (!c.HasValue)
                    continue;
                sum += c.Value.Z;
                n++;
            }

            return n == 0 ? 0f : sum / n;
        }

        private static Numerics.Vector2 ToNum(OpenTK.Vector2 v)
        {
            return new(v.X, v.Y);
        }

        private static OpenTK.Vector3 ToOpenTK(Numerics.Vector3 v)
        {
            return new(v.X, v.Y, v.Z);
        }

        public readonly record struct OffMeshLink(OpenTK.Vector3 Start, OpenTK.Vector3 End);
    }
}
