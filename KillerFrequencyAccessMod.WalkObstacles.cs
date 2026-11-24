using System;
using System.Collections.Generic;
using UnityEngine;

namespace Killer_Frequency_Access;

/// <summary>
/// Going around things the walking graph does not know about. The SCN_Main NavMesh was baked from the
/// 2,086 Navigation Static objects (walls, furniture); none of the scene's 637 Rigidbodies are in it:
/// door leaves and drawers, the two wheeled alibi boards (NavMeshObstacle with carving off), and
/// hand-sized props (mops, fire extinguishers, bottles) that the player may move or drop.
///
/// When a walk stalls: the player's own capsule (CharacterController radius and height, lifted by its
/// stepOffset so step-over clutter is ignored) is cast toward the next corner against the layers the
/// player's layer collides with (Physics.GetIgnoreLayerCollision). The collider hit becomes an obstacle:
/// its bounds in x/z, grown by the capsule radius and a margin. A detour is the shortest path from the
/// feet around the corners of the walk's known obstacles to a point further along the route, where every
/// leg stays on the walking graph and passes the same capsule cast. No detour: the walk says what blocks
/// it and stops.
/// </summary>
public sealed partial class KillerFrequencyAccessMod
{
    private const float ObstacleMarginMeters = 0.1f;
    private const float BlockerProbeMeters = 0.5f;
    private const float RejoinSearchMeters = 4f;
    private const float RejoinStepMeters = 0.5f;
    private const int MaxDetoursPerWalk = 4;

    private readonly List<(Collider Collider, Rect Footprint, string Name)> _walkObstacles = new();
    private int _walkDetours;

    private void ResetWalkObstacles()
    {
        _walkObstacles.Clear();
        _walkDetours = 0;
    }

    /// <summary>
    /// Called on a stall. True when the walk has been handled: a detour was put in front of the route, or
    /// the walk was stopped because nothing gets around the blocker. False when no blocker was found (the
    /// caller replans as before).
    /// </summary>
    private bool TryHandleBlocker(Transform origin)
    {
        var player = GetFlatScreenPlayer();
        var controller = player != null ? player.characterController : null;
        if (controller == null || _walkCornerIndex >= _walkCorners.Count)
        {
            return false;
        }

        var feet = GetFeet(origin);
        var toCorner = _walkCorners[_walkCornerIndex] - feet;
        toCorner.y = 0f;
        if (toCorner.sqrMagnitude < 1e-4f || !TryCastPlayerCapsule(controller, feet - toCorner.normalized * 0.1f, toCorner.normalized, BlockerProbeMeters + 0.1f, out var blocker))
        {
            MelonLoader.MelonLogger.Msg("[walk] stall: nothing found in the player's way toward the next corner");
            return false;
        }

        var name = GetSpokenName(blocker.transform);
        var known = _walkObstacles.FindIndex(o => o.Collider == blocker);
        if (known < 0)
        {
            var bounds = blocker.bounds;
            var grow = controller.radius + ObstacleMarginMeters;
            var footprint = Rect.MinMaxRect(bounds.min.x - grow, bounds.min.z - grow, bounds.max.x + grow, bounds.max.z + grow);
            _walkObstacles.Add((blocker, footprint, name));
            MelonLoader.MelonLogger.Msg($"[walk] blocked by {BuildHierarchyPath(blocker.transform)} (layer {blocker.gameObject.layer}, bounds {bounds.min} to {bounds.max})");
        }

        _walkDetours++;
        if (_walkDetours > MaxDetoursPerWalk || !TryPlanDetour(controller, feet, out var detour))
        {
            MelonLoader.MelonLogger.Msg($"[walk] no way around {name} ({_walkDetours} detours this walk)");
            StopAutoWalk($"blocked by {name}");
            SpeakWalk($"Blocked by {name}. No way around");
            return true;
        }

        _walkCorners.RemoveRange(0, detour.ResumeIndex);
        _walkCorners.InsertRange(0, detour.Corners);
        _walkCornerIndex = 0;
        _walkStuckReplans = 0;
        MelonLoader.MelonLogger.Msg($"[walk] detour around {name}: {detour.Corners.Count} corners, {detour.Length:0.0} m, rejoining the route");
        SpeakWalk($"Going around {name}");
        return true;
    }

