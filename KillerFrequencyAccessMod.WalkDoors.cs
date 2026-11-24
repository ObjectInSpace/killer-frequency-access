using System;
using System.Collections.Generic;
using UnityEngine;

namespace Killer_Frequency_Access;

/// <summary>
/// Doors on the auto-walk route (the user agreed 2026-11-23: open unlocked doors along the way, never
/// locked ones). The NavMesh runs through doorways whatever the door's state, so a closed door blocks
/// the walk unless it is opened.
///
/// Game facts used:
/// - Doors are Il2Cpp.DoorOpenable: a Rigidbody (rigidBody) on a HingeJoint (hinge). It does not override
///   Openable.Open(), and the base Open() does not move it (2026-11-23 log: HowOpen stayed at 0.01 on
///   every door). The player opens one by dragging: FsArcSegmentMotionHandler turns the hinge spring and
///   the body's gravity off while dragging (originalHingeUseSpringValue, originalRbUseGravityValue) and
///   adds torque, capped at its maxAngularVelocity. The walk does the same: spring and gravity off,
///   the body turned about the hinge axis at the handler's maxAngularVelocity.
/// - A door is on the route when a route leg passes through one of its colliders (the colliders under
///   its Rigidbody). Many doors are not room portals (door wc stalled walks with no open attempt), so
///   portals are not used.
/// - Openable.IsLocked(), DoorOpenable.IsFullyOpen() / HowOpen().
/// Like Date Everything Access, the walk stops short of the door while it opens, and pushes it away from
/// the player first; a door that does not move that way is pulled instead.
/// The spring and gravity stay off until the player is past the door, so self-closing doors do not swing
/// shut on the way through.
/// </summary>
public sealed partial class KillerFrequencyAccessMod
{
    private const float DoorOpenAtRouteMeters = 1.5f;
    private const float DoorLookAheadMeters = 2.5f;
    private const float DoorOpenWaitSeconds = 3f;
    private const float DoorReverseAfterSeconds = 0.6f;
    private const float DoorPastMeters = 2.5f;
    private const float DoorNearMeters = 1.2f;
    private static readonly float[] DoorProbeHeights = { 0.4f, 1.1f };

    private sealed class HeldDoor
    {
        public Il2Cpp.DoorOpenable Door = null!;
        public bool UseSpring;
        public bool UseGravity;
        public bool WasNear;
    }

    private readonly List<Il2Cpp.DoorOpenable> _walkDoors = new();
    private readonly Dictionary<int, Il2Cpp.DoorOpenable> _doorByColliderId = new();
    private readonly List<HeldDoor> _heldOpenDoors = new();
    private int _doorLayerMask;
    private Il2Cpp.DoorOpenable? _walkOpeningDoor;
    private float _walkDoorOpenStartTime;
    private float _walkDoorOpenStartAmount;
    private float _walkDoorSign;
    private bool _walkDoorReversed;
    private float _walkDoorSpeed;
    private int _walkDoorsOpened;

    private void CollectWalkDoors()
    {
        _walkDoors.Clear();
        _doorByColliderId.Clear();
        _doorLayerMask = 0;
        _walkOpeningDoor = null;
        _walkDoorsOpened = 0;
        foreach (var door in UnityEngine.Object.FindObjectsOfType<Il2Cpp.DoorOpenable>())
        {
            if (door == null || door.rigidBody == null || door.hinge == null)
            {
                continue;
            }

            _walkDoors.Add(door);
            foreach (var collider in door.rigidBody.GetComponentsInChildren<Collider>())
            {
                if (collider != null && !collider.isTrigger)
                {
                    _doorByColliderId[collider.GetInstanceID()] = door;
                    _doorLayerMask |= 1 << collider.gameObject.layer;
                }
            }
        }

        LogOnce($"walk-doors|{_walkDoors.Count}", $"[walk] {_walkDoors.Count} hinged doors, {_doorByColliderId.Count} door colliders, layer mask 0x{_doorLayerMask:X}");
        var step = GetFlatScreenPlayer()?.characterController?.stepOffset ?? 0f;
        if (step <= 0f)
        {
            LogOnce("walk-no-step", "[nav] CharacterController.stepOffset unreadable; floor pieces not joined");
            return;
        }

        try
        {
            JoinFloorsAtDoorways(step);
            JoinFloorGaps(step);
        }
        catch (Exception ex)
        {
            LogOnce($"join-error|{ex.Message}", $"[nav] joining floor pieces failed: {ex}");
        }
    }

