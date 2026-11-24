using System;
using UnityEngine;

namespace Killer_Frequency_Access;

/// <summary>
/// Physics queries the game's build supports. Physics.CapsuleCastAll is stripped from it: both calls
/// failed with "Method unstripping failed" in the 2026-11-23 run (the obstacle detour and the floor gap
/// check). Physics.Raycast and Physics.Linecast work (door finding, standing spots). So a body sweep is a
/// grid of single rays over the body's front: heights every BodyRayStepMeters from bottom to top, at the
/// body's centre line and both sides.
/// </summary>
public sealed partial class KillerFrequencyAccessMod
{
    private const float BodyRayStepMeters = 0.15f;
    private const int RayPassThroughLimit = 8;

    /// <summary>
    /// The nearest collider that <paramref name="counts"/> accepts, for a body of <paramref name="radius"/>
    /// spanning heights <paramref name="bottom"/>..<paramref name="top"/> above <paramref name="feet"/> moving
    /// <paramref name="distance"/> along <paramref name="direction"/>. The distance out is how far the body
    /// moves before touching it.
    /// </summary>
    private static bool TrySweepBody(Vector3 feet, Vector3 direction, float distance, float bottom, float top, float radius, int mask, Func<Collider, bool> counts, out Collider blocker, out float blockerDistance)
    {
        blocker = null!;
        blockerDistance = float.MaxValue;
        direction.y = 0f;
        if (direction.sqrMagnitude < 1e-8f)
        {
            return false;
        }

        direction.Normalize();
        var side = Vector3.Cross(Vector3.up, direction) * radius;
        var rows = Math.Max(1, Mathf.CeilToInt((top - bottom) / BodyRayStepMeters));
        for (var row = 0; row <= rows; row++)
        {
            var height = Mathf.Lerp(bottom, top, row / (float)rows);
            for (var column = -1; column <= 1; column++)
            {
                var origin = feet + Vector3.up * height + side * column;
                if (TryRaycastFirst(origin, direction, distance + radius, mask, counts, out var hit, out var hitDistance))
                {
                    var moved = Math.Max(0f, hitDistance - radius);
                    if (moved < blockerDistance)
                    {
                        blockerDistance = moved;
                        blocker = hit;
                    }
                }
            }
        }

        return blocker != null;
    }

    /// <summary>The first collider along the ray that <paramref name="counts"/> accepts, passing through the others.</summary>
    private static bool TryRaycastFirst(Vector3 origin, Vector3 direction, float distance, int mask, Func<Collider, bool> counts, out Collider collider, out float hitDistance)
    {
        collider = null!;
        hitDistance = 0f;
        var travelled = 0f;
        for (var i = 0; i < RayPassThroughLimit && distance > 0f; i++)
        {
            if (!Physics.Raycast(origin, direction, out var hit, distance, mask, QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            if (hit.collider != null && counts(hit.collider))
            {
                collider = hit.collider;
                hitDistance = travelled + hit.distance;
                return true;
            }

            var advance = hit.distance + 0.01f;
            origin += direction * advance;
            distance -= advance;
            travelled += advance;
        }

        return false;
    }
}
