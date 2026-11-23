using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Killer_Frequency_Access;

public sealed partial class KillerFrequencyAccessMod
{
    private void HandleObjectNavigationHotkeys()
    {
        try
        {
            if (IsMenuActive())
            {
                if (_isAutoWalking)
                {
                    StopAutoWalk(false);
                }

                return;
            }

            if (HandleObjectListKeys())
            {
                return;
            }

            if (IsAutoWalkPressed())
            {
                StartAutoWalkToSelectedTarget();
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
        if (observedId != _lastObservedInteractableObjectId)
        {
            SetListContextLookedAt(observed.transform);
        }

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

    private static bool IsAutoWalkPressed()
    {
        return IsKeyDownSafe(Key.Backspace);
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

}

