using System;
using System.Collections.Generic;
using UnityEngine;

namespace Killer_Frequency_Access;

/// <summary>
/// Posture during auto-walk (the user asked 2026-11-23: stand to walk, since standing is faster, and take
/// whatever posture the object needs at the end).
///
/// One rule, no special cases: every standing spot carries the posture it works in (spots are searched
/// at both of the game's eye heights, PlayerBase.playerHeight 1.6 standing and 0.9 crouched); the walk
/// stands while more than WalkStandBeyondMeters of route remain, then takes the chosen spot's posture.
/// Routes never need crouching: the SCN_Main NavMesh was baked for a 2 m agent (agentHeight 2), and the
/// player's CharacterController is 2 m tall standing (rip), so every route fits a standing player.
///
/// Game facts used: FlatScreenPlayer.currentMoveMode (WALK, CROUCH) and ConfigureForMoveMode, the game's
/// own routine for entering a mode (walkMoveSpeed 2.55 against crouchMoveSpeed 1.25 in SCN_Main);
/// transitioningBetweenMoveModes while the camera moves; PlayerBase.crouchCheckLayerMask, the layers the
/// game checks above a crouched player before standing, so the walk never stands the player up into
/// something. If the player changes posture themselves during a walk, the walk leaves posture alone
/// for the rest of it.
/// </summary>
public sealed partial class KillerFrequencyAccessMod
{
    private const float WalkStandBeyondMeters = 1.5f;
    private const float CrouchedSpotPenaltyMeters = 0.5f;

    /// <summary>Posture each standing spot works in, parallel to _walkStandpoints.</summary>
    private readonly List<bool> _walkStandpointCrouched = new();
    private bool _walkManagesPosture;
    private bool? _walkRequestedCrouch;
    private bool _walkHeadroomBlockedLogged;

    /// <summary>
    /// Spots in both postures, nearest first with crouched spots counted CrouchedSpotPenaltyMeters further
    /// (standing wins a near tie: it is faster and the player usually walks standing). Falls back to the
    /// current posture only when the game's eye heights cannot be read.
    /// </summary>
    private void FindStandpointsBothPostures(Transform origin, Transform target)
    {
        _walkStandpoints.Clear();
        _walkStandpointCrouched.Clear();
        if (!TryGetEyeHeights(out var standing, out var crouched))
        {
            FindStandpoints(origin, target);
            var crouchedNow = IsCrouching();
            foreach (var _ in _walkStandpoints)
            {
                _walkStandpointCrouched.Add(crouchedNow);
            }

            LogOnce("walk-no-eye-heights", "[walk] PlayerBase.playerHeight unreadable; spots for the current posture only");
            return;
        }

        var standingSpots = new List<Vector3>();
        var crouchedSpots = new List<Vector3>();
        FindStandpoints(origin, target, standing, standingSpots, "standing");
        FindStandpoints(origin, target, crouched, crouchedSpots, "crouching");
        var from = origin.position;
        var all = new List<(Vector3 Spot, bool Crouched, float Score)>();
        foreach (var s in standingSpots)
        {
            all.Add((s, false, Vector3.Distance(from, s)));
        }

        foreach (var s in crouchedSpots)
        {
            all.Add((s, true, Vector3.Distance(from, s) + CrouchedSpotPenaltyMeters));
        }

        all.Sort((a, b) => a.Score.CompareTo(b.Score));
        foreach (var (spot, isCrouched, _) in all)
        {
            _walkStandpoints.Add(spot);
            _walkStandpointCrouched.Add(isCrouched);
        }
    }

    private void BeginWalkPosture()
    {
        _walkManagesPosture = TryGetEyeHeights(out _, out _);
        _walkRequestedCrouch = null;
        _walkHeadroomBlockedLogged = false;
    }

