using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;
using UnityEngine.AI;

namespace Killer_Frequency_Access;

/// <summary>
/// The scene's NavMesh polygons and their neighbour links, generated from the rip's NavMesh assets by
/// tools\rip\gen_navmesh.py (the game strips NavMeshPath and NavMesh.CalculateTriangulation, so the
/// mod cannot read them at runtime). Routes are an A* over polygons, straightened with the funnel
/// (string pulling) through the shared edges.
///
/// Why: the 0.5 m lattice search over NavMesh.SamplePosition failed between floors. The stairs are
/// ~40 degree runs of 0.3 m sloped strips, so each lattice step rises ~0.42 m, at the edge of the 0.5 m
/// sample radius (2026-11-22 run: "no NavMesh route" after the whole floor was searched, 3,800 nodes).
/// The rip's NavMesh_0 connects basement, ground, the 1.0 m level and the first floor in one region.
///
/// Which graph is live is not taken from scene names: each graph is checked against the live NavMesh
/// with NavMesh.SamplePosition on its own polygon centres, and the one that matches is used.
/// </summary>
internal sealed class NavGraph
{
    private const float GridCell = 2f;

    public readonly string Name;
    private readonly Vector3[][] _polys;
    private readonly (int Other, Vector3 A, Vector3 B)[][] _links;
    private readonly Dictionary<(int, int), List<int>> _grid = new();

