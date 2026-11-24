using System;
using System.Collections.Generic;
using UnityEngine;

namespace Killer_Frequency_Access;

/// <summary>
/// Auto-walk to the selected target, built on game facts:
/// - Route: the scene's baked NavMesh (SCN_Main NavMesh_0, Intro/Menu NavMesh_1, Epilogue NavMesh).
///   The game strips NavMeshPath and CalculateTriangulation, so the polygons come from the rip
///   (NavGraph, tools\rip\gen_navmesh.py): A* over polygons, funnel through their shared edges one
///   floor at a time. The graph is the only walking data: an earlier 0.5 m lattice over
///   NavMesh.SamplePosition could not climb the ~40 degree stairs between floors and was removed.
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
    /// <summary>How far a point may be from the walking graph and still be snapped onto it.</summary>
    private const float NavSnapMeters = 2f;

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

    /// <summary>
    /// A corner counts as reached only within this height of the player's feet. Corners are reached in
    /// x/z, and on stairs a landing's corner can lie right above or below the player (Date Everything
    /// Access: the ground landing counted as reached from the floor below). Stair runs in SCN_Main rise
    /// 2.3 m over 2.75 m, so 0.6 m keeps the check to the player's own stretch of stairs.
    /// </summary>
    private const float WalkCornerReachedHeightMeters = 0.6f;

    /// <summary>A route failed because the two ends are on separate parts of the walking graph.</summary>
    private bool _walkFloorNotConnected;

    private void StartAutoWalkToSelectedTarget()
    {
        var target = GetListWalkTarget();
        if (target == null)
        {
            SpeakWalk(_listLevel == ListLevel.Rooms
                ? "Choose an object first: End lists the objects in the room"
                : "Nothing selected to walk to");
            return;
        }

        if (_sweepActive)
        {
            StopSweep("the player started a walk");
        }

        StartAutoWalkTo(target);
    }

    /// <summary>Why the last walk ended (the reason given to StopAutoWalk), for the coverage sweep.</summary>
    private string _lastWalkStopReason = string.Empty;

    /// <summary>Walk speech; silent during a coverage sweep (hundreds of walks), which logs instead.</summary>
    private void SpeakWalk(string text)
    {
        if (_sweepActive)
        {
            return;
        }

        SpeakSelectionText(text);
    }

    /// <param name="keepContainerPlan">True for the walk to a container that must be opened first (WalkContainers).</param>
    private void StartAutoWalkTo(Transform target, bool keepContainerPlan = false)
    {
        if (!keepContainerPlan)
        {
            ClearContainerPlan();
        }

        if (!TryGetNavigationOrigin(out var origin))
        {
            _lastWalkStopReason = "no player";
            SpeakWalk("Player position unavailable");
            return;
        }

        var name = GetSpokenName(target);
        if (IsReadyToUse(target))
        {
            if (_containerToOpen != null)
            {
                // Already at the container: the walk stays active so UpdateAutoWalk opens it.
                _autoWalkTarget = target;
                _isAutoWalking = true;
                _walkAiming = false;
                _walkCorners.Clear();
                return;
            }

            _lastWalkStopReason = "arrived (already in range)";
            SpeakInRangeWithAction(target);
            return;
        }

        _autoWalkTarget = target;
        _isAutoWalking = true;
        _walkAiming = false;
        _walkStuckReplans = 0;
        CollectWalkDoors();
        ResetWalkObstacles();
        // Posture decides reach (desk buttons are out of sight crouched, things under furniture only
        // crouched), so spots are searched in both postures and the walk takes the spot's posture.
        _sightBlockers.Clear();
        _walkFloorNotConnected = false;
        FindStandpointsBothPostures(origin, target);
        _walkStandpointIndex = 0;
        BeginWalkPosture();
        if (_walkStandpoints.Count == 0 && !keepContainerPlan && TryRedirectHiddenTarget(target))
        {
            return;
        }

        if (!PlanWalk(GetFeet(origin), target, "start"))
        {
            StopAutoWalk($"no path to {name}");
            SpeakWalk(_walkFloorNotConnected
                ? $"No path to {name}: the floor where you stand does not connect to it"
                : $"No path to {name}");
            return;
        }

        SpeakWalk($"Walking to {name}");
    }

    /// <summary>
    /// Plans to the current standing spot; a spot with no NavMesh route (a separate NavMesh island:
    /// "no NavMesh route" after ~3800 nodes in the 2026-11-19 run, which then ended the walk) is skipped
    /// for the next one.
    /// </summary>
    private bool PlanWalk(Vector3 from, Transform target, string reason)
    {
        while (true)
        {
            if (PlanWalkToCurrentSpot(from, target, reason))
            {
                return true;
            }

            if (_walkStandpointIndex + 1 >= _walkStandpoints.Count)
            {
                return false;
            }

            _walkStandpointIndex++;
            reason = $"spot {_walkStandpointIndex + 1} (previous unreachable)";
        }
    }

    /// <summary>Route from the NavMesh: player's nearest NavMesh point to the current spot (or the target's nearest point).</summary>
    private bool PlanWalkToCurrentSpot(Vector3 from, Transform target, string reason)
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
        var graph = GetLiveNavGraph();
        if (graph == null)
        {
            MelonLoader.MelonLogger.Msg($"[walk] {reason}: no walking graph for this scene");
            return false;
        }

        if (!graph.TrySnapConnected(from, goal, NavSnapMeters, IsFloorMoveClear, out var start, out var end, out var snapFailure))
        {
            _walkFloorNotConnected |= snapFailure.StartsWith("start and goal are on separate", StringComparison.Ordinal);
            MelonLoader.MelonLogger.Msg($"[walk] {reason}: {GetSpokenName(target)}: {snapFailure} ({graph.Name})");
            return false;
        }

        if (!graph.TryRoute(start, end, _walkCorners, out var searched, out var failure))
        {
            _walkFloorNotConnected |= failure == "polygons not connected";
            MelonLoader.MelonLogger.Msg($"[walk] {reason}: no route to {GetSpokenName(target)} on {graph.Name}: {failure} ({searched} polygons searched)");
            return false;
        }

        // Skip corners already reached (the first is the start point).
        while (_walkCornerIndex < _walkCorners.Count - 1 && IsCornerReached(start, _walkCorners[_walkCornerIndex]))
        {
            _walkCornerIndex++;
        }

        MelonLoader.MelonLogger.Msg($"[walk] {reason}: to {GetSpokenName(target)}, {_walkCorners.Count} corners, {searched} polygons searched, {RouteLength(start, _walkCorners):0.0} m of route");
        return true;
    }

    private void UpdateAutoWalk(float deltaTime)
    {
        UpdateHeldDoors();
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

            var now = Time.unscaledTime;
            if (_containerOpening || (_containerToOpen != null && IsReadyToUse(target)))
            {
                // At the container: open it, then walk on to the object inside.
                UpdateContainerOpening(now);
                return;
            }

            if (IsReadyToUse(target))
            {
                StopAutoWalk("arrived");
                SpeakInRangeWithAction(target);
                return;
            }

            var camera = GetInteractorCamera();
            var cameraPosition = camera != null ? camera.transform.position : origin.position;
            var nearest = GetNearestPoint(target, cameraPosition);
            var pathDone = _walkCornerIndex >= _walkCorners.Count;

            if (_walkAiming || pathDone)
            {
                UpdateAiming(origin, target, deltaTime, now);
                return;
            }

            if (HandleDoorsOnRoute(origin, now))
            {
                return;
            }

            // Stand to walk; take the spot's posture for the last stretch.
            UpdateWalkPosture(origin, RemainingRouteMeters(GetFeet(origin)) <= WalkStandBeyondMeters);

            // Walking along the NavMesh route.
            var corner = _walkCorners[_walkCornerIndex];
            if (IsCornerReached(GetFeet(origin), corner))
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
                    LogWalkStall(origin);
                    if (TryHandleBlocker(origin))
                    {
                        // Detour in place (or the walk stopped: nothing gets around the blocker).
                        _walkProgressPosition = origin.position;
                        _walkProgressTime = now;
                        _nextWalkReplanTime = now + WalkReplanSeconds;
                        return;
                    }

                    if (_walkStuckReplans > WalkMaxStuckReplans)
                    {
                        StopAutoWalk("stuck");
                        SpeakWalk($"Stuck. {GetSpokenName(target)} is {DescribeWhere(origin, nearest)}");
                        return;
                    }

                    if (!PlanWalk(GetFeet(origin), target, $"replan (stuck {_walkStuckReplans})"))
                    {
                        StopAutoWalk("no path on replan");
                        SpeakWalk($"No path to {GetSpokenName(target)}");
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

        if (UpdateWalkPosture(origin, finalStretch: true))
        {
            // The eye is moving to the spot's height; aim from there once it has settled.
            _walkAimStartTime = now;
            _walkAimedTime = -1f;
            return;
        }

        var remaining = AimAt(ResolveInteractionAimPoint(target), deltaTime);
        if (remaining <= AimSettleDegrees && _walkAimedTime < 0f)
        {
            _walkAimedTime = now;
        }

        // A dock for the held item is never the game's observedInteractable (no log has one; the held
        // record walk failed "aimed, game observes nothing" at all 11 spots). The game picks the dock in
        // FsPlaceHandler when Place is used, so arrival here is the view itself pointing at the dock.
        if (_walkAimedTime >= 0f && _placesForHeldIds.Contains(target.GetInstanceID()) && IsViewOnTarget(target))
        {
            StopAutoWalk("arrived (view on dock for held item)");
            SpeakInRangeWithAction(target);
            return;
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
            if (PlanWalk(GetFeet(origin), target, $"next spot {_walkStandpointIndex + 1}"))
            {
                return;
            }
        }

        StopAutoWalk($"no spot worked (game observes {observed})");
        SpeakWalk($"Could not reach {GetSpokenName(target)}. It is {DescribeWhere(origin, GetNearestPoint(target, origin.position))}. Pointing at {observed}");
    }

    /// <summary>The game's own move mode: FlatScreenPlayer.currentMoveMode is CROUCH.</summary>
    private static bool IsCrouching()
    {
        var player = Il2Cpp.FlatScreenPlayer.instance;
        return player != null && player.currentMoveMode == Il2Cpp.PlayerMoveModes.CROUCH;
    }

    /// <summary>
    /// The game's eye heights above the feet: PlayerBase.playerHeight, _maxPlayerHeight standing (1.6 in
    /// SCN_Main) and _minPlayerHeight crouched (0.9); the logs measured 1.60 and 0.90.
    /// </summary>
    private static bool TryGetEyeHeights(out float standing, out float crouched)
    {
        standing = crouched = 0f;
        var player = Il2Cpp.FlatScreenPlayer.instance;
        if (player == null)
        {
            return false;
        }

        var height = player.playerHeight;
        standing = height._maxPlayerHeight;
        crouched = height._minPlayerHeight;
        return standing > 0f && crouched > 0f;
    }

    /// <summary>The player's eye height above the feet now, for the posture the game reports.</summary>
    private static float CurrentEyeHeight(Transform origin)
    {
        if (TryGetEyeHeights(out var standing, out var crouched))
        {
            return IsCrouching() ? crouched : standing;
        }

        var camera = GetInteractorCamera();
        return camera == null ? 1.6f : camera.transform.position.y - GetFeet(origin).y;
    }

    /// <summary>The game's foot location (PlayerBase.GetFootLocation); the player transform is not at the feet.</summary>
    private static Vector3 GetFeet(Transform origin)
    {
        var player = origin.GetComponent<Il2Cpp.FlatScreenPlayer>() ?? Il2Cpp.FlatScreenPlayer.instance;
        if (player != null)
        {
            return player.GetFootLocation();
        }

        LogOnce("walk-no-player-feet", "[walk] FlatScreenPlayer.instance is null; using the player transform for the feet");
        return origin.position;
    }

    /// <summary>Spots for the current posture into _walkStandpoints.</summary>
    private void FindStandpoints(Transform origin, Transform target)
    {
        FindStandpoints(origin, target, CurrentEyeHeight(origin), _walkStandpoints, IsCrouching() ? "crouching" : "standing");
    }

    /// <summary>
    /// NavMesh points around the target from which the target can be used at <paramref name="eyeHeight"/>,
    /// nearest to the player first. The eye there must be within fsMaxReachDistance of the target's nearest
    /// collider point, the look must be inside the game's pitch limit, and a raycast toward the aim point
    /// with the game's interaction mask must hit one of the target's own colliders.
    /// Floor heights at each probe come from the walking graph (every floor under that x/z). Before, the
    /// probe was sampled at the player's own height, so a target on another floor got 0 spots.
    /// </summary>
    private void FindStandpoints(Transform origin, Transform target, float eyeHeight, List<Vector3> spots, string posture)
    {
        spots.Clear();
        if (!TryGetGameInteractable(target, out var interactable))
        {
            return;
        }

        var aim = ResolveInteractionAimPoint(target);
        var reach = interactable.fsMaxReachDistance;
        int mask = Il2Cpp.ControllerBaseInteractor.raycastLayerMask.value;
        // The game clamps the view's pitch to CameraHandler.verticalViewAngleLimit (Seated applies in a chair).
        // Spots that need a steeper look never settle: the record player run stalled 25-60 degrees short at
        // every close spot ("view did not settle"), because the turntable is low and the spots were next to it.
        var pitchLimit = Il2Cpp.CameraHandler.verticalViewAngleLimit;
        LogOnce($"walk-pitch-limit|{pitchLimit}|{Il2Cpp.CameraHandler.verticalViewAngleLimitSeated}",
            $"[walk] view pitch limit {pitchLimit:0.#} degrees (seated {Il2Cpp.CameraHandler.verticalViewAngleLimitSeated:0.#})");
        // A floor can hold a usable spot only if an eye above it can be within reach of the aim point.
        var lowestFloor = aim.y - eyeHeight - reach;
        var highestFloor = aim.y - eyeHeight + reach;
        var graph = GetLiveNavGraph();
        if (graph == null)
        {
            return;
        }

        var floors = new List<float>();
        var tested = 0;
        var tooSteep = 0;
        for (var radius = 0.35f; radius <= reach + 0.01f; radius += 0.25f)
        {
            for (var step = 0; step < 16; step++)
            {
                var angle = step * Mathf.PI * 2f / 16f;
                var x = aim.x + Mathf.Cos(angle) * radius;
                var z = aim.z + Mathf.Sin(angle) * radius;
                floors.Clear();
                graph.FloorHeightsAt(x, z, floors);
                foreach (var floorY in floors)
                {
                    if (floorY < lowestFloor || floorY > highestFloor)
                    {
                        continue;
                    }

                    var spot = new Vector3(x, floorY, z);
                    if (spots.Exists(p => Vector3.Distance(p, spot) < 0.2f))
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
                    if (pitchLimit > 0f)
                    {
                        var pitch = Mathf.Abs(Mathf.Atan2(toAim.y, new Vector2(toAim.x, toAim.z).magnitude) * Mathf.Rad2Deg);
                        if (pitch > pitchLimit - AimSettleDegrees)
                        {
                            tooSteep++;
                            continue;
                        }
                    }

                    if (!Physics.Raycast(eye, toAim.normalized, out var rayHit, toAim.magnitude + 0.3f, mask)
                        || rayHit.collider == null)
                    {
                        continue;
                    }

                    if (!TryGetGameInteractable(rayHit.collider.transform, out var hitInteractable)
                        || hitInteractable.GetInstanceID() != interactable.GetInstanceID())
                    {
                        // Something else is in the line of sight; remembered so a walk with no spot can say
                        // what hides the target (a closed drawer, the slot holding it).
                        NoteSightBlocker(rayHit.collider);
                        continue;
                    }

                    spots.Add(spot);
                }
            }
        }

        // 3D distance: a spot on the floor above can be right over the player.
        var from = origin.position;
        spots.Sort((a, b) => Vector3.Distance(from, a).CompareTo(Vector3.Distance(from, b)));
        MelonLoader.MelonLogger.Msg($"[walk] {spots.Count} usable spots {posture} for {GetSpokenName(target)} ({tested} NavMesh points tested, {tooSteep} past the {pitchLimit:0.#} degree view limit, reach {reach:0.00} m, eye {eyeHeight:0.00} m)");
    }

    /// <summary>
    /// The camera's forward ray, with the game's interaction mask, first hits one of the target's own
    /// colliders within the target's fsMaxReachDistance.
    /// </summary>
    private static bool IsViewOnTarget(Transform target)
    {
        var camera = GetInteractorCamera();
        if (camera == null || !TryGetGameInteractable(target, out var interactable))
        {
            return false;
        }

        int mask = Il2Cpp.ControllerBaseInteractor.raycastLayerMask.value;
        var view = camera.transform;
        return Physics.Raycast(view.position, view.forward, out var hit, interactable.fsMaxReachDistance, mask)
            && hit.collider != null
            && TryGetGameInteractable(hit.collider.transform, out var hitInteractable)
            && hitInteractable.GetInstanceID() == interactable.GetInstanceID();
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
        SetViewRotation(Quaternion.RotateTowards(current, desired, AimDegreesPerSecond * deltaTime));
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
        SetViewRotation(Quaternion.RotateTowards(current, desired, AutoTurnDegreesPerSecond * deltaTime));
    }

    private static Il2Cpp.CameraHandler? _cameraHandler;

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
    /// Sets the view through CameraHandler.ResetToRotation, the only route the game's look update keeps.
    /// A fallback that wrote playerBody/cameraHinge directly was removed: its "view did not turn" check
    /// compared remaining angles while the walk's goal direction changed at each corner (22 to 161 degrees),
    /// so it tripped in nearly every session, and once on, the game overwrote those transforms and every
    /// standing spot timed out with "view did not settle" (logs 2026-11-05 to 11-19: 24 arrivals and 4 spot
    /// failures before the switch, about 236 spot failures after).
    /// </summary>
    private static void SetViewRotation(Quaternion rotation)
    {
        GetCameraHandler()?.ResetToRotation(rotation);
    }

    /// <summary>
    /// The game observes the target. Requiring a performable action as well failed walks that had reached
    /// the target (2026-11-19 run: cassette slot observed with Dock=no/Undock=no for a tape that does not
    /// fit, notes and post-its observed with PickUp=no while the hands were full); the arrival speech says
    /// what can be done, or that nothing can right now.
    /// </summary>
    private bool IsReadyToUse(Transform target)
    {
        return IsObservedByGame(target);
    }

    private void SpeakInRangeWithAction(Transform target)
    {
        var actions = GetPerformableVerbs(target);
        if (_placesForHeldIds.Contains(target.GetInstanceID()) && TryGetHeld(out var held, out _) && held != null)
        {
            actions.Insert(0, $"Fits {GetSpokenName(held)}, {DescribePlaceKeys()}");
        }

        var name = GetSpokenName(target);
        SpeakWalk(actions.Count == 0 ? $"{name} in range. Nothing to do with it right now" : $"{name} in range. {string.Join(". ", actions)}");
    }

    private void StopAutoWalk(string reason)
    {
        if (!_isAutoWalking)
        {
            return;
        }

        MelonLoader.MelonLogger.Msg($"[walk] stop: {reason}");
        _lastWalkStopReason = reason;
        _isAutoWalking = false;
        _autoWalkTarget = null;
        _walkAiming = false;
        _walkCorners.Clear();
        ClearContainerPlan();
    }

    private void StopAutoWalk(bool speakCancelled)
    {
        StopAutoWalk(speakCancelled ? "cancelled" : "interrupted");
        if (speakCancelled)
        {
            SpeakWalk("Auto walk cancelled");
        }
    }

    private static List<NavGraph>? _navGraphs;
    private static NavGraph? _liveNavGraph;
    private static string? _liveNavGraphKey;

    /// <summary>
    /// Each scene's NavMesh asset, from the scenes' NavMeshSettings.m_NavMeshData in the rip (the game
    /// will not be updated, so this is final).
    /// </summary>
    private static readonly Dictionary<string, string> NavGraphByScene = new()
    {
        ["SCN_Main"] = "NavMesh_0",
        ["SCN_Intro"] = "NavMesh_1",
        ["SCN_Menu"] = "NavMesh_1",
        ["SCN_Epilogue"] = "NavMesh",
    };

    /// <summary>The walking graph of the loaded scene that has one; looked up again when the loaded scenes change.</summary>
    private static NavGraph? GetLiveNavGraph()
    {
        var key = string.Empty;
        string? graphName = null;
        for (var i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).name;
            key += scene + "|";
            if (graphName == null && NavGraphByScene.TryGetValue(scene, out var name))
            {
                graphName = name;
            }
        }

        if (key == _liveNavGraphKey)
        {
            return _liveNavGraph;
        }

        _liveNavGraphKey = key;
        try
        {
            _navGraphs ??= NavGraph.LoadAll();
        }
        catch (Exception ex)
        {
            MelonLoader.MelonLogger.Warning($"[nav] could not load the walking graphs: {ex.Message}");
            _navGraphs = new List<NavGraph>();
        }

        _liveNavGraph = graphName == null ? null : _navGraphs.Find(g => g.Name == graphName);
        MelonLoader.MelonLogger.Msg(_liveNavGraph != null
            ? $"[nav] scenes {key.TrimEnd('|')}: walking graph {_liveNavGraph.Name} ({_liveNavGraph.PolygonCount} polygons)"
            : $"[nav] scenes {key.TrimEnd('|')}: no walking graph");
        return _liveNavGraph;
    }

    /// <summary>
    /// Where a stalled walk is relative to its route (Date Everything Access logs the same for stall
    /// triage): feet, the leg being walked, how far off that leg the feet are, the nearest closed room
    /// door, and whether the feet are still on the walking graph.
    /// </summary>
    private void LogWalkStall(Transform origin)
    {
        var feet = GetFeet(origin);
        var legStart = _walkCornerIndex > 0 ? _walkCorners[_walkCornerIndex - 1] : _walkProgressPosition;
        var corner = _walkCornerIndex < _walkCorners.Count ? _walkCorners[_walkCornerIndex] : feet;
        var leg = new Vector2(corner.x - legStart.x, corner.z - legStart.z);
        var toFeet = new Vector2(feet.x - legStart.x, feet.z - legStart.z);
        var t = leg.sqrMagnitude < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(toFeet, leg) / leg.sqrMagnitude);
        var offRoute = (toFeet - leg * t).magnitude;
        var graph = GetLiveNavGraph();
        var onMesh = graph != null && graph.TryClosestPoint(feet, 0.3f, out var meshPoint)
            ? $"on the walking graph ({Vector3.Distance(meshPoint, feet):0.00} m)"
            : "off the walking graph";
        var doorText = "no closed door within 2 m";
        foreach (var door in _walkDoors)
        {
            if (door != null && door.rigidBody != null && !door.IsFullyOpen()
                && Vector3.Distance(door.rigidBody.worldCenterOfMass, feet) < 2f)
            {
                doorText = $"closed door {GetSpokenName(door.transform)} {Vector3.Distance(door.rigidBody.worldCenterOfMass, feet):0.0} m away (open {door.HowOpen():0.00}, locked {door.IsLocked()})";
                break;
            }
        }

        MelonLoader.MelonLogger.Msg($"[walk] stall {_walkStuckReplans}: feet {feet}, corner {_walkCornerIndex + 1} of {_walkCorners.Count} at {corner} ({PlanarDistance(feet, corner):0.00} m, height {corner.y - feet.y:+0.00;-0.00}), {offRoute:0.00} m off the leg, {t:0%} along it, {onMesh}, {doorText}");
    }

    /// <summary>Within reach of the corner in x/z, at its height (feet and corner are both floor points).</summary>
    private static bool IsCornerReached(Vector3 feet, Vector3 corner)
    {
        return PlanarDistance(feet, corner) <= WalkCornerReachedMeters
            && Math.Abs(feet.y - corner.y) <= WalkCornerReachedHeightMeters;
    }

    private static float RouteLength(Vector3 start, List<Vector3> corners)
    {
        var total = 0f;
        var from = start;
        foreach (var corner in corners)
        {
            total += Vector3.Distance(from, corner);
            from = corner;
        }

        return total;
    }

    private static float PlanarDistance(Vector3 a, Vector3 b)
    {
        return Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
    }
}
