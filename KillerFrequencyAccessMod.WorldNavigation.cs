using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;

namespace Killer_Frequency_Access;

public sealed partial class KillerFrequencyAccessMod
{
    private void HandleObjectNavigationHotkeys()
    {
        try
        {
            var now = Time.unscaledTime;
            var handledNavigationInput = false;
            if (IsMenuActive())
            {
                if (_isAutoWalking)
                {
                    StopAutoWalk(false);
                }

                if (_isObjectListOpen)
                {
                    CloseObjectList(string.Empty);
                }

                return;
            }

            if (HandleObjectListKeys())
            {
                MarkNavigationInteractionActive();
                return;
            }

            if (IsCyclePreviousPressed())
            {
                MarkNavigationInteractionActive();
                if (_reachableTargets.Count == 0 && CanRunOnDemandTargetRefresh(now))
                {
                    RefreshReachableTargets(allowSlowFallback: false);
                    if (_reachableTargets.Count < 4 && CanRunSlowNavigationFallback(now))
                    {
                        RefreshReachableTargets(allowSlowFallback: true);
                    }
                }
                CycleTarget(-1);
                handledNavigationInput = true;
            }
            else if (IsCycleNextPressed())
            {
                MarkNavigationInteractionActive();
                if (_reachableTargets.Count == 0 && CanRunOnDemandTargetRefresh(now))
                {
                    RefreshReachableTargets(allowSlowFallback: false);
                    if (_reachableTargets.Count < 4 && CanRunSlowNavigationFallback(now))
                    {
                        RefreshReachableTargets(allowSlowFallback: true);
                    }
                }
                CycleTarget(1);
                handledNavigationInput = true;
            }

            if (IsAutoWalkPressed())
            {
                MarkNavigationInteractionActive();
                StartAutoWalkToSelectedTarget();
                handledNavigationInput = true;
            }

            if (!handledNavigationInput && ShouldRunNavigationBackgroundRefresh(now))
            {
                RefreshReachableTargets(allowSlowFallback: false);
                _nextTargetBackgroundRefreshTime = now + TargetBackgroundRefreshSeconds;
            }
        }
        catch (Exception ex)
        {
            LoggerInstance.Warning($"Object navigation disabled this frame: {ex.Message}");
        }
    }

    private void MoveNavigationOrigin(Transform origin, Vector3 worldDelta)
    {
        var controller = origin.GetComponent<CharacterController>();
        if (controller != null && controller.enabled)
        {
            controller.Move(worldDelta);
            return;
        }

        var rigidbody = origin.GetComponent<Rigidbody>();
        if (rigidbody != null && !rigidbody.isKinematic)
        {
            rigidbody.MovePosition(rigidbody.position + worldDelta);
            return;
        }

        origin.position += worldDelta;
    }

    private void RefreshReachableTargets(bool forceComponentCacheRefresh = false, bool allowSlowFallback = false)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            var previousId = (_selectedTargetIndex >= 0 && _selectedTargetIndex < _reachableTargets.Count)
                ? _reachableTargets[_selectedTargetIndex].ObjectId
                : -1;
            _reachableTargets.Clear();

            if (!TryGetNavigationOrigin(out var origin))
            {
                _selectedTargetIndex = -1;
                if (!_loggedMissingOrigin)
                {
                    _loggedMissingOrigin = true;
                    LoggerInstance.Warning("[targets] no navigation origin: no ControllerBaseInteractor in the loaded scenes");
                }
                return;
            }

            var originPosition = origin.position;
            var candidateByObjectId = new Dictionary<int, Transform>();
            CollectGameInteractables(candidateByObjectId);

            // Every object that passes the object-list filter (room and category); no count cap.
            foreach (var targetTransform in candidateByObjectId.Values)
            {
                if (targetTransform == null
                    || !targetTransform.gameObject.activeInHierarchy
                    || !MatchesActiveFilter(targetTransform, originPosition))
                {
                    continue;
                }

                if (TryCreateNearbyTarget(originPosition, targetTransform, out var target))
                {
                    _reachableTargets.Add(target);
                }
            }

            _reachableTargets.Sort((a, b) => a.DistanceMeters.CompareTo(b.DistanceMeters));

            if (_reachableTargets.Count == 0)
            {
                _selectedTargetIndex = -1;
                return;
            }

            if (_pendingSelectObjectId != -1)
            {
                previousId = _pendingSelectObjectId;
                _pendingSelectObjectId = -1;
            }

