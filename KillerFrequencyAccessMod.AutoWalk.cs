using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Killer_Frequency_Access;

/// <summary>
/// Auto-walk to the selected target, built on game facts:
/// - Route: the scene's baked NavMesh (SCN_Main NavMesh_0, Intro/Menu NavMesh_1, Epilogue NavMesh).
///   The game strips NavMeshPath, so NavMesh.CalculatePath cannot be called; the route is an A* search
///   over a 0.5 m lattice where each node is NavMesh.SamplePosition and each step must pass
///   NavMesh.Raycast (no NavMesh edge between the two points), then shortened with the same raycast.
///   Followed corner by corner with the player's CharacterController.
/// - View: the game's CameraHandler.ResetToRotation (FlatScreenPlayer.instance._cameraHandler). The
///   game's look update owns the camera, so writing the camera transform directly is overwritten
///   (the rooms/held build: walks to small desk buttons never finished).
/// - Arrival: the game reports the target as observedInteractable and CanPerform is true for one
///   of its flat-screen interactions (or it is a dock that accepts the held item). The mod's own reach
///   estimate disagreed with the game in that run, so it no longer decides arrival.
/// Every start, replan and stop is logged as [walk].
/// </summary>
public sealed partial class KillerFrequencyAccessMod
{
    private const float WalkCornerReachedMeters = 0.3f;
    private const float WalkReplanSeconds = 1.0f;
    private const float WalkStuckSeconds = 1.5f;
    private const int WalkMaxStuckReplans = 3;
    private const float AimDegreesPerSecond = 240f;
    private const float AimSettleDegrees = 2f;
    private const float AimInchSpeed = 0.6f;
    private const float AimGiveUpSeconds = 4f;
    private const float NavMeshSampleMeters = 2f;

    private readonly List<Vector3> _walkCorners = new();
    private int _walkCornerIndex;
    private float _nextWalkReplanTime;
    private Vector3 _walkProgressPosition;
    private float _walkProgressTime;
    private int _walkStuckReplans;
    private bool _walkAiming;
    private float _walkAimStartTime;

    private void StartAutoWalkToSelectedTarget()
    {
        if (_reachableTargets.Count == 0 || _selectedTargetIndex < 0 || _selectedTargetIndex >= _reachableTargets.Count)
        {
            RefreshReachableTargets();
        }

        if (_reachableTargets.Count == 0)
        {
            SpeakSelectionText($"Nothing listed: {DescribeActiveFilter()}");
            return;
        }

        _selectedTargetIndex = Mathf.Clamp(_selectedTargetIndex, 0, _reachableTargets.Count - 1);
        var target = _reachableTargets[_selectedTargetIndex].Transform;
        if (target == null || !TryGetNavigationOrigin(out var origin))
        {
            SpeakSelectionText("Player position unavailable");
            return;
        }

        var name = GetSpokenName(target);
        if (IsReadyToUse(target))
        {
            SpeakInRangeWithAction(target);
            return;
        }

        _autoWalkTarget = target;
        _isAutoWalking = true;
        _walkAiming = false;
        _walkStuckReplans = 0;
        if (!PlanWalk(origin.position, target, "start"))
        {
            StopAutoWalk($"no path to {name}");
            SpeakSelectionText($"No path to {name}");
            return;
        }

        SpeakSelectionText($"Walking to {name}");
    }

