using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using GameInteractableBase = Il2Cpp.InteractableBase;
using GameInteraction = Il2CppActions.InteractionBase;
using GameInteractor = Il2Cpp.ControllerBaseInteractor;

namespace Killer_Frequency_Access;

/// <summary>
/// Everything the mod knows about interactable objects, taken from the game's own types.
/// Facts used (see docs/game-api.md, verified against the rip of every scene):
/// - Every interactable carries Il2CppActions.Interactables.Interactable, an Il2Cpp.InteractableBase (searched as the base).
/// - Its actions are Il2CppActions.InteractionBase components on the same GameObject.
/// - Its colliders are InteractableBase.m_Colliders; its reach is InteractableBase.fsMaxReachDistance.
/// - What the player is pointing at is ControllerBaseInteractor.observedInteractable (one interactor per scene).
/// - The game shows no item names; names come from GameObjectNames.ByPath, generated from the rip.
/// </summary>
public sealed partial class KillerFrequencyAccessMod
{
    /// <summary>Spoken verb per interaction class. The class names are the complete set found in the rip.</summary>
    private static readonly Dictionary<string, string> InteractionVerbs = new(StringComparer.Ordinal)
    {
        ["PickUpInteraction"] = "pick up",
        ["FsInspectInteraction"] = "inspect",
        ["FsPlaceInteraction"] = "place",
        ["DropInteraction"] = "drop",
        ["FsThrowInteraction"] = "throw",
        ["FsSwapInHandsInteraction"] = "swap hands",
        ["DockInteraction"] = "put in",
        ["UndockInteraction"] = "take out",
        ["RedockInteraction"] = "put back",
        ["GraspInteraction"] = "grab",
        ["ReleaseInteraction"] = "let go",
        ["FsBoundedMotionInteraction"] = "drag",
        ["UseInteraction"] = "use",
        ["TouchInteraction"] = "touch",
        ["CollectInteraction"] = "collect",
        ["TakeInteraction"] = "take",
        ["FsPeekInteraction"] = "peek",
        // Not listed: DisableObjectOnDropInteraction. Its input is Place on some objects and Drop on
        // others (rip), so its verb comes from its input (InputVerbs).
    };

    /// <summary>Leaf object name to spoken name, only for leaf names that map to exactly one spoken name.</summary>
    private static readonly Dictionary<string, string> UniqueNameByLeaf = BuildUniqueLeafIndex();

    /// <summary>Spoken name per Interactable instance, fixed the first time it is seen (items move when held or docked).</summary>
    private static readonly Dictionary<int, string> NameByInteractableId = new();

    private static readonly HashSet<string> LoggedOnce = new(StringComparer.Ordinal);

    private static GameInteractor? _playerInteractor;
    /// <summary>The game Interactable that owns <paramref name="target"/> (the Interactable itself, or the nearest one above it).</summary>
    private static bool TryGetGameInteractable(Transform? target, out GameInteractableBase interactable)
    {
        interactable = null!;
        if (target == null)
        {
            return false;
        }

        var found = target.GetComponentInParent<GameInteractableBase>();
        if (found == null)
        {
            return false;
        }

        interactable = found;
        return true;
    }

    /// <summary>
    /// The player's interactor. SCN_Main, Intro, Epilogue and Menu each hold exactly one, on the PlayerController
    /// GameObject that also carries the player's CharacterController.
    /// </summary>
    private static bool TryGetPlayerInteractor(out GameInteractor interactor)
    {
        if (_playerInteractor == null || !_playerInteractor.isActiveAndEnabled)
        {
            _playerInteractor = UnityEngine.Object.FindObjectOfType<GameInteractor>();
        }

        interactor = _playerInteractor!;
        return interactor != null;
    }

    /// <summary>What the game says the player is pointing at right now.</summary>
    private static bool TryGetObservedInteractable(out GameInteractableBase observed)
    {
        observed = null!;
        if (!TryGetPlayerInteractor(out var interactor))
        {
            return false;
        }

        var value = interactor.observedInteractable;
        if (value == null)
        {
            return false;
        }

        observed = value;
        return true;
    }

