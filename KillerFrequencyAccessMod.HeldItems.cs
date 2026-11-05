using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.InputSystem;
using GameInputType = Il2Cpp.InputActionType;
using GameInteractableBase = Il2Cpp.InteractableBase;
using GameInteraction = Il2CppActions.InteractionBase;

namespace Killer_Frequency_Access;

/// <summary>
/// What the player holds, where it fits, which key does each action, and what the game is performing.
///
/// Facts used:
/// - Held items: ControllerBaseInteractor.GetPrimaryHeldInteractable / GetSecondaryHeldInteractable.
/// - Where an item fits: PhysicsHoldableDock.CanDock(PhysicsHoldable), the game's own check, which runs
///   the dock's registered callbacks (for example VinylPlayer.OnCanDockVinylCallback).
/// - In flat-screen mode an item goes into a dock with the Place action on the held item.
/// - Keys: each interaction's controllerInputs are ControllerActionInputDescription assets naming an
///   InputActionType (Gameplay_PlaceHeldItem = map "Gameplay", action "PlaceHeldItem"). The bindings are
///   read live from the game's "InputActions" asset, so rebinding is respected. An interaction with no
///   controllerInputs has no flat-screen input (VR only: every Grasp/Release, 93 of 107 Dock in SCN_Main).
/// </summary>
public sealed partial class KillerFrequencyAccessMod
{
    /// <summary>Verb per game input, for interactions not in <see cref="InteractionVerbs"/>.</summary>
    private static readonly Dictionary<string, string> InputVerbs = new(StringComparer.Ordinal)
    {
        ["Gameplay_MainInteract"] = "use",
        ["Gameplay_PlaceHeldItem"] = "place",
        ["Gameplay_DropHeldItem"] = "drop",
        ["Gameplay_ThrowHeldItem"] = "throw",
        ["Gameplay_TriggerInspectHeldItem"] = "inspect",
        ["Gameplay_SwapItemsInHands"] = "swap hands",
    };

    private static InputActionAsset? _gameInputActions;
    private static readonly Dictionary<string, string> KeyTextByInput = new(StringComparer.Ordinal);

    private int _lastPrimaryHeldId = -1;
    private int _lastSecondaryHeldId = -1;
    private float _nextHeldCheckTime;
    private string _lastPerformingSignature = string.Empty;

    /// <summary>The game inputs (InputActionType names) that trigger an interaction in flat-screen mode.</summary>
    private static List<string> GetFlatScreenInputs(GameInteraction interaction)
    {
        var result = new List<string>();
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2Cpp.ActionInputDescriptionBase>? inputs;
        try
        {
            inputs = interaction.controllerInputs;
        }
        catch
        {
            return result;
        }

        if (inputs == null)
        {
            return result;
        }

        for (var i = 0; i < inputs.Length; i++)
        {
            var description = inputs[i]?.TryCast<Il2CppActions.ControllerActionInputDescription>();
            if (description == null)
            {
                continue;
            }

            var name = description.inputActionType.ToString();
            if (!result.Contains(name))
            {
                result.Add(name);
            }
        }

        return result;
    }

    private static bool HasFlatScreenInput(GameInteraction interaction)
    {
        return GetFlatScreenInputs(interaction).Count > 0;
    }