            _selectedTargetIndex = 0;
            if (previousId != -1)
            {
                for (var i = 0; i < _reachableTargets.Count; i++)
                {
                    if (_reachableTargets[i].ObjectId == previousId)
                    {
                        _selectedTargetIndex = i;
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LoggerInstance.Warning($"Target refresh failed: {ex.Message}");
            _reachableTargets.Clear();
            _selectedTargetIndex = -1;
        }
        finally
        {
            _lastReachableTargetsRefreshTime = Time.unscaledTime;
            timer.Stop();
            RecordTargetRefreshMetrics(timer.Elapsed.TotalMilliseconds);
        }
    }

    private static bool TryCreateNearbyTarget(Vector3 origin, Transform candidate, out ReachableTarget target)
    {
        target = default;
        if (candidate == null)
        {
            return false;
        }

        var nearest = ResolveTargetProximityPoint(origin, candidate);
        if (!IsFiniteVector3(nearest))
        {
            return false;
        }

        var distance = Vector2.Distance(new Vector2(origin.x, origin.z), new Vector2(nearest.x, nearest.z));
        target = new ReachableTarget(candidate.GetInstanceID(), candidate, GetSpokenName(candidate), nearest, distance);
        return true;
    }

    private static string ResolveReachableTargetDisplayName(ReachableTarget target)
    {
        return target.DisplayName;
    }

    private static Vector3 ResolveTargetProximityPoint(Vector3 originPosition, Transform target)
    {
        return GetNearestPoint(target, originPosition);
    }

    private Vector3 ResolveInteractionAimPoint(Transform target)
    {
        var camera = GetInteractorCamera();
        return GetAimPoint(target, camera != null ? camera.transform.position : target.position);
    }

    /// <summary>
    /// Speaks the object the game reports the player is pointing at, when it changes. The first
    /// object-list run showed the observed object dropping to none between frames, which re-announced
    /// the same object every 0.25 s; the marker is only cleared after half a second with nothing observed.
    /// </summary>
    private void UpdateObservedInteractableAnnouncement()
    {
        var now = Time.unscaledTime;
        if (IsMenuActive() || !TryGetObservedInteractable(out var observed))
        {
            if (_lastObservedInteractableObjectId != -1 && now - _lastObservedSeenTime > 0.5f)
            {
                _lastObservedInteractableObjectId = -1;
            }

            return;
        }

        _lastObservedSeenTime = now;
        var observedId = observed.GetInstanceID();
        if (observedId == _lastObservedInteractableObjectId
            || now - _lastObservedInteractableSpeakTime < ObservedInteractableSpeakCooldownSeconds)
        {
            return;
        }

        var actions = GetPerformableVerbs(observed);
        if (_placesForHeldIds.Contains(observed.transform.GetInstanceID())
            && TryGetHeld(out var held, out _) && held != null)
        {
            // The game's CanDock accepts the held item here; in flat-screen mode it goes in with Place.
            actions.Insert(0, $"Fits {GetSpokenName(held)}, {DescribePlaceKeys()}");
        }

        var name = GetSpokenName(observed);
        SpeakSelectionTextDedup($"observed|{observedId}", actions.Count == 0 ? name : $"{name}. {string.Join(". ", actions)}");
        _lastObservedInteractableSpeakTime = now;
        _lastObservedInteractableObjectId = observedId;
    }

    private float _lastObservedSeenTime;

    /// <summary>"0.6 meters" under 3 m (desk scale), whole meters beyond.</summary>
    private static string DescribeDistance(float meters)
    {
        return meters < 3f
            ? $"{Math.Max(0.1f, (float)Math.Round(meters, 1)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} meters"
            : $"{Mathf.RoundToInt(meters)} meters";
    }

    /// <summary>"above" or "below" when the point is more than 0.3 m off the camera's height, else empty.</summary>
    private static string DescribeHeight(Vector3 point)
    {
        var camera = GetInteractorCamera();
        if (camera == null)
        {
            return string.Empty;
        }

        var dy = point.y - camera.transform.position.y;
        return dy > 0.3f ? "above" : dy < -0.3f ? "below" : string.Empty;
    }

    /// <summary>"0.6 meters front right, below": distance from the player, direction, height.</summary>
    private static string DescribeWhere(Transform origin, Vector3 point)
    {
        var distance = Vector3.Distance(origin.position, point);
        var text = $"{DescribeDistance(distance)} {DescribeDirectionRelativeTo(origin, point)}";
        var height = DescribeHeight(point);
        return height.Length == 0 ? text : $"{text}, {height}";
    }

    private void CycleTarget(int direction)
    {
        if (_reachableTargets.Count == 0)
        {
            SpeakSelectionText($"Nothing listed: {DescribeActiveFilter()}");
            return;
        }

        var count = _reachableTargets.Count;
        if (_selectedTargetIndex < 0)
        {
            _selectedTargetIndex = 0;
        }

        var now = Time.unscaledTime;
        for (var attempts = 0; attempts < count; attempts++)
        {
            _selectedTargetIndex = (_selectedTargetIndex + direction + count) % count;
            var target = _reachableTargets[_selectedTargetIndex];
            if (target.Transform == null || !target.Transform.gameObject.activeInHierarchy)
            {
                continue;
            }

            if (IsTemporarilyUnreachableTarget(target.ObjectId, now))
            {
                continue;
            }

            AnnounceTargetSelection(target);
            return;
        }

        SpeakSelectionText($"Nothing listed: {DescribeActiveFilter()}");
    }

    private bool IsTemporarilyUnreachableTarget(int objectId, float now)
    {
        if (!_temporarilyUnreachableTargetUntil.TryGetValue(objectId, out var untilTime))
        {
            return false;
        }

        if (now >= untilTime)
        {
            _temporarilyUnreachableTargetUntil.Remove(objectId);
            return false;
        }

        return true;
    }

    private void MarkTemporarilyUnreachableTarget(int objectId, float now)
    {
        _temporarilyUnreachableTargetUntil[objectId] = now + UnreachableTargetRetrySeconds;
    }

    private static bool TryHasDirectNavPath(Vector3 from, Vector3 to)
    {
        if (!NavMesh.SamplePosition(from, out var startHit, 4f, NavMesh.AllAreas))
        {
            return false;
        }

        if (!NavMesh.SamplePosition(to, out var endHit, 4f, NavMesh.AllAreas))
        {
            return false;
        }

        return !NavMesh.Raycast(startHit.position, endHit.position, out _, NavMesh.AllAreas);
    }

    private static string ReadableObjectName(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return "object";
        }

        var result = rawName.Replace("_", " ").Replace("-", " ");
        result = Regex.Replace(result, "([a-z])([A-Z])", "$1 $2");
        result = Regex.Replace(result, "\\s+", " ");
        return result.Trim();
    }

