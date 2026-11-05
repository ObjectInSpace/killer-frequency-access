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
/// - View: the game's CameraHandler.ResetToRotation (the CameraHandler under the player). The
///   game's look update owns the camera, so writing the camera transform directly is overwritten
///   (the rooms/held build: walks to small desk buttons never finished).
/// - Where to stand: NavMesh points around the target from which, at the player's eye height, the
///   target's collider is within its fsMaxReachDistance and a raycast with the game's interaction mask
///   (ControllerBaseInteractor.raycastLayerMask) hits the target first. Tried nearest first; if the game
///   does not observe the target after aiming from one, the next is tried. Before this (aiming-fix run)
///   the walk aimed from wherever it was within 1.2 m and phone line buttons failed with "observes nothing".
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
    private float _walkAimedTime = -1f;
    private readonly List<Vector3> _walkStandpoints = new();
    private int _walkStandpointIndex;
    private const float AimObserveWaitSeconds = 1.0f;

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
        FindStandpoints(origin, target);
        _walkStandpointIndex = 0;
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

        var goal = _walkStandpointIndex < _walkStandpoints.Count
            ? _walkStandpoints[_walkStandpointIndex]
            : GetNearestPoint(target, from);
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
            var pathDone = _walkCornerIndex >= _walkCorners.Count;

            if (_walkAiming || pathDone)
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

    /// <summary>At the standing spot: turn the view to the target; if the game does not observe it, try the next spot.</summary>
    private void UpdateAiming(Transform origin, Transform target, float deltaTime, float now)
    {
        if (!_walkAiming)
        {
            _walkAiming = true;
            _walkAimStartTime = now;
            _walkAimedTime = -1f;
            MelonLoader.MelonLogger.Msg($"[walk] aiming at {GetSpokenName(target)} from spot {_walkStandpointIndex + 1} of {_walkStandpoints.Count}");
        }

        var remaining = AimAt(ResolveInteractionAimPoint(target), deltaTime);
        if (remaining <= AimSettleDegrees && _walkAimedTime < 0f)
        {
            _walkAimedTime = now;
        }

        var aimedLongEnough = _walkAimedTime >= 0f && now - _walkAimedTime > AimObserveWaitSeconds;
        var timedOut = now - _walkAimStartTime > AimGiveUpSeconds;
        if (!aimedLongEnough && !timedOut)
        {
            return;
        }

        var observed = TryGetObservedInteractable(out var other) ? GetSpokenName(other) : "nothing";
        var why = timedOut && !aimedLongEnough ? $"view did not settle ({remaining:0} degrees left)" : "aimed";
        MelonLoader.MelonLogger.Msg($"[walk] spot {_walkStandpointIndex + 1} failed: {why}, game observes {observed}");
        _walkStandpointIndex++;
        if (_walkStandpointIndex < _walkStandpoints.Count)
        {
            _walkAiming = false;
            if (PlanWalk(origin.position, target, $"next spot {_walkStandpointIndex + 1}"))
            {
                return;
            }
        }

        StopAutoWalk($"no spot worked (game observes {observed})");
        SpeakSelectionText($"Could not reach {GetSpokenName(target)}. It is {DescribeWhere(origin, GetNearestPoint(target, origin.position))}. Pointing at {observed}");
    }

    /// <summary>
    /// NavMesh points around the target from which the target can be used, nearest to the player first.
    /// From eye height (the player's current camera height above its feet) the target's nearest collider
    /// point must be within fsMaxReachDistance, and a raycast toward the aim point with the game's
    /// interaction mask must hit one of the target's own colliders.
    /// </summary>
    private void FindStandpoints(Transform origin, Transform target)
    {
        _walkStandpoints.Clear();
        var camera = GetInteractorCamera();
        if (camera == null || !TryGetGameInteractable(target, out var interactable))
        {
            return;
        }

        // Spots are NavMesh (floor) points, so eye height is measured from the game's foot location
        // (PlayerBase.GetFootLocation). The player transform is not at the feet: measuring from it gave
        // 0.90 m in the standing-spot run, rays at desk height, and 0 spots for the phone line buttons.
        var feet = origin.position;
        // FlatScreenPlayer is on the same PlayerController GameObject as the interactor (rip).
        var player = origin.GetComponent<Il2Cpp.FlatScreenPlayer>() ?? Il2Cpp.FlatScreenPlayer.instance;
        if (player != null)
        {
            feet = player.GetFootLocation();
        }
        else if (NavMesh.SamplePosition(origin.position, out var footHit, NavMeshSampleMeters, NavMesh.AllAreas))
        {
            feet = footHit.position;
            LogOnce("walk-no-player-feet", "[walk] FlatScreenPlayer.instance is null; feet taken from the NavMesh under the player");
        }

        var eyeHeight = camera.transform.position.y - feet.y;
        var aim = ResolveInteractionAimPoint(target);
        var reach = interactable.fsMaxReachDistance;
        int mask = Il2Cpp.ControllerBaseInteractor.raycastLayerMask.value;
        var tested = 0;
        for (var radius = 0.35f; radius <= reach + 0.01f; radius += 0.25f)
        {
            for (var step = 0; step < 16; step++)
            {
                var angle = step * Mathf.PI * 2f / 16f;
                var probe = new Vector3(aim.x + Mathf.Cos(angle) * radius, origin.position.y, aim.z + Mathf.Sin(angle) * radius);
                if (!NavMesh.SamplePosition(probe, out var hit, 0.5f, NavMesh.AllAreas))
                {
                    continue;
                }

                var spot = hit.position;
                if (_walkStandpoints.Exists(p => Vector3.Distance(p, spot) < 0.2f))
                {
                    continue;
                }

                tested++;
                var eye = spot + Vector3.up * eyeHeight;
                if (Vector3.Distance(eye, GetNearestPoint(target, eye)) > reach)
                {
                    continue;
                }

                var toAim = aim - eye;
                if (!Physics.Raycast(eye, toAim.normalized, out var rayHit, toAim.magnitude + 0.3f, mask)
                    || rayHit.collider == null
                    || !TryGetGameInteractable(rayHit.collider.transform, out var hitInteractable)
                    || hitInteractable.GetInstanceID() != interactable.GetInstanceID())
                {
                    continue;
                }

                _walkStandpoints.Add(spot);
            }
        }

        var from = origin.position;
        _walkStandpoints.Sort((a, b) => PlanarDistance(from, a).CompareTo(PlanarDistance(from, b)));
        MelonLoader.MelonLogger.Msg($"[walk] {_walkStandpoints.Count} usable spots for {GetSpokenName(target)} ({tested} NavMesh points tested, reach {reach:0.00} m, eye {eyeHeight:0.00} m)");
    }

    /// <summary>
    /// Turns the view toward a point. Returns the degrees still between the camera's actual rotation
    /// and the point (measured before this frame's turn, so a turn the game ignores is not counted).
    /// </summary>
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
        var remaining = Quaternion.Angle(current, desired);
        SetViewRotation(Quaternion.RotateTowards(current, desired, AimDegreesPerSecond * deltaTime), remaining);
        return remaining;
    }

    /// <summary>Turns the view level toward a horizontal direction while walking.</summary>
    private static void FaceYaw(Vector3 planarDirection, float deltaTime)
    {
        var camera = GetInteractorCamera();
        if (camera == null || planarDirection.sqrMagnitude < 0.0001f)
        {
            return;
        }

        var current = camera.transform.rotation;
        var desired = Quaternion.LookRotation(planarDirection.normalized, Vector3.up);
        SetViewRotation(Quaternion.RotateTowards(current, desired, AutoTurnDegreesPerSecond * deltaTime), Quaternion.Angle(current, desired));
    }

    private static Il2Cpp.CameraHandler? _cameraHandler;
    private static bool _viewUseTransforms;
    private static float _viewCheckStartTime = -1f;
    private static float _viewCheckStartAngle;

    /// <summary>
    /// The player's CameraHandler. In the rip it sits on PlayerController/CameraJoint/CameraFirstPerson,
    /// a child of the interactor's GameObject; FlatScreenPlayer._cameraHandler is not serialized and was
    /// null at runtime (auto-walk/controller run).
    /// </summary>
    private static Il2Cpp.CameraHandler? GetCameraHandler()
    {
        if (_cameraHandler != null)
        {
            return _cameraHandler;
        }

        if (TryGetPlayerInteractor(out var interactor))
        {
            _cameraHandler = interactor.GetComponentInChildren<Il2Cpp.CameraHandler>(true);
        }

        if (_cameraHandler == null)
        {
            _cameraHandler = UnityEngine.Object.FindObjectOfType<Il2Cpp.CameraHandler>();
        }

        LogOnce($"walk-camera-handler|{(_cameraHandler == null ? "none" : _cameraHandler.name)}",
            _cameraHandler == null ? "[walk] no CameraHandler found; cannot turn the view" : $"[walk] view turns through CameraHandler on {BuildHierarchyPath(_cameraHandler.transform)}");
        return _cameraHandler;
    }

    /// <summary>
    /// Sets the view through CameraHandler.ResetToRotation. If the camera has not turned at all after a
    /// second of asking, switches (once, logged) to the transforms the handler drives: playerBody for
    /// yaw, cameraHinge for pitch.
    /// </summary>
    private static void SetViewRotation(Quaternion rotation, float remainingDegrees)
    {
        var handler = GetCameraHandler();
        if (handler == null)
        {
            return;
        }

        if (!_viewUseTransforms)
        {
            handler.ResetToRotation(rotation);
            var now = Time.unscaledTime;
            if (remainingDegrees < 5f)
            {
                _viewCheckStartTime = -1f;
            }
            else if (_viewCheckStartTime < 0f)
            {
                _viewCheckStartTime = now;
                _viewCheckStartAngle = remainingDegrees;
            }
            else if (now - _viewCheckStartTime > 1f)
            {
                if (remainingDegrees > _viewCheckStartAngle - 1f)
                {
                    _viewUseTransforms = true;
                    MelonLoader.MelonLogger.Msg($"[walk] ResetToRotation did not turn the view in 1 s ({_viewCheckStartAngle:0} to {remainingDegrees:0} degrees); using playerBody and cameraHinge");
                }

                _viewCheckStartTime = -1f;
            }

            return;
        }

        var euler = rotation.eulerAngles;
        if (handler.playerBody != null)
        {
            handler.playerBody.rotation = Quaternion.Euler(0f, euler.y, 0f);
        }

        if (handler.cameraHinge != null)
        {
            handler.cameraHinge.localRotation = Quaternion.Euler(euler.x, 0f, 0f);
        }
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
