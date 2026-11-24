using System;
using System.Collections.Generic;
using UnityEngine;
using GameInteractableBase = Il2Cpp.InteractableBase;

namespace Killer_Frequency_Access;

/// <summary>
/// Objects that cannot be seen from anywhere because something encloses or holds them. When the
/// standing-spot search finds no spot, the colliders its sight lines hit instead say why, one rule for
/// every case:
/// - most hits on a closed container (Il2Cpp.Openable above the hit collider that is not IsFullyOpen:
///   DrawerOpenable, DoorOpenable on cabinets): walk to the container, open it (hinged doors pushed on their
///   hinge as in WalkDoors, drawers with Openable.Open(); the
///   user agreed 2026-11-23), wait until it is open, then walk to the object. Locked: say so.
///   DrawerOpenable overrides Open(); DoorOpenable does not, so whether the base Open() moves a cabinet
///   door is unverified and every open is checked with HowOpen(), as for room doors.
/// - most hits on an interactable that holds the object (the object is below it in the hierarchy, as a
///   tape in a cassette player slot, where the game reported the slot observed in the 2026-11-19 run):
///   walk to the holder instead; its own actions (take out) reach the object.
/// </summary>
public sealed partial class KillerFrequencyAccessMod
{
    private const float ContainerOpenWaitSeconds = 3f;

    private readonly Dictionary<int, (Collider Collider, int Hits)> _sightBlockers = new();
    private Il2Cpp.Openable? _containerToOpen;
    private Transform? _containerFinalTarget;
    private bool _containerOpening;
    private float _containerOpenStartTime;
    private float _containerOpenStartAmount;

    private void NoteSightBlocker(Collider collider)
    {
        var id = collider.GetInstanceID();
        _sightBlockers[id] = _sightBlockers.TryGetValue(id, out var known) ? (collider, known.Hits + 1) : (collider, 1);
    }

    private void ClearContainerPlan()
    {
        _containerToOpen = null;
        _containerFinalTarget = null;
        _containerOpening = false;
    }

    /// <summary>
    /// Called when a walk found no standing spot. True when the walk was redirected (to a container to
    /// open, or to the holder of the target) or stopped with an explanation.
    /// </summary>
    private bool TryRedirectHiddenTarget(Transform target)
    {
        if (_sightBlockers.Count == 0)
        {
            return false;
        }

        // The collider hit most often from the candidate eyes.
        Collider? blocker = null;
        var most = 0;
        foreach (var (collider, hits) in _sightBlockers.Values)
        {
            if (collider != null && hits > most)
            {
                blocker = collider;
                most = hits;
            }
        }

        if (blocker == null)
        {
            return false;
        }

        var name = GetSpokenName(target);
        var openable = blocker.GetComponentInParent<Il2Cpp.Openable>();
        if (openable != null && !openable.IsFullyOpen())
        {
            var containerName = GetSpokenName(openable.transform);
            if (openable.IsLocked())
            {
                MelonLoader.MelonLogger.Msg($"[walk] {name} is behind {BuildHierarchyPath(openable.transform)}, which is locked");
                StopAutoWalk("container locked");
                SpeakWalk($"{name} is in {containerName}, which is locked");
                return true;
            }

            var containerTarget = ContainerInteractable(openable);
            MelonLoader.MelonLogger.Msg($"[walk] {name} is hidden by closed {BuildHierarchyPath(openable.transform)} ({most} sight lines); walking to {(containerTarget != null ? BuildHierarchyPath(containerTarget) : "its position")} to open it");
            _containerToOpen = openable;
            _containerFinalTarget = target;
            SpeakWalk($"{name} is in {containerName}. Opening it first");
            StartAutoWalkTo(containerTarget ?? openable.transform, keepContainerPlan: true);
            return true;
        }

        if (TryGetGameInteractable(blocker.transform, out var holder) && target.IsChildOf(holder.transform) && holder.transform != target)
        {
            MelonLoader.MelonLogger.Msg($"[walk] {name} is held by {BuildHierarchyPath(holder.transform)} ({most} sight lines); walking to it");
            SpeakWalk($"{name} is in {GetSpokenName(holder.transform)}. Walking there");
            StartAutoWalkTo(holder.transform);
            return true;
        }

        MelonLoader.MelonLogger.Msg($"[walk] no spot for {name}; sight lines mostly hit {BuildHierarchyPath(blocker.transform)} ({most})");
        return false;
    }