    /// <summary>
    /// Shortest path over a small visibility graph: the feet, the corners of every known obstacle footprint
    /// (on the walking graph), and rejoin points along the rest of the route outside every footprint. Each
    /// rejoin point is scored with the route still left after it.
    /// </summary>
    private bool TryPlanDetour(CharacterController controller, Vector3 feet, out (List<Vector3> Corners, int ResumeIndex, float Length) detour)
    {
        detour = (new List<Vector3>(), 0, 0f);
        var graph = GetLiveNavGraph();
        if (graph == null)
        {
            return false;
        }

        var nodes = new List<Vector3> { feet };
        foreach (var (_, footprint, _) in _walkObstacles)
        {
            foreach (var c in new[] { new Vector2(footprint.xMin, footprint.yMin), new Vector2(footprint.xMax, footprint.yMin), new Vector2(footprint.xMax, footprint.yMax), new Vector2(footprint.xMin, footprint.yMax) })
            {
                if (graph.TryClosestPoint(new Vector3(c.x, feet.y, c.y), 0.4f, out var onGraph)
                    && new Vector2(onGraph.x - c.x, onGraph.z - c.y).magnitude < 0.05f
                    && Math.Abs(onGraph.y - feet.y) < WalkCornerReachedHeightMeters)
                {
                    nodes.Add(onGraph);
                }
            }
        }

        // Rejoin points: along the rest of the route, outside every footprint, with the route length left.
        var firstRejoin = nodes.Count;
        var rejoinResume = new List<int>();
        var rejoinRemaining = new List<float>();
        var total = RemainingRouteMeters(feet);
        var travelled = 0f;
        var from = feet;
        for (var i = _walkCornerIndex; i < _walkCorners.Count && travelled < RejoinSearchMeters; i++)
        {
            var to = _walkCorners[i];
            var length = Vector3.Distance(from, to);
            for (var d = RejoinStepMeters; d <= length + 1e-3f && travelled + d <= RejoinSearchMeters; d += RejoinStepMeters)
            {
                var point = Vector3.Lerp(from, to, Math.Min(1f, d / Math.Max(length, 1e-4f)));
                if (!InsideAnyObstacle(point))
                {
                    nodes.Add(point);
                    rejoinResume.Add(i);
                    rejoinRemaining.Add(total - (travelled + d));
                }
            }

            travelled += length;
            from = to;
        }

        if (nodes.Count == firstRejoin)
        {
            return false;
        }

        // Dijkstra from the feet; a leg is allowed when it crosses no footprint, stays on the walking
        // graph and the player's capsule fits along it.
        var count = nodes.Count;
        var cost = new float[count];
        var previous = new int[count];
        var done = new bool[count];
        for (var i = 0; i < count; i++)
        {
            cost[i] = float.MaxValue;
            previous[i] = -1;
        }

        cost[0] = 0f;
        var best = -1;
        var bestTotal = float.MaxValue;
        for (var round = 0; round < count; round++)
        {
            var u = -1;
            for (var i = 0; i < count; i++)
            {
                if (!done[i] && cost[i] < float.MaxValue && (u < 0 || cost[i] < cost[u]))
                {
                    u = i;
                }
            }

            if (u < 0)
            {
                break;
            }

            done[u] = true;
            if (u >= firstRejoin)
            {
                var withRest = cost[u] + rejoinRemaining[u - firstRejoin];
                if (withRest < bestTotal)
                {
                    bestTotal = withRest;
                    best = u;
                }

                continue; // a rejoin point ends the detour
            }

            for (var v = 1; v < count; v++)
            {
                if (done[v])
                {
                    continue;
                }

                var length = Vector3.Distance(nodes[u], nodes[v]);
                if (cost[u] + length >= cost[v] || !IsLegClear(controller, graph, nodes[u], nodes[v]))
                {
                    continue;
                }

                cost[v] = cost[u] + length;
                previous[v] = u;
            }
        }

        if (best < 0)
        {
            return false;
        }

        var corners = new List<Vector3>();
        for (var n = best; n > 0; n = previous[n])
        {
            corners.Add(nodes[n]);
        }

        corners.Reverse();
        detour = (corners, rejoinResume[best - firstRejoin], cost[best]);
        return true;
    }