    private static bool IsSameInteractable(GameInteractableBase? left, GameInteractableBase? right)
    {
        return left != null && right != null && left.GetInstanceID() == right.GetInstanceID();
    }

    /// <summary>The camera the game raycasts from when choosing what the player points at.</summary>
    private static Camera? GetInteractorCamera()
    {
        if (TryGetPlayerInteractor(out var interactor) && interactor.interactorCamera != null)
        {
            return interactor.interactorCamera;
        }

        return Camera.main;
    }

    /// <summary>The Interactable's own collider (from m_Colliders) nearest to <paramref name="from"/>.</summary>
    private static bool TryGetInteractableCollider(GameInteractableBase interactable, Vector3 from, out Collider collider)
    {
        collider = null!;
        var colliders = interactable.m_Colliders;
        if (colliders == null)
        {
            return false;
        }

        var best = float.MaxValue;
        for (var i = 0; i < colliders.Count; i++)
        {
            var candidate = colliders[i];
            if (candidate == null || !candidate.enabled || !candidate.gameObject.activeInHierarchy)
            {
                continue;
            }

            var distance = (candidate.ClosestPoint(from) - from).sqrMagnitude;
            if (distance < best)
            {
                best = distance;
                collider = candidate;
            }
        }

        return collider != null;
    }

    /// <summary>Nearest point of the Interactable's colliders to <paramref name="from"/>; its position if it has none.</summary>
    private static Vector3 GetNearestPoint(Transform target, Vector3 from)
    {
        if (TryGetGameInteractable(target, out var interactable)
            && TryGetInteractableCollider(interactable, from, out var collider))
        {
            return collider.ClosestPoint(from);
        }

        return target.position;
    }

    /// <summary>Centre of the Interactable's nearest collider, the point to look at so the game's raycast hits it.</summary>
    private static Vector3 GetAimPoint(Transform target, Vector3 from)
    {
        if (TryGetGameInteractable(target, out var interactable)
            && TryGetInteractableCollider(interactable, from, out var collider))
        {
            return collider.bounds.center;
        }

        return target.position;
    }

    /// <summary>True when the camera is within the Interactable's own fsMaxReachDistance of its nearest collider.</summary>
    private bool IsWithinReach(Transform target)
    {
        var camera = GetInteractorCamera();
        if (camera == null || !TryGetGameInteractable(target, out var interactable))
        {
            return false;
        }

        var from = camera.transform.position;
        if (!TryGetInteractableCollider(interactable, from, out var collider))
        {
            return false;
        }

        return Vector3.Distance(from, collider.ClosestPoint(from)) <= interactable.fsMaxReachDistance;
    }

    /// <summary>True when the game itself reports <paramref name="target"/> as the object the player points at.</summary>
    private bool IsObservedByGame(Transform target)
    {
        return TryGetGameInteractable(target, out var interactable)
            && TryGetObservedInteractable(out var observed)
            && IsSameInteractable(interactable, observed);
    }

    /// <summary>Verbs of the flat-screen interactions the game marks Available, highest Priority first.</summary>
    private static List<string> GetAvailableVerbs(GameInteractableBase interactable)
    {
        var result = new List<(int Priority, string Verb)>();
        foreach (var interaction in interactable.GetComponents<GameInteraction>())
        {
            if (interaction == null || !interaction.enabled || !HasFlatScreenInput(interaction))
            {
                continue;
            }

            bool isAvailable;
            int priority;
            try
            {
                isAvailable = interaction.Available;
                priority = interaction.Priority;
            }
            catch (Exception ex)
            {
                LogOnce($"available-error|{GetInteractionTypeName(interaction)}", $"[targets] Available threw on {GetInteractionTypeName(interaction)}: {ex.Message}");
                continue;
            }

            if (!isAvailable)
            {
                continue;
            }

            var verb = GetVerb(interaction);
            if (verb.Length > 0 && !result.Exists(r => r.Verb == verb))
            {
                result.Add((priority, verb));
            }
        }

        result.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        return result.ConvertAll(r => r.Verb);
    }

