using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Killer_Frequency_Access;

// Read-only runtime facts the rip cannot answer:
// - whether the game's Debug action map (numpad events, F5/F9, 1-3 "Test", right Ctrl skip, F4 free cam)
//   is ever enabled in the release build, and which maps/actions are enabled at each moment;
// - what UI/Navigate_InGame is actually bound to at runtime (the asset has empty keyboard composites);
// - whether the player can walk while a dialogue choice is up.
// Logs only on change. Never enables, disables, rebinds or injects anything.
public sealed partial class KillerFrequencyAccessMod
{
    private const float InputDiagPollSeconds = 0.5f;
    private const float ChoiceMoveLogMeters = 0.3f;

    private float _nextInputDiagTime;
    private string _lastEnabledActionsKey = string.Empty;
    private bool _inputDiagStaticLogged;
    private Il2CppServices.DialogueManager? _diagDialogueManager;
    private bool _lastDialogueActive;
    private string _lastChoiceKey = string.Empty;
    private Vector3? _choiceStartFeet;
    private bool _choiceMoveLogged;

    private void UpdateInputDiagnostics()
    {
        var now = Time.unscaledTime;
        if (now < _nextInputDiagTime)
        {
            return;
        }

        _nextInputDiagTime = now + InputDiagPollSeconds;

        try
        {
            LogInputDiagStatic();
            LogEnabledActionsIfChanged();
            LogDialogueChoiceState();
        }
        catch (System.Exception ex)
        {
            LogOnce($"inputdiag-error|{ex.GetType().Name}", $"[inputdiag] error: {ex.Message}");
        }
    }

    private void LogInputDiagStatic()
    {
        if (_inputDiagStaticLogged)
        {
            return;
        }

        _inputDiagStaticLogged = true;
        LoggerInstance.Msg($"[inputdiag] Debug.isDebugBuild={UnityEngine.Debug.isDebugBuild}");
    }

    private void LogEnabledActionsIfChanged()
    {
        var enabled = InputSystem.ListEnabledActions();
        var byMap = new SortedDictionary<string, List<InputAction>>();
        foreach (var action in enabled)
        {
            var mapName = action.actionMap?.name ?? "(no map)";
            if (!byMap.TryGetValue(mapName, out var list))
            {
                list = new List<InputAction>();
                byMap[mapName] = list;
            }

            list.Add(action);
        }

        var key = string.Join("|", byMap.Select(kv => $"{kv.Key}:{string.Join(",", kv.Value.Select(a => a.name).OrderBy(n => n))}"));
        if (key == _lastEnabledActionsKey)
        {
            return;
        }

        _lastEnabledActionsKey = key;
        var sb = new StringBuilder("[inputdiag] enabled actions changed:");
        foreach (var (mapName, actions) in byMap)
        {
            sb.Append($"\n  {mapName} ({actions.Count}): {string.Join(", ", actions.Select(a => a.name).OrderBy(n => n))}");
        }

        // Full bindings for the actions this question is about, as the game has them right now.
        foreach (var action in enabled)
        {
            var mapName = action.actionMap?.name ?? string.Empty;
            if (mapName == "Debug" || action.name == "Navigate_InGame" || action.name == "Navigate" || action.name == "Submit" || action.name == "Move")
            {
                sb.Append($"\n  bindings {mapName}/{action.name}:");
                foreach (var binding in action.bindings)
                {
                    var kind = binding.isComposite ? "composite " : binding.isPartOfComposite ? "  part " : string.Empty;
                    var path = string.IsNullOrEmpty(binding.effectivePath) ? "(empty)" : binding.effectivePath;
                    sb.Append($"\n    {kind}{binding.name} {path}");
                }
            }
        }

        LoggerInstance.Msg(sb.ToString());
    }

    private void LogDialogueChoiceState()
    {
        if (_diagDialogueManager == null)
        {
            _diagDialogueManager = UnityEngine.Object.FindObjectOfType<Il2CppServices.DialogueManager>();
            if (_diagDialogueManager == null)
            {
                return;
            }
        }

        var active = _diagDialogueManager._dialogueActive;
        var choiceKey = _diagDialogueManager._choiceKey ?? string.Empty;
        if (active != _lastDialogueActive || choiceKey != _lastChoiceKey)
        {
            LoggerInstance.Msg($"[inputdiag] dialogue active={active} choiceKey=\"{choiceKey}\" (was active={_lastDialogueActive} choiceKey=\"{_lastChoiceKey}\")");
            _lastDialogueActive = active;
            _lastChoiceKey = choiceKey;
            _choiceStartFeet = choiceKey.Length > 0 ? GetDiagFeet() : null;
            _choiceMoveLogged = false;
        }

        if (_choiceStartFeet.HasValue && !_choiceMoveLogged)
        {
            var feet = GetDiagFeet();
            if (feet.HasValue)
            {
                var moved = Vector3.Distance(feet.Value, _choiceStartFeet.Value);
                if (moved >= ChoiceMoveLogMeters)
                {
                    _choiceMoveLogged = true;
                    LoggerInstance.Msg($"[inputdiag] player moved {moved:0.0} m while choice \"{choiceKey}\" is up: walking is allowed during this choice");
                }
            }
        }
    }

    private static Vector3? GetDiagFeet()
    {
        var player = Il2Cpp.FlatScreenPlayer.instance;
        return player != null ? player.GetFootLocation() : null;
    }
}