    /// <summary>Route from the NavMesh: player's nearest NavMesh point to the target's nearest NavMesh point.</summary>
    private bool PlanWalk(Vector3 from, Transform target, string reason)
    {
        _walkCorners.Clear();
        _walkCornerIndex = 0;
        var now = Time.unscaledTime;
        _nextWalkReplanTime = now + WalkReplanSeconds;
        _walkProgressPosition = from;
        _walkProgressTime = now;

        var goal = GetNearestPoint(target, from);
        if (!NavMesh.SamplePosition(from, out var startHit, NavMeshSampleMeters, NavMesh.AllAreas))
        {
            MelonLoader.MelonLogger.Msg($"[walk] {reason}: no NavMesh within {NavMeshSampleMeters} m of the player at {from}");
            return false;
        }

        if (!NavMesh.SamplePosition(goal, out var endHit, NavMeshSampleMeters, NavMesh.AllAreas))
        {
            MelonLoader.MelonLogger.Msg($"[walk] {reason}: no NavMesh within {NavMeshSampleMeters} m of {GetSpokenName(target)} at {goal}");
            return false;
        }

        if (!TryFindNavMeshRoute(startHit.position, endHit.position, _walkCorners, out var expanded))
        {
            MelonLoader.MelonLogger.Msg($"[walk] {reason}: no NavMesh route to {GetSpokenName(target)} ({expanded} nodes searched)");
            return false;
        }

        // Skip corners already reached (the first is the start point).
        while (_walkCornerIndex < _walkCorners.Count - 1 && PlanarDistance(from, _walkCorners[_walkCornerIndex]) <= WalkCornerReachedMeters)
        {
            _walkCornerIndex++;
        }

        MelonLoader.MelonLogger.Msg($"[walk] {reason}: to {GetSpokenName(target)}, {_walkCorners.Count} corners, {expanded} nodes searched, {PlanarDistance(from, endHit.position):0.0} m");
        return true;
    }

    private void UpdateAutoWalk(float deltaTime)
    {
        if (!_isAutoWalking)
        {
            return;
        }

        try
        {
            var target = _autoWalkTarget;
            if (IsMenuActive())
            {
                StopAutoWalk("menu opened");
                return;
            }

            if (target == null || !target.gameObject.activeInHierarchy)
            {
                StopAutoWalk("target gone");
                return;
            }

            if (!TryGetNavigationOrigin(out var origin))
            {
                StopAutoWalk("no player");
                return;
            }

            if (IsReadyToUse(target))
            {
                StopAutoWalk("arrived");
                SpeakInRangeWithAction(target);
                return;
            }

            var now = Time.unscaledTime;
            var camera = GetInteractorCamera();
            var cameraPosition = camera != null ? camera.transform.position : origin.position;
            var nearest = GetNearestPoint(target, cameraPosition);
            var reach = TryGetGameInteractable(target, out var interactable) ? interactable.fsMaxReachDistance : 1.2f;
            var pathDone = _walkCornerIndex >= _walkCorners.Count;

            if (_walkAiming || pathDone || PlanarDistance(origin.position, nearest) <= reach)
            {
                UpdateAiming(origin, target, deltaTime, now);
                return;
            }

            // Walking along the NavMesh route.
            var corner = _walkCorners[_walkCornerIndex];
            if (PlanarDistance(origin.position, corner) <= WalkCornerReachedMeters)
            {
                _walkCornerIndex++;
                return;
            }

            // Progress check once a second; the route is only searched again when the player has not moved.
            if (now >= _nextWalkReplanTime)
            {
                _nextWalkReplanTime = now + WalkReplanSeconds;
                var moved = PlanarDistance(origin.position, _walkProgressPosition);
                if (moved >= 0.1f)
                {
                    _walkStuckReplans = 0;
                    _walkProgressPosition = origin.position;
                    _walkProgressTime = now;
                }
                else if (now - _walkProgressTime >= WalkStuckSeconds)
                {
                    _walkStuckReplans++;
                    if (_walkStuckReplans > WalkMaxStuckReplans)
                    {
                        StopAutoWalk("stuck");
                        SpeakSelectionText($"Stuck. {GetSpokenName(target)} is {DescribeWhere(origin, nearest)}");
                        return;
                    }

                    if (!PlanWalk(origin.position, target, $"replan (stuck {_walkStuckReplans})"))
                    {
                        StopAutoWalk("no path on replan");
                        SpeakSelectionText($"No path to {GetSpokenName(target)}");
                    }

                    return;
                }
            }

            var toCorner = corner - origin.position;
            toCorner.y = 0f;
            FaceYaw(toCorner, deltaTime);
            var step = Mathf.Min(AutoWalkSpeed * deltaTime, toCorner.magnitude);
            MoveNavigationOrigin(origin, toCorner.normalized * step);
        }
        catch (Exception ex)
        {
            LoggerInstance.Warning($"[walk] stopped by error: {ex.Message}");
            StopAutoWalk("error");
        }
    }