    private static List<string> GetAvailableVerbs(Transform target)
    {
        return TryGetGameInteractable(target, out var interactable) ? GetAvailableVerbs(interactable) : new List<string>();
    }

    /// <summary>
    /// Actions with their keys ("pick up, left click or numpad 0") that the game says the player can do
    /// right now: InteractionBase.CanPerform with the player's
    /// interactor. The first run showed Available is true for drop and throw on items not held, so
    /// Available alone does not mean "possible now".
    /// </summary>
    private static List<string> GetPerformableVerbs(GameInteractableBase interactable)
    {
        var result = new List<(int Priority, string Verb)>();
        if (!TryGetPlayerInteractor(out var interactor))
        {
            return new List<string>();
        }

        var log = new List<string>();
        foreach (var interaction in interactable.GetComponents<GameInteraction>())
        {
            if (interaction == null || !interaction.enabled)
            {
                continue;
            }

            var typeName = GetInteractionTypeName(interaction);
            if (!HasFlatScreenInput(interaction))
            {
                log.Add($"{typeName}=VR only");
                continue;
            }

            bool canPerform;
            int priority;
            try
            {
                canPerform = interaction.Available && interaction.CanPerform(interactor);
                priority = interaction.Priority;
            }
            catch (Exception ex)
            {
                LogOnce($"canperform-error|{typeName}", $"[targets] CanPerform threw on {typeName}: {ex.Message}");
                continue;
            }

            log.Add($"{typeName}={(canPerform ? "yes" : "no")}");
            if (!canPerform)
            {
                continue;
            }

            var action = DescribeActionWithKeys(interaction);
            if (action.Length > 0 && !result.Exists(r => r.Verb == action))
            {
                result.Add((priority, action));
            }
        }

        MelonLoader.MelonLogger.Msg($"[observed] {GetSpokenName(interactable)}: {string.Join(", ", log)}");
        result.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        return result.ConvertAll(r => r.Verb);
    }

    private static List<string> GetPerformableVerbs(Transform target)
    {
        return TryGetGameInteractable(target, out var interactable) ? GetPerformableVerbs(interactable) : new List<string>();
    }

    /// <summary>Whether the game's CanPerform says any flat-screen interaction on the target can run now. No logging.</summary>
    private static bool HasPerformableInteraction(Transform target)
    {
        if (!TryGetGameInteractable(target, out var interactable) || !TryGetPlayerInteractor(out var interactor))
        {
            return false;
        }

        foreach (var interaction in interactable.GetComponents<GameInteraction>())
        {
            if (interaction == null || !interaction.enabled || !HasFlatScreenInput(interaction))
            {
                continue;
            }

            try
            {
                if (interaction.Available && interaction.CanPerform(interactor))
                {
                    return true;
                }
            }
            catch
            {
                // Logged by GetPerformableVerbs when the object is announced.
            }
        }

        return false;
    }

    private static bool HasAvailableInteraction(Transform target)
    {
        return GetAvailableVerbs(target).Count > 0;
    }

    /// <summary>Every interaction on the object with its Available flag, for the log.</summary>
    private static string DescribeAllInteractions(GameInteractableBase interactable)
    {
        var parts = new List<string>();
        foreach (var interaction in interactable.GetComponents<GameInteraction>())
        {
            if (interaction == null)
            {
                continue;
            }

            string state;
            try
            {
                state = interaction.Available ? "available" : "unavailable";
            }
            catch (Exception ex)
            {
                state = $"error {ex.Message}";
            }

            parts.Add($"{GetInteractionTypeName(interaction)}={state}{(HasFlatScreenInput(interaction) ? string.Empty : " (VR only)")}");
        }

        return string.Join(", ", parts);
    }