    private bool InsideAnyObstacle(Vector3 point)
    {
        foreach (var (_, footprint, _) in _walkObstacles)
        {
            if (footprint.Contains(new Vector2(point.x, point.z)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The leg crosses no known footprint, stays on the walking graph, and the player's capsule fits.</summary>
    private bool IsLegClear(CharacterController controller, NavGraph graph, Vector3 a, Vector3 b)
    {
        var a2 = new Vector2(a.x, a.z);
        var b2 = new Vector2(b.x, b.z);
        foreach (var (_, footprint, _) in _walkObstacles)
        {
            // Shrunk slightly so legs along a footprint's edge (corner to corner) are allowed.
            var inner = Rect.MinMaxRect(footprint.xMin + 0.02f, footprint.yMin + 0.02f, footprint.xMax - 0.02f, footprint.yMax - 0.02f);
            if (SegmentCrossesRect(a2, b2, inner))
            {
                return false;
            }
        }

        var length = Vector3.Distance(a, b);
        var steps = Math.Max(1, Mathf.CeilToInt(length / 0.15f));
        for (var s = 0; s <= steps; s++)
        {
            var p = Vector3.Lerp(a, b, s / (float)steps);
            if (!graph.TryClosestPoint(p, 0.4f, out var onGraph)
                || new Vector2(onGraph.x - p.x, onGraph.z - p.z).magnitude > 0.05f
                || Math.Abs(onGraph.y - p.y) > 0.4f)
            {
                return false;
            }
        }

        var direction = b - a;
        direction.y = 0f;
        return direction.sqrMagnitude < 1e-6f || !TryCastPlayerCapsule(controller, a, direction.normalized, direction.magnitude, out _);
    }

    private static bool SegmentCrossesRect(Vector2 a, Vector2 b, Rect rect)
    {
        if (rect.width <= 0f || rect.height <= 0f)
        {
            return false;
        }

        if (rect.Contains(a) || rect.Contains(b))
        {
            return true;
        }

        // Liang-Barsky clip of the segment against the rectangle.
        var d = b - a;
        float t0 = 0f, t1 = 1f;
        var p = new[] { -d.x, d.x, -d.y, d.y };
        var q = new[] { a.x - rect.xMin, rect.xMax - a.x, a.y - rect.yMin, rect.yMax - a.y };
        for (var i = 0; i < 4; i++)
        {
            if (Math.Abs(p[i]) < 1e-9f)
            {
                if (q[i] < 0f)
                {
                    return false;
                }

                continue;
            }

            var t = q[i] / p[i];
            if (p[i] < 0f)
            {
                t0 = Math.Max(t0, t);
            }
            else
            {
                t1 = Math.Min(t1, t);
            }

            if (t0 > t1)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The first collider the player's capsule hits moving from <paramref name="feet"/> along
    /// <paramref name="direction"/>: the CharacterController's radius and height, its bottom lifted by
    /// stepOffset (what the controller steps over is not a blocker), against the layers the player's layer
    /// collides with. The player's own hierarchy (body, held items) and triggers are skipped.
    /// </summary>
    private static bool TryCastPlayerCapsule(CharacterController controller, Vector3 feet, Vector3 direction, float distance, out Collider blocker)
    {
        var playerRoot = controller.transform.root;
        var bottom = controller.stepOffset + 0.02f;
        var top = Math.Max(bottom, controller.height - 0.02f);
        return TrySweepBody(feet, direction, distance, bottom, top, controller.radius, PlayerCollisionMask(controller.gameObject.layer),
            collider => collider.transform.root != playerRoot, out blocker, out _);
    }

    private static int _playerCollisionMask = -1;
    private static int _playerCollisionLayer = -1;

    /// <summary>The layers the player's layer collides with (the game's layer collision matrix).</summary>
    private static int PlayerCollisionMask(int layer)
    {
        if (layer == _playerCollisionLayer)
        {
            return _playerCollisionMask;
        }

        var mask = 0;
        for (var other = 0; other < 32; other++)
        {
            if (!Physics.GetIgnoreLayerCollision(layer, other))
            {
                mask |= 1 << other;
            }
        }

        _playerCollisionLayer = layer;
        _playerCollisionMask = mask;
        MelonLoader.MelonLogger.Msg($"[walk] player layer {layer} collides with layer mask {mask}");
        return mask;
    }
}
