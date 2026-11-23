using System;
using System.Collections.Generic;
using UnityEngine;

namespace Killer_Frequency_Access;

/// <summary>
/// Doors on the auto-walk route (the user agreed 2026-11-23: open unlocked doors along the way, never
/// locked ones). The NavMesh runs through doorways whatever the door's state, so a closed door stopped
/// walks as "stuck" (door wc, first floor main studio door, door dumpster in the 2026-11-22 run).
///
/// Game facts used:
/// - Room doors are Il2Cpp.RoomPortalDoor: its plane (RoomPortal.plane) and half size (extents) give the
///   doorway; _door is the Il2Cpp.DoorOpenable.
/// - Openable.IsLocked(), DoorOpenable.IsFullyOpen() / HowOpen(), and Openable.Open(). DoorOpenable does
///   not override Open(), and whether the base Open() swings a hinged door is unverified, so every open is
///   checked: if HowOpen() has not risen after DoorOpenWaitSeconds the walk stops and says so.
/// Like Date Everything Access, the walk stops short of the doorway while the door opens, so the player is
/// not standing in the swing.
/// </summary>
public sealed partial class KillerFrequencyAccessMod
{
    private const float DoorOpenAtRouteMeters = 1.5f;
    private const float DoorOpenWaitSeconds = 3f;
    private const float DoorPortalMarginMeters = 0.3f;

    private readonly List<Il2Cpp.RoomPortalDoor> _walkDoors = new();
    private Il2Cpp.DoorOpenable? _walkOpeningDoor;
    private float _walkDoorOpenStartTime;
    private float _walkDoorOpenStartAmount;
    private int _walkDoorsOpened;

    private void CollectWalkDoors()
    {
        _walkDoors.Clear();
        _walkOpeningDoor = null;
        _walkDoorsOpened = 0;
        foreach (var portal in UnityEngine.Object.FindObjectsOfType<Il2Cpp.RoomPortalDoor>())
        {
            if (portal != null && portal._door != null)
            {
                _walkDoors.Add(portal);
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
            return WaitForDoor(now);
        }

        if (!TryFindNextDoorOnRoute(GetFeet(origin), out var portal, out var alongRoute) || alongRoute > DoorOpenAtRouteMeters)
        {
            return false;
        }

        var door = portal._door;
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

        _walkOpeningDoor = door;
        _walkDoorOpenStartTime = now;
        _walkDoorOpenStartAmount = door.HowOpen();
        MelonLoader.MelonLogger.Msg($"[walk] opening {name} ({alongRoute:0.0} m ahead on the route, open {_walkDoorOpenStartAmount:0.00})");
        door.Open();
        _walkDoorsOpened++;
        SpeakWalk($"Opening {name}");
        return true;
    }

    private bool WaitForDoor(float now)
    {
        var door = _walkOpeningDoor!;
        if (door == null)
        {
            _walkOpeningDoor = null;
            return false;
        }

        var amount = door.HowOpen();
        if (door.IsFullyOpen())
        {
            MelonLoader.MelonLogger.Msg($"[walk] {GetSpokenName(door.transform)} open ({amount:0.00}) after {now - _walkDoorOpenStartTime:0.0} s");
            _walkOpeningDoor = null;
            _nextWalkReplanTime = now + WalkReplanSeconds;
            _walkProgressTime = now;
            return false;
        }

        if (now - _walkDoorOpenStartTime < DoorOpenWaitSeconds)
        {
            return true;
        }

        var name = GetSpokenName(door.transform);
        _walkOpeningDoor = null;
        if (amount > _walkDoorOpenStartAmount + 0.2f)
        {
            // Open enough to pass; it may still be swinging or stopped against something.
            MelonLoader.MelonLogger.Msg($"[walk] {name} partly open ({_walkDoorOpenStartAmount:0.00} to {amount:0.00}) after {DoorOpenWaitSeconds:0} s; walking on");
            _walkProgressTime = now;
            return false;
        }

        MelonLoader.MelonLogger.Msg($"[walk] {name} did not open: Open() left HowOpen at {amount:0.00} (was {_walkDoorOpenStartAmount:0.00})");
        StopAutoWalk("door did not open");
        SpeakWalk($"{name} is closed and did not open. Open it, then walk again");
        return true;
    }

    /// <summary>
    /// The first room doorway the remaining route passes through: a route leg (feet, then the remaining
    /// corners) whose ends lie on opposite sides of the portal's plane, crossing it within the portal's
    /// half width (plus a margin) of its centre and within its half height. Distance is along the route.
    /// </summary>
    private bool TryFindNextDoorOnRoute(Vector3 feet, out Il2Cpp.RoomPortalDoor portal, out float alongRoute)
    {
        portal = null!;
        alongRoute = 0f;
        var from = feet;
        for (var i = _walkCornerIndex; i < _walkCorners.Count; i++)
        {
            var to = _walkCorners[i];
            Il2Cpp.RoomPortalDoor? best = null;
            var bestT = float.MaxValue;
            foreach (var candidate in _walkDoors)
            {
                if (candidate == null || !candidate.gameObject.activeInHierarchy)
                {
                    continue;
                }

                var plane = candidate.plane;
                var da = plane.GetDistanceToPoint(from);
                var db = plane.GetDistanceToPoint(to);
                if (da * db > 0f || Math.Abs(da - db) < 1e-4f)
                {
                    continue;
                }

                var t = da / (da - db);
                var crossing = Vector3.Lerp(from, to, t);
                var centre = candidate.transform.position;
                var extents = candidate.extents;
                var lateral = crossing - centre;
                lateral.y = 0f;
                if (lateral.magnitude > extents.x + DoorPortalMarginMeters || Math.Abs(crossing.y - centre.y) > extents.y + DoorPortalMarginMeters)
                {
                    continue;
                }

                if (t < bestT)
                {
                    best = candidate;
                    bestT = t;
                }
            }

            if (best != null)
            {
                portal = best;
                alongRoute += Vector3.Distance(from, to) * bestT;
                return true;
            }

            alongRoute += Vector3.Distance(from, to);
            from = to;
        }

        return false;
    }
}