    private static string GetVerb(GameInteraction interaction)
    {
        var typeName = GetInteractionTypeName(interaction);
        LogInteractionTextOnce(typeName, interaction);
        if (InteractionVerbs.TryGetValue(typeName, out var verb))
        {
            return verb;
        }

        foreach (var input in GetFlatScreenInputs(interaction))
        {
            if (InputVerbs.TryGetValue(input, out var inputVerb))
            {
                return inputVerb;
            }
        }

        LogOnce($"unknown-interaction|{typeName}", $"[targets] interaction class {typeName} is not in the verb table and has no known input; spoken as its class name");
        return typeName;
    }

    /// <summary>The Il2Cpp class name (for example PickUpInteraction), not the interop wrapper's.</summary>
    private static string GetInteractionTypeName(GameInteraction interaction)
    {
        try
        {
            return interaction.GetIl2CppType().Name;
        }
        catch
        {
            return interaction.GetType().Name;
        }
    }

    /// <summary>Open question: what InteractionBase.Name and Description hold. Logged once per class.</summary>
    private static void LogInteractionTextOnce(string typeName, GameInteraction interaction)
    {
        var key = $"interaction-text|{typeName}";
        if (LoggedOnce.Contains(key))
        {
            return;
        }

        string name, description;
        try
        {
            name = interaction.Name ?? "<null>";
            description = interaction.Description ?? "<null>";
        }
        catch (Exception ex)
        {
            name = description = $"<error {ex.Message}>";
        }

        LogOnce(key, $"[interactions] {typeName}: Name=\"{name}\" Description=\"{description}\"");
    }

    /// <summary>
    /// Spoken name from the rip-generated table: by full hierarchy path, else by a leaf name that maps to
    /// exactly one spoken name. A miss is logged and the object's own name is spoken.
    /// </summary>
    private static string GetSpokenName(GameInteractableBase interactable)
    {
        var id = interactable.GetInstanceID();
        if (NameByInteractableId.TryGetValue(id, out var cached))
        {
            return cached;
        }

        var transform = interactable.transform;
        var path = BuildHierarchyPath(transform);
        string spoken;
        if (GameObjectNames.ByPath.TryGetValue(path, out var byPath))
        {
            spoken = byPath;
        }
        else if (GameObjectNames.ByPath.TryGetValue(path.Replace("/AttachPoint/", "/"), out var byDockedPath))
        {
            // First run: docked items are re-parented under an AttachPoint child at runtime
            // (rip ".../ItemDock/Item", runtime ".../ItemDock/AttachPoint/Item").
            spoken = byDockedPath;
        }
        else if (UniqueNameByLeaf.TryGetValue(transform.name, out var byLeaf))
        {
            spoken = byLeaf;
        }
        else
        {
            spoken = ReadableObjectName(transform.name);
            LogOnce($"name-miss|{path}", $"[names] not in GameObjectNames: {path}");
        }

        NameByInteractableId[id] = spoken;
        return spoken;
    }

    private static string GetSpokenName(Transform target)
    {
        return TryGetGameInteractable(target, out var interactable)
            ? GetSpokenName(interactable)
            : ReadableObjectName(target.name);
    }

    private static string BuildHierarchyPath(Transform transform)
    {
        var names = new List<string>();
        for (var current = transform; current != null; current = current.parent)
        {
            names.Add(current.name);
        }

        names.Reverse();
        return string.Join("/", names);
    }

    private static Dictionary<string, string> BuildUniqueLeafIndex()
    {
        var byLeaf = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var pair in GameObjectNames.ByPath)
        {
            var slash = pair.Key.LastIndexOf('/');
            var leaf = slash >= 0 ? pair.Key[(slash + 1)..] : pair.Key;
            if (byLeaf.TryGetValue(leaf, out var existing))
            {
                if (existing != null && existing != pair.Value)
                {
                    byLeaf[leaf] = null;
                }
            }
            else
            {
                byLeaf[leaf] = pair.Value;
            }
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in byLeaf)
        {
            if (pair.Value != null)
            {
                result[pair.Key] = pair.Value;
            }
        }

        return result;
    }

    private static void LogOnce(string key, string message)
    {
        if (LoggedOnce.Add(key))
        {
            MelonLoader.MelonLogger.Msg(message);
        }
    }
}