    /// <summary>Near the target: turn the view to it through the game's camera, inching closer while the game does not observe it.</summary>
    private void UpdateAiming(Transform origin, Transform target, float deltaTime, float now)
    {
        if (!_walkAiming)
        {
            _walkAiming = true;
            _walkAimStartTime = now;
            MelonLoader.MelonLogger.Msg($"[walk] aiming at {GetSpokenName(target)}");
        }

        var aimPoint = ResolveInteractionAimPoint(target);
        var remaining = AimAt(aimPoint, deltaTime);
        if (remaining > AimSettleDegrees)
        {
            return;
        }

        if (now - _walkAimStartTime > AimGiveUpSeconds)
        {
            var observed = TryGetObservedInteractable(out var other) ? GetSpokenName(other) : "nothing";
            StopAutoWalk($"aimed but not observed (game observes {observed})");
            SpeakSelectionText($"Could not reach {GetSpokenName(target)}. It is {DescribeWhere(origin, GetNearestPoint(target, origin.position))}. Pointing at {observed}");
            return;
        }

        var toTarget = GetNearestPoint(target, origin.position) - origin.position;
        toTarget.y = 0f;
        if (toTarget.magnitude > 0.05f)
        {
            MoveNavigationOrigin(origin, toTarget.normalized * Mathf.Min(AimInchSpeed * deltaTime, toTarget.magnitude));
        }
    }

    /// <summary>Turns the view toward a point through the game's camera handler. Returns the degrees left to turn.</summary>
    private static float AimAt(Vector3 point, float deltaTime)
    {
        var camera = GetInteractorCamera();
        if (camera == null)
        {
            return 0f;
        }

        var direction = point - camera.transform.position;
        if (direction.sqrMagnitude < 0.0001f)
        {
            return 0f;
        }

        var desired = Quaternion.LookRotation(direction.normalized, Vector3.up);
        var current = camera.transform.rotation;
        var next = Quaternion.RotateTowards(current, desired, AimDegreesPerSecond * deltaTime);
        SetViewRotation(next);
        return Quaternion.Angle(next, desired);
    }

    /// <summary>Turns the view level toward a horizontal direction while walking.</summary>
    private static void FaceYaw(Vector3 planarDirection, float deltaTime)
    {
        var camera = GetInteractorCamera();
        if (camera == null || planarDirection.sqrMagnitude < 0.0001f)
        {
            return;
        }

        var desired = Quaternion.LookRotation(planarDirection.normalized, Vector3.up);
        SetViewRotation(Quaternion.RotateTowards(camera.transform.rotation, desired, AutoTurnDegreesPerSecond * deltaTime));
    }

    /// <summary>The game's CameraHandler.ResetToRotation; logged once if it is unavailable.</summary>
    private static void SetViewRotation(Quaternion rotation)
    {
        var player = Il2Cpp.FlatScreenPlayer.instance;
        var handler = player == null ? null : player._cameraHandler;
        if (handler == null)
        {
            LogOnce("walk-no-camera-handler", "[walk] FlatScreenPlayer.instance._cameraHandler is null; cannot turn the view");
            return;
        }

        handler.ResetToRotation(rotation);
    }

    /// <summary>The game observes the target and can perform one of its actions (or it is a dock that accepts the held item).</summary>
    private bool IsReadyToUse(Transform target)
    {
        if (!IsObservedByGame(target))
        {
            return false;
        }

        return HasPerformableInteraction(target) || _placesForHeldIds.Contains(target.GetInstanceID());
    }

    private void SpeakInRangeWithAction(Transform target)
    {
        var actions = GetPerformableVerbs(target);
        if (_placesForHeldIds.Contains(target.GetInstanceID()) && TryGetHeld(out var held, out _) && held != null)
        {
            actions.Insert(0, $"Fits {GetSpokenName(held)}, {DescribePlaceKeys()}");
        }

        var name = GetSpokenName(target);
        SpeakSelectionText(actions.Count == 0 ? $"{name} in range" : $"{name} in range. {string.Join(". ", actions)}");
    }

