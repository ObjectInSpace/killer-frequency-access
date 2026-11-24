using UnityEngine;

namespace Killer_Frequency_Access;

/// <summary>
/// Floor pieces the NavMesh bake left apart where the player can in fact walk or crouch across.
///
/// The bake (NavMesh_0: agentHeight 2, agentClimb 0.05) splits the floor at door sills and under low
/// openings. The player steps up to CharacterController.stepOffset and crouches to its crouched eye height.
/// Case: dictaphone 5 is behind the broken basement wall (SM_Basement_Exposed_Wall_Broken, its collider
/// 0.7 to 1.65 m above the floor over 1.5 m), on a 72-polygon piece 0.95 m from the main floor; the
/// 2026-11-23 run said "no path".
///
/// Each gap from NavGraph.FindGapCandidates is checked against the scene's colliders with the player's own
/// body: the CharacterController's radius moved from one side to the other (a grid of rays: see
/// PhysicsQueries), from just above
/// the step height up to the crouched head (crouched eye height plus the radius; the crouched controller
/// height is set at runtime by FlatScreenPlayer.AdjustCharacterControllerHeight and is not in the rip),
/// over floor at the midpoint. A gap that also clears the standing head (standing eye height plus 0.2 m,
/// as the standing headroom check) is walked standing; otherwise it is a crouch passage and the walk
/// crouches through it. Doors (the walk opens them), triggers, moving props (the bake left them out too)
/// and the player are not obstacles here.
/// </summary>
public sealed partial class KillerFrequencyAccessMod
{
    private const float GapMaxMeters = 1.2f;
    private const float GapSampleMeters = 0.25f;
    private const float CrouchPassageNearMeters = 1.0f;
    private const float CrouchPassageAheadMeters = 1.5f;

    private void JoinFloorGaps(float step)
    {
        var graph = GetLiveNavGraph();
        var player = GetFlatScreenPlayer();
        var controller = player?.characterController;
        if (graph == null || graph.GapsChecked || controller == null || !TryGetEyeHeights(out var standing, out var crouched))
        {
            return;
        }

        graph.GapsChecked = true;
        LogCrouchedController(controller);
        var radius = controller.radius;
        var playerRoot = player!.transform.root;
        var candidates = graph.FindGapCandidates(GapMaxMeters, step, GapSampleMeters);
        var tested = 0;
        var joined = 0;
        foreach (var gap in candidates)
        {
            if (graph.SameRegion(gap.PolyA, gap.PolyB))
            {
                continue;
            }

            tested++;
            if (!IsGapPassable(gap.A, gap.B, step, radius, crouched, standing, playerRoot, out var crouch))
            {
                continue;
            }

            var sizeA = graph.RegionSize(gap.PolyA);
            var sizeB = graph.RegionSize(gap.PolyB);
            var result = graph.Join(gap.PolyA, gap.PolyB, (gap.A + gap.B) * 0.5f, crouch);
            joined++;
            MelonLoader.MelonLogger.Msg($"[nav] gap {gap.Gap:0.00} m between floor pieces of {sizeA} and {sizeB} polygons{(crouch ? ", crouching" : string.Empty)}: {result}");
        }

        MelonLoader.MelonLogger.Msg($"[nav] floor gaps on {graph.Name}: {candidates.Count} candidates, {tested} checked, {joined} joined ({graph.CrouchPassages.Count} crouch passages)");
    }

    private bool IsGapPassable(Vector3 a, Vector3 b, float step, float radius, float crouchedEye, float standingEye, Transform playerRoot, out bool crouch)
    {
        crouch = false;
        var across = b - a;
        across.y = 0f;
        var distance = across.magnitude;
        if (distance < 1e-3f)
        {
            return IsFloorUnder((a + b) * 0.5f, step, playerRoot);
        }

        var direction = across / distance;
        var low = step + radius;
        if (IsSweepBlocked(a, direction, distance, low, crouchedEye, radius, playerRoot) || !IsFloorUnder((a + b) * 0.5f, step, playerRoot))
        {
            return false;
        }

        crouch = IsSweepBlocked(a, direction, distance, crouchedEye, standingEye + 0.2f, radius, playerRoot);
        return true;
    }

    /// <summary>A body of <paramref name="radius"/> spanning heights bottom..top above <paramref name="from"/>, moved across.</summary>
    private bool IsSweepBlocked(Vector3 from, Vector3 direction, float distance, float bottom, float top, float radius, Transform playerRoot)
    {
        return TrySweepBody(from, direction, distance, bottom, top, radius, Physics.DefaultRaycastLayers,
            collider => IsSolidForWalking(collider, playerRoot), out _, out _);
    }

    private bool IsFloorUnder(Vector3 point, float step, Transform playerRoot)
    {
        var origin = point + Vector3.up * (step + 0.2f);
        return TryRaycastFirst(origin, Vector3.down, 2f * step + 0.4f, Physics.DefaultRaycastLayers,
                   collider => IsSolidForWalking(collider, playerRoot), out _, out var distance)
               && Mathf.Abs(origin.y - distance - point.y) <= step;
    }

    private bool IsSolidForWalking(Collider? collider, Transform playerRoot)
    {
        if (collider == null || collider.isTrigger || _doorByColliderId.ContainsKey(collider.GetInstanceID()) || collider.transform.root == playerRoot)
        {
            return false;
        }

        var body = collider.attachedRigidbody;
        return body == null || body.isKinematic;
    }

    /// <summary>
    /// Whether the walk should be crouched here: within CrouchPassageNearMeters of a crouch passage, or one
    /// lies on the route within CrouchPassageAheadMeters.
    /// </summary>
    private bool IsCrouchPassageNear(Vector3 feet)
    {
        var graph = GetLiveNavGraph();
        if (graph == null || graph.CrouchPassages.Count == 0)
        {
            return false;
        }

        foreach (var passage in graph.CrouchPassages)
        {
            if (PlanarDistance(feet, passage) <= CrouchPassageNearMeters && Mathf.Abs(feet.y - passage.y) < 1f)
            {
                return true;
            }
        }

        var along = 0f;
        var from = feet;
        for (var i = _walkCornerIndex; i < _walkCorners.Count && along <= CrouchPassageAheadMeters; i++)
        {
            along += Vector3.Distance(from, _walkCorners[i]);
            from = _walkCorners[i];
            foreach (var passage in graph.CrouchPassages)
            {
                if ((from - passage).sqrMagnitude < 0.0025f && along <= CrouchPassageAheadMeters)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private float _loggedCrouchedHeight = -1f;

    /// <summary>Logs the CharacterController height whenever the game has the player crouched, to check the passage height used.</summary>
    private void LogCrouchedController(CharacterController controller)
    {
        if (IsCrouching() && Mathf.Abs(controller.height - _loggedCrouchedHeight) > 0.01f)
        {
            _loggedCrouchedHeight = controller.height;
            MelonLoader.MelonLogger.Msg($"[walk] crouched CharacterController height {controller.height:0.00}, radius {controller.radius:0.00}, centre {controller.center}");
        }
    }
}