    /// <summary>
    /// The keys or buttons for a game input on the device the game last saw input from
    /// (GamePadIconService.GetLastInputDeviceNameForPrimaryUser, the same source the game's own button
    /// glyphs use). Keyboard or mouse: "left click or numpad 0". A gamepad: each binding resolved on that
    /// device, spoken with the device's own control name (Unity's layout displayName, for example "A" on
    /// an Xbox pad, "Cross" on a PlayStation pad).
    /// </summary>
    private static string DescribeKeys(string inputName)
    {
        var device = GetActiveInputDevice();
        var usePointer = device == null || device is Keyboard || device is Mouse;
        var cacheKey = $"{inputName}|{(usePointer ? "keyboard and mouse" : device!.name)}";
        if (KeyTextByInput.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var text = string.Empty;
        var asset = GetGameInputActions();
        var separator = inputName.IndexOf('_');
        if (asset != null && separator > 0)
        {
            var action = asset.FindAction($"{inputName[..separator]}/{inputName[(separator + 1)..]}", false);
            if (action != null)
            {
                var keys = new List<string>();
                var bindings = action.bindings;
                for (var i = 0; i < bindings.Count; i++)
                {
                    var path = bindings[i].effectivePath ?? string.Empty;
                    var isPointerPath = path.StartsWith("<Keyboard>/", StringComparison.Ordinal) || path.StartsWith("<Mouse>/", StringComparison.Ordinal);
                    var key = usePointer
                        ? (isPointerPath ? DescribeControlPath(path) : string.Empty)
                        : (isPointerPath ? string.Empty : DescribeDeviceControl(device!, path));
                    if (key.Length > 0 && !keys.Contains(key))
                    {
                        keys.Add(key);
                    }
                }

                text = string.Join(" or ", keys);
            }
        }

        if (text.Length == 0)
        {
            LogOnce($"keys-missing|{cacheKey}", $"[keys] no binding found for {inputName} on {(usePointer ? "keyboard and mouse" : device!.name)}");
        }
        else
        {
            LogOnce($"keys|{cacheKey}", $"[keys] {inputName} on {(usePointer ? "keyboard and mouse" : device!.name)}: {text}");
        }

        KeyTextByInput[cacheKey] = text;
        return text;
    }

    /// <summary>The InputSystem device whose name the game reports as the last one used.</summary>
    private static InputDevice? GetActiveInputDevice()
    {
        string name;
        try
        {
            name = Il2Cpp.GamePadIconService.GetLastInputDeviceNameForPrimaryUser() ?? string.Empty;
        }
        catch (Exception ex)
        {
            LogOnce("active-device-error", $"[keys] GetLastInputDeviceNameForPrimaryUser failed: {ex.Message}");
            return null;
        }

        if (name.Length == 0)
        {
            return null;
        }

        var devices = InputSystem.devices;
        for (var i = 0; i < devices.Count; i++)
        {
            var device = devices[i];
            if (device != null && device.name == name)
            {
                LogOnce($"active-device|{name}", $"[keys] active device: {name} ({device.layout})");
                return device;
            }
        }

        LogOnce($"active-device-missing|{name}", $"[keys] the game reports device {name}, which InputSystem does not list");
        return null;
    }

    /// <summary>A binding path ("&lt;Gamepad&gt;/buttonSouth") resolved on the device, spoken with the device's control name.</summary>
    private static string DescribeDeviceControl(InputDevice device, string path)
    {
        var slash = path.IndexOf('/');
        if (slash < 0 || path.Contains('{'))
        {
            return string.Empty;
        }

        var control = device.TryGetChildControl(path[(slash + 1)..]);
        if (control == null)
        {
            return string.Empty;
        }

        var name = control.displayName;
        return string.IsNullOrWhiteSpace(name) ? SplitWords(path[(slash + 1)..].Replace('/', ' ')) : name;
    }

    private static InputActionAsset? GetGameInputActions()
    {
        if (_gameInputActions != null)
        {
            return _gameInputActions;
        }

        foreach (var asset in Resources.FindObjectsOfTypeAll<InputActionAsset>())
        {
            if (asset != null && asset.name == "InputActions")
            {
                _gameInputActions = asset;
                break;
            }
        }

        if (_gameInputActions == null)
        {
            LogOnce("keys-no-asset", "[keys] the game's InputActions asset was not found");
        }

        return _gameInputActions;
    }

    /// <summary>"&lt;Mouse&gt;/leftButton" = "left click", "&lt;Keyboard&gt;/numpad2" = "numpad 2", "&lt;Keyboard&gt;/e" = "E".</summary>
    private static string DescribeControlPath(string path)
    {
        var device = path.StartsWith("<Mouse>/", StringComparison.Ordinal) ? "mouse" : "keyboard";
        var control = path[(path.IndexOf('/') + 1)..];
        if (device == "mouse")
        {
            return control switch
            {
                "leftButton" => "left click",
                "rightButton" => "right click",
                "middleButton" => "middle click",
                _ when control.StartsWith("scroll", StringComparison.Ordinal) => "scroll wheel",
                _ => $"mouse {SplitWords(control)}",
            };
        }

        if (control.StartsWith("numpad", StringComparison.Ordinal))
        {
            return $"numpad {SplitWords(control[6..])}";
        }

        return control.Length == 1 ? control.ToUpperInvariant() : SplitWords(control);
    }

    private static string SplitWords(string value)
    {
        return Regex.Replace(value, "([a-z])([A-Z0-9])", "$1 $2").ToLowerInvariant();
    }

    /// <summary>"pick up, left click or numpad 0" for one interaction.</summary>
    private static string DescribeActionWithKeys(GameInteraction interaction)
    {
        var verb = GetVerb(interaction);
        var keys = new List<string>();
        foreach (var input in GetFlatScreenInputs(interaction))
        {
            var key = DescribeKeys(input);
            if (key.Length > 0)
            {
                keys.Add(key);
            }
        }

        return keys.Count == 0 ? verb : $"{verb}, {string.Join(", ", keys)}";
    }

    private static bool TryGetHeld(out GameInteractableBase? primary, out GameInteractableBase? secondary)
    {
        primary = secondary = null;
        if (!TryGetPlayerInteractor(out var interactor))
        {
            return false;
        }

        try
        {
            primary = interactor.GetPrimaryHeldInteractable();
            secondary = interactor.GetSecondaryHeldInteractable();
        }
        catch (Exception ex)
        {
            LogOnce("held-error", $"[held] reading held items failed: {ex.Message}");
            return false;
        }

        return true;
    }

    /// <summary>Docks whose game check (PhysicsHoldableDock.CanDock) accepts the item the player holds.</summary>
    private static List<Transform> FindPlacesForHeldItem()
    {
        var result = new List<Transform>();
        if (!TryGetHeld(out var primary, out _) || primary == null)
        {
            return result;
        }

        var holdable = primary.GetComponent<Il2CppInteractables.PhysicsHoldable>();
        if (holdable == null)
        {
            return result;
        }

        var tested = 0;
        foreach (var dock in UnityEngine.Object.FindObjectsOfType<Il2CppInteractables.PhysicsHoldableDock>())
        {
            if (dock == null || !dock.isActiveAndEnabled)
            {
                continue;
            }

            tested++;
            bool fits;
            try
            {
                fits = dock.CanDock(holdable);
            }
            catch (Exception ex)
            {
                LogOnce($"candock-error|{dock.name}", $"[held] CanDock threw on {dock.name}: {ex.Message}");
                continue;
            }

            if (!fits)
            {
                continue;
            }

            var interactable = dock.GetComponentInParent<GameInteractableBase>();
            if (interactable != null && !result.Exists(t => t.GetInstanceID() == interactable.transform.GetInstanceID()))
            {
                result.Add(interactable.transform);
            }
        }

        MelonLoader.MelonLogger.Msg($"[held] {GetSpokenName(primary)} fits {result.Count} of {tested} docks: {string.Join(", ", result.ConvertAll(t => GetSpokenName(t)))}");
        return result;
    }

    /// <summary>Speaks what the player holds when it changes, and where the held item fits.</summary>
    private void UpdateHeldItemAnnouncement()
    {
        var now = Time.unscaledTime;
        if (now < _nextHeldCheckTime || IsMenuActive())
        {
            return;
        }

        _nextHeldCheckTime = now + 0.2f;
        if (!TryGetHeld(out var primary, out var secondary))
        {
            return;
        }

        var primaryId = primary == null ? -1 : primary.GetInstanceID();
        var secondaryId = secondary == null ? -1 : secondary.GetInstanceID();
        if (primaryId == _lastPrimaryHeldId && secondaryId == _lastSecondaryHeldId)
        {
            return;
        }

        var hadSomething = _lastPrimaryHeldId != -1 || _lastSecondaryHeldId != -1;
        _lastPrimaryHeldId = primaryId;
        _lastSecondaryHeldId = secondaryId;

        if (primary == null && secondary == null)
        {
            MelonLoader.MelonLogger.Msg("[held] hands empty");
            _placesForHeldIds = new HashSet<int>();
            if (hadSomething)
            {
                SpeakSelectionText("Hands empty");
            }

            return;
        }

        var text = primary != null ? $"Holding {GetSpokenName(primary)}" : string.Empty;
        if (secondary != null)
        {
            text += text.Length == 0 ? $"Holding {GetSpokenName(secondary)}" : $", and {GetSpokenName(secondary)} in the other hand";
        }

        MelonLoader.MelonLogger.Msg($"[held] {text}");
        var places = FindPlacesForHeldItem();
        _placesForHeldIds = new HashSet<int>(places.ConvertAll(t => t.GetInstanceID()));
        if (places.Count > 0)
        {
            var names = places.ConvertAll(t => GetSpokenName(t));
            text += places.Count <= 3
                ? $". Fits {string.Join(", ", names)}"
                : $". Fits {places.Count} places";
            text += $". To put it in, aim at it and {DescribePlaceKeys()}";
        }

        SpeakSelectionText(text);
    }

    private static string DescribePlaceKeys()
    {
        var keys = DescribeKeys("Gameplay_PlaceHeldItem");
        return keys.Length == 0 ? "place" : $"place with {keys}";
    }

    /// <summary>Logs which interactions the game is performing, so a test run shows what actually happened.</summary>
    private void LogPerformingInteractions()
    {
        if (!TryGetPlayerInteractor(out var interactor))
        {
            return;
        }

        var performing = interactor.PerformingInteractions;
        var parts = new List<string>();
        if (performing != null)
        {
            for (var i = 0; i < performing.Count; i++)
            {
                var interaction = performing[i];
                if (interaction == null)
                {
                    continue;
                }

                var target = interaction.Interactable;
                parts.Add($"{GetInteractionTypeName(interaction)} on {(target == null ? "?" : GetSpokenName(target))}");
            }
        }

        var signature = string.Join("; ", parts);
        if (signature != _lastPerformingSignature)
        {
            _lastPerformingSignature = signature;
            MelonLoader.MelonLogger.Msg(parts.Count == 0 ? "[performing] nothing" : $"[performing] {signature}");
        }
    }
}