    /// <summary>
    /// Whether moving a route end from <paramref name="from"/> to <paramref name="to"/> crosses nothing solid:
    /// a line at knee height through the game's colliders, doors excluded (the walk opens them).
    /// </summary>
    private bool IsFloorMoveClear(Vector3 from, Vector3 to)
    {
        var up = Vector3.up * DoorProbeHeights[0];
        var mask = Physics.DefaultRaycastLayers & ~_doorLayerMask;
        if (!Physics.Linecast(from + up, to + up, out var hit, mask, QueryTriggerInteraction.Ignore))
        {
            return true;
        }

        LogOnce($"snap-blocked|{hit.collider.name}", $"[walk] not moving the route end {Vector3.Distance(from, to):0.0} m: {BuildHierarchyPath(hit.collider.transform)} is in the way");
        return false;
    }

    private const float DoorwaySideMeters = 1.0f;
    private const float DoorwaySnapMeters = 1.5f;

    /// <summary>
    /// Joins floor pieces the bake split at a doorway, through the game's own doorways: every
    /// Il2Cpp.RoomPortal (plane and centre). The player's CharacterController.stepOffset (0.3 m in the rip, read live) is the
    /// largest height step allowed. Already joined graphs report "already connected" and change nothing.
    /// </summary>
    private static void JoinFloorsAtDoorways(float step)
    {
        var graph = GetLiveNavGraph();
        if (graph == null)
        {
            return;
        }

        foreach (var portal in UnityEngine.Object.FindObjectsOfType<Il2Cpp.RoomPortal>())
        {
            if (portal == null)
            {
                continue;
            }

            var plane = portal.plane;
            var centre = portal.transform.position;
            centre -= plane.normal * plane.GetDistanceToPoint(centre);
            var sideA = centre + plane.normal * DoorwaySideMeters;
            var sideB = centre - plane.normal * DoorwaySideMeters;
            if (graph.TryConnectAcross(sideA, sideB, plane, DoorwaySnapMeters, step, out var result))
            {
                MelonLoader.MelonLogger.Msg($"[nav] doorway {portal.name}: {result}");
            }
            else if (result != "already connected")
            {
                LogOnce($"doorway|{graph.Name}|{portal.name}|{result}", $"[nav] doorway {portal.name} not joined: {result}");
            }
        }
    }

    /// <summary>
    /// Handles the next closed door on the rest of the route. True while the walk must not move this frame
    /// (waiting for a door to open) or has been stopped.
    /// </summary>
    private bool HandleDoorsOnRoute(Transform origin, float now)
    {
        if (_walkOpeningDoor != null)
        {
            switch (StepDoorPush(now, out var report))
            {
                case DoorPushState.Pushing:
                    return true;
                case DoorPushState.Failed:
                    var failed = GetSpokenName(_walkOpeningDoor.transform);
                    MelonLoader.MelonLogger.Msg($"[walk] {failed} did not open: {report}");
                    _walkOpeningDoor = null;
                    StopAutoWalk("door did not open");
                    SpeakWalk($"{failed} is closed and did not open. Open it, then walk again");
                    return true;
                default:
                    MelonLoader.MelonLogger.Msg($"[walk] {GetSpokenName(_walkOpeningDoor.transform)} {report}");
                    _walkOpeningDoor = null;
                    _nextWalkReplanTime = now + WalkReplanSeconds;
                    _walkProgressTime = now;
                    return false;
            }
        }

        if (!TryFindNextDoorOnRoute(GetFeet(origin), out var door, out var alongRoute) || alongRoute > DoorOpenAtRouteMeters)
        {
            return false;
        }

        if (IsDoorOpenByGame(door))
        {
            return false;
        }

        var name = GetSpokenName(door.transform);
        if (door.IsLocked())
        {
            MelonLoader.MelonLogger.Msg($"[walk] {name} on the route is locked; stopping {alongRoute:0.0} m before it");
            var target = _autoWalkTarget;
            StopAutoWalk("door on route locked");
            SpeakWalk($"{name} is locked{(target != null ? $". It is on the way to {GetSpokenName(target)}" : string.Empty)}");
            return true;
        }

        if (!TryBeginDoorPush(door, origin.position, now, out var why))
        {
            MelonLoader.MelonLogger.Msg($"[walk] {name}: {why}");
            StopAutoWalk("door cannot be pushed");
            SpeakWalk($"{name} is closed. Open it, then walk again");
            return true;
        }

        MelonLoader.MelonLogger.Msg($"[walk] opening {name} ({alongRoute:0.0} m ahead on the route, {why})");
        _walkDoorsOpened++;
        SpeakWalk($"Opening {name}");
        return true;
    }

    private enum DoorPushState
    {
        Pushing,
        Open,
        PartlyOpen,
        Failed,
    }