    private void StopAutoWalk(string reason)
    {
        if (!_isAutoWalking)
        {
            return;
        }

        MelonLoader.MelonLogger.Msg($"[walk] stop: {reason}");
        _isAutoWalking = false;
        _autoWalkTarget = null;
        _walkAiming = false;
        _walkCorners.Clear();
    }

    private void StopAutoWalk(bool speakCancelled)
    {
        StopAutoWalk(speakCancelled ? "cancelled" : "interrupted");
        if (speakCancelled)
        {
            SpeakSelectionText("Auto walk cancelled");
        }
    }

    private const float RouteCellMeters = 0.5f;
    private const int RouteMaxExpansions = 30000;

    /// <summary>
    /// A* over the NavMesh: nodes are NavMesh.SamplePosition points on a lattice, a step is allowed when
    /// NavMesh.Raycast finds no edge between its ends. The result is shortened by skipping corners the
    /// raycast can see past. Corners go into <paramref name="corners"/>, start excluded, goal included.
    /// </summary>
    private static bool TryFindNavMeshRoute(Vector3 start, Vector3 goal, List<Vector3> corners, out int expanded)
    {
        corners.Clear();
        expanded = 0;
        if (!NavMesh.Raycast(start, goal, out _, NavMesh.AllAreas))
        {
            corners.Add(goal);
            return true;
        }

        (int X, int Z, int Y) Key(Vector3 p) => (Mathf.RoundToInt(p.x / RouteCellMeters), Mathf.RoundToInt(p.z / RouteCellMeters), Mathf.RoundToInt(p.y / 1.0f));

        var startKey = Key(start);
        var position = new Dictionary<(int, int, int), Vector3> { [startKey] = start };
        var cost = new Dictionary<(int, int, int), float> { [startKey] = 0f };
        var parent = new Dictionary<(int, int, int), (int, int, int)>();
        var closed = new HashSet<(int, int, int)>();
        var open = new SortedSet<(float F, int Order, (int, int, int) Key)>();
        var order = 0;
        open.Add((Vector3.Distance(start, goal), order++, startKey));
        (int, int, int)? reached = null;

        while (open.Count > 0 && expanded < RouteMaxExpansions)
        {
            var current = open.Min;
            open.Remove(current);
            var key = current.Key;
            if (!closed.Add(key))
            {
                continue;
            }

            expanded++;
            var here = position[key];
            if (Vector3.Distance(here, goal) <= RouteCellMeters * 1.5f && !NavMesh.Raycast(here, goal, out _, NavMesh.AllAreas))
            {
                reached = key;
                break;
            }

            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dz == 0)
                    {
                        continue;
                    }

                    var probe = new Vector3(here.x + dx * RouteCellMeters, here.y, here.z + dz * RouteCellMeters);
                    if (!NavMesh.SamplePosition(probe, out var hit, RouteCellMeters, NavMesh.AllAreas))
                    {
                        continue;
                    }

                    var next = hit.position;
                    var nextKey = Key(next);
                    if (closed.Contains(nextKey) || NavMesh.Raycast(here, next, out _, NavMesh.AllAreas))
                    {
                        continue;
                    }

                    var g = cost[key] + Vector3.Distance(here, next);
                    if (cost.TryGetValue(nextKey, out var known) && known <= g)
                    {
                        continue;
                    }

                    cost[nextKey] = g;
                    position[nextKey] = next;
                    parent[nextKey] = key;
                    open.Add((g + Vector3.Distance(next, goal), order++, nextKey));
                }
            }
        }

        if (reached == null)
        {
            return false;
        }

        var raw = new List<Vector3> { goal };
        for (var k = reached.Value; !k.Equals(startKey); k = parent[k])
        {
            raw.Add(position[k]);
        }

        raw.Add(start);
        raw.Reverse();

        // Shorten: from each corner, jump to the farthest later corner the NavMesh raycast can reach.
        var i = 0;
        while (i < raw.Count - 1)
        {
            var j = raw.Count - 1;
            while (j > i + 1 && NavMesh.Raycast(raw[i], raw[j], out _, NavMesh.AllAreas))
            {
                j--;
            }

            corners.Add(raw[j]);
            i = j;
        }

        return true;
    }

    private static float PlanarDistance(Vector3 a, Vector3 b)
    {
        return Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
    }
}
