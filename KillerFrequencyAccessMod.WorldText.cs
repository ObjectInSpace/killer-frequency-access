using System.Collections.Generic;
using Il2CppServices;
using Il2CppTMPro;
using UnityEngine;

namespace Killer_Frequency_Access;

// On-screen text from the game's own text boxes instead of sweeping every TMP_Text in the scene:
// - WorldUIManager (decompiled): _subTitleSpeakerDisplay + _subTitleDisplay (subtitles), choicePromptDisplay
//   (the question above conversation answers), _itemInfoDisplay, _settingChangedInfoDisplay (toasts).
// - DiegeticTextDisplay / ScreenDiegeticTextDisplay: text panels, spoken when _state is FullyShown.
// Each box is spoken when it becomes visible with text, or its text changes while visible.
public sealed partial class KillerFrequencyAccessMod
{
    private const float WorldTextPollSeconds = 0.1f;
    private const float DiegeticRefreshSeconds = 2.0f;

    private readonly Dictionary<string, string> _lastWorldText = new();
    private readonly List<Il2Cpp.DiegeticTextDisplay> _diegeticDisplays = new();
    private float _nextWorldTextTime;
    private float _nextDiegeticRefreshTime;
    private WorldUIManager? _worldUi;

    private WorldUIManager? TryGetWorldUi()
    {
        if (_worldUi != null)
        {
            return _worldUi;
        }

        try
        {
            _worldUi = WorldServices.uiManager;
        }
        catch
        {
            _worldUi = null;
        }

        return _worldUi;
    }

    private static bool IsDialogueAnswer(WorldUIManager ui, Transform transform)
    {
        var root = ui.answerRoot;
        return root != null && transform.IsChildOf(root) && transform != root;
    }

    private static (int Index, int Count) AnswerPosition(WorldUIManager ui, Transform transform)
    {
        var root = ui.answerRoot;
        var index = 0;
        var count = 0;
        for (var i = 0; i < root.childCount; i++)
        {
            var child = root.GetChild(i);
            if (!child.gameObject.activeInHierarchy)
            {
                continue;
            }

            count++;
            if (transform == child || transform.IsChildOf(child))
            {
                index = count;
            }
        }

        return (index, count);
    }

    private void UpdateWorldText()
    {
        var now = Time.unscaledTime;
        if (now < _nextWorldTextTime)
        {
            return;
        }

        _nextWorldTextTime = now + WorldTextPollSeconds;
        try
        {
            var ui = TryGetWorldUi();
            if (ui != null)
            {
                var speaker = VisibleText(ui._subTitleSpeakerDisplay);
                var line = VisibleText(ui._subTitleDisplay);
                SpeakWorldTextIfChanged("subtitle", line.Length > 0 && speaker.Length > 0 ? $"{speaker}: {line}" : line);
                SpeakWorldTextIfChanged("choice-prompt", VisibleText(ui.choicePromptDisplay));
                SpeakWorldTextIfChanged("item-info", VisibleText(ui._itemInfoDisplay));
                SpeakWorldTextIfChanged("setting-changed", VisibleText(ui._settingChangedInfoDisplay));
            }

            UpdateDiegeticText(now);
        }
        catch (System.Exception ex)
        {
            LogOnce($"worldtext-error|{ex.GetType().Name}|{ex.Message}", $"[worldtext] error: {ex}");
        }
    }

    private void UpdateDiegeticText(float now)
    {
        if (now >= _nextDiegeticRefreshTime)
        {
            _nextDiegeticRefreshTime = now + DiegeticRefreshSeconds;
            // Include inactive displays: the inspect view's left/right displays
            // (FsInspectDiegeticTextDetector.leftDisplay/rightDisplay) may only be active while showing, and
            // FindObjectsOfType skipped them (user report 2026-11-05: magazine inner pages never read).
            var count = _diegeticDisplays.Count;
            _diegeticDisplays.Clear();
            foreach (var display in Resources.FindObjectsOfTypeAll<Il2Cpp.DiegeticTextDisplay>())
            {
                if (display != null && display.gameObject.scene.IsValid())
                {
                    _diegeticDisplays.Add(display);
                }
            }

            if (_diegeticDisplays.Count != count)
            {
                LoggerInstance.Msg($"[worldtext] {_diegeticDisplays.Count} diegetic text displays: {string.Join(", ", _diegeticDisplays.ConvertAll(d => d.name))}");
            }
        }

        // Left inspect display before the right one: on an open spread the left page comes first. The
        // user heard the right page (STEP 4-5) before the left (STEP 1-3) when both showed together.
        var left = _inspectDetector?.leftDisplay;
        _diegeticDisplays.Sort((a, b) => (b == left).CompareTo(a == left));
        foreach (var display in _diegeticDisplays)
        {
            if (display == null)
            {
                continue;
            }

            var shown = display._state == Il2Cpp.DiegeticTextDisplay.State.FullyShown && display.gameObject.activeInHierarchy;
            var text = shown && display._textComponent != null ? NormalizeText(display._textComponent.text) : string.Empty;
            if (SpeakWorldTextIfChanged($"diegetic|{display.GetInstanceID()}", text) && text.Length > 0)
            {
                LoggerInstance.Msg($"[worldtext] read from display {display.name}");
            }
        }

        LogInspectedPageList();
    }

    private Il2Cpp.FsInspectDiegeticTextDetector? _inspectDetector;
    private int _lastInspectedPageCount = -1;
    /// <summary>Logs how many text pages the inspected item has, on change.</summary>
    private void LogInspectedPageList()
    {
        if (_inspectDetector == null)
        {
            foreach (var detector in Resources.FindObjectsOfTypeAll<Il2Cpp.FsInspectDiegeticTextDetector>())
            {
                if (detector != null && detector.gameObject.scene.IsValid())
                {
                    _inspectDetector = detector;
                    break;
                }
            }

            if (_inspectDetector == null)
            {
                return;
            }
        }

        var pages = _inspectDetector.inspectedLocalizers;
        var count = pages?.Count ?? 0;
        if (count == _lastInspectedPageCount)
        {
            return;
        }

        _lastInspectedPageCount = count;
        var names = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var page = pages![i];
            names.Add(page?.localizer != null ? $"{page.localizer.name} (right display={page.useRightDisplay})" : "(none)");
        }

        LoggerInstance.Msg($"[worldtext] inspected item has {count} text page(s): {string.Join(", ", names)}");
    }

    private static string VisibleText(TMP_Text? text)
    {
        return text != null && IsVisible(text) ? NormalizeText(text.text) : string.Empty;
    }

    /// <summary>Speaks the text if it changed since last time for this key. Returns whether it changed.</summary>
    private bool SpeakWorldTextIfChanged(string key, string text)
    {
        _lastWorldText.TryGetValue(key, out var last);
        if (text == (last ?? string.Empty))
        {
            return false;
        }

        _lastWorldText[key] = text;
        if (text.Length > 0)
        {
            SpeakSelectionText(text);
        }

        return true;
    }
}
