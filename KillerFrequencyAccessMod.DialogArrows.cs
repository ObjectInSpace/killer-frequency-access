using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Killer_Frequency_Access;

// Arrow keys move through dialogue choices while choices are on screen (user request, 2026-11-04).
//
// Rip facts: UI/Navigate_InGame (what the d-pad drives in conversations) has keyboard "Arrows" and
// "WSAD" composites with no parts, so the keyboard does nothing there. Gameplay/Move and MoveRaw bind
// both WASD and the arrows, and walking stays allowed during a choice (user observation).
//
// So while WorldUIManager.answerRoot has active answers, this gives the game's own Navigate_InGame
// action an arrow-key composite and switches the arrow parts of Move/MoveRaw off with an empty
// binding override; when the answers go away it reverses both. WASD always walks. Nothing is injected:
// the game reads its own actions as usual. Overrides are re-checked on every asset found, so an
// override that was ever saved into the game's settings is cleared outside dialogue.
public sealed partial class KillerFrequencyAccessMod
{
    private const float DialogArrowsAssetRescanSeconds = 5.0f;
    private const string NavigateInGamePath = "UI/Navigate_InGame";
    private static readonly string[] MoveActionPaths = { "Gameplay/Move", "Gameplay/MoveRaw" };
    private static readonly (string Part, string Path)[] ArrowParts =
    {
        ("up", "<Keyboard>/upArrow"),
        ("down", "<Keyboard>/downArrow"),
        ("left", "<Keyboard>/leftArrow"),
        ("right", "<Keyboard>/rightArrow"),
    };

    private Il2CppServices.WorldUIManager? _dialogWorldUi;
    private bool _dialogArrowsActive;
    private bool _dialogArrowsApplied;
    private int _lastAnswerCount = -1;
    private float _nextDialogArrowsRescan;

    private void UpdateDialogArrows()
    {
        try
        {
            var answers = CountActiveAnswers();
            if (answers != _lastAnswerCount)
            {
                LoggerInstance.Msg($"[dialogkeys] active answers {answers} (was {Math.Max(_lastAnswerCount, 0)})");
                _lastAnswerCount = answers;
            }

            var wantActive = answers > 0;
            var now = Time.unscaledTime;
            if (_dialogArrowsApplied && wantActive == _dialogArrowsActive && now < _nextDialogArrowsRescan)
            {
                return;
            }

            _nextDialogArrowsRescan = now + DialogArrowsAssetRescanSeconds;
            var changed = !_dialogArrowsApplied || wantActive != _dialogArrowsActive;
            _dialogArrowsActive = wantActive;
            _dialogArrowsApplied = true;
            var touched = ApplyDialogArrowState(wantActive);
            if (changed || touched > 0)
            {
                LoggerInstance.Msg($"[dialogkeys] arrows {(wantActive ? "drive dialogue choices" : "walk")}; {touched} binding(s) changed");
            }
        }
        catch (Exception ex)
        {
            LogOnce($"dialogkeys-error|{ex.GetType().Name}|{ex.Message}", $"[dialogkeys] error: {ex}");
        }
    }

    private int CountActiveAnswers()
    {
        if (_dialogWorldUi == null)
        {
            _dialogWorldUi = UnityEngine.Object.FindObjectOfType<Il2CppServices.WorldUIManager>();
            if (_dialogWorldUi == null)
            {
                return 0;
            }
        }

        var root = _dialogWorldUi.answerRoot;
        if (root == null || !root.gameObject.activeInHierarchy)
        {
            return 0;
        }

        var count = 0;
        for (var i = 0; i < root.childCount; i++)
        {
            if (root.GetChild(i).gameObject.activeInHierarchy)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Applies the arrow state to every loaded InputActionAsset (the game may use per-user copies). Returns bindings changed.</summary>
    private int ApplyDialogArrowState(bool dialogActive)
    {
        var touched = 0;
        foreach (var asset in Resources.FindObjectsOfTypeAll<InputActionAsset>())
        {
            var navigate = asset.FindAction(NavigateInGamePath, false);
            if (navigate != null)
            {
                touched += EnsureArrowComposite(navigate);
                // Navigate_InGame arrows on only during dialogue.
                touched += SetArrowPartsEnabled(navigate, dialogActive);
            }

            foreach (var movePath in MoveActionPaths)
            {
                var move = asset.FindAction(movePath, false);
                if (move != null)
                {
                    // Walking arrows off only during dialogue.
                    touched += SetArrowPartsEnabled(move, !dialogActive);
                }
            }
        }

        return touched;
    }

    private int EnsureArrowComposite(InputAction navigate)
    {
        var bindings = navigate.bindings;
        for (var i = 0; i < bindings.Count; i++)
        {
            if (bindings[i].isPartOfComposite && IsArrowPath(bindings[i].path))
            {
                return 0;
            }
        }

        // Adding bindings may not be allowed while the map is enabled; disable the map, add, then restore
        // each action's own enabled state (the game toggles in-game UI actions individually).
        var map = navigate.actionMap;
        var states = new List<(InputAction Action, bool Enabled)>();
        var mapWasEnabled = map != null && map.enabled;
        if (map != null)
        {
            foreach (var action in map.actions)
            {
                states.Add((action, action.enabled));
            }

            if (mapWasEnabled)
            {
                map.Disable();
            }
        }

        var composite = InputActionSetupExtensions.AddCompositeBinding(navigate, "2DVector", null, null);
        foreach (var (part, path) in ArrowParts)
        {
            composite = composite.With(part, path, null, null);
        }

        foreach (var (action, enabled) in states)
        {
            if (enabled)
            {
                action.Enable();
            }
        }

        MelonLoader.MelonLogger.Msg($"[dialogkeys] added arrow composite to {navigate.actionMap?.name}/{navigate.name} in asset {navigate.actionMap?.asset?.name}");
        return ArrowParts.Length;
    }

    /// <summary>Turns the keyboard arrow parts of an action on (no override) or off (empty override). Returns bindings changed.</summary>
    private static int SetArrowPartsEnabled(InputAction action, bool enabled)
    {
        var changed = 0;
        var bindings = action.bindings;
        for (var i = 0; i < bindings.Count; i++)
        {
            var binding = bindings[i];
            if (!binding.isPartOfComposite || !IsArrowPath(binding.path))
            {
                continue;
            }

            var overridden = binding.overridePath != null;
            if (enabled && overridden)
            {
                InputActionRebindingExtensions.RemoveBindingOverride(action, i);
                changed++;
            }
            else if (!enabled && binding.overridePath != string.Empty)
            {
                InputActionRebindingExtensions.ApplyBindingOverride(action, i, string.Empty);
                changed++;
            }
        }

        return changed;
    }

    private static bool IsArrowPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        foreach (var (_, arrowPath) in ArrowParts)
        {
            if (string.Equals(path, arrowPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