    /// <summary>
    /// The mod's own pace for swinging a door (radians per second), within the game's cap
    /// (FsArcSegmentMotionHandler.maxAngularVelocity, 30 in SCN_Main). At the cap a cabinet door slammed
    /// and the coroner's report inside was lost from the walk ("target gone", 2026-11-23 run).
    /// </summary>
    private const float DoorPushRadiansPerSecond = 2f;
    private const float DoorOpenDegrees = 80f;
    private const float DoorPartlyOpenDegrees = 30f;
    private const float DoorMovedDegrees = 5f;

    private Quaternion _walkDoorStartRotation;

    /// <summary>
    /// The game's own "open": IsFullyOpen() while HowOpen() is a real number. Some cabinet doors read
    /// HowOpen as infinity or 0.00 (2026-11-23 run), so it is not trusted then.
    /// </summary>
    private static bool IsDoorOpenByGame(Il2Cpp.DoorOpenable door)
    {
        var amount = door.HowOpen();
        return !float.IsNaN(amount) && !float.IsInfinity(amount) && door.IsFullyOpen();
    }

    /// <summary>Starts pushing a hinged door open (away from <paramref name="player"/> first). The reason out says why not, or the speed.</summary>
    private bool TryBeginDoorPush(Il2Cpp.DoorOpenable door, Vector3 player, float now, out string report)
    {
        var handler = door.fsBoundedMotionInteraction?.motionHandler?.TryCast<Il2Cpp.FsArcSegmentMotionHandler>();
        if (door.rigidBody == null || door.hinge == null || handler == null || handler.maxAngularVelocity <= 0f)
        {
            report = "no hinge, rigidbody or FsArcSegmentMotionHandler to open it with";
            return false;
        }

        HoldDoorOpen(door);
        _walkOpeningDoor = door;
        _walkDoorOpenStartTime = now;
        _walkDoorStartRotation = door.rigidBody.transform.rotation;
        _walkDoorSpeed = Mathf.Min(DoorPushRadiansPerSecond, handler.maxAngularVelocity);
        _walkDoorSign = PushAwaySign(door, player);
        _walkDoorReversed = false;
        report = $"HowOpen {door.HowOpen():0.00}, {_walkDoorSpeed:0.0} rad/s";
        return true;
    }

    /// <summary>
    /// One frame of pushing _walkOpeningDoor. Progress is the door's own turn from where it started (its
    /// transform), and the game's IsFullyOpen when HowOpen is a real number. A door that has not turned
    /// DoorMovedDegrees after DoorReverseAfterSeconds is pulled instead.
    /// </summary>
    private DoorPushState StepDoorPush(float now, out string report)
    {
        var door = _walkOpeningDoor!;
        report = string.Empty;
        if (door == null || door.rigidBody == null || door.hinge == null)
        {
            report = "door gone";
            return DoorPushState.Failed;
        }

        var turned = Quaternion.Angle(_walkDoorStartRotation, door.rigidBody.transform.rotation);
        var elapsed = now - _walkDoorOpenStartTime;
        if (IsDoorOpenByGame(door) || turned >= DoorOpenDegrees)
        {
            door.rigidBody.angularVelocity = Vector3.zero;
            report = $"open (turned {turned:0} degrees, HowOpen {door.HowOpen():0.00}) after {elapsed:0.0} s, {(_walkDoorReversed ? "pulled" : "pushed")}";
            return DoorPushState.Open;
        }

        if (!_walkDoorReversed && elapsed >= DoorReverseAfterSeconds && turned < DoorMovedDegrees)
        {
            // It did not swing away from the player: this door opens the other way.
            MelonLoader.MelonLogger.Msg($"[walk] {GetSpokenName(door.transform)} did not move when pushed ({turned:0} degrees); pulling instead");
            _walkDoorSign = -_walkDoorSign;
            _walkDoorReversed = true;
            _walkDoorOpenStartTime = now;
            _walkDoorStartRotation = door.rigidBody.transform.rotation;
            elapsed = 0f;
        }

        if (elapsed < DoorOpenWaitSeconds)
        {
            var axis = door.hinge.transform.TransformDirection(door.hinge.axis).normalized;
            door.rigidBody.WakeUp();
            door.rigidBody.angularVelocity = axis * (_walkDoorSign * _walkDoorSpeed);
            return DoorPushState.Pushing;
        }

        door.rigidBody.angularVelocity = Vector3.zero;
        if (turned >= DoorPartlyOpenDegrees)
        {
            report = $"partly open (turned {turned:0} degrees) after {DoorOpenWaitSeconds:0} s; walking on";
            return DoorPushState.PartlyOpen;
        }

        report = $"pushed and pulled, turned {turned:0} degrees, HowOpen {door.HowOpen():0.00}";
        return DoorPushState.Failed;
    }