    /// <summary>The player body: the PlayerController GameObject that holds the interactor and CharacterController.</summary>
    private static bool TryGetNavigationOrigin(out Transform origin)
    {
        origin = null!;
        if (!TryGetPlayerInteractor(out var interactor))
        {
            return false;
        }

        origin = interactor.transform;
        return true;
    }

    private static bool IsCyclePreviousPressed()
    {
        return IsKeyDownSafe(Key.LeftBracket);
    }

    private static bool IsCycleNextPressed()
    {
        return IsKeyDownSafe(Key.RightBracket);
    }

    private static bool IsAutoWalkPressed()
    {
        return IsKeyDownSafe(Key.Backspace);
    }

    private void AnnounceTargetSelection(ReachableTarget target)
    {
        var displayName = ResolveReachableTargetDisplayName(target);
        if (!TryGetNavigationOrigin(out var origin))
        {
            SpeakSelectionText(displayName);
            return;
        }

        var point = target.Transform != null ? GetNearestPoint(target.Transform, origin.position) : target.Destination;
        var where = DescribeWhere(origin, point);
        var position = $"{_selectedTargetIndex + 1} of {_reachableTargets.Count}";
        if (_filterScope == RoomScope.AllRooms && target.Transform != null)
        {
            var room = RoomOf(target.Transform.position);
            SpeakSelectionText($"{displayName}, {where}, {room?.Name ?? "no room"}, {position}");
            return;
        }

        SpeakSelectionText($"{displayName}, {where}, {position}");
    }

    private static string DescribeDirectionRelativeTo(Transform origin, Vector3 destination)
    {
        var reference = Camera.main != null ? Camera.main.transform : origin;

        var forward = reference.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude <= 0.0001f)
        {
            return "ahead";
        }

        var toTarget = destination - origin.position;
        toTarget.y = 0f;
        if (toTarget.sqrMagnitude <= 0.0001f)
        {
            return "ahead";
        }

        var angle = Vector3.SignedAngle(forward.normalized, toTarget.normalized, Vector3.up);
        if (angle > -22.5f && angle <= 22.5f) return "front";
        if (angle > 22.5f && angle <= 67.5f) return "front right";
        if (angle > 67.5f && angle <= 112.5f) return "right";
        if (angle > 112.5f && angle <= 157.5f) return "back right";
        if (angle > 157.5f || angle <= -157.5f) return "back";
        if (angle > -157.5f && angle <= -112.5f) return "back left";
        if (angle > -112.5f && angle <= -67.5f) return "left";
        return "front left";
    }

    private static bool IsFiniteVector3(Vector3 value)
    {
        return !float.IsNaN(value.x)
            && !float.IsNaN(value.y)
            && !float.IsNaN(value.z)
            && !float.IsInfinity(value.x)
            && !float.IsInfinity(value.y)
            && !float.IsInfinity(value.z);
    }

    private readonly struct ReachableTarget
    {
        public ReachableTarget(int objectId, Transform transform, string displayName, Vector3 destination, float distanceMeters)
        {
            ObjectId = objectId;
            Transform = transform;
            DisplayName = displayName;
            Destination = destination;
            DistanceMeters = distanceMeters;
        }

        public int ObjectId { get; }
        public Transform Transform { get; }
        public string DisplayName { get; }
        public Vector3 Destination { get; }
        public float DistanceMeters { get; }
    }
}