    /// <summary>
    /// Applies the posture the walk wants now. True while a posture change is under way (the eye height
    /// is moving, so aiming must wait).
    /// </summary>
    private bool UpdateWalkPosture(Transform origin, bool finalStretch, bool passage = false)
    {
        var player = GetFlatScreenPlayer();
        if (player?.characterController != null)
        {
            LogCrouchedController(player.characterController);
        }

        if (!_walkManagesPosture || player == null)
        {
            return false;
        }

        if (player.transitioningBetweenMoveModes)
        {
            return true;
        }

        var crouchedNow = player.currentMoveMode == Il2Cpp.PlayerMoveModes.CROUCH;
        if (_walkRequestedCrouch.HasValue && _walkRequestedCrouch.Value != crouchedNow)
        {
            // Not the posture the walk set: the player changed it, so the player decides from here.
            _walkManagesPosture = false;
            MelonLoader.MelonLogger.Msg($"[walk] posture: player chose {(crouchedNow ? "crouching" : "standing")}; the walk leaves posture alone");
            return false;
        }

        var spotCrouched = _walkStandpointIndex < _walkStandpointCrouched.Count && _walkStandpointCrouched[_walkStandpointIndex];
        var wantCrouch = passage || (finalStretch && spotCrouched);
        if (wantCrouch == crouchedNow)
        {
            _walkRequestedCrouch = crouchedNow;
            return false;
        }

        if (!wantCrouch && !HasStandingHeadroom(player, origin))
        {
            if (!_walkHeadroomBlockedLogged)
            {
                _walkHeadroomBlockedLogged = true;
                MelonLoader.MelonLogger.Msg("[walk] posture: something above (PlayerBase.crouchCheckLayerMask); staying crouched");
            }

            _walkRequestedCrouch = crouchedNow;
            return false;
        }

        var mode = wantCrouch ? Il2Cpp.PlayerMoveModes.CROUCH : Il2Cpp.PlayerMoveModes.WALK;
        player.currentMoveMode = mode;
        player.ConfigureForMoveMode(mode);
        _walkRequestedCrouch = wantCrouch;
        MelonLoader.MelonLogger.Msg($"[walk] posture: {(wantCrouch ? (passage ? "crouching for a low passage" : "crouching for the spot") : finalStretch ? "standing for the spot" : "standing to walk")} (ConfigureForMoveMode {mode})");
        return true;
    }

    /// <summary>
    /// Room to stand: no collider on the game's crouch-check layers in the standing capsule above the feet
    /// (the player's CharacterController radius and the standing eye height plus a margin).
    /// </summary>
    private static bool HasStandingHeadroom(Il2Cpp.FlatScreenPlayer player, Transform origin)
    {
        if (!TryGetEyeHeights(out var standing, out _))
        {
            return false;
        }

        var controller = player.characterController;
        var radius = controller != null ? controller.radius : 0.15f;
        var feet = GetFeet(origin);
        var bottom = feet + Vector3.up * (radius + 0.3f);
        var top = feet + Vector3.up * (standing + 0.2f - radius);
        // The mask includes layer 0 (bits 262657 in SCN_Main), so the player's own collider and anything
        // held under it are skipped.
        var playerRoot = player.transform.root;
        foreach (var hit in Physics.OverlapCapsule(bottom, top, radius, player.crouchCheckLayerMask.value, QueryTriggerInteraction.Ignore))
        {
            if (hit != null && hit.transform.root != playerRoot)
            {
                LogOnce($"walk-headroom|{hit.name}", $"[walk] headroom blocked by {BuildHierarchyPath(hit.transform)}");
                return false;
            }
        }

        return true;
    }

    /// <summary>Route length still to walk: feet to the current corner, then corner to corner.</summary>
    private float RemainingRouteMeters(Vector3 feet)
    {
        var total = 0f;
        var from = feet;
        for (var i = _walkCornerIndex; i < _walkCorners.Count; i++)
        {
            total += Vector3.Distance(from, _walkCorners[i]);
            from = _walkCorners[i];
        }

        return total;
    }
}