    /// <summary>+1 when turning the body about the hinge axis moves the door's middle away from the player.</summary>
    private static float PushAwaySign(Il2Cpp.DoorOpenable door, Vector3 player)
    {
        var hinge = door.hinge;
        var axis = hinge.transform.TransformDirection(hinge.axis).normalized;
        var anchor = hinge.transform.TransformPoint(hinge.anchor);
        var middle = door.rigidBody.worldCenterOfMass;
        var swing = Vector3.Cross(axis, middle - anchor);
        return Vector3.Dot(swing, middle - player) >= 0f ? 1f : -1f;
    }

    /// <summary>Turns the door's spring and gravity off, as the game's drag does, until the player is past it.</summary>
    private void HoldDoorOpen(Il2Cpp.DoorOpenable door)
    {
        var id = door.GetInstanceID();
        foreach (var held in _heldOpenDoors)
        {
            if (held.Door != null && held.Door.GetInstanceID() == id)
            {
                return;
            }
        }

        _heldOpenDoors.Add(new HeldDoor { Door = door, UseSpring = door.hinge.useSpring, UseGravity = door.rigidBody.useGravity });
        door.hinge.useSpring = false;
        door.rigidBody.useGravity = false;
    }

    /// <summary>Gives doors back their spring and gravity once the player is past them or no longer walking.</summary>
    private void UpdateHeldDoors()
    {
        if (_heldOpenDoors.Count == 0 || !TryGetNavigationOrigin(out var origin))
        {
            return;
        }

        var feet = GetFeet(origin);
        for (var i = _heldOpenDoors.Count - 1; i >= 0; i--)
        {
            var held = _heldOpenDoors[i];
            if (held.Door == null || held.Door.rigidBody == null || held.Door.hinge == null)
            {
                _heldOpenDoors.RemoveAt(i);
                continue;
            }

            if (_walkOpeningDoor != null && _walkOpeningDoor.GetInstanceID() == held.Door.GetInstanceID())
            {
                continue;
            }

            var distance = Vector3.Distance(feet, held.Door.rigidBody.worldCenterOfMass);
            held.WasNear |= distance <= DoorNearMeters;
            if ((_isAutoWalking && !(held.WasNear && distance >= DoorPastMeters)) || (!_isAutoWalking && distance < DoorNearMeters))
            {
                continue;
            }

            held.Door.hinge.useSpring = held.UseSpring;
            held.Door.rigidBody.useGravity = held.UseGravity;
            MelonLoader.MelonLogger.Msg($"[walk] {GetSpokenName(held.Door.transform)} released ({distance:0.0} m away)");
            _heldOpenDoors.RemoveAt(i);
        }
    }

    /// <summary>
    /// The first door the remaining route passes through, within DoorLookAheadMeters: a route leg, traced at
    /// knee and chest height above the floor, that hits one of a door's colliders. The walk's own target door
    /// is not one to open. Distance is along the route to the hit.
    /// </summary>
    private bool TryFindNextDoorOnRoute(Vector3 feet, out Il2Cpp.DoorOpenable door, out float alongRoute)
    {
        door = null!;
        alongRoute = 0f;
        if (_doorLayerMask == 0)
        {
            return false;
        }

        var targetDoorId = _autoWalkTarget != null && _autoWalkTarget.GetComponentInParent<Il2Cpp.DoorOpenable>() is { } targetDoor ? targetDoor.GetInstanceID() : 0;
        var from = feet;
        for (var i = _walkCornerIndex; i < _walkCorners.Count && alongRoute <= DoorLookAheadMeters; i++)
        {
            var to = _walkCorners[i];
            var legLength = Vector3.Distance(from, to);
            Il2Cpp.DoorOpenable? best = null;
            var bestDistance = float.MaxValue;
            foreach (var height in DoorProbeHeights)
            {
                var up = Vector3.up * height;
                if (Physics.Linecast(from + up, to + up, out var hit, _doorLayerMask, QueryTriggerInteraction.Ignore)
                    && _doorByColliderId.TryGetValue(hit.collider.GetInstanceID(), out var hitDoor)
                    && hitDoor.GetInstanceID() != targetDoorId
                    && hit.distance < bestDistance)
                {
                    best = hitDoor;
                    bestDistance = hit.distance;
                }
            }

            if (best != null)
            {
                door = best;
                alongRoute += bestDistance;
                return true;
            }

            alongRoute += legLength;
            from = to;
        }

        return false;
    }
}
