using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
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
            if (IsEnvironmentScanPressed())
            {
                if (IsMenuActive())
                {
                    SpeakSelectionText("Close menu to scan");
                }
                else
                {
                    ToggleEnvironmentScanMode();
                    MarkNavigationInteractionActive();
                }

                handledNavigationInput = true;
            }

            if (IsMenuActive())
            {
                if (_isEnvironmentScanEnabled)
                {
                    StopContinuousEnvironmentScan(true);
                }

                if (_isAutoWalking)
                {
                    StopAutoWalk(false);
                }

                return;
            }

            UpdateEnvironmentScanMode();

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

    private void UpdateAutoWalk(float deltaTime)
    {
        if (!_isAutoWalking)
        {
            return;
        }

        try
        {
            if (IsMenuActive())
            {
                StopAutoWalk(false);
                return;
            }

            if (_autoWalkTarget == null || !_autoWalkTarget.gameObject.activeInHierarchy)
            {
                StopAutoWalk(false);
                return;
            }

            if (!TryGetNavigationOrigin(out var origin))
            {
                StopAutoWalk(false);
                return;
            }

            var destination = ResolveNavigationDestinationPoint(origin.position, _autoWalkTarget);
            var aimPoint = ResolveInteractionAimPoint(_autoWalkTarget);
            var isReadyForInteraction = IsTargetReadyForInteraction(origin, _autoWalkTarget, aimPoint, out var isWithinGameInteractRange);
            if (isReadyForInteraction)
            {
                SpeakInRangeWithAction(_autoWalkTarget);
                StopAutoWalk(false);
                return;
            }

            if (isWithinGameInteractRange)
            {
                TurnTowardsTarget(origin, aimPoint, deltaTime);

                // Fine alignment phase for small/precise targets:
                // keep inching forward while the exact interactable is not centered yet.
                if (!TryResolveDirectCrosshairInteractable(out var crosshairTarget)
                    || !DoTargetsRepresentSameInteractable(crosshairTarget, _autoWalkTarget))
                {
                    var fineApproach = destination - origin.position;
                    fineApproach.y = 0f;
                    if (fineApproach.sqrMagnitude > 0.0025f)
                    {
                        var fineStep = Mathf.Min((AutoWalkSpeed * 0.45f) * deltaTime, fineApproach.magnitude);
                        if (fineStep > 0.0001f)
                        {
                            MoveNavigationOrigin(origin, fineApproach.normalized * fineStep);
                        }
                    }
                }

                return;
            }

            var now = Time.unscaledTime;
            var shouldRefreshWaypoint = !_hasAutoWalkWaypoint
                || now >= _nextAutoWalkWaypointRefreshTime
                || Vector2.Distance(
                    new Vector2(origin.position.x, origin.position.z),
                    new Vector2(_autoWalkWaypoint.x, _autoWalkWaypoint.z)) <= 0.45f;
            if (shouldRefreshWaypoint)
            {
                if (_autoWalkUseDirectSteering)
                {
                    if (IsDirectPathBlocked(origin.position, destination, _autoWalkTarget))
                    {
                        if (!HandleAutoWalkPathFailure())
                        {
                            return;
                        }
                    }
                    else
                    {
                        _autoWalkPathFailureCount = 0;
                        _autoWalkWaypoint = destination;
                    }
                }
                else
                {
                    if (!TryGetNavigableWaypoint(origin.position, destination, out var computedWaypoint))
                    {
                        if (TryResolveReachableDestination(origin.position, _autoWalkTarget!, out var recoveredDestination, out var recoveredUsingNav))
                        {
                            destination = recoveredDestination;
                            _autoWalkUseDirectSteering = !recoveredUsingNav;
                            if (_autoWalkUseDirectSteering)
                            {
                                if (IsDirectPathBlocked(origin.position, destination, _autoWalkTarget))
                                {
                                    if (!HandleAutoWalkPathFailure())
                                    {
                                        return;
                                    }
                                }
                                else
                                {
                                    _autoWalkPathFailureCount = 0;
                                    _autoWalkWaypoint = destination;
                                }
                            }
                            else if (TryGetNavigableWaypoint(origin.position, destination, out computedWaypoint))
                            {
                                _autoWalkPathFailureCount = 0;
                                _autoWalkWaypoint = computedWaypoint;
                            }
                            else if (!HandleAutoWalkPathFailure())
                            {
                                return;
                            }
                        }
                        else if (!HandleAutoWalkPathFailure())
                        {
                            return;
                        }
                    }
                    else
                    {
                        _autoWalkPathFailureCount = 0;
                        _autoWalkWaypoint = computedWaypoint;
                    }
                }
                _hasAutoWalkWaypoint = true;
                _nextAutoWalkWaypointRefreshTime = now + AutoWalkWaypointRefreshSeconds;
            }

            var waypoint = _autoWalkWaypoint;

            var toWaypoint = waypoint - origin.position;
            toWaypoint.y = 0f;
            if (toWaypoint.sqrMagnitude <= 0.0001f)
            {
                return;
            }

            TurnTowardsTarget(origin, waypoint, deltaTime);
            var stepMagnitude = Mathf.Min(AutoWalkSpeed * deltaTime, toWaypoint.magnitude);
            var move = toWaypoint.normalized * stepMagnitude;
            MoveNavigationOrigin(origin, move);
        }
        catch (Exception ex)
        {
            LoggerInstance.Warning($"Auto walk stopped: {ex.Message}");
            StopAutoWalk(false);
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
                return;
            }

            var originPosition = origin.position;
            var candidateByObjectId = new Dictionary<int, Transform>();
            var perfMode = GetCurrentPerfMode();
            CollectGameInteractables(candidateByObjectId);

            if (candidateByObjectId.Count == 0)
            {
                _selectedTargetIndex = -1;
                return;
            }

            var orderedCandidates = new List<(Transform Target, float Distance)>(candidateByObjectId.Count);
            foreach (var pair in candidateByObjectId)
            {
                var targetTransform = pair.Value;
                if (targetTransform == null || !targetTransform.gameObject.activeInHierarchy)
                {
                    continue;
                }

                var proximityPoint = ResolveTargetProximityPoint(originPosition, targetTransform);
                var distance = Vector2.Distance(
                    new Vector2(originPosition.x, originPosition.z),
                    new Vector2(proximityPoint.x, proximityPoint.z));
                orderedCandidates.Add((targetTransform, distance));
            }

            orderedCandidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            var maxCandidateLimit = perfMode == PerfMode.StudioLike
                ? Mathf.Min(MaxCurrentRoomTargetCandidates, 18)
                : MaxCurrentRoomTargetCandidates;
            var candidateLimit = Mathf.Min(maxCandidateLimit, orderedCandidates.Count);
            var validationAttempts = 0;
            for (var i = 0; i < candidateLimit; i++)
            {
                if (validationAttempts >= MaxPathValidationAttemptsPerRefresh
                    || _reachableTargets.Count >= MaxWalkableTargetsPerRefresh)
                {
                    break;
                }

                var targetTransform = orderedCandidates[i].Target;
                if (!TryCreateNearbyTarget(originPosition, targetTransform, out var target))
                {
                    continue;
                }

                validationAttempts++;
                _reachableTargets.Add(target);
            }

            _reachableTargets.Sort((a, b) => a.DistanceMeters.CompareTo(b.DistanceMeters));

            if (_reachableTargets.Count == 0)
            {
                _selectedTargetIndex = -1;
                return;
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

        var destinationProbe = ResolveTargetProximityPoint(origin, candidate);
        if (!IsFiniteVector3(destinationProbe))
        {
            return false;
        }

        var distance = Vector2.Distance(
            new Vector2(origin.x, origin.z),
            new Vector2(destinationProbe.x, destinationProbe.z));
        if (distance <= 0.4f || float.IsNaN(distance) || float.IsInfinity(distance))
        {
            return false;
        }

        target = new ReachableTarget(
            candidate.GetInstanceID(),
            candidate,
            GameTargetLabels.TryGetValue(candidate.GetInstanceID(), out var gameLabel)
                ? gameLabel
                : ReadableObjectName(candidate.gameObject.name),
            destinationProbe,
            distance);
        return true;
    }

    private static bool TryResolveReachableDestination(Vector3 origin, Transform candidate, out Vector3 destination, out bool usedNavMeshPathing)
    {
        destination = default;
        usedNavMeshPathing = false;
        if (candidate == null)
        {
            return false;
        }

        var anchor = ResolveTargetProximityPoint(origin, candidate);
        if (!NavMesh.SamplePosition(origin, out var startHit, 4f, NavMesh.AllAreas))
        {
            if (IsFiniteVector3(anchor) && !IsDirectPathBlocked(origin, anchor, candidate))
            {
                destination = anchor;
                return true;
            }

            return false;
        }

        var toAnchor = anchor - startHit.position;
        toAnchor.y = 0f;
        var baseDirection = toAnchor.sqrMagnitude > 0.0001f ? toAnchor.normalized : Vector3.forward;
        var radii = new[] { 0f, 0.9f, 1.8f };
        var yawOffsets = new[] { 0f, 60f, -60f, 120f, -120f, 180f };
        var sampledEnds = new List<Vector3>(radii.Length * yawOffsets.Length);

        for (var radiusIndex = 0; radiusIndex < radii.Length; radiusIndex++)
        {
            var radius = radii[radiusIndex];
            for (var angleIndex = 0; angleIndex < yawOffsets.Length; angleIndex++)
            {
                var direction = Quaternion.Euler(0f, yawOffsets[angleIndex], 0f) * baseDirection;
                var probe = anchor + (direction * radius);
                var sampleDistance = 1.6f + (radius * 0.35f);
                if (!NavMesh.SamplePosition(probe, out var endHit, sampleDistance, NavMesh.AllAreas))
                {
                    continue;
                }

                var endPoint = endHit.position;
                sampledEnds.Add(endPoint);
                if (!TryHasDirectNavPath(startHit.position, endPoint))
                {
                    continue;
                }

                destination = endPoint;
                usedNavMeshPathing = true;
                return true;
            }
        }

        for (var i = 0; i < sampledEnds.Count; i++)
        {
            var endPoint = sampledEnds[i];
            if (!TryFindIndirectWaypoint(startHit.position, endPoint, out _))
            {
                continue;
            }

            destination = endPoint;
            usedNavMeshPathing = true;
            return true;
        }

        if (IsFiniteVector3(anchor) && !IsDirectPathBlocked(origin, anchor, candidate))
        {
            destination = anchor;
            return true;
        }

        return false;
    }


    private static Vector3 ResolveTargetProximityPoint(Vector3 originPosition, Transform target)
    {
        if (target != null && TryGetInteractableCollider(target, out var collider))
        {
            var closest = collider.ClosestPoint(originPosition);
            if (IsFiniteVector3(closest))
            {
                return closest;
            }
        }

        return target != null ? target.position : originPosition;
    }

    private static bool IsDirectPathBlocked(Vector3 from, Vector3 to, Transform? target)
    {
        var planar = to - from;
        planar.y = 0f;
        var distance = planar.magnitude;
        if (distance <= 0.25f || float.IsNaN(distance) || float.IsInfinity(distance))
        {
            return false;
        }

        var direction = planar / Mathf.Max(0.001f, distance);
        var castDistance = Mathf.Max(0f, distance - 0.15f);
        var highStart = from + (Vector3.up * 0.9f);
        var lowStart = from + (Vector3.up * 0.2f);
        return IsBlockingHitAlongRay(highStart, direction, castDistance, target)
            || IsBlockingHitAlongRay(lowStart, direction, castDistance, target);
    }

    private static bool IsBlockingHitAlongRay(Vector3 origin, Vector3 direction, float distance, Transform? target)
    {
        if (!Physics.Raycast(origin, direction, out var hit, distance))
        {
            return false;
        }

        var hitTransform = hit.collider != null ? hit.collider.transform : null;
        if (hitTransform == null || target == null)
        {
            return true;
        }

        return !(hitTransform == target || hitTransform.IsChildOf(target) || target.IsChildOf(hitTransform));
    }

    private void CycleTarget(int direction)
    {
        if (_reachableTargets.Count == 0)
        {
            SpeakSelectionText("No nearby objects");
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

        SpeakSelectionText("No nearby objects");
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

    private void StartAutoWalkToSelectedTarget()
    {
        if (_reachableTargets.Count == 0)
        {
            RefreshReachableTargets(allowSlowFallback: false);
            var now = Time.unscaledTime;
            if (_reachableTargets.Count == 0 && CanRunSlowNavigationFallback(now))
            {
                RefreshReachableTargets(allowSlowFallback: true);
            }
            if (_reachableTargets.Count == 0)
            {
                SpeakSelectionText("No nearby objects");
                return;
            }
        }

        if (_selectedTargetIndex < 0 || _selectedTargetIndex >= _reachableTargets.Count)
        {
            RefreshReachableTargets(allowSlowFallback: false);
            var now = Time.unscaledTime;
            if (_reachableTargets.Count == 0 && CanRunSlowNavigationFallback(now))
            {
                RefreshReachableTargets(allowSlowFallback: true);
            }
            if (_reachableTargets.Count == 0)
            {
                SpeakSelectionText("No nearby objects");
                return;
            }

            _selectedTargetIndex = 0;
        }

        if (!TryGetNavigationOrigin(out var origin))
        {
            SpeakSelectionText("Player position unavailable");
            return;
        }

        if (!TryResolveNextAutoWalkableTarget(origin.position, out var target, out var usedNavMeshPathing))
        {
            SpeakSelectionText("No valid path");
            return;
        }

        _autoWalkUseDirectSteering = !usedNavMeshPathing;

        var aimPoint = ResolveInteractionAimPoint(target.Transform);
        var isReadyForInteraction = IsTargetReadyForInteraction(origin, target.Transform, aimPoint, out _);
        if (isReadyForInteraction)
        {
            SpeakInRangeWithAction(target.Transform);
            return;
        }

        _autoWalkTarget = target.Transform;
        _isAutoWalking = true;
        _hasAutoWalkWaypoint = false;
        _nextAutoWalkWaypointRefreshTime = -100f;
        _autoWalkPathFailureCount = 0;
        var displayName = ResolveReachableTargetDisplayName(target);
        SpeakSelectionText($"Walking to {displayName}");
    }

    private bool TryResolveNextAutoWalkableTarget(Vector3 originPosition, out ReachableTarget target, out bool usedNavMeshPathing)
    {
        target = default;
        usedNavMeshPathing = false;
        if (_reachableTargets.Count == 0)
        {
            _selectedTargetIndex = -1;
            return false;
        }

        var startIndex = Mathf.Clamp(_selectedTargetIndex, 0, _reachableTargets.Count - 1);
        var maxCandidateCount = Mathf.Min(_reachableTargets.Count, 10);
        var candidateOrder = BuildCandidateIndexOrder(startIndex, _reachableTargets.Count, maxCandidateCount);
        var now = Time.unscaledTime;

        for (var orderIndex = 0; orderIndex < candidateOrder.Count; orderIndex++)
        {
            var candidateIndex = candidateOrder[orderIndex];
            if (candidateIndex < 0 || candidateIndex >= _reachableTargets.Count)
            {
                continue;
            }

            var candidate = _reachableTargets[candidateIndex];
            if (candidate.Transform == null || !candidate.Transform.gameObject.activeInHierarchy)
            {
                continue;
            }

            if (IsTemporarilyUnreachableTarget(candidate.ObjectId, now))
            {
                continue;
            }

            if (TryResolveReachableDestination(originPosition, candidate.Transform, out var resolvedDestination, out var usedNav)
                || TryResolveDirectInteractionDestination(originPosition, candidate.Transform, out resolvedDestination))
            {
                var resolvedDistance = Vector2.Distance(
                    new Vector2(originPosition.x, originPosition.z),
                    new Vector2(resolvedDestination.x, resolvedDestination.z));
                candidate = new ReachableTarget(
                    candidate.ObjectId,
                    candidate.Transform,
                    candidate.DisplayName,
                    resolvedDestination,
                    resolvedDistance);
                _reachableTargets[candidateIndex] = candidate;
                _selectedTargetIndex = candidateIndex;
                target = candidate;
                usedNavMeshPathing = usedNav;
                return true;
            }

            MarkTemporarilyUnreachableTarget(candidate.ObjectId, now);
        }

        _selectedTargetIndex = _reachableTargets.Count > 0 ? Mathf.Clamp(startIndex, 0, _reachableTargets.Count - 1) : -1;
        return false;
    }

    private static List<int> BuildCandidateIndexOrder(int startIndex, int count, int maxCount)
    {
        var cappedCount = Mathf.Clamp(maxCount, 0, count);
        var ordered = new List<int>(cappedCount);
        if (count <= 0 || cappedCount <= 0)
        {
            return ordered;
        }

        var start = Mathf.Clamp(startIndex, 0, count - 1);
        ordered.Add(start);
        if (cappedCount == 1)
        {
            return ordered;
        }

        for (var offset = 1; ordered.Count < cappedCount && offset < count; offset++)
        {
            var forward = start + offset;
            if (forward < count)
            {
                ordered.Add(forward);
                if (ordered.Count >= cappedCount)
                {
                    break;
                }
            }

            var backward = start - offset;
            if (backward >= 0)
            {
                ordered.Add(backward);
            }
        }

        return ordered;
    }

    private bool TryResolveDirectInteractionDestination(Vector3 originPosition, Transform target, out Vector3 destination)
    {
        destination = default;
        if (target == null || !target.gameObject.activeInHierarchy)
        {
            return false;
        }

        if (!HasAvailableInteraction(target))
        {
            return false;
        }

        var probeDestination = ResolveNavigationDestinationPoint(originPosition, target);
        if (!IsFiniteVector3(probeDestination))
        {
            probeDestination = ResolveTargetProximityPoint(originPosition, target);
            if (!IsFiniteVector3(probeDestination))
            {
                return false;
            }
        }

        if (!IsDirectPathBlocked(originPosition, probeDestination, target))
        {
            destination = probeDestination;
            return true;
        }

        if (TryGetNavigationOrigin(out var origin))
        {
            var aimPoint = ResolveInteractionAimPoint(target);
            if (IsTargetReadyForInteraction(origin, target, aimPoint, out _))
            {
                destination = origin.position;
                return true;
            }
        }

        if (TryResolveDirectCrosshairInteractable(out var crosshairTarget)
            && DoTargetsRepresentSameInteractable(crosshairTarget, target))
        {
            destination = probeDestination;
            return true;
        }

        return false;
    }

    private void StopAutoWalk(bool speakCancelled)
    {
        if (!_isAutoWalking)
        {
            return;
        }

        _isAutoWalking = false;
        _autoWalkTarget = null;
        _hasAutoWalkWaypoint = false;
        _nextAutoWalkWaypointRefreshTime = -100f;
        _autoWalkUseDirectSteering = false;
        _autoWalkPathFailureCount = 0;

        if (speakCancelled)
        {
            SpeakSelectionText("Auto walk cancelled");
        }
    }

    private bool HandleAutoWalkPathFailure()
    {
        _autoWalkPathFailureCount++;
        if (_autoWalkPathFailureCount < MaxAutoWalkPathFailureFrames)
        {
            _hasAutoWalkWaypoint = false;
            _nextAutoWalkWaypointRefreshTime = Time.unscaledTime + 0.05f;
            return false;
        }

        SpeakSelectionText("No valid path");
        StopAutoWalk(false);
        return true;
    }

    private static bool LooksInteractable(MonoBehaviour component)
    {
        if (component == null || IsLikelyVisualOnlyComponent(component))
        {
            return false;
        }

        if (LooksLikeGameInteractable(component))
        {
            return true;
        }

        var typeName = component.GetType().Name.ToLowerInvariant();
        var objectName = component.gameObject.name.ToLowerInvariant();

        if (component.gameObject.CompareTag("Interactable"))
        {
            return true;
        }

        return ContainsInteractableKeyword(typeName) || ContainsInteractableKeyword(objectName);
    }

    private static bool LooksLikeGameInteractable(MonoBehaviour component)
    {
        var type = component.GetType();
        var fullName = type.FullName ?? string.Empty;
        var typeName = type.Name;

        if (string.Equals(typeName, "InteractableItem", StringComparison.Ordinal)
            || string.Equals(fullName, "Il2Cpp.InteractableItem", StringComparison.Ordinal))
        {
            return true;
        }

        if (fullName.Contains("Il2CppInteractables.", StringComparison.Ordinal))
        {
            return true;
        }

        // Fallback for other concrete interaction wrappers without hard references.
        return TryResolveInteractionAccessors(type, out _, out _)
            && type.GetProperty("InteractableCollider", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null;
    }

    private string ResolveInteractableDisplayName(Transform candidate)
    {
        var localizedDisplayName = TryGetLocalizedDisplayName(candidate);
        if (!string.IsNullOrWhiteSpace(localizedDisplayName))
        {
            var sanitizedLocalized = SanitizeReadableName(localizedDisplayName);
            if (!string.IsNullOrWhiteSpace(sanitizedLocalized))
            {
                return sanitizedLocalized;
            }
        }

        var objectHandleName = TryGetObjectStringHandleName(candidate);
        if (!string.IsNullOrWhiteSpace(objectHandleName))
        {
            var sanitizedHandle = SanitizeReadableName(objectHandleName);
            if (!string.IsNullOrWhiteSpace(sanitizedHandle))
            {
                return sanitizedHandle;
            }
        }

        var hierarchyName = ResolveHierarchyReadableName(candidate);
        if (!string.IsNullOrWhiteSpace(hierarchyName))
        {
            return hierarchyName;
        }

        var actionDerivedName = TryResolveActionDerivedName(candidate);
        if (!string.IsNullOrWhiteSpace(actionDerivedName))
        {
            return actionDerivedName;
        }

        var fallback = SanitizeReadableName(ReadableObjectName(candidate.gameObject.name));
        if (!string.IsNullOrWhiteSpace(fallback) && !IsLikelyTechnicalObjectName(fallback))
        {
            return fallback;
        }

        return "interactable object";
    }

    private static string ResolveHierarchyReadableName(Transform candidate)
    {
        var bestName = string.Empty;
        var bestScore = int.MinValue;
        var current = candidate;
        for (var depth = 0; current != null && depth < 6; depth++)
        {
            var readable = SanitizeReadableName(ReadableObjectName(current.gameObject.name));
            if (string.IsNullOrWhiteSpace(readable) || IsLikelyTechnicalObjectName(readable))
            {
                current = current.parent;
                continue;
            }

            var tokenCount = readable.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            var hasDigit = false;
            for (var i = 0; i < readable.Length; i++)
            {
                if (char.IsDigit(readable[i]))
                {
                    hasDigit = true;
                    break;
                }
            }

            var score = (tokenCount * 10) - (depth * 3) - (hasDigit ? 4 : 0);
            if (score > bestScore)
            {
                bestScore = score;
                bestName = readable;
            }

            current = current.parent;
        }

        return bestName;
    }

    private static string SanitizeReadableName(string value)
    {
        var normalized = NormalizeText(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return string.Empty;
        }

        var filtered = new List<string>(tokens.Length);
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i].Trim();
            if (token.Length <= 1)
            {
                continue;
            }

            var lower = token.ToLowerInvariant();
            if (lower is "player"
                or "trigger"
                or "interactable"
                or "interaction"
                or "collider"
                or "detector"
                or "helper"
                or "proxy"
                or "manager"
                or "volume"
                or "waypoint"
                or "spawn"
                or "runtime"
                or "logic"
                or "zone"
                or "controller"
                or "script"
                or "component"
                or "checkpoint")
            {
                continue;
            }

            filtered.Add(token);
        }

        return filtered.Count == 0 ? string.Empty : string.Join(" ", filtered);
    }

    private static string TryResolveActionDerivedName(Transform candidate)
    {
        var actionText = NormalizeText(TryGetPrimaryActionText(candidate));
        if (!IsUsableActionText(actionText))
        {
            return string.Empty;
        }

        var lower = actionText.ToLowerInvariant();
        string remainder = lower;
        var prefixes = new[]
        {
            "pick up ",
            "pickup ",
            "take ",
            "grab ",
            "collect ",
            "open ",
            "close ",
            "unlock ",
            "lock ",
            "activate ",
            "deactivate ",
            "turn on ",
            "turn off ",
            "switch ",
            "use ",
            "inspect ",
            "examine ",
            "read ",
            "listen to "
        };
        for (var i = 0; i < prefixes.Length; i++)
        {
            if (remainder.StartsWith(prefixes[i], StringComparison.Ordinal))
            {
                remainder = remainder[prefixes[i].Length..];
                break;
            }
        }

        var candidateName = SanitizeReadableName(ReadableObjectName(remainder));
        if (!string.IsNullOrWhiteSpace(candidateName) && !IsLikelyTechnicalObjectName(candidateName))
        {
            return candidateName;
        }

        return string.Empty;
    }

    private static bool IsLikelyTechnicalObjectName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var normalized = value.ToLowerInvariant();
        if (normalized.Length <= 2)
        {
            return true;
        }

        return normalized.Contains("trigger")
            || normalized.Contains("unlock")
            || normalized.Contains("player")
            || normalized.Contains("interactable")
            || normalized.Contains("interaction")
            || normalized.Contains("collider")
            || normalized.Contains("detector")
            || normalized.Contains("helper")
            || normalized.Contains("proxy")
            || normalized.Contains("manager")
            || normalized.Contains("volume")
            || normalized.Contains("waypoint")
            || normalized.Contains("spawn")
            || normalized.Contains("runtime")
            || normalized.Contains("logic");
    }

    private static string TryGetLocalizedDisplayName(Transform candidate)
    {
        var components = candidate.GetComponentsInChildren<MonoBehaviour>(true);
        foreach (var component in components)
        {
            if (component == null)
            {
                continue;
            }

            if (!string.Equals(component.GetType().FullName, "Il2Cpp.DiegeticTextLocalizer", StringComparison.Ordinal))
            {
                continue;
            }

            var localizedStringProperty = component.GetType().GetProperty("LocalizedStringToDisplay", BindingFlags.Instance | BindingFlags.Public);
            if (localizedStringProperty == null)
            {
                continue;
            }

            object localizedString;
            try
            {
                localizedString = localizedStringProperty.GetValue(component)!;
            }
            catch
            {
                continue;
            }

            if (localizedString == null)
            {
                continue;
            }

            var getLocalizedStringMethod = localizedString.GetType().GetMethod("GetLocalizedString", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (getLocalizedStringMethod == null)
            {
                continue;
            }

            try
            {
                var resolvedText = getLocalizedStringMethod.Invoke(localizedString, null) as string;
                var normalized = NormalizeText(resolvedText);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    return normalized;
                }
            }
            catch
            {
                // Ignore one-off localization failures and continue fallback chain.
            }
        }

        return string.Empty;
    }

    private static string TryGetObjectStringHandleName(Transform candidate)
    {
        var components = candidate.GetComponentsInChildren<MonoBehaviour>(true);
        foreach (var component in components)
        {
            if (component == null)
            {
                continue;
            }

            // InteractableItem stores the canonical in-game object label category.
            var handleProperty = component.GetType().GetProperty("_stringHandle", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (handleProperty == null)
            {
                var handleField = component.GetType().GetField("_stringHandle", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (handleField == null)
                {
                    continue;
                }

                object handleFieldValue;
                try
                {
                    handleFieldValue = handleField.GetValue(component)!;
                }
                catch
                {
                    continue;
                }

                if (handleFieldValue == null)
                {
                    continue;
                }

                var fieldHandleText = handleFieldValue.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(fieldHandleText)
                    || string.Equals(fieldHandleText, "NONE", StringComparison.OrdinalIgnoreCase)
                    || int.TryParse(fieldHandleText, out _))
                {
                    continue;
                }

                return ReadableObjectName(fieldHandleText);
            }

            object handleValue;
            try
            {
                handleValue = handleProperty.GetValue(component)!;
            }
            catch
            {
                continue;
            }

            if (handleValue == null)
            {
                continue;
            }

            var rawHandleText = handleValue.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(rawHandleText)
                || string.Equals(rawHandleText, "NONE", StringComparison.OrdinalIgnoreCase)
                || int.TryParse(rawHandleText, out _))
            {
                continue;
            }

            return ReadableObjectName(rawHandleText);
        }

        return string.Empty;
    }

    private static bool ContainsInteractableKeyword(string value)
    {
        return value.Contains("interact")
            || value.Contains("pickup")
            || value.Contains("usable")
            || value.Contains("switch")
            || value.Contains("lever")
            || value.Contains("door")
            || value.Contains("drawer")
            || value.Contains("collect")
            || value.Contains("inventory");
    }

    private static Transform ResolveNavigationTargetTransform(Transform candidate)
    {
        if (candidate == null)
        {
            return null!;
        }

        if (TryResolveExplicitInteractionRoot(candidate, out var interactionRoot) && interactionRoot != null)
        {
            return interactionRoot;
        }

        if (!IsLikelyVisualOnlyCandidate(candidate))
        {
            return candidate;
        }

        if (candidate.parent != null && !IsLikelyVisualOnlyCandidate(candidate.parent))
        {
            return candidate.parent;
        }

        return candidate;
    }

    private static bool TryResolveExplicitInteractionRoot(Transform candidate, out Transform interactionRoot)
    {
        interactionRoot = null!;
        if (candidate == null)
        {
            return false;
        }

        var current = candidate;
        for (var depth = 0; current != null && depth < 8; depth++)
        {
            if (TryGetInteractableItem(current, out var interactableItem) && interactableItem != null)
            {
                if (TryGetInteractableColliderFromComponent(interactableItem, out var interactableCollider)
                    && interactableCollider != null
                    && interactableCollider.transform != null)
                {
                    interactionRoot = interactableCollider.transform;
                    return true;
                }

                interactionRoot = interactableItem.transform;
                return true;
            }

            var components = current.GetComponents<MonoBehaviour>();
            for (var i = 0; i < components.Length; i++)
            {
                var component = components[i];
                if (component == null || IsLikelyVisualOnlyComponent(component))
                {
                    continue;
                }

                if (!LooksLikeGameInteractable(component))
                {
                    continue;
                }

                if (TryGetInteractableColliderFromComponent(component, out var componentCollider)
                    && componentCollider != null
                    && componentCollider.transform != null)
                {
                    interactionRoot = componentCollider.transform;
                    return true;
                }

                interactionRoot = component.transform;
                return true;
            }

            current = current.parent;
        }

        return false;
    }

    private static bool IsLikelyVisualOnlyComponent(MonoBehaviour component)
    {
        if (component == null)
        {
            return true;
        }

        var typeName = component.GetType().Name ?? string.Empty;
        var fullName = component.GetType().FullName ?? string.Empty;
        if (typeName.IndexOf("DynamicOutline", StringComparison.OrdinalIgnoreCase) >= 0
            || fullName.IndexOf("DynamicOutline", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        return IsLikelyVisualOnlyName(typeName)
            || IsLikelyVisualOnlyName(component.gameObject != null ? component.gameObject.name : string.Empty);
    }

    private static bool IsLikelyVisualOnlyCandidate(Transform target)
    {
        if (target == null)
        {
            return true;
        }

        if (IsLikelyVisualOnlyName(target.gameObject.name))
        {
            return true;
        }

        var components = target.GetComponents<MonoBehaviour>();
        for (var i = 0; i < components.Length; i++)
        {
            var component = components[i];
            if (component == null)
            {
                continue;
            }

            if (!IsLikelyVisualOnlyComponent(component))
            {
                return false;
            }
        }

        if (target.GetComponent<MeshRenderer>() != null
            || target.GetComponent<SkinnedMeshRenderer>() != null
            || target.GetComponent<MeshFilter>() != null)
        {
            return true;
        }

        return false;
    }

    private static bool IsLikelyVisualOnlyName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.ToLowerInvariant();
        return normalized.Contains("dynamicoutline")
            || normalized.Contains("dynamic_outline")
            || normalized.Contains("outline")
            || normalized.Contains("mesh")
            || normalized.Contains("renderer")
            || normalized.Contains("vfx")
            || normalized.Contains("fx_")
            || normalized.Contains("visual")
            || normalized.Contains("lod");
    }



    private static bool TryGetNavigableWaypoint(Vector3 from, Vector3 to, out Vector3 waypoint)
    {
        waypoint = to;
        if (TryHasDirectNavPath(from, to))
        {
            return true;
        }

        return TryFindIndirectWaypoint(from, to, out waypoint);
    }

    private static bool TryFindIndirectWaypoint(Vector3 from, Vector3 to, out Vector3 waypoint)
    {
        waypoint = default;
        if (!NavMesh.SamplePosition(from, out var startHit, 4f, NavMesh.AllAreas))
        {
            return false;
        }

        if (!NavMesh.SamplePosition(to, out var endHit, 4f, NavMesh.AllAreas))
        {
            return false;
        }

        var start = startHit.position;
        var end = endHit.position;
        var toTarget = end - start;
        toTarget.y = 0f;
        if (toTarget.sqrMagnitude <= 0.0001f)
        {
            waypoint = end;
            return true;
        }

        var baseDirection = toTarget.normalized;
        var radii = new[] { 1.5f, 3f, 4.5f, 6f, 8f, 10f };
        var angleOffsets = new[] { 0f, 25f, -25f, 50f, -50f, 75f, -75f, 100f, -100f, 130f, -130f, 160f, -160f, 180f };

        var bestScore = float.MaxValue;
        var found = false;
        foreach (var radius in radii)
        {
            foreach (var angleOffset in angleOffsets)
            {
                var direction = Quaternion.Euler(0f, angleOffset, 0f) * baseDirection;
                var probe = start + direction * radius;
                if (!NavMesh.SamplePosition(probe, out var waypointHit, 1.75f, NavMesh.AllAreas))
                {
                    continue;
                }

                var candidate = waypointHit.position;
                if (!TryHasDirectNavPath(start, candidate) || !TryHasDirectNavPath(candidate, end))
                {
                    continue;
                }

                var score = Vector2.Distance(new Vector2(start.x, start.z), new Vector2(candidate.x, candidate.z))
                    + Vector2.Distance(new Vector2(candidate.x, candidate.z), new Vector2(end.x, end.z));
                if (score >= bestScore)
                {
                    continue;
                }

                bestScore = score;
                waypoint = candidate;
                found = true;
            }
        }

        return found;
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

    private static bool TryGetNavigationOrigin(out Transform origin)
    {
        origin = null!;

        var taggedPlayer = GameObject.FindWithTag("Player");
        if (taggedPlayer != null)
        {
            origin = taggedPlayer.transform;
            return true;
        }

        var controller = UnityEngine.Object.FindObjectOfType<CharacterController>();
        if (controller != null && controller.gameObject.activeInHierarchy)
        {
            origin = controller.transform;
            return true;
        }

        if (Camera.main != null)
        {
            origin = Camera.main.transform;
            return true;
        }

        return false;
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

    private static bool IsEnvironmentScanPressed()
    {
        if (IsKeyDownSafe(Key.Backslash, Key.OEM5)
            || IsKeyDownSafe(Key.OEM1)
            || IsKeyDownSafe(Key.OEM2)
            || IsKeyDownSafe(Key.OEM3)
            || IsKeyDownSafe(Key.OEM4))
        {
            return true;
        }

        try
        {
            if (Input.GetKeyDown(KeyCode.Backslash)
                || Input.GetKeyDown(KeyCode.Slash))
            {
                return true;
            }
        }
        catch
        {
            // Ignore legacy input path failures and rely on Input System only.
        }

        return false;
    }

    private readonly struct ScanDetection
    {
        public ScanDetection(int cueId, ScanCueType cueType, Vector3 position, float distanceMeters, float sizeMeters)
        {
            CueId = cueId;
            CueType = cueType;
            Position = position;
            DistanceMeters = distanceMeters;
            SizeMeters = sizeMeters;
        }

        public int CueId { get; }
        public ScanCueType CueType { get; }
        public Vector3 Position { get; }
        public float DistanceMeters { get; }
        public float SizeMeters { get; }
    }

    private void ToggleEnvironmentScanMode()
    {
        if (_isEnvironmentScanEnabled)
        {
            StopContinuousEnvironmentScan(true);
            return;
        }

        _isEnvironmentScanEnabled = true;
        _nextEnvironmentScanUpdateTime = -100f;
    }

    private void UpdateEnvironmentScanMode()
    {
        if (!_isEnvironmentScanEnabled)
        {
            return;
        }

        var now = Time.unscaledTime;
        if (now < _nextEnvironmentScanUpdateTime)
        {
            UpdateContinuousScanCueDecay(now);
            return;
        }

        _nextEnvironmentScanUpdateTime = now + EnvironmentScanUpdateIntervalSeconds;
        if (!TryGetNavigationOrigin(out var origin))
        {
            StopContinuousEnvironmentScan(true);
            return;
        }

        var detections = PerformContinuousEnvironmentScanStep(origin);
        PlayContinuousScanField(origin, detections);
    }

    private List<ScanDetection> PerformContinuousEnvironmentScanStep(Transform origin)
    {
        var detections = BuildOpenPathDetections(origin);
        if (TryBuildNearestOpenPathDetection(origin, out var primaryDetection))
        {
            var merged = new List<ScanDetection>(detections.Count + 1)
            {
                primaryDetection
            };

            for (var i = 0; i < detections.Count; i++)
            {
                var candidate = detections[i];
                if (AreCueDirectionsNear(origin.position, primaryDetection.Position, candidate.Position, 16f))
                {
                    continue;
                }

                merged.Add(candidate);
            }

            detections = merged;
        }

        if (detections.Count == 0)
        {
            ResetPathStallTracking();
            return new List<ScanDetection>();
        }

        if (ShouldSuppressPathToneForStalledMovement(origin, out var intentDirection))
        {
            var filtered = new List<ScanDetection>(detections.Count);
            for (var i = 0; i < detections.Count; i++)
            {
                var detection = detections[i];
                var toDetection = detection.Position - origin.position;
                toDetection.y = 0f;
                if (toDetection.sqrMagnitude <= 0.0001f)
                {
                    continue;
                }

                var angle = Vector3.Angle(intentDirection, toDetection.normalized);
                if (angle >= PathStallGuideOverrideAngleDegrees)
                {
                    filtered.Add(detection);
                }
            }

            return filtered;
        }

        return detections;
    }

    private static bool AreCueDirectionsNear(Vector3 originPosition, Vector3 firstPosition, Vector3 secondPosition, float thresholdDegrees)
    {
        var first = firstPosition - originPosition;
        var second = secondPosition - originPosition;
        first.y = 0f;
        second.y = 0f;
        if (first.sqrMagnitude <= 0.0001f || second.sqrMagnitude <= 0.0001f)
        {
            return false;
        }

        return Vector3.Angle(first.normalized, second.normalized) <= thresholdDegrees;
    }

    private bool ShouldSuppressPathToneForStalledMovement(Transform origin, out Vector3 intentDirection)
    {
        intentDirection = default;
        if (!TryGetMovementIntentDirection(origin, out intentDirection))
        {
            ResetPathStallTracking();
            return false;
        }

        var now = Time.unscaledTime;
        var currentPosition = origin.position;
        if (!_hasPathStallSample)
        {
            _hasPathStallSample = true;
            _pathStallLastSampleTime = now;
            _pathStallLastPosition = currentPosition;
            _pathStallElapsed = 0f;
            return false;
        }

        var dt = Mathf.Max(0.0001f, now - _pathStallLastSampleTime);
        _pathStallLastSampleTime = now;
        var movedDistance = Vector2.Distance(
            new Vector2(currentPosition.x, currentPosition.z),
            new Vector2(_pathStallLastPosition.x, _pathStallLastPosition.z));
        _pathStallLastPosition = currentPosition;
        var measuredSpeed = movedDistance / dt;

        var controller = origin.GetComponent<CharacterController>();
        if (controller == null || controller.gameObject == null || !controller.gameObject.activeInHierarchy)
        {
            controller = UnityEngine.Object.FindObjectOfType<CharacterController>();
        }

        if (controller != null && controller.gameObject != null && controller.gameObject.activeInHierarchy)
        {
            var velocity = controller.velocity;
            velocity.y = 0f;
            measuredSpeed = Mathf.Max(measuredSpeed, velocity.magnitude);
        }

        if (measuredSpeed > PathStallSpeedThreshold)
        {
            _pathStallElapsed = 0f;
            return false;
        }

        _pathStallElapsed += dt;
        return _pathStallElapsed >= PathStallSuppressSeconds;
    }

    private List<ScanDetection> BuildOpenPathDetections(Transform origin)
    {
        var detections = new List<ScanDetection>();
        if (origin == null)
        {
            return detections;
        }

        var basePosition = origin.position;
        var guidanceForward = ResolvePathGuidanceForward(origin);
        var directions = BuildRadialPathDirections(guidanceForward);
        if (directions.Count == 0)
        {
            return detections;
        }

        var hasStartNavSample = NavMesh.SamplePosition(basePosition, out var startHit, 4f, NavMesh.AllAreas);
        var navStart = hasStartNavSample ? startHit.position : basePosition;
        var rayOrigin = basePosition + (Vector3.up * 0.85f);
        const float targetProbeDistance = 3.5f;
        const float minimumProbeDistance = 1.3f;
        const float maximumProbeDistance = 8f;
        const float minimumOpenClearance = 3.0f;
        const float minimumPathDistance = 1.0f;
        const float maximumPathDistance = 4f;
        var evaluationLimit = Mathf.Max(1, Mathf.Min(MaxRadialPathEvaluationsPerTick, directions.Count));
        var directionStride = Mathf.Max(1, Mathf.CeilToInt(directions.Count / (float)evaluationLimit));
        for (int evaluated = 0, directionIndex = 0;
             directionIndex < directions.Count && evaluated < evaluationLimit;
             directionIndex += directionStride, evaluated++)
        {
            var direction = directions[directionIndex];
            var clearance = EnvironmentScanDistance;
            if (Physics.Raycast(rayOrigin, direction, out var blockingHit, EnvironmentScanDistance))
            {
                if (blockingHit.collider != null && !IsIgnoredScanTarget(blockingHit.collider.transform, origin))
                {
                    var blockingDistance = float.IsNaN(blockingHit.distance) || float.IsInfinity(blockingHit.distance)
                        ? Vector3.Distance(rayOrigin, ResolveScanHitPosition(blockingHit, rayOrigin))
                        : Mathf.Max(0f, blockingHit.distance);
                    clearance = Mathf.Clamp(blockingDistance, 0f, EnvironmentScanDistance);
                }
            }

            clearance = Mathf.Max(0.2f, clearance);
            if (clearance < minimumOpenClearance)
            {
                continue;
            }

            var resolvedDirection = direction;
            var pathReachable = !hasStartNavSample;
            if (hasStartNavSample)
            {
                var maxProbe = Mathf.Min(maximumProbeDistance, EnvironmentScanDistance, clearance - 0.15f);
                if (maxProbe >= minimumProbeDistance)
                {
                    var probeDistance = Mathf.Clamp(targetProbeDistance, minimumProbeDistance, maxProbe);
                    var probePoint = navStart + (direction * probeDistance);
                    if (NavMesh.SamplePosition(probePoint, out var endHit, 2.5f, NavMesh.AllAreas)
                        && TryHasDirectNavPath(navStart, endHit.position))
                    {
                        var toProbe = endHit.position - navStart;
                        toProbe.y = 0f;
                        if (toProbe.sqrMagnitude > 0.0001f)
                        {
                            resolvedDirection = toProbe.normalized;
                            pathReachable = true;
                        }
                    }
                }
            }

            if (!pathReachable)
            {
                continue;
            }

            var pathDistance = Mathf.Clamp(Mathf.Min(targetProbeDistance, clearance * 0.75f), minimumPathDistance, maximumPathDistance);
            var detectionPosition = basePosition + (resolvedDirection * pathDistance);
            var cueSize = Mathf.Clamp(pathDistance * 0.35f, 0.9f, 2.1f);
            detections.Add(new ScanDetection(
                HashCode.Combine(0x50415448, directionIndex),
                ScanCueType.Wall,
                detectionPosition,
                pathDistance,
                cueSize));
        }

        return detections;
    }

    private static List<Vector3> BuildRadialPathDirections(Vector3 forward)
    {
        var directions = new List<Vector3>(8);
        var baseForward = forward.sqrMagnitude <= 0.0001f ? Vector3.forward : forward.normalized;
        for (var step = 0; step < 8; step++)
        {
            var yaw = step * 45f;
            var direction = Quaternion.Euler(0f, yaw, 0f) * baseForward;
            direction.y = 0f;
            if (direction.sqrMagnitude <= 0.0001f)
            {
                continue;
            }

            directions.Add(direction.normalized);
        }

        return directions;
    }

    private void ResetPathStallTracking()
    {
        _hasPathStallSample = false;
        _pathStallLastSampleTime = -100f;
        _pathStallElapsed = 0f;
    }

    private static bool TryGetMovementIntentDirection(Transform origin, out Vector3 movementDirection)
    {
        movementDirection = default;

        var horizontal = 0f;
        var vertical = 0f;
        var keyboard = Keyboard.current;
        if (keyboard != null)
        {
            if (keyboard[Key.A].isPressed || keyboard[Key.LeftArrow].isPressed)
            {
                horizontal -= 1f;
            }

            if (keyboard[Key.D].isPressed || keyboard[Key.RightArrow].isPressed)
            {
                horizontal += 1f;
            }

            if (keyboard[Key.W].isPressed || keyboard[Key.UpArrow].isPressed)
            {
                vertical += 1f;
            }

            if (keyboard[Key.S].isPressed || keyboard[Key.DownArrow].isPressed)
            {
                vertical -= 1f;
            }
        }

        var gamepad = Gamepad.current;
        if (gamepad != null)
        {
            var stick = gamepad.leftStick.ReadValue();
            if (Mathf.Abs(stick.x) > 0.12f)
            {
                horizontal += stick.x;
            }

            if (Mathf.Abs(stick.y) > 0.12f)
            {
                vertical += stick.y;
            }
        }

        try
        {
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow))
            {
                horizontal -= 1f;
            }

            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow))
            {
                horizontal += 1f;
            }

            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow))
            {
                vertical += 1f;
            }

            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow))
            {
                vertical -= 1f;
            }

            horizontal += Input.GetAxisRaw("Horizontal");
            vertical += Input.GetAxisRaw("Vertical");
        }
        catch
        {
            // Input System path already handled.
        }

        horizontal = Mathf.Clamp(horizontal, -1f, 1f);
        vertical = Mathf.Clamp(vertical, -1f, 1f);

        if (Mathf.Abs(horizontal) <= 0.001f && Mathf.Abs(vertical) <= 0.001f)
        {
            return false;
        }

        var forward = origin.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude <= 0.0001f)
        {
            forward = Vector3.forward;
        }

        var right = origin.right;
        right.y = 0f;
        if (right.sqrMagnitude <= 0.0001f)
        {
            right = Vector3.Cross(Vector3.up, forward);
        }

        var world = (right.normalized * horizontal) + (forward.normalized * vertical);
        world.y = 0f;
        if (world.sqrMagnitude <= 0.0001f)
        {
            return false;
        }

        movementDirection = world.normalized;
        return true;
    }

    private bool TryBuildNearestOpenPathDetection(Transform origin, out ScanDetection detection)
    {
        detection = default;
        if (origin == null)
        {
            return false;
        }

        var basePosition = origin.position;
        var guidanceForward = ResolvePathGuidanceForward(origin);
        var currentWaypointDistance = 0f;
        var useExistingWaypoint = false;
        if (_hasPathGuidanceWaypoint)
        {
            currentWaypointDistance = Vector2.Distance(
                new Vector2(basePosition.x, basePosition.z),
                new Vector2(_pathGuidanceWaypoint.x, _pathGuidanceWaypoint.z));

            var shouldRecomputeWaypoint = float.IsNaN(currentWaypointDistance)
                || float.IsInfinity(currentWaypointDistance)
                || currentWaypointDistance <= PathGuidanceWaypointReachedDistance
                || currentWaypointDistance > EnvironmentScanDistance * 1.6f;

            if (!shouldRecomputeWaypoint && _pathGuidanceLastDistance > 0f)
            {
                if (currentWaypointDistance > _pathGuidanceLastDistance + PathGuidanceWaypointDriftDistance)
                {
                    shouldRecomputeWaypoint = true;
                }
                else if (Mathf.Abs(currentWaypointDistance - _pathGuidanceLastDistance) <= PathGuidanceStallDistanceEpsilon)
                {
                    _pathGuidanceStalledUpdates++;
                    if (_pathGuidanceStalledUpdates >= PathGuidanceMaxStalledUpdates)
                    {
                        shouldRecomputeWaypoint = true;
                    }
                }
                else
                {
                    _pathGuidanceStalledUpdates = 0;
                }
            }
            else if (!shouldRecomputeWaypoint)
            {
                _pathGuidanceStalledUpdates = 0;
            }

            if (shouldRecomputeWaypoint)
            {
                InvalidatePathGuidanceWaypoint();
            }
            else
            {
                useExistingWaypoint = true;
            }
        }

        var createdNewWaypoint = false;
        if (!useExistingWaypoint)
        {
            var directions = BuildPathGuidanceDirections(guidanceForward);
            if (directions.Length == 0)
            {
                return false;
            }

            var hasStartNavSample = NavMesh.SamplePosition(basePosition, out var startHit, 4f, NavMesh.AllAreas);
            var navStart = hasStartNavSample ? startHit.position : basePosition;
            var rayOrigin = basePosition + (Vector3.up * 0.85f);
            const float targetProbeDistance = 3.5f;
            const float minimumProbeDistance = 1.4f;
            const float maximumProbeDistance = 8f;
            const float forwardWeight = 0.75f;
            const float clearanceWeight = 1.5f;
            const float navReachableBonus = 3.5f;
            var evaluationLimit = Mathf.Max(1, Mathf.Min(MaxRadialPathEvaluationsPerTick, directions.Length));
            var directionStride = Mathf.Max(1, Mathf.CeilToInt(directions.Length / (float)evaluationLimit));

            var hasCandidate = false;
            var bestScore = float.MinValue;
            var bestClearance = 0.25f;
            var bestDirection = guidanceForward;

            for (int evaluated = 0, directionIndex = 0;
                 directionIndex < directions.Length && evaluated < evaluationLimit;
                 directionIndex += directionStride, evaluated++)
            {
                var direction = directions[directionIndex];
                var clearance = EnvironmentScanDistance;
                if (Physics.Raycast(rayOrigin, direction, out var blockingHit, EnvironmentScanDistance))
                {
                    if (blockingHit.collider != null && !IsIgnoredScanTarget(blockingHit.collider.transform, origin))
                    {
                        var blockingDistance = float.IsNaN(blockingHit.distance) || float.IsInfinity(blockingHit.distance)
                            ? Vector3.Distance(rayOrigin, ResolveScanHitPosition(blockingHit, rayOrigin))
                            : Mathf.Max(0f, blockingHit.distance);
                        clearance = Mathf.Clamp(blockingDistance, 0f, EnvironmentScanDistance);
                    }
                }

                clearance = Mathf.Max(0.25f, clearance);
                var turnAngle = Mathf.Abs(Vector3.SignedAngle(guidanceForward, direction, Vector3.up));
                var forwardScore = 1f - Mathf.Clamp01(turnAngle / 180f);
                var score = (clearance * clearanceWeight) + (forwardScore * forwardWeight);
                var resolvedDirection = direction;

                if (hasStartNavSample)
                {
                    var maxProbe = Mathf.Min(maximumProbeDistance, EnvironmentScanDistance, clearance - 0.15f);
                    if (maxProbe >= minimumProbeDistance)
                    {
                        var probeDistance = Mathf.Clamp(targetProbeDistance, minimumProbeDistance, maxProbe);
                        var probePoint = navStart + (direction * probeDistance);
                        if (NavMesh.SamplePosition(probePoint, out var endHit, 2.5f, NavMesh.AllAreas)
                            && TryHasDirectNavPath(navStart, endHit.position))
                        {
                            var toProbe = endHit.position - navStart;
                            toProbe.y = 0f;
                            if (toProbe.sqrMagnitude > 0.0001f)
                            {
                                resolvedDirection = toProbe.normalized;
                                score += navReachableBonus;
                            }
                        }
                    }
                }

                if (!hasCandidate
                    || score > bestScore + 0.01f
                    || (Mathf.Abs(score - bestScore) <= 0.01f && clearance > bestClearance))
                {
                    hasCandidate = true;
                    bestScore = score;
                    bestClearance = clearance;
                    bestDirection = resolvedDirection;
                }
            }

            if (!hasCandidate)
            {
                return false;
            }

            var waypointDistance = Mathf.Clamp(Mathf.Min(targetProbeDistance, bestClearance * 0.75f), 1.25f, 4f);
            _pathGuidanceWaypoint = basePosition + (bestDirection * waypointDistance);
            _hasPathGuidanceWaypoint = true;
            currentWaypointDistance = waypointDistance;
            createdNewWaypoint = true;
            _pathGuidanceStalledUpdates = 0;
        }

        _pathGuidanceLastDistance = currentWaypointDistance;
        var cueType = ResolvePathGuidanceCueType(currentWaypointDistance, createdNewWaypoint);
        var cueSize = Mathf.Clamp(currentWaypointDistance * 0.4f, 0.9f, 2.4f);
        detection = new ScanDetection(
            HashCode.Combine(0x4F50454E, (int)cueType),
            cueType,
            _pathGuidanceWaypoint,
            Mathf.Max(0.01f, currentWaypointDistance),
            cueSize);
        return true;
    }

    private void InvalidatePathGuidanceWaypoint()
    {
        _hasPathGuidanceWaypoint = false;
        _pathGuidanceWaypoint = default;
        _pathGuidanceLastDistance = -1f;
        _pathGuidanceStalledUpdates = 0;
    }

    private static Vector3 ResolvePathGuidanceForward(Transform origin)
    {
        var reference = Camera.main != null ? Camera.main.transform : origin;
        var forward = reference.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude <= 0.0001f)
        {
            forward = origin.forward;
            forward.y = 0f;
        }

        if (forward.sqrMagnitude <= 0.0001f)
        {
            return Vector3.forward;
        }

        return forward.normalized;
    }

    private static ScanCueType ResolvePathGuidanceCueType(float distanceToWaypoint, bool isNewWaypoint)
    {
        if (isNewWaypoint)
        {
            return ScanCueType.Wall;
        }

        if (distanceToWaypoint <= PathGuidanceHighToneDistance)
        {
            return ScanCueType.Object;
        }

        if (distanceToWaypoint <= PathGuidanceMediumToneDistance)
        {
            return ScanCueType.Door;
        }

        return ScanCueType.Wall;
    }


    private void UpdateContinuousScanCueDecay(float now)
    {
        if (_activeEnvironmentScanCues.Count == 0)
        {
            return;
        }

        if (!TryGetNavigationOrigin(out var origin))
        {
            return;
        }

        var cueIdsToStop = new List<int>();
        foreach (var pair in _activeEnvironmentScanCues)
        {
            var cue = pair.Value;
            var elapsed = now - cue.LastSeenTime;
            if (elapsed >= EnvironmentScanCueGraceSeconds + EnvironmentScanCueFadeSeconds)
            {
                cueIdsToStop.Add(pair.Key);
                continue;
            }

            var fadeProgress = Mathf.Clamp01((elapsed - EnvironmentScanCueGraceSeconds) / Mathf.Max(0.01f, EnvironmentScanCueFadeSeconds));
            var intensity = 1f - Mathf.Max(0f, fadeProgress);
            UpdateContinuousEnvironmentCueSource(origin, cue.Source, cue.CueType, cue.Position, cue.DistanceMeters, cue.SizeMeters, intensity);
        }

        for (var i = 0; i < cueIdsToStop.Count; i++)
        {
            StopContinuousEnvironmentCue(cueIdsToStop[i]);
        }
    }

    private void StopContinuousEnvironmentScan(bool disableMode)
    {
        if (disableMode)
        {
            _isEnvironmentScanEnabled = false;
        }

        _nextEnvironmentScanUpdateTime = -100f;
        ResetPathStallTracking();
        InvalidatePathGuidanceWaypoint();
        StopContinuousScanFieldPlayback();
        if (_activeEnvironmentScanCues.Count > 0)
        {
            var cueIds = new List<int>(_activeEnvironmentScanCues.Keys);
            for (var i = 0; i < cueIds.Count; i++)
            {
                StopContinuousEnvironmentCue(cueIds[i]);
            }
        }

        if (_environmentScanCueRoot != null)
        {
            try
            {
                UnityEngine.Object.Destroy(_environmentScanCueRoot);
            }
            catch
            {
                // Ignore cleanup failures.
            }

            _environmentScanCueRoot = null;
        }
    }

    private void PlayContinuousScanField(Transform origin, IReadOnlyList<ScanDetection> detections)
    {
        if (detections == null || detections.Count == 0)
        {
            StopContinuousScanFieldPlayback();
            return;
        }

        try
        {
            var waveData = BuildContinuousScanFieldWaveData(origin, detections);
            if (waveData.Length == 0)
            {
                return;
            }

            if (_continuousScanFieldBufferHandle.IsAllocated)
            {
                _continuousScanFieldBufferHandle.Free();
            }

            _continuousScanFieldBufferHandle = System.Runtime.InteropServices.GCHandle.Alloc(
                waveData,
                System.Runtime.InteropServices.GCHandleType.Pinned);
            var played = PlaySound(
                _continuousScanFieldBufferHandle.AddrOfPinnedObject(),
                IntPtr.Zero,
                SoundFlagAsync | SoundFlagMemory | SoundFlagNoDefault);
            if (!played && _continuousScanFieldBufferHandle.IsAllocated)
            {
                _continuousScanFieldBufferHandle.Free();
            }
        }
        catch
        {
            // Ignore mix failures this frame.
        }
    }

    private void StopContinuousScanFieldPlayback()
    {
        _ = PlaySound(IntPtr.Zero, IntPtr.Zero, 0u);
        if (_continuousScanFieldBufferHandle.IsAllocated)
        {
            _continuousScanFieldBufferHandle.Free();
        }
    }

    private static byte[] BuildContinuousScanFieldWaveData(Transform origin, IReadOnlyList<ScanDetection> detections)
    {
        const int sampleRate = 22050;
        const float durationSeconds = 0.24f;
        const float globalOutputGain = 0.55f;
        var sampleCount = Mathf.Max(1, Mathf.RoundToInt(sampleRate * durationSeconds));
        var dataBytes = sampleCount * 2 * sizeof(short);
        using var stream = new System.IO.MemoryStream(44 + dataBytes);
        using var writer = new System.IO.BinaryWriter(stream);

        writer.Write(new[] { 'R', 'I', 'F', 'F' });
        writer.Write(36 + dataBytes);
        writer.Write(new[] { 'W', 'A', 'V', 'E' });
        writer.Write(new[] { 'f', 'm', 't', ' ' });
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2 * sizeof(short));
        writer.Write((short)(2 * sizeof(short)));
        writer.Write((short)16);
        writer.Write(new[] { 'd', 'a', 't', 'a' });
        writer.Write(dataBytes);

        var listener = Camera.main != null ? Camera.main.transform : origin;
        var components = new (float angular, float gain, float left, float right)[detections.Count];
        var componentCount = 0;
        for (var i = 0; i < detections.Count; i++)
        {
            var detection = detections[i];
            var relative = listener.InverseTransformPoint(detection.Position);
            var planarDistance = new Vector2(relative.x, relative.z).magnitude;
            var distance = planarDistance > 0.01f ? planarDistance : Mathf.Max(0.01f, detection.DistanceMeters);
            var distanceFactor = Mathf.Clamp01(distance / Mathf.Max(1f, EnvironmentScanDistance));
            var panCenter = Mathf.Clamp(relative.x / (Mathf.Abs(relative.x) + Mathf.Abs(relative.z) + 0.001f), -1f, 1f);
            var width = Mathf.Clamp01(detection.SizeMeters / Mathf.Max(0.5f, distance)) * 0.9f;

            var left = Mathf.Clamp01(((1f - panCenter) * 0.5f) + width * 0.5f);
            var right = Mathf.Clamp01(((1f + panCenter) * 0.5f) + width * 0.5f);
            var baseGain = detection.CueType switch
            {
                ScanCueType.Wall => 0.28f,
                ScanCueType.Door => 0.34f,
                _ => 0.4f
            };
            var gain = baseGain * Mathf.Lerp(1f, 0.2f, distanceFactor);
            var angular = 2f * Mathf.PI * ResolveContinuousEnvironmentCueFrequency(detection.CueType) / sampleRate;
            components[componentCount++] = (angular, gain, left, right);
        }

        var maxAbs = 0f;
        var leftMix = new float[sampleCount];
        var rightMix = new float[sampleCount];
        for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            var fadeIn = Mathf.Clamp01(sampleIndex / (sampleRate * 0.01f));
            var fadeOut = Mathf.Clamp01((sampleCount - sampleIndex) / (sampleRate * 0.03f));
            var envelope = Mathf.Min(fadeIn, fadeOut);
            var leftValue = 0f;
            var rightValue = 0f;
            for (var c = 0; c < componentCount; c++)
            {
                var component = components[c];
                var tone = Mathf.Sin(sampleIndex * component.angular) * component.gain * envelope;
                leftValue += tone * component.left;
                rightValue += tone * component.right;
            }

            leftMix[sampleIndex] = leftValue;
            rightMix[sampleIndex] = rightValue;
            maxAbs = Mathf.Max(maxAbs, Mathf.Abs(leftValue), Mathf.Abs(rightValue));
        }

        var norm = maxAbs > 0.98f ? 0.98f / maxAbs : 1f;
        var finalGain = norm * globalOutputGain;
        for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            var leftSample = (short)Mathf.Clamp(Mathf.RoundToInt(leftMix[sampleIndex] * finalGain * short.MaxValue), short.MinValue, short.MaxValue);
            var rightSample = (short)Mathf.Clamp(Mathf.RoundToInt(rightMix[sampleIndex] * finalGain * short.MaxValue), short.MinValue, short.MaxValue);
            writer.Write(leftSample);
            writer.Write(rightSample);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private void StopContinuousEnvironmentCue(int cueId)
    {
        if (!_activeEnvironmentScanCues.TryGetValue(cueId, out var cue))
        {
            return;
        }

        try
        {
            if (cue.Source != null)
            {
                cue.Source.Stop();
                if (cue.Source.gameObject != null)
                {
                    UnityEngine.Object.Destroy(cue.Source.gameObject);
                }
            }
        }
        catch
        {
            // Ignore stale source failures.
        }

        _activeEnvironmentScanCues.Remove(cueId);
    }

    private static Vector3[] BuildPathGuidanceDirections(Vector3 referenceForward)
    {
        var normalizedForward = referenceForward.sqrMagnitude <= 0.0001f
            ? Vector3.forward
            : referenceForward.normalized;
        const float yawStepDegrees = 15f;
        var yawBands = Mathf.RoundToInt(360f / yawStepDegrees);
        var directions = new List<Vector3>(yawBands);
        for (var yawBand = 0; yawBand < yawBands; yawBand++)
        {
            var yaw = yawBand * yawStepDegrees;
            var direction = Quaternion.Euler(0f, yaw, 0f) * normalizedForward;
            if (direction.sqrMagnitude <= 0.0001f)
            {
                continue;
            }

            directions.Add(direction.normalized);
        }

        return directions.ToArray();
    }

    private static bool IsIgnoredScanTarget(Transform hitTransform, Transform origin)
    {
        if (hitTransform == null || origin == null)
        {
            return true;
        }

        if (hitTransform == origin
            || hitTransform.IsChildOf(origin)
            || origin.IsChildOf(hitTransform))
        {
            return true;
        }

        return false;
    }

    private static Vector3 ResolveScanHitPosition(RaycastHit hit, Vector3 fallbackPosition)
    {
        if (IsFiniteVector3(hit.point))
        {
            return hit.point;
        }

        if (hit.collider != null)
        {
            var center = hit.collider.bounds.center;
            if (IsFiniteVector3(center))
            {
                return center;
            }
        }

        return fallbackPosition;
    }


    private void UpdateContinuousEnvironmentCueSource(
        Transform origin,
        AudioSource source,
        ScanCueType cueType,
        Vector3 cuePosition,
        float distanceMeters,
        float sizeMeters,
        float intensity)
    {
        if (source == null)
        {
            return;
        }

        var resolvedDistance = distanceMeters;
        if (float.IsNaN(resolvedDistance) || float.IsInfinity(resolvedDistance) || resolvedDistance <= 0f)
        {
            resolvedDistance = Vector3.Distance(origin.position, cuePosition);
        }

        var resolvedSize = float.IsNaN(sizeMeters) || float.IsInfinity(sizeMeters) || sizeMeters <= 0f
            ? 1f
            : sizeMeters;
        var distanceFactor = Mathf.Clamp01(resolvedDistance / Mathf.Max(1f, EnvironmentScanDistance));
        var sizeFactor = Mathf.Clamp01(resolvedSize / 8f);
        var baseVolume = ResolveContinuousEnvironmentCueBaseVolume(cueType);
        var sizeGain = Mathf.Lerp(0.85f, 1.35f, sizeFactor);
        var attenuatedVolume = baseVolume * sizeGain * Mathf.Lerp(1f, 0.2f, distanceFactor) * Mathf.Clamp01(intensity);

        source.transform.position = cuePosition;
        source.pitch = ResolveContinuousEnvironmentCuePitch(cueType);
        source.spread = Mathf.Clamp(20f + (resolvedSize * 18f), 15f, 180f);
        source.minDistance = Mathf.Clamp(0.6f + (resolvedSize * 0.25f), 0.6f, 8f);
        source.maxDistance = Mathf.Clamp(EnvironmentScanDistance + (resolvedSize * 2.2f), EnvironmentScanDistance + 4f, EnvironmentScanDistance + 80f);
        source.volume = Mathf.Clamp(attenuatedVolume, 0f, 1f);
        if (!source.isPlaying)
        {
            source.Play();
        }
    }

    private static float ResolveContinuousEnvironmentCueBaseVolume(ScanCueType cueType)
    {
        return cueType switch
        {
            ScanCueType.Wall => 0.52f,
            ScanCueType.Door => 0.62f,
            ScanCueType.Object => 0.72f,
            _ => 0.6f
        };
    }

    private static int ResolveContinuousEnvironmentCueFrequency(ScanCueType cueType)
    {
        return cueType switch
        {
            ScanCueType.Wall => WallToneFrequency,
            ScanCueType.Door => DoorToneFrequency,
            ScanCueType.Object => ObjectToneFrequency,
            _ => DoorToneFrequency
        };
    }

    private static float ResolveContinuousEnvironmentCuePitch(ScanCueType cueType)
    {
        return ResolveScanTonePitch(ResolveContinuousEnvironmentCueFrequency(cueType));
    }


    private void DisposeEnvironmentScanToneSources()
    {
        if (_environmentScanCueRoot != null)
        {
            try
            {
                UnityEngine.Object.Destroy(_environmentScanCueRoot);
            }
            catch
            {
                // Ignore shutdown cleanup failures.
            }

            _environmentScanCueRoot = null;
        }

        _environmentScanLoopedToneClip = null;
    }


    private static float ResolveScanTonePitch(int frequency)
    {
        if (frequency == WallToneFrequency)
        {
            return 0.8f;
        }

        if (frequency == DoorToneFrequency)
        {
            return 1.1f;
        }

        if (frequency == ObjectToneFrequency)
        {
            return 1.45f;
        }

        return 1f;
    }



    private void AnnounceTargetSelection(ReachableTarget target)
    {
        var displayName = ResolveReachableTargetDisplayName(target);
        if (!TryGetNavigationOrigin(out var origin))
        {
            SpeakSelectionText(displayName);
            return;
        }

        var destination = target.Transform != null ? target.Transform.position : target.Destination;
        var meters = Mathf.Max(1, Mathf.RoundToInt(Vector2.Distance(
            new Vector2(origin.position.x, origin.position.z),
            new Vector2(destination.x, destination.z))));
        var direction = DescribeDirectionRelativeTo(origin, destination);
        SpeakSelectionText($"{displayName}, {meters} meters, {direction}");
    }

    private string ResolveReachableTargetDisplayName(ReachableTarget target)
    {
        if (target.Transform == null)
        {
            return target.DisplayName;
        }

        var resolvedName = ResolveInteractableDisplayName(target.Transform);
        if (!string.IsNullOrWhiteSpace(resolvedName) && !IsLikelyTechnicalObjectName(resolvedName))
        {
            return resolvedName;
        }

        if (!string.IsNullOrWhiteSpace(target.DisplayName))
        {
            return target.DisplayName;
        }

        return ReadableObjectName(target.Transform.gameObject.name);
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

    private static void TurnTowardsTarget(Transform origin, Vector3 destination, float deltaTime)
    {
        var bodyTarget = destination - origin.position;
        bodyTarget.y = 0f;
        if (bodyTarget.sqrMagnitude <= 0.0001f)
        {
            return;
        }

        var targetYaw = Quaternion.LookRotation(bodyTarget.normalized, Vector3.up).eulerAngles.y;

        // Keep body orientation aligned with the navigation target.
        var originEuler = origin.rotation.eulerAngles;
        var targetOriginRotation = Quaternion.Euler(originEuler.x, targetYaw, originEuler.z);
        origin.rotation = Quaternion.RotateTowards(origin.rotation, targetOriginRotation, AutoTurnDegreesPerSecond * deltaTime);

        // Also steer the active camera so the user hears and sees the same target direction.
        if (Camera.main == null)
        {
            return;
        }

        var cameraTransform = Camera.main.transform;
        var cameraDirection = destination - cameraTransform.position;
        var targetCameraRotation = cameraDirection.sqrMagnitude <= 0.0001f
            ? Quaternion.Euler(cameraTransform.rotation.eulerAngles.x, targetYaw, cameraTransform.rotation.eulerAngles.z)
            : Quaternion.LookRotation(cameraDirection.normalized, Vector3.up);
        cameraTransform.rotation = Quaternion.RotateTowards(cameraTransform.rotation, targetCameraRotation, AutoTurnDegreesPerSecond * deltaTime);
    }

    private static Vector3 ResolveNavigationDestinationPoint(Vector3 origin, Transform target)
    {
        if (target == null)
        {
            return origin;
        }

        if (TryGetInteractableCollider(target, out var collider))
        {
            var effectiveCollider = ResolveEffectiveInteractionCollider(target, collider) ?? collider;
            var closestPoint = effectiveCollider.ClosestPoint(origin);
            if (IsFiniteVector3(closestPoint))
            {
                return closestPoint;
            }
        }

        return target.position;
    }

    private static Vector3 ResolveInteractionAimPoint(Transform target)
    {
        if (target == null)
        {
            return Vector3.zero;
        }

        if (TryGetInteractableCollider(target, out var collider))
        {
            var effectiveCollider = ResolveEffectiveInteractionCollider(target, collider) ?? collider;
            var center = effectiveCollider.bounds.center;
            if (!IsFiniteVector3(center))
            {
                center = target.position;
            }

            var extents = effectiveCollider.bounds.extents;
            var maxExtent = Mathf.Max(extents.x, Mathf.Max(extents.y, extents.z));

            var camera = Camera.main;
            if (camera != null && maxExtent > 0.28f)
            {
                var closest = effectiveCollider.ClosestPoint(camera.transform.position);
                if (IsFiniteVector3(closest))
                {
                    // Large objects: closest point gives stable surface aiming.
                    return closest;
                }
            }

            if (IsFiniteVector3(center))
            {
                // Small objects: aim slightly above center to avoid landing on nearby ledge/surface edges.
                if (maxExtent <= 0.28f)
                {
                    var verticalOffset = Mathf.Clamp(extents.y * 0.35f, 0.02f, 0.08f);
                    return center + (Vector3.up * verticalOffset);
                }

                return center;
            }
        }

        return target.position;
    }

    private bool IsTargetReadyForInteraction(Transform origin, Transform target, Vector3 aimPoint, out bool isWithinGameInteractRange)
    {
        isWithinGameInteractRange = false;
        if (!TryGetInteractionHandler(out _))
        {
            return false;
        }

        var targetWithinDistance = IsTargetWithinGameInteractionDistance(target)
            && IsWithinPracticalInteractionDistance(origin, target);
        var targetHasAvailableInteraction = HasAvailableInteraction(target);
        isWithinGameInteractRange = targetWithinDistance && targetHasAvailableInteraction;
        if (!targetWithinDistance || !targetHasAvailableInteraction)
        {
            return false;
        }

        if (!IsTargetObservedInteractable(target))
        {
            return false;
        }

        if (!TryResolveDirectCrosshairInteractable(out var crosshairTarget))
        {
            return false;
        }

        if (!DoTargetsRepresentSameInteractable(crosshairTarget, target))
        {
            return false;
        }

        var crosshairWithinDistance = IsTargetWithinGameInteractionDistance(crosshairTarget)
            && IsWithinPracticalInteractionDistance(origin, crosshairTarget);
        if (!crosshairWithinDistance)
        {
            return false;
        }

        return HasAvailableInteraction(crosshairTarget);
    }

    private void UpdateObservedInteractableAnnouncement()
    {
        if (IsMenuActive())
        {
            _lastObservedInteractableObjectId = -1;
            return;
        }

        if (!TryResolveHandIconInteractable(out var observedTarget, allowFallback: false)
            && !TryResolveCrosshairInteractableByProximity(out observedTarget))
        {
            _lastObservedInteractableObjectId = -1;
            return;
        }

        var observedId = observedTarget.GetInstanceID();
        var now = Time.unscaledTime;
        if (observedId == _lastObservedInteractableObjectId
            || now - _lastObservedInteractableSpeakTime < ObservedInteractableSpeakCooldownSeconds)
        {
            return;
        }

        var observedName = ResolveInteractableDisplayName(observedTarget);
        if (!string.IsNullOrWhiteSpace(observedName))
        {
            SpeakSelectionTextDedup($"observed|{observedId}", observedName);
            _lastObservedInteractableSpeakTime = now;
        }

        _lastObservedInteractableObjectId = observedId;
    }

    private bool TryResolveCrosshairInteractableByProximity(out Transform target)
    {
        target = null!;
        var camera = Camera.main;
        if (camera == null)
        {
            return false;
        }

        var origin = camera.transform.position;
        var direction = camera.transform.forward;
        var hits = Physics.RaycastAll(origin, direction, HandIconFallbackRaycastDistance, ~0, QueryTriggerInteraction.Collide);
        if (hits == null || hits.Length == 0)
        {
            return false;
        }

        var bestDistance = float.MaxValue;
        for (var i = 0; i < hits.Length; i++)
        {
            var hit = hits[i];
            if (hit.collider == null || hit.collider.transform == null || hit.distance > bestDistance)
            {
                continue;
            }

            var candidate = ResolveNavigationTargetTransform(hit.collider.transform);
            if (candidate == null || !candidate.gameObject.activeInHierarchy)
            {
                continue;
            }

            if (!IsLikelyCrosshairInteractableTarget(candidate))
            {
                continue;
            }

            if (!IsLikelyWithinCrosshairInteractRange(camera.transform.position, candidate))
            {
                continue;
            }

            target = candidate;
            bestDistance = hit.distance;
        }

        return target != null;
    }

    private bool TryResolveDirectCrosshairInteractable(out Transform target)
    {
        target = null!;
        var camera = Camera.main;
        if (camera == null)
        {
            return false;
        }

        var origin = camera.transform.position;
        var direction = camera.transform.forward;
        var hits = Physics.RaycastAll(origin, direction, HandIconFallbackRaycastDistance, ~0, QueryTriggerInteraction.Collide);
        if (hits == null || hits.Length == 0)
        {
            return false;
        }

        var orderedHits = new List<RaycastHit>(hits.Length);
        for (var i = 0; i < hits.Length; i++)
        {
            orderedHits.Add(hits[i]);
        }

        orderedHits.Sort((left, right) => left.distance.CompareTo(right.distance));
        for (var i = 0; i < orderedHits.Count; i++)
        {
            var hit = orderedHits[i];
            if (hit.collider == null)
            {
                continue;
            }

            if (hit.collider.isTrigger)
            {
                continue;
            }

            var candidate = ResolveNavigationTargetTransform(hit.collider.transform);
            if (candidate == null || !candidate.gameObject.activeInHierarchy)
            {
                return false;
            }

            if (!IsLikelyCrosshairInteractableTarget(candidate))
            {
                return false;
            }

            if (!IsLikelyWithinCrosshairInteractRange(camera.transform.position, candidate))
            {
                return false;
            }

            target = candidate;
            return true;
        }

        return false;
    }

    private bool TryResolveHandIconInteractable(out Transform observedTarget, bool allowFallback = true)
    {
        observedTarget = null!;
        if (TryGetInteractionHandler(out var interactionHandler)
            && TryGetObservedInteractableTransform(interactionHandler, out var observedTransform))
        {
            var resolved = ResolveNavigationTargetTransform(observedTransform);
            if (resolved != null
                && resolved.gameObject.activeInHierarchy
                && IsObservedTargetAlignedWithCrosshair(resolved))
            {
                observedTarget = resolved;
                return true;
            }
        }

        if (allowFallback && TryResolveCrosshairInteractableTransform(out var crosshairTarget))
        {
            observedTarget = crosshairTarget;
            return true;
        }

        return false;
    }

    private bool TryResolveCrosshairInteractableTransform(out Transform target)
    {
        target = null!;
        var camera = Camera.main;
        if (camera == null)
        {
            return false;
        }

        var origin = camera.transform.position;
        var direction = camera.transform.forward;
        var hits = Physics.RaycastAll(origin, direction, HandIconFallbackRaycastDistance, ~0, QueryTriggerInteraction.Collide);
        if (hits == null || hits.Length == 0)
        {
            return false;
        }

        var bestDistance = float.MaxValue;
        for (var i = 0; i < hits.Length; i++)
        {
            var hit = hits[i];
            if (hit.collider == null || hit.collider.transform == null)
            {
                continue;
            }

            if (hit.distance > bestDistance)
            {
                continue;
            }

            var candidate = ResolveNavigationTargetTransform(hit.collider.transform);
            if (candidate == null || !candidate.gameObject.activeInHierarchy)
            {
                continue;
            }

            var inGameInteractionRange = IsColliderWithinGameInteractionDistance(hit.collider);
            if (!IsLikelyCrosshairInteractableTarget(candidate))
            {
                if (!inGameInteractionRange)
                {
                    continue;
                }
            }

            if (!inGameInteractionRange && !IsLikelyWithinCrosshairInteractRange(camera.transform.position, candidate))
            {
                continue;
            }

            if (!HasAvailableInteraction(candidate) && !inGameInteractionRange)
            {
                continue;
            }

            target = candidate;
            bestDistance = hit.distance;
        }

        return target != null;
    }

    private static bool IsLikelyWithinCrosshairInteractRange(Vector3 cameraPosition, Transform target)
    {
        if (target == null)
        {
            return false;
        }

        if (TryGetInteractableCollider(target, out var collider))
        {
            var closestPoint = collider.ClosestPoint(cameraPosition);
            var distance = Vector3.Distance(cameraPosition, closestPoint);
            var maxDistance = ResolveAdaptiveInteractionDistance(collider, closeBias: 0.08f);
            return distance <= maxDistance;
        }

        return Vector3.Distance(cameraPosition, target.position) <= 1.1f;
    }

    private static bool IsLikelyCrosshairInteractableTarget(Transform target)
    {
        if (target == null)
        {
            return false;
        }

        if (TryGetInteractableItem(target, out _))
        {
            return true;
        }

        var parentComponents = target.GetComponentsInParent<MonoBehaviour>(true);
        for (var i = 0; i < parentComponents.Length; i++)
        {
            var component = parentComponents[i];
            if (component != null && LooksInteractable(component))
            {
                return true;
            }
        }

        var childComponents = target.GetComponentsInChildren<MonoBehaviour>(true);
        for (var i = 0; i < childComponents.Length; i++)
        {
            var component = childComponents[i];
            if (component != null && LooksInteractable(component))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasAvailableInteraction(Transform target)
    {
        if (target == null)
        {
            return false;
        }

        if (!TryGetInteractableItem(target, out var interactableItem))
        {
            return false;
        }

        var itemType = interactableItem.GetType();
        if (!TryResolveInteractionAccessors(itemType, out var getCountMethod, out var getInteractionMethod))
        {
            return false;
        }

        if (!TryGetInteractionCount(interactableItem, getCountMethod, out var count))
        {
            return false;
        }

        if (count <= 0)
        {
            return false;
        }

        var sawInteraction = false;
        var sawAvailabilityFlag = false;
        for (var i = 0; i < count; i++)
        {
            object interaction;
            if (!TryInvokeInteractionByIndex(getInteractionMethod, interactableItem, i, out interaction))
            {
                continue;
            }

            if (interaction == null)
            {
                continue;
            }

            sawInteraction = true;
            if (!TryResolveInteractionAvailability(interaction, out var available))
            {
                continue;
            }

            sawAvailabilityFlag = true;
            if (!available)
            {
                continue;
            }

            if (TryGetStringProperty(interaction, "Name", out var interactionName)
                && IsUsableActionText(NormalizeText(interactionName)))
            {
                return true;
            }

            if (TryGetStringProperty(interaction, "Description", out var interactionDescription)
                && IsUsableActionText(NormalizeText(interactionDescription)))
            {
                return true;
            }

            if (TryGetStringProperty(interaction, "_defaultStringHandle", out var defaultHandle)
                && IsUsableActionText(ReadableObjectName(defaultHandle)))
            {
                return true;
            }

            // Interaction is available but text metadata is missing.
            return true;
        }

        if (!sawAvailabilityFlag)
        {
            // Some builds hide availability behind non-public members; treat listed interactions as actionable.
            return sawInteraction;
        }

        return false;
    }

    private static bool TryResolveInteractionAvailability(object interaction, out bool available)
    {
        available = false;
        if (interaction == null)
        {
            return false;
        }

        var availabilityPropertyNames = new[]
        {
            "Available",
            "IsAvailable",
            "CanInteract",
            "IsInteractable",
            "Enabled",
            "IsEnabled",
            "CanUse",
            "Usable",
            "IsUsable",
            "Valid",
            "IsValid"
        };

        for (var i = 0; i < availabilityPropertyNames.Length; i++)
        {
            if (!TryGetBooleanProperty(interaction, availabilityPropertyNames[i], out var value))
            {
                continue;
            }

            available = value;
            return true;
        }

        return false;
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

    private bool IsTargetWithinGameInteractionDistance(Transform target)
    {
        if (target == null)
        {
            return false;
        }

        if (!TryGetInteractableCollider(target, out var collider))
        {
            return false;
        }

        return IsColliderWithinGameInteractionDistance(collider);
    }

    private bool IsColliderWithinGameInteractionDistance(Collider collider)
    {
        if (collider == null)
        {
            return false;
        }

        if (!TryGetInteractionHandler(out var interactionHandler))
        {
            return false;
        }

        MethodInfo method;
        if (!TryFindColliderBooleanMethod(interactionHandler.GetType(), "IsObjectWithinInterationDistance", out method)
            && !TryFindColliderBooleanMethod(interactionHandler.GetType(), "IsObjectWithinInteractionDistance", out method))
        {
            method = null!;
        }
        if (method == null)
        {
            return false;
        }

        try
        {
            var result = method.Invoke(interactionHandler, new object[] { collider });
            return result is bool inRange && inRange;
        }
        catch
        {
            return false;
        }
    }

    private bool TryGetInteractionHandler(out MonoBehaviour interactionHandler)
    {
        interactionHandler = null!;
        if (_interactionHandlerComponent != null
            && _interactionHandlerComponent.gameObject.activeInHierarchy
            && TryScoreInteractionHandlerCandidate(_interactionHandlerComponent, out _))
        {
            interactionHandler = _interactionHandlerComponent;
            return true;
        }

        var now = Time.unscaledTime;
        if (now < _nextInteractionHandlerSearchTime)
        {
            return false;
        }

        var components = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
        MonoBehaviour? bestMatch = null;
        var bestScore = int.MinValue;
        foreach (var component in components)
        {
            if (component == null || !component.gameObject.activeInHierarchy)
            {
                continue;
            }

            if (!TryScoreInteractionHandlerCandidate(component, out var score))
            {
                continue;
            }

            if (score > bestScore)
            {
                bestMatch = component;
                bestScore = score;
            }
        }

        if (bestMatch != null)
        {
            _interactionHandlerComponent = bestMatch;
            _nextInteractionHandlerSearchTime = -100f;
            interactionHandler = bestMatch;
            return true;
        }

        _nextInteractionHandlerSearchTime = now + InteractionHandlerSearchRetrySeconds;
        return false;
    }


    private static bool TryScoreInteractionHandlerCandidate(MonoBehaviour component, out int score)
    {
        score = int.MinValue;
        if (component == null)
        {
            return false;
        }

        var type = component.GetType();
        var fullName = type.FullName ?? string.Empty;
        var typeName = type.Name ?? string.Empty;
        var hasNameMatch = false;
        if (string.Equals(fullName, "Il2Cpp.InteractionHandler", StringComparison.Ordinal))
        {
            score = 120;
            hasNameMatch = true;
        }
        else if (fullName.Contains("InteractionHandler", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("InteractionHandler", StringComparison.OrdinalIgnoreCase))
        {
            score = 90;
            hasNameMatch = true;
        }
        else
        {
            score = 0;
        }

        var hasDistanceMethod = HasColliderDistanceMethod(type);
        if (hasDistanceMethod)
        {
            score += 45;
        }

        var hasObservedMember = type.GetProperty("ObservedInteractableItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null
            || type.GetField("ObservedInteractableItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null
            || type.GetProperty("observedInteractable", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null
            || type.GetField("observedInteractable", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null;
        if (hasObservedMember)
        {
            score += 30;
        }

        if (hasDistanceMethod && hasObservedMember)
        {
            score += 20;
        }

        if (!hasNameMatch && !hasDistanceMethod && !hasObservedMember)
        {
            return false;
        }

        return score >= 40;
    }

    private static bool HasColliderDistanceMethod(Type type)
    {
        if (type == null)
        {
            return false;
        }

        if (TryFindColliderBooleanMethod(type, "IsObjectWithinInterationDistance", out _)
            || TryFindColliderBooleanMethod(type, "IsObjectWithinInteractionDistance", out _))
        {
            return true;
        }

        var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        for (var i = 0; i < methods.Length; i++)
        {
            var method = methods[i];
            if (!string.Equals(method.ReturnType?.Name, typeof(bool).Name, StringComparison.Ordinal))
            {
                continue;
            }

            var methodName = method.Name ?? string.Empty;
            if (!methodName.Contains("Distance", StringComparison.OrdinalIgnoreCase)
                || !methodName.Contains("Inter", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parameters = method.GetParameters();
            if (parameters.Length == 1 && typeof(Collider).IsAssignableFrom(parameters[0].ParameterType))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryFindColliderBooleanMethod(Type type, string methodName, out MethodInfo method)
    {
        method = null!;
        if (type == null || string.IsNullOrWhiteSpace(methodName))
        {
            return false;
        }

        var candidates = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        for (var i = 0; i < candidates.Length; i++)
        {
            var candidate = candidates[i];
            if (!string.Equals(candidate.Name, methodName, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(candidate.ReturnType?.Name, typeof(bool).Name, StringComparison.Ordinal))
            {
                continue;
            }

            var parameters = candidate.GetParameters();
            if (parameters.Length != 1 || !typeof(Collider).IsAssignableFrom(parameters[0].ParameterType))
            {
                continue;
            }

            method = candidate;
            return true;
        }

        return false;
    }

    private bool IsTargetObservedInteractable(Transform target)
    {
        if (target == null || !TryResolveHandIconInteractable(out var observedTransform, allowFallback: false))
        {
            return false;
        }

        return DoTargetsRepresentSameInteractable(observedTransform, target);
    }

    private static bool DoTargetsRepresentSameInteractable(Transform left, Transform right)
    {
        if (left == null || right == null)
        {
            return false;
        }

        var leftResolved = ResolveNavigationTargetTransform(left);
        var rightResolved = ResolveNavigationTargetTransform(right);
        if (leftResolved == null || rightResolved == null)
        {
            return false;
        }

        if (leftResolved.GetInstanceID() == rightResolved.GetInstanceID())
        {
            return true;
        }

        if (TryGetInteractableCollider(leftResolved, out var leftCollider)
            && TryGetInteractableCollider(rightResolved, out var rightCollider)
            && leftCollider != null
            && rightCollider != null
            && leftCollider.GetInstanceID() == rightCollider.GetInstanceID())
        {
            return true;
        }

        if (TryGetInteractableItem(leftResolved, out var leftItem)
            && TryGetInteractableItem(rightResolved, out var rightItem)
            && leftItem != null
            && rightItem != null
            && leftItem.GetInstanceID() == rightItem.GetInstanceID())
        {
            return true;
        }

        return false;
    }

    private bool IsObservedTargetAlignedWithCrosshair(Transform target)
    {
        if (target == null)
        {
            return false;
        }

        var camera = Camera.main;
        if (camera == null)
        {
            return true;
        }

        var aimPoint = ResolveInteractionAimPoint(target);
        var toTarget = aimPoint - camera.transform.position;
        var distance = toTarget.magnitude;
        if (distance <= 0.02f)
        {
            return true;
        }

        var viewAngle = Vector3.Angle(camera.transform.forward, toTarget / distance);
        var maxAimAngle = ResolveAdaptiveCrosshairAimAngle(target);
        if (viewAngle > maxAimAngle)
        {
            return false;
        }
        return true;
    }

    private bool IsWithinPracticalInteractionDistance(Transform origin, Transform target)
    {
        if (target == null)
        {
            return false;
        }

        var referencePosition = Camera.main != null ? Camera.main.transform.position : origin.position;
        if (TryGetInteractableCollider(target, out var collider))
        {
            var closestPoint = collider.ClosestPoint(referencePosition);
            var distance = Vector3.Distance(referencePosition, closestPoint);
            var maxDistance = ResolveAdaptiveInteractionDistance(collider, closeBias: -0.06f);
            return distance <= maxDistance;
        }

        return Vector3.Distance(referencePosition, target.position) <= 1.25f;
    }

    private static float ResolveAdaptiveCrosshairAimAngle(Transform target)
    {
        if (target == null || !TryGetInteractableCollider(target, out var collider))
        {
            return 6.5f;
        }

        var maxExtent = Mathf.Max(collider.bounds.extents.x, Mathf.Max(collider.bounds.extents.y, collider.bounds.extents.z));
        var normalized = Mathf.InverseLerp(0.05f, 0.85f, maxExtent);
        return Mathf.Lerp(3.25f, 9.25f, normalized);
    }

    private static float ResolveAdaptiveInteractionDistance(Collider collider, float closeBias)
    {
        if (collider == null)
        {
            return 1.1f;
        }

        var extents = collider.bounds.extents;
        var maxExtent = Mathf.Max(extents.x, Mathf.Max(extents.y, extents.z));
        var normalized = Mathf.InverseLerp(0.05f, 1.2f, maxExtent);
        var baseDistance = Mathf.Lerp(0.62f, 2.1f, normalized);
        return Mathf.Clamp(baseDistance + closeBias, 0.55f, 2.2f);
    }

    private static bool TryGetObservedInteractableTransform(MonoBehaviour interactionHandler, out Transform observedTransform)
    {
        observedTransform = null!;
        if (interactionHandler == null)
        {
            return false;
        }

        if (!TryGetMemberValue(interactionHandler, "ObservedInteractableItem", out var observedValue)
            && !TryGetMemberValue(interactionHandler, "observedInteractable", out observedValue))
        {
            observedValue = null;
        }

        if (TryExtractTransformFromObservedValue(observedValue, out observedTransform))
        {
            return true;
        }

        var type = interactionHandler.GetType();
        var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        for (var i = 0; i < properties.Length; i++)
        {
            var property = properties[i];
            if (property == null || property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            var name = property.Name ?? string.Empty;
            if (!IsObservedMemberName(name))
            {
                continue;
            }

            object value;
            try
            {
                value = property.GetValue(interactionHandler);
            }
            catch
            {
                continue;
            }

            if (TryExtractTransformFromObservedValue(value, out observedTransform))
            {
                return true;
            }
        }

        var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        for (var i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            if (field == null)
            {
                continue;
            }

            var name = field.Name ?? string.Empty;
            if (!IsObservedMemberName(name))
            {
                continue;
            }

            object value;
            try
            {
                value = field.GetValue(interactionHandler);
            }
            catch
            {
                continue;
            }

            if (TryExtractTransformFromObservedValue(value, out observedTransform))
            {
                return true;
            }
        }

        var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        for (var i = 0; i < methods.Length; i++)
        {
            var method = methods[i];
            if (method == null || method.GetParameters().Length != 0)
            {
                continue;
            }

            var name = method.Name ?? string.Empty;
            if (!name.StartsWith("Get", StringComparison.Ordinal)
                || !IsObservedMemberName(name))
            {
                continue;
            }

            object value;
            try
            {
                value = method.Invoke(interactionHandler, null);
            }
            catch
            {
                continue;
            }

            if (TryExtractTransformFromObservedValue(value, out observedTransform))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractTransformFromObservedValue(object value, out Transform observedTransform)
    {
        return TryExtractTransformFromObservedValue(value, 0, out observedTransform);
    }

    private static bool TryExtractTransformFromObservedValue(object value, int depth, out Transform observedTransform)
    {
        observedTransform = null!;
        if (value == null || depth > 2)
        {
            return false;
        }

        if (value is Transform directTransform && directTransform != null)
        {
            observedTransform = directTransform;
            return true;
        }

        if (value is Component observedComponent && observedComponent != null)
        {
            observedTransform = observedComponent.transform;
            return observedTransform != null;
        }

        if (value is GameObject observedGameObject && observedGameObject != null)
        {
            observedTransform = observedGameObject.transform;
            return observedTransform != null;
        }

        if (value is Collider observedCollider && observedCollider != null)
        {
            observedTransform = observedCollider.transform;
            return true;
        }

        var valueType = value.GetType();
        var colliderProperty = valueType.GetProperty("InteractableCollider", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (colliderProperty?.GetValue(value) is Collider reflectedCollider && reflectedCollider != null)
        {
            observedTransform = reflectedCollider.transform;
            return observedTransform != null;
        }

        var transformProperty = valueType.GetProperty("transform", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (transformProperty?.GetValue(value) is Transform reflectedTransform && reflectedTransform != null)
        {
            observedTransform = reflectedTransform;
            return true;
        }

        var nestedMemberNames = new[]
        {
            "ObservedInteractableItem",
            "observedInteractable",
            "CurrentInteractable",
            "CurrentTarget",
            "HoveredInteractable",
            "FocusedInteractable",
            "InteractableItem",
            "Interactable",
            "Target",
            "Object",
            "Item",
            "gameObject",
            "GameObject",
            "Collider",
            "collider",
            "InteractableCollider",
            "transform"
        };

        for (var i = 0; i < nestedMemberNames.Length; i++)
        {
            if (!TryGetMemberValue(value, nestedMemberNames[i], out var nestedValue))
            {
                continue;
            }

            if (ReferenceEquals(nestedValue, value))
            {
                continue;
            }

            if (TryExtractTransformFromObservedValue(nestedValue, depth + 1, out observedTransform))
            {
                return true;
            }
        }

        var properties = valueType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        for (var i = 0; i < properties.Length; i++)
        {
            var property = properties[i];
            if (property == null || property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            var propertyName = property.Name ?? string.Empty;
            if (!IsObservedMemberName(propertyName) && !propertyName.Contains("transform", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            object nestedValue;
            try
            {
                nestedValue = property.GetValue(value);
            }
            catch
            {
                continue;
            }

            if (ReferenceEquals(nestedValue, value))
            {
                continue;
            }

            if (TryExtractTransformFromObservedValue(nestedValue, depth + 1, out observedTransform))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsObservedMemberName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return name.Contains("observ", StringComparison.OrdinalIgnoreCase)
            || name.Contains("interact", StringComparison.OrdinalIgnoreCase)
            || name.Contains("focus", StringComparison.OrdinalIgnoreCase)
            || name.Contains("hover", StringComparison.OrdinalIgnoreCase)
            || name.Contains("target", StringComparison.OrdinalIgnoreCase)
            || name.Contains("look", StringComparison.OrdinalIgnoreCase)
            || name.Contains("aim", StringComparison.OrdinalIgnoreCase)
            || name.Contains("cursor", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetMemberValue(object instance, string memberName, out object value)
    {
        value = null!;
        if (instance == null)
        {
            return false;
        }

        var type = instance.GetType();
        var property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null)
        {
            try
            {
                value = property.GetValue(instance)!;
                return true;
            }
            catch
            {
                return false;
            }
        }

        var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field == null)
        {
            return false;
        }

        try
        {
            value = field.GetValue(instance)!;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetInteractableCollider(Transform target, out Collider collider)
    {
        collider = null!;

        if (TryGetInteractableItem(target, out var interactableItem)
            && TryGetInteractableColliderFromComponent(interactableItem, out var itemCollider)
            && itemCollider != null)
        {
            collider = ResolveEffectiveInteractionCollider(target, itemCollider) ?? itemCollider;
            return true;
        }

        var behaviourComponents = target.GetComponents<MonoBehaviour>();
        foreach (var component in behaviourComponents)
        {
            if (component == null)
            {
                continue;
            }

            if (!TryGetInteractableColliderFromComponent(component, out var reflectedCollider) || reflectedCollider == null)
            {
                continue;
            }

            collider = ResolveEffectiveInteractionCollider(target, reflectedCollider) ?? reflectedCollider;
            return true;
        }

        var direct = target.GetComponent<Collider>();
        if (direct != null)
        {
            collider = ResolveEffectiveInteractionCollider(target, direct) ?? direct;
            return true;
        }

        var allColliders = target.GetComponentsInChildren<Collider>(true);
        var bestCollider = SelectBestColliderCandidate(target.position, allColliders);
        if (bestCollider != null)
        {
            collider = ResolveEffectiveInteractionCollider(target, bestCollider) ?? bestCollider;
            return true;
        }

        return false;
    }

    private static Collider? ResolveEffectiveInteractionCollider(Transform target, Collider primaryCollider)
    {
        if (target == null || primaryCollider == null)
        {
            return primaryCollider;
        }

        var primaryMaxExtent = Mathf.Max(primaryCollider.bounds.extents.x, Mathf.Max(primaryCollider.bounds.extents.y, primaryCollider.bounds.extents.z));
        if (primaryMaxExtent <= 0.42f && !primaryCollider.isTrigger)
        {
            return primaryCollider;
        }

        var colliders = target.GetComponentsInChildren<Collider>(true);
        if (colliders == null || colliders.Length == 0)
        {
            return primaryCollider;
        }

        Collider? best = null;
        var bestScore = float.MaxValue;
        for (var i = 0; i < colliders.Length; i++)
        {
            var candidate = colliders[i];
            if (candidate == null || candidate == primaryCollider || candidate.isTrigger)
            {
                continue;
            }

            var candidateExtents = candidate.bounds.extents;
            var candidateMaxExtent = Mathf.Max(candidateExtents.x, Mathf.Max(candidateExtents.y, candidateExtents.z));
            if (candidateMaxExtent >= primaryMaxExtent * 0.92f)
            {
                continue;
            }

            // Prefer compact colliders close to the target center; this avoids snapping to broad parent surfaces.
            var distanceToTarget = Vector3.Distance(candidate.bounds.center, target.position);
            var score = (candidateMaxExtent * 2.5f) + (distanceToTarget * 0.65f);
            if (score < bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best ?? primaryCollider;
    }

    private static Collider? SelectBestColliderCandidate(Vector3 referencePosition, Collider[] colliders)
    {
        if (colliders == null || colliders.Length == 0)
        {
            return null;
        }

        Collider? best = null;
        var bestIsTrigger = true;
        var bestVolume = float.MaxValue;
        var bestDistance = float.MaxValue;
        for (var i = 0; i < colliders.Length; i++)
        {
            var candidate = colliders[i];
            if (candidate == null)
            {
                continue;
            }

            var isTrigger = candidate.isTrigger;
            var extents = candidate.bounds.extents;
            var volume = Mathf.Max(extents.x * extents.y * extents.z * 8f, 0.000001f);
            var distance = Vector3.Distance(referencePosition, candidate.bounds.center);
            if (best == null
                || (bestIsTrigger && !isTrigger)
                || (bestIsTrigger == isTrigger && volume < bestVolume)
                || (bestIsTrigger == isTrigger && Mathf.Abs(volume - bestVolume) < 0.0001f && distance < bestDistance))
            {
                best = candidate;
                bestIsTrigger = isTrigger;
                bestVolume = volume;
                bestDistance = distance;
            }
        }

        return best;
    }

    private static bool TryGetInteractableColliderFromComponent(MonoBehaviour component, out Collider collider)
    {
        collider = null!;
        if (component == null)
        {
            return false;
        }

        var type = component.GetType();
        var property = type.GetProperty("InteractableCollider", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null)
        {
            object propertyValue;
            try
            {
                propertyValue = property.GetValue(component)!;
            }
            catch
            {
                propertyValue = null!;
            }

            if (TryExtractColliderFromUnknownValue(propertyValue, out collider))
            {
                return true;
            }
        }

        var field = type.GetField("InteractableCollider", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field == null)
        {
            return false;
        }

        object fieldValue;
        try
        {
            fieldValue = field.GetValue(component)!;
        }
        catch
        {
            return false;
        }

        return TryExtractColliderFromUnknownValue(fieldValue, out collider);
    }

    private static bool TryExtractColliderFromUnknownValue(object value, out Collider collider)
    {
        collider = null!;
        if (value == null)
        {
            return false;
        }

        if (value is Collider directCollider && directCollider != null)
        {
            collider = directCollider;
            return true;
        }

        if (value is GameObject go && go != null)
        {
            var fromGo = go.GetComponent<Collider>();
            if (fromGo != null)
            {
                collider = fromGo;
                return true;
            }
        }

        if (value is Component component && component != null)
        {
            var fromComponent = component.GetComponent<Collider>();
            if (fromComponent != null)
            {
                collider = fromComponent;
                return true;
            }

            if (component is Collider componentCollider)
            {
                collider = componentCollider;
                return true;
            }
        }

        var valueType = value.GetType();
        var nestedColliderProperty = valueType.GetProperty("Collider", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (nestedColliderProperty != null)
        {
            object nestedColliderValue;
            try
            {
                nestedColliderValue = nestedColliderProperty.GetValue(value)!;
            }
            catch
            {
                nestedColliderValue = null!;
            }

            if (nestedColliderValue is Collider nestedCollider && nestedCollider != null)
            {
                collider = nestedCollider;
                return true;
            }
        }

        return false;
    }

    private static string TryGetPrimaryActionText(Transform target)
    {
        if (target == null)
        {
            return string.Empty;
        }

        if (!TryGetInteractableItem(target, out var interactableItem))
        {
            return string.Empty;
        }

        var itemType = interactableItem.GetType();
        if (!TryResolveInteractionAccessors(itemType, out var getCountMethod, out var getInteractionMethod))
        {
            return string.Empty;
        }

        if (!TryGetInteractionCount(interactableItem, getCountMethod, out var count))
        {
            return string.Empty;
        }

        if (count <= 0)
        {
            return string.Empty;
        }

        // First pass: prefer currently available interactions.
        for (var i = 0; i < count; i++)
        {
            if (!TryReadInteractionText(getInteractionMethod, interactableItem, i, requireAvailable: true, out var actionText))
            {
                continue;
            }

            return actionText;
        }

        // Fallback: any defined interaction text.
        for (var i = 0; i < count; i++)
        {
            if (!TryReadInteractionText(getInteractionMethod, interactableItem, i, requireAvailable: false, out var actionText))
            {
                continue;
            }

            return actionText;
        }

        return string.Empty;
    }

    private static bool TryResolveInteractionAccessors(Type interactableItemType, out MethodInfo getCountMethod, out MethodInfo getInteractionMethod)
    {
        getCountMethod = null!;
        getInteractionMethod = null!;
        if (interactableItemType == null)
        {
            return false;
        }

        var methods = interactableItemType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        for (var i = 0; i < methods.Length; i++)
        {
            var method = methods[i];
            if (method == null || method.GetParameters().Length != 0)
            {
                continue;
            }

            if (!string.Equals(method.Name, "GetInteractionsCount", StringComparison.Ordinal)
                && !(method.Name.Contains("Interaction", StringComparison.OrdinalIgnoreCase)
                    && method.Name.Contains("Count", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!IsIntLikeType(method.ReturnType))
            {
                continue;
            }

            getCountMethod = method;
            break;
        }

        getInteractionMethod = ResolveGetInteractionByIndex(interactableItemType);
        return getCountMethod != null && getInteractionMethod != null;
    }

    private static bool TryGetInteractionCount(object interactableItem, MethodInfo getCountMethod, out int count)
    {
        count = 0;
        if (interactableItem == null || getCountMethod == null)
        {
            return false;
        }

        object rawCount;
        try
        {
            rawCount = getCountMethod.Invoke(interactableItem, null)!;
        }
        catch
        {
            return false;
        }

        if (!TryConvertToInt(rawCount, out count))
        {
            return false;
        }

        if (count < 0)
        {
            count = 0;
        }

        return true;
    }

    private static bool TryInvokeInteractionByIndex(MethodInfo getInteractionMethod, object interactableItem, int index, out object interaction)
    {
        interaction = null!;
        if (getInteractionMethod == null || interactableItem == null)
        {
            return false;
        }

        var parameters = getInteractionMethod.GetParameters();
        if (parameters.Length != 1)
        {
            return false;
        }

        var argument = ConvertIndexArgument(index, parameters[0].ParameterType);
        if (argument == null)
        {
            return false;
        }

        try
        {
            interaction = getInteractionMethod.Invoke(interactableItem, new[] { argument })!;
            return interaction != null;
        }
        catch
        {
            return false;
        }
    }

    private static object? ConvertIndexArgument(int index, Type parameterType)
    {
        if (parameterType == null)
        {
            return null;
        }

        if (parameterType == typeof(int) || parameterType == typeof(object))
        {
            return index;
        }

        if (parameterType == typeof(short))
        {
            return (short)index;
        }

        if (parameterType == typeof(long))
        {
            return (long)index;
        }

        if (parameterType.IsEnum)
        {
            return Enum.ToObject(parameterType, index);
        }

        var fullName = parameterType.FullName ?? string.Empty;
        if (fullName.Contains("Int32", StringComparison.OrdinalIgnoreCase)
            || fullName.Contains("Int16", StringComparison.OrdinalIgnoreCase)
            || fullName.Contains("Int64", StringComparison.OrdinalIgnoreCase))
        {
            var constructor = parameterType.GetConstructor(new[] { typeof(int) });
            if (constructor != null)
            {
                try
                {
                    return constructor.Invoke(new object[] { index });
                }
                catch
                {
                    // Continue fallback chain.
                }
            }

            var implicitMethod = parameterType.GetMethod("op_Implicit", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(int) }, null);
            if (implicitMethod != null)
            {
                try
                {
                    return implicitMethod.Invoke(null, new object[] { index });
                }
                catch
                {
                    // Continue fallback chain.
                }
            }
        }

        try
        {
            return Convert.ChangeType(index, parameterType, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryConvertToInt(object rawValue, out int value)
    {
        value = 0;
        if (rawValue == null)
        {
            return false;
        }

        switch (rawValue)
        {
            case int intValue:
                value = intValue;
                return true;
            case short shortValue:
                value = shortValue;
                return true;
            case long longValue:
                if (longValue > int.MaxValue)
                {
                    value = int.MaxValue;
                    return true;
                }

                if (longValue < int.MinValue)
                {
                    value = int.MinValue;
                    return true;
                }

                value = (int)longValue;
                return true;
        }

        var text = rawValue.ToString();
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            || int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value);
    }

    private static bool IsIntLikeType(Type type)
    {
        if (type == null)
        {
            return false;
        }

        if (type == typeof(int) || type == typeof(short) || type == typeof(long))
        {
            return true;
        }

        var fullName = type.FullName ?? string.Empty;
        return fullName.Contains("Int32", StringComparison.OrdinalIgnoreCase)
            || fullName.Contains("Int16", StringComparison.OrdinalIgnoreCase)
            || fullName.Contains("Int64", StringComparison.OrdinalIgnoreCase);
    }

    private static MethodInfo? ResolveGetInteractionByIndex(Type interactableItemType)
    {
        var methods = interactableItemType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo? fallback = null;
        for (var i = 0; i < methods.Length; i++)
        {
            var method = methods[i];
            if (method == null)
            {
                continue;
            }

            var parameters = method.GetParameters();
            if (parameters.Length != 1 || !IsIntLikeType(parameters[0].ParameterType))
            {
                continue;
            }

            if (string.Equals(method.Name, "GetInteraction", StringComparison.Ordinal))
            {
                return method;
            }

            if (method.Name.Contains("Interaction", StringComparison.OrdinalIgnoreCase))
            {
                fallback = method;
            }
        }

        return fallback;
    }

    private static bool TryReadInteractionText(
        MethodInfo getInteractionMethod,
        object interactableItem,
        int index,
        bool requireAvailable,
        out string actionText)
    {
        actionText = string.Empty;

        object interaction;
        if (!TryInvokeInteractionByIndex(getInteractionMethod, interactableItem, index, out interaction))
        {
            return false;
        }

        if (interaction == null)
        {
            return false;
        }

        if (requireAvailable)
        {
            if (!TryResolveInteractionAvailability(interaction, out var isAvailable))
            {
                return false;
            }

            if (!isAvailable)
            {
                return false;
            }
        }

        if (TryGetStringProperty(interaction, "Name", out var interactionName))
        {
            var normalizedName = NormalizeText(interactionName);
            if (IsUsableActionText(normalizedName))
            {
                actionText = normalizedName;
                return true;
            }
        }

        if (TryGetStringProperty(interaction, "Description", out var interactionDescription))
        {
            var normalizedDescription = NormalizeText(interactionDescription);
            if (IsUsableActionText(normalizedDescription))
            {
                actionText = normalizedDescription;
                return true;
            }
        }

        if (TryGetStringProperty(interaction, "_defaultStringHandle", out var defaultHandleText))
        {
            var fromHandle = ReadableObjectName(defaultHandleText).ToLowerInvariant();
            if (IsUsableActionText(fromHandle))
            {
                actionText = fromHandle;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetStringProperty(object instance, string propertyName, out string value)
    {
        value = string.Empty;
        if (instance == null)
        {
            return false;
        }

        var type = instance.GetType();
        var property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property == null)
        {
            var field = type.GetField(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                object fieldValue;
                try
                {
                    fieldValue = field.GetValue(instance)!;
                }
                catch
                {
                    return false;
                }

                if (fieldValue == null)
                {
                    return false;
                }

                value = fieldValue.ToString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(value);
            }

            var method = type.GetMethod(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null)
                ?? type.GetMethod($"get_{propertyName}", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (method == null)
            {
                return false;
            }

            object methodValue;
            try
            {
                methodValue = method.Invoke(instance, null)!;
            }
            catch
            {
                return false;
            }

            if (methodValue == null)
            {
                return false;
            }

            value = methodValue.ToString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }

        object rawValue;
        try
        {
            rawValue = property.GetValue(instance)!;
        }
        catch
        {
            return false;
        }

        if (rawValue == null)
        {
            return false;
        }

        value = rawValue.ToString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetBooleanProperty(object instance, string propertyName, out bool value)
    {
        value = false;
        if (instance == null)
        {
            return false;
        }

        var type = instance.GetType();
        var property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property == null)
        {
            var field = type.GetField(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                object fieldValue;
                try
                {
                    fieldValue = field.GetValue(instance)!;
                }
                catch
                {
                    return false;
                }

                if (fieldValue is bool fieldBool)
                {
                    value = fieldBool;
                    return true;
                }
            }

            var method = type.GetMethod(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null)
                ?? type.GetMethod($"get_{propertyName}", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (method == null)
            {
                return false;
            }

            try
            {
                var methodValue = method.Invoke(instance, null);
                if (methodValue is bool methodBool)
                {
                    value = methodBool;
                    return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        object rawValue;
        try
        {
            rawValue = property.GetValue(instance)!;
        }
        catch
        {
            return false;
        }

        if (rawValue is bool boolValue)
        {
            value = boolValue;
            return true;
        }

        return false;
    }

    private static bool TryGetInteractableItem(Transform target, out MonoBehaviour interactableItem)
    {
        interactableItem = null!;

        var directComponents = target.GetComponents<MonoBehaviour>();
        foreach (var component in directComponents)
        {
            if (component == null)
            {
                continue;
            }

            if (IsInteractableItemComponent(component))
            {
                interactableItem = component;
                return true;
            }
        }

        var parentComponents = target.GetComponentsInParent<MonoBehaviour>(true);
        foreach (var component in parentComponents)
        {
            if (component == null)
            {
                continue;
            }

            if (IsInteractableItemComponent(component))
            {
                interactableItem = component;
                return true;
            }
        }

        var childComponents = target.GetComponentsInChildren<MonoBehaviour>(true);
        foreach (var component in childComponents)
        {
            if (component == null)
            {
                continue;
            }

            if (IsInteractableItemComponent(component))
            {
                interactableItem = component;
                return true;
            }
        }

        return false;
    }

    private static bool IsInteractableItemComponent(MonoBehaviour component)
    {
        if (component == null)
        {
            return false;
        }

        var type = component.GetType();
        var fullName = type.FullName ?? string.Empty;
        var typeName = type.Name ?? string.Empty;
        return string.Equals(fullName, "Il2Cpp.InteractableItem", StringComparison.Ordinal)
            || fullName.Contains("InteractableItem", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("InteractableItem", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUsableActionText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (int.TryParse(value, out _))
        {
            return false;
        }

        return true;
    }

    private void SpeakInRangeWithAction(Transform targetTransform)
    {
        var objectName = ResolveInteractableDisplayName(targetTransform);
        var actionText = TryGetPrimaryActionText(targetTransform);
        if (string.IsNullOrWhiteSpace(actionText))
        {
            SpeakSelectionText($"{objectName} in range");
            return;
        }

        SpeakSelectionText($"{objectName} in range, {actionText}");
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