    /// <summary>The container's flat-screen interactable (on it or below it, not a VR-only handle), to walk to.</summary>
    private static Transform? ContainerInteractable(Il2Cpp.Openable openable)
    {
        Transform? fallback = null;
        foreach (var candidate in openable.GetComponentsInChildren<GameInteractableBase>())
        {
            if (candidate == null || !candidate.isActiveAndEnabled)
            {
                continue;
            }

            if (!candidate.OculusOnly)
            {
                return candidate.transform;
            }

            fallback ??= candidate.transform;
        }

        var above = openable.GetComponentInParent<GameInteractableBase>();
        return above != null && !above.OculusOnly ? above.transform : fallback;
    }

    /// <summary>
    /// Arrived at a container to open: opens it, waits, then walks on to the object inside. True while
    /// this owns the walk (the caller must not finish the walk as arrived).
    /// </summary>
    private bool UpdateContainerOpening(float now)
    {
        if (_containerToOpen == null)
        {
            return false;
        }

        var container = _containerToOpen;
        var name = GetSpokenName(container.transform);
        var hinged = container.TryCast<Il2Cpp.DoorOpenable>();
        if (!_containerOpening)
        {
            _containerOpening = true;
            _containerOpenStartTime = now;
            _containerOpenStartAmount = container.HowOpen();
            if (hinged != null && TryGetNavigationOrigin(out var origin) && TryBeginDoorPush(hinged, origin.position, now, out var started))
            {
                // Cabinet doors are DoorOpenables: Open() does not move them (2026-11-23 run), so they are
                // pushed on their hinge like room doors.
                MelonLoader.MelonLogger.Msg($"[walk] opening {BuildHierarchyPath(container.transform)} on its hinge ({started})");
                return true;
            }

            MelonLoader.MelonLogger.Msg($"[walk] opening {BuildHierarchyPath(container.transform)} (open {_containerOpenStartAmount:0.00})");
            container.Open();
            return true;
        }

        var target = _containerFinalTarget;
        float amount;
        if (hinged != null && _walkOpeningDoor != null)
        {
            var state = StepDoorPush(now, out var report);
            if (state == DoorPushState.Pushing)
            {
                return true;
            }

            _walkOpeningDoor = null;
            ClearContainerPlan();
            if (state == DoorPushState.Failed)
            {
                MelonLoader.MelonLogger.Msg($"[walk] {name} did not open: {report}");
                StopAutoWalk("container did not open");
                SpeakWalk($"{name} did not open. Open it, then walk again");
                return true;
            }

            MelonLoader.MelonLogger.Msg($"[walk] {name} {report}; walking on to {(target != null ? GetSpokenName(target) : "the object")}");
            StopAutoWalk("container opened");
            if (target != null)
            {
                StartAutoWalkTo(target);
            }

            return true;
        }

        amount = container.HowOpen();
        var opened = container.IsFullyOpen();
        if (!opened && now - _containerOpenStartTime < ContainerOpenWaitSeconds)
        {
            return true;
        }

        ClearContainerPlan();
        if (!opened && amount <= _containerOpenStartAmount + 0.2f)
        {
            MelonLoader.MelonLogger.Msg($"[walk] {name} did not open: Open() left HowOpen at {amount:0.00} (was {_containerOpenStartAmount:0.00})");
            StopAutoWalk("container did not open");
            SpeakWalk($"{name} did not open. Open it, then walk again");
            return true;
        }

        MelonLoader.MelonLogger.Msg($"[walk] {name} open ({amount:0.00}) after {now - _containerOpenStartTime:0.0} s; walking on to {(target != null ? GetSpokenName(target) : "the object")}");
        StopAutoWalk("container opened");
        if (target != null)
        {
            StartAutoWalkTo(target);
        }

        return true;
    }
}
