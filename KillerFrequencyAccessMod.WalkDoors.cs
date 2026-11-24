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
        JoinFloorsAtDoorways();
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
    /// Il2Cpp.RoomPortal (plane and centre). The player's CharacterController.stepOffset (0.3 m, rip) is the
    /// largest height step allowed. Already joined graphs report "already connected" and change nothing.
    /// </summary>
    private static void JoinFloorsAtDoorways()
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
            if (graph.TryConnectAcross(sideA, sideB, plane, DoorwaySnapMeters, 0.3f, out var result))
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
            return PushDoorOpen(origin, now);
        }

        if (!TryFindNextDoorOnRoute(GetFeet(origin), out var door, out var alongRoute) || alongRoute > DoorOpenAtRouteMeters)
        {
            return false;
        }

        if (door.IsFullyOpen())
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

        var handler = door.fsBoundedMotionInteraction?.motionHandler?.TryCast<Il2Cpp.FsArcSegmentMotionHandler>();
        if (handler == null || handler.maxAngularVelocity <= 0f)
        {
            MelonLoader.MelonLogger.Msg($"[walk] {name} has no FsArcSegmentMotionHandler speed to open it with");
            StopAutoWalk("door has no motion handler");
            SpeakWalk($"{name} is closed. Open it, then walk again");
            return true;
        }

        HoldDoorOpen(door);
        _walkOpeningDoor = door;
        _walkDoorOpenStartTime = now;
        _walkDoorOpenStartAmount = door.HowOpen();
        _walkDoorSpeed = handler.maxAngularVelocity;
        _walkDoorSign = PushAwaySign(door, origin.position);
        _walkDoorReversed = false;
        MelonLoader.MelonLogger.Msg($"[walk] opening {name} ({alongRoute:0.0} m ahead on the route, open {_walkDoorOpenStartAmount:0.00}, {_walkDoorSpeed:0.0} rad/s)");
        _walkDoorsOpened++;
        SpeakWalk($"Opening {name}");
        return true;
    }

    /// <summary>Turns the door about its hinge each frame until it is open, then lets the walk go on.</summary>
    private bool PushDoorOpen(Transform origin, float now)
    {
        var door = _walkOpeningDoor!;
        if (door == null || door.rigidBody == null || door.hinge == null)
        {
            _walkOpeningDoor = null;
            return false;
        }

        var amount = door.HowOpen();
        var name = GetSpokenName(door.transform);
        var elapsed = now - _walkDoorOpenStartTime;
        if (door.IsFullyOpen())
        {
            MelonLoader.MelonLogger.Msg($"[walk] {name} open ({amount:0.00}) after {elapsed:0.0} s{(_walkDoorReversed ? ", pulled" : ", pushed")}");
            FinishDoorPush(door, now);
            return false;
        }

        if (!_walkDoorReversed && elapsed >= DoorReverseAfterSeconds && amount < _walkDoorOpenStartAmount + 0.05f)
        {
            // It did not swing away from the player: this door opens the other way.
            MelonLoader.MelonLogger.Msg($"[walk] {name} did not move when pushed ({amount:0.00}); pulling instead");
            _walkDoorSign = -_walkDoorSign;
            _walkDoorReversed = true;
            _walkDoorOpenStartTime = now;
            _walkDoorOpenStartAmount = amount;
            elapsed = 0f;
        }

        if (elapsed < DoorOpenWaitSeconds)
        {
            var axis = door.hinge.transform.TransformDirection(door.hinge.axis).normalized;
            door.rigidBody.WakeUp();
            door.rigidBody.angularVelocity = axis * (_walkDoorSign * _walkDoorSpeed);
            return true;
        }

        if (amount > _walkDoorOpenStartAmount + 0.2f)
        {
            // Open enough to pass; it may be stopped against something.
            MelonLoader.MelonLogger.Msg($"[walk] {name} partly open ({_walkDoorOpenStartAmount:0.00} to {amount:0.00}) after {DoorOpenWaitSeconds:0} s; walking on");
            FinishDoorPush(door, now);
            return false;
        }

        MelonLoader.MelonLogger.Msg($"[walk] {name} did not open: pushed and pulled, HowOpen at {amount:0.00} (was {_walkDoorOpenStartAmount:0.00})");
        door.rigidBody.angularVelocity = Vector3.zero;
        _walkOpeningDoor = null;
        StopAutoWalk("door did not open");
        SpeakWalk($"{name} is closed and did not open. Open it, then walk again");
        return true;
    }

    private void FinishDoorPush(Il2Cpp.DoorOpenable door, float now)
    {
        door.rigidBody.angularVelocity = Vector3.zero;
        _walkOpeningDoor = null;
        _nextWalkReplanTime = now + WalkReplanSeconds;
        _walkProgressTime = now;
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
