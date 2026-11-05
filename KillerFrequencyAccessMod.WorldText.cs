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
            _diegeticDisplays.Clear();
            foreach (var display in UnityEngine.Object.FindObjectsOfType<Il2Cpp.DiegeticTextDisplay>())
            {
                _diegeticDisplays.Add(display);
            }
        }

        foreach (var display in _diegeticDisplays)
        {
            if (display == null)
            {
                continue;
            }

            var shown = display._state == Il2Cpp.DiegeticTextDisplay.State.FullyShown;
            var text = shown && display._textComponent != null ? NormalizeText(display._textComponent.text) : string.Empty;
            SpeakWorldTextIfChanged($"diegetic|{display.GetInstanceID()}", text);
        }
    }

    private static string VisibleText(TMP_Text? text)
    {
        return text != null && IsVisible(text) ? NormalizeText(text.text) : string.Empty;
    }

    private void SpeakWorldTextIfChanged(string key, string text)
    {
        _lastWorldText.TryGetValue(key, out var last);
        if (text == (last ?? string.Empty))
        {
            return;
        }

        _lastWorldText[key] = text;
        if (text.Length > 0)
        {
            SpeakSelectionText(text);
        }
    }
}