    private NavGraph(string name, Vector3[][] polys, (int, Vector3, Vector3)[][] links)
    {
        Name = name;
        _polys = polys;
        _links = links;
        for (var i = 0; i < polys.Length; i++)
        {
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var v in polys[i])
            {
                minX = Math.Min(minX, v.x); maxX = Math.Max(maxX, v.x);
                minZ = Math.Min(minZ, v.z); maxZ = Math.Max(maxZ, v.z);
            }

            for (var gx = Cell(minX); gx <= Cell(maxX); gx++)
            {
                for (var gz = Cell(minZ); gz <= Cell(maxZ); gz++)
                {
                    if (!_grid.TryGetValue((gx, gz), out var list))
                    {
                        _grid[(gx, gz)] = list = new List<int>();
                    }

                    list.Add(i);
                }
            }
        }
    }

    public int PolygonCount => _polys.Length;

    private static int Cell(float v) => Mathf.FloorToInt(v / GridCell);

    public static List<NavGraph> LoadAll()
    {
        var result = new List<NavGraph>();
        var assembly = typeof(NavGraph).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith("Nav.", StringComparison.Ordinal) || !resource.EndsWith(".navbin.gz", StringComparison.Ordinal))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var reader = new BinaryReader(gzip);
            if (new string(reader.ReadChars(4)) != "KFNV" || reader.ReadInt32() != 1)
            {
                MelonLoader.MelonLogger.Warning($"[nav] {resource}: unknown format");
                continue;
            }

            var count = reader.ReadInt32();
            var polys = new Vector3[count][];
            var links = new (int, Vector3, Vector3)[count][];
            for (var i = 0; i < count; i++)
            {
                var vc = reader.ReadByte();
                polys[i] = new Vector3[vc];
                for (var k = 0; k < vc; k++)
                {
                    polys[i][k] = ReadVector(reader);
                }

                var lc = reader.ReadUInt16();
                links[i] = new (int, Vector3, Vector3)[lc];
                for (var k = 0; k < lc; k++)
                {
                    links[i][k] = (reader.ReadInt32(), ReadVector(reader), ReadVector(reader));
                }
            }

            var name = resource["Nav.".Length..^".navbin.gz".Length];
            result.Add(new NavGraph(name, polys, links));
        }

        return result;
    }

    private static Vector3 ReadVector(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    /// <summary>
    /// Checks the graph against the live NavMesh: polygon centres spread over the graph must each have
    /// a NavMesh point within 5 cm. Returns how many of the samples matched.
    /// </summary>
    public int CountLiveMatches(int samples)
    {
        var matched = 0;
        for (var s = 0; s < samples; s++)
        {
            var centre = Centre(_polys[(int)((long)s * _polys.Length / samples)]);
            if (NavMesh.SamplePosition(centre, out var hit, 0.2f, NavMesh.AllAreas) && Vector3.Distance(hit.position, centre) < 0.05f)
            {
                matched++;
            }
        }

        return matched;
    }

    private static Vector3 Centre(Vector3[] poly)
    {
        var sum = Vector3.zero;
        foreach (var v in poly)
        {
            sum += v;
        }

        return sum / poly.Length;
    }

    /// <summary>The height of every polygon (every floor) whose x/z covers the point.</summary>
    public void FloorHeightsAt(float x, float z, List<float> heights)
    {
        if (!_grid.TryGetValue((Cell(x), Cell(z)), out var candidates))
        {
            return;
        }

        foreach (var i in candidates)
        {
            if (TryHeightInside(_polys[i], x, z, out var y) && !heights.Exists(h => Math.Abs(h - y) < 0.1f))
            {
                heights.Add(y);
            }
        }
    }

    /// <summary>The polygon under a NavMesh point: inside it in x/z, with the closest height (within 0.5 m).</summary>
    public int Locate(Vector3 point)
    {
        if (!_grid.TryGetValue((Cell(point.x), Cell(point.z)), out var candidates))
        {
            return -1;
        }

        var best = -1;
        var bestDy = 0.5f;
        var nearest = -1;
        var nearestScore = float.MaxValue;
        foreach (var i in candidates)
        {
            var poly = _polys[i];
            if (TryHeightInside(poly, point.x, point.z, out var y))
            {
                var dy = Math.Abs(y - point.y);
                if (dy < bestDy)
                {
                    best = i;
                    bestDy = dy;
                }

                continue;
            }

            // SamplePosition can return a point on a polygon's edge; accept the closest edge within 10 cm.
            var planar = PlanarDistanceToPolygon(poly, point, out var edgeY);
            var height = Math.Abs(edgeY - point.y);
            if (planar < 0.1f && height < 0.5f && planar + height < nearestScore)
            {
                nearest = i;
                nearestScore = planar + height;
            }
        }

        return best >= 0 ? best : nearest;
    }

    private static bool TryHeightInside(Vector3[] poly, float x, float z, out float y)
    {
        var a = poly[0];
        for (var k = 1; k < poly.Length - 1; k++)
        {
            var b = poly[k];
            var c = poly[k + 1];
            var d = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
            if (Math.Abs(d) < 1e-9f)
            {
                continue;
            }

            var w1 = ((b.z - c.z) * (x - c.x) + (c.x - b.x) * (z - c.z)) / d;
            var w2 = ((c.z - a.z) * (x - c.x) + (a.x - c.x) * (z - c.z)) / d;
            var w3 = 1f - w1 - w2;
            if (w1 >= -1e-4f && w2 >= -1e-4f && w3 >= -1e-4f)
            {
                y = w1 * a.y + w2 * b.y + w3 * c.y;
                return true;
            }
        }

        y = 0f;
        return false;
    }

    private static float PlanarDistanceToPolygon(Vector3[] poly, Vector3 p, out float y)
    {
        var best = float.MaxValue;
        y = 0f;
        for (var k = 0; k < poly.Length; k++)
        {
            var a = poly[k];
            var b = poly[(k + 1) % poly.Length];
            var ab = new Vector2(b.x - a.x, b.z - a.z);
            var ap = new Vector2(p.x - a.x, p.z - a.z);
            var t = ab.sqrMagnitude < 1e-9f ? 0f : Mathf.Clamp01(Vector2.Dot(ap, ab) / ab.sqrMagnitude);
            var d = (ap - ab * t).magnitude;
            if (d < best)
            {
                best = d;
                y = Mathf.Lerp(a.y, b.y, t);
            }
        }

        return best;
    }

    /// <summary>
    /// Route between two NavMesh points. Corners go into <paramref name="corners"/>, start excluded,
    /// goal included. False when either point is on no polygon or the polygons are not connected.
    /// </summary>
    public bool TryRoute(Vector3 start, Vector3 goal, List<Vector3> corners, out int searched, out string failure)
    {
        corners.Clear();
        searched = 0;
        failure = string.Empty;
        var from = Locate(start);
        var to = Locate(goal);
        if (from < 0 || to < 0)
        {
            failure = from < 0 ? $"start {start} is on no polygon" : $"goal {goal} is on no polygon";
            return false;
        }

        var path = FindPolygonPath(from, to, start, goal, out searched);
        if (path == null)
        {
            failure = "polygons not connected";
            return false;
        }

        StringPull(path, start, goal, corners);
        return true;
    }

    private List<int>? FindPolygonPath(int from, int to, Vector3 start, Vector3 goal, out int searched)
    {
        searched = 0;
        if (from == to)
        {
            return new List<int> { from };
        }

        var cost = new Dictionary<int, float> { [from] = 0f };
        var at = new Dictionary<int, Vector3> { [from] = start };
        var parent = new Dictionary<int, int>();
        var open = new SortedSet<(float F, int Order, int Poly)>();
        var order = 0;
        open.Add((Vector3.Distance(start, goal), order++, from));
        while (open.Count > 0)
        {
            var current = open.Min;
            open.Remove(current);
            var poly = current.Poly;
            var g = cost[poly];
            if (current.F > g + Vector3.Distance(at[poly], goal) + 1e-4f)
            {
                continue; // stale entry
            }

            searched++;
            if (poly == to)
            {
                var path = new List<int> { to };
                while (path[^1] != from)
                {
                    path.Add(parent[path[^1]]);
                }

                path.Reverse();
                return path;
            }

            var here = at[poly];
            foreach (var (other, a, b) in _links[poly])
            {
                var mid = (a + b) * 0.5f;
                var next = g + Vector3.Distance(here, mid);
                if (cost.TryGetValue(other, out var known) && known <= next)
                {
                    continue;
                }

                cost[other] = next;
                at[other] = mid;
                parent[other] = poly;
                open.Add((next + Vector3.Distance(mid, goal), order++, other));
            }
        }

        return null;
    }

    private static float TriArea2(Vector3 a, Vector3 b, Vector3 c)
    {
        var ax = b.x - a.x;
        var az = b.z - a.z;
        var bx = c.x - a.x;
        var bz = c.z - a.z;
        return bx * az - ax * bz;
    }

    /// <summary>Funnel through the shared edges (Recast's string pulling; edge order gives left and right).</summary>
    private void StringPull(List<int> path, Vector3 start, Vector3 goal, List<Vector3> corners)
    {
        var portals = new List<(Vector3 Left, Vector3 Right)> { (start, start) };
        for (var i = 0; i + 1 < path.Count; i++)
        {
            foreach (var (other, a, b) in _links[path[i]])
            {
                if (other == path[i + 1])
                {
                    portals.Add((a, b));
                    break;
                }
            }
        }

        portals.Add((goal, goal));
        var apex = start;
        var left = start;
        var right = start;
        int leftIndex = 0, rightIndex = 0;
        for (var i = 1; i < portals.Count; i++)
        {
            var (l, r) = portals[i];
            if (TriArea2(apex, right, r) <= 0f)
            {
                if (apex == right || TriArea2(apex, left, r) > 0f)
                {
                    right = r;
                    rightIndex = i;
                }
                else
                {
                    corners.Add(left);
                    apex = left;
                    right = left;
                    rightIndex = leftIndex;
                    i = leftIndex;
                    continue;
                }
            }

            if (TriArea2(apex, left, l) >= 0f)
            {
                if (apex == left || TriArea2(apex, right, l) < 0f)
                {
                    left = l;
                    leftIndex = i;
                }
                else
                {
                    corners.Add(right);
                    apex = right;
                    left = right;
                    leftIndex = rightIndex;
                    i = rightIndex;
                    continue;
                }
            }
        }

        if (corners.Count == 0 || corners[^1] != goal)
        {
            corners.Add(goal);
        }
    }
}
