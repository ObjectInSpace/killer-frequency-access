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

                if (_isObjectListOpen)
                {
                    CloseObjectList(string.Empty);
                }

                return;
            }

            UpdateEnvironmentScanMode();

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

