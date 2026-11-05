using System.Collections.Generic;
using Il2CppMenuSystem;
using Il2CppServices;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Killer_Frequency_Access;

// Menu and dialogue-choice speech, built on the game's own menu types (decompiled Il2CppMenuSystem and
// the SCN_Menu rip), not on screen-position guesses.
//
// - Open menu: BootServices.menuManager.currentTopMenu (MenuId: Main, Load, Save, settings tabs,
//   InGameMenu, DialogueBox, Credits).
// - Focus: EventSystem.currentSelectedGameObject; for conversation answers also
//   WorldUIManager._cachedDialogChoice, the answer the game currently holds.
// - Values from the widget types: Slider (+ the row's SliderPercent text), TMP_Dropdown (T17Dropdown) caption,
//   Toggle (dropdown list items), InputRebinder.buttonLabel, MenuButton label, and On/Off pairs
//   (MenuSystem.ToggleButton on a MenuButton; "on" when its Animator runs _pressedAnimator, which is what
//   SetAsTogglePressed switches to).
// - Row titles: every settings row in the rip is one of four prefab layouts. The row root is named
//   Menu_Option_*, P_MenuItem_Option_* or P_MenuItem_Settings_ControlRebinding_*; the title is its child
//   Option_Title (rebinding rows: Label), and the description is Option_Text.
// - DialogueBox (pop-up): title and body from its LocalizeStringEvent targets, spoken before its first button.
public sealed partial class KillerFrequencyAccessMod
{
    private const float MenuSpeechPollSeconds = 0.05f;
    private static readonly string[] SettingsRowPrefixes = { "Menu_Option_", "P_MenuItem_Option_", "P_MenuItem_Settings_ControlRebinding_" };

    private float _nextMenuSpeechTime;
    private int _focusedId;
    private string _focusedValue = string.Empty;
    private string _lastDialogueBoxText = string.Empty;
    private string _lastTopMenuKey = string.Empty;

    private static bool IsMenuActive()
    {
        try
        {
            var top = BootServices.menuManager?.currentTopMenu;
            return top != null && top.isVisible;
        }
        catch
        {
            return false;
        }
    }

    private void UpdateMenuSpeech()
    {
        var now = Time.unscaledTime;
        if (now < _nextMenuSpeechTime)
        {
            return;
        }

        _nextMenuSpeechTime = now + MenuSpeechPollSeconds;
        try
        {
            LogTopMenuChange();
            var focused = ResolveFocused();
            if (focused == null)
            {
                _focusedId = 0;
                _focusedValue = string.Empty;
                return;
            }

            var id = focused.GetInstanceID();
            var value = DescribeValue(focused);
            if (id != _focusedId)
            {
                _focusedId = id;
                _focusedValue = value;
                SpeakSelectionText(DescribeFocused(focused, value));
            }
            else if (value != _focusedValue)
            {
                _focusedValue = value;
                SpeakSelectionText(value);
            }
        }
        catch (System.Exception ex)
        {
            LogOnce($"menu-error|{ex.GetType().Name}|{ex.Message}", $"[menu] error: {ex}");
        }
    }

    private void LogTopMenuChange()
    {
        string key;
        try
        {
            var top = BootServices.menuManager?.currentTopMenu;
            key = top == null ? "none" : $"{top.id} visible={top.isVisible} focus={top.hasFocus}";
        }
        catch
        {
            // Log 2026-11-05: Menu.get_isVisible threw NullReferenceException while the main menu closed.
            return;
        }

        if (key != _lastTopMenuKey)
        {
            _lastTopMenuKey = key;
            LoggerInstance.Msg($"[menu] top menu {key}");
        }
    }

    private GameObject? ResolveFocused()
    {
        var selected = EventSystem.current?.currentSelectedGameObject;
        if (selected != null && selected.activeInHierarchy)
        {
            return selected;
        }

        // Conversation answers: the game keeps the answer it holds in _cachedDialogChoice.
        var ui = TryGetWorldUi();
        var cached = ui?._cachedDialogChoice;
        if (cached != null && cached.gameObject.activeInHierarchy && IsDialogueAnswer(ui!, cached.transform))
        {
            LogOnce("menu-focus-cached-choice", "[menu] conversation focus read from WorldUIManager._cachedDialogChoice (EventSystem had none)");
            return cached.gameObject;
        }

        return null;
    }

    /// <summary>The part of the focused control that can change while it stays focused.</summary>
    private static string DescribeValue(GameObject focused)
    {
        var slider = focused.GetComponent<Slider>();
        if (slider != null)
        {
            var percent = focused.GetComponentInChildren<Il2Cpp.SliderPercent>(true)
                ?? FindSettingsRow(focused.transform)?.GetComponentInChildren<Il2Cpp.SliderPercent>(true);
            var percentText = percent?._percentText != null ? NormalizeText(percent._percentText.text) : string.Empty;
            return percentText.Length > 0 ? percentText : slider.value.ToString("0.##");
        }

        var dropdown = focused.GetComponent<TMP_Dropdown>();
        if (dropdown != null)
        {
            var caption = dropdown.captionText != null ? NormalizeText(dropdown.captionText.text) : string.Empty;
            if (caption.Length == 0 && dropdown.options != null && dropdown.value >= 0 && dropdown.value < dropdown.options.Count)
            {
                caption = NormalizeText(dropdown.options[dropdown.value].text);
            }

            return caption;
        }

        var toggle = focused.GetComponent<Toggle>();
        if (toggle != null)
        {
            var label = LabelOf(focused);
            return toggle.isOn ? $"{label}, checked" : label;
        }

        var rebinder = focused.GetComponent<Il2Cpp.InputRebinder>();
        if (rebinder != null)
        {
            var binding = rebinder.buttonLabel != null ? NormalizeText(rebinder.buttonLabel.text) : string.Empty;
            return binding.Length > 0 ? binding : "unbound";
        }

        var button = focused.GetComponent<MenuButton>();
        if (button != null && focused.GetComponent<Il2CppMenuSystem.ToggleButton>() != null)
        {
            var label = LabelOf(focused);
            return IsTogglePressed(button) ? $"{label}, selected" : label;
        }

        var slot = focused.GetComponentInParent<SaveGameSlot>();
        if (slot != null)
        {
            return DescribeSaveSlot(slot);
        }

        return LabelOf(focused);
    }

    /// <summary>Full announcement when focus lands on a control.</summary>
    private string DescribeFocused(GameObject focused, string value)
    {
        var parts = new List<string>();

        var box = focused.GetComponentInParent<DialogueBox>();
        if (box != null)
        {
            var boxText = JoinNonEmpty(". ", TextOf(box._titleLocaliseStringEvent), TextOf(box._bodyLocaliseStringEvent));
            if (boxText.Length > 0 && boxText != _lastDialogueBoxText)
            {
                parts.Add(boxText);
            }

            _lastDialogueBoxText = boxText;
        }
        else
        {
            _lastDialogueBoxText = string.Empty;
        }

        var row = FindSettingsRow(focused.transform);
        var title = row != null ? RowTitle(row) : string.Empty;
        if (title.Length > 0 && title != value)
        {
            parts.Add(title);
        }

        parts.Add(value);

        if (row != null && focused.GetComponent<Il2Cpp.InputRebinder>() != null)
        {
            // Rip: each rebinding row has P_MenuItem_Button_Primary and P_MenuItem_Button_Secondary.
            if (focused.name.EndsWith("_Primary", System.StringComparison.Ordinal))
            {
                parts.Add("primary");
            }
            else if (focused.name.EndsWith("_Secondary", System.StringComparison.Ordinal))
            {
                parts.Add("secondary");
            }
        }

        if (IsMenuTab(focused))
        {
            parts.Add("tab");
        }

        var ui = TryGetWorldUi();
        if (ui != null && IsDialogueAnswer(ui, focused.transform))
        {
            var (index, count) = AnswerPosition(ui, focused.transform);
            if (count > 0)
            {
                parts.Add($"{index} of {count}");
            }
        }

        if (row != null)
        {
            var description = ChildText(row, "Option_Text");
            if (description.Length > 0)
            {
                parts.Add(description);
            }
        }

        return JoinNonEmpty(", ", parts.ToArray());
    }

    private static Transform? FindSettingsRow(Transform start)
    {
        var current = start;
        while (current != null)
        {
            foreach (var prefix in SettingsRowPrefixes)
            {
                if (current.name.StartsWith(prefix, System.StringComparison.Ordinal))
                {
                    return current;
                }
            }

            current = current.parent;
        }

        return null;
    }

    private static string RowTitle(Transform row)
    {
        var title = ChildText(row, "Option_Title");
        return title.Length > 0 ? title : ChildText(row, "Label");
    }

    /// <summary>Text of the direct child with this name (the rip's row layouts put titles there).</summary>
    private static string ChildText(Transform parent, string childName)
    {
        for (var i = 0; i < parent.childCount; i++)
        {
            var child = parent.GetChild(i);
            if (child.name == childName && child.gameObject.activeInHierarchy)
            {
                var text = child.GetComponent<TMP_Text>();
                return text != null ? NormalizeText(text.text) : string.Empty;
            }
        }

        return string.Empty;
    }

    private static string LabelOf(GameObject focused)
    {
        var text = focused.GetComponentInChildren<TMP_Text>();
        var label = text != null ? NormalizeText(text.text) : string.Empty;
        if (label.Length > 0)
        {
            return label;
        }

        LogOnce($"menu-no-label|{focused.name}", $"[menu] focused {focused.name} has no TMP_Text label; speaking its object name");
        return ReadableObjectName(focused.name);
    }

    private static string TextOf(Component? component)
    {
        if (component == null)
        {
            return string.Empty;
        }

        var text = component.GetComponent<TMP_Text>();
        return text != null && text.gameObject.activeInHierarchy ? NormalizeText(text.text) : string.Empty;
    }

    private static bool IsTogglePressed(MenuButton button)
    {
        var animator = button._animator;
        var pressed = button._pressedAnimator;
        return animator != null && pressed != null && animator.runtimeAnimatorController == pressed;
    }

    private static bool IsMenuTab(GameObject focused)
    {
        var tabs = focused.GetComponentInParent<MenuTabs>();
        var buttons = tabs?._MenuTabButtons;
        if (buttons == null)
        {
            return false;
        }

        foreach (var button in buttons)
        {
            if (button != null && button.gameObject == focused)
            {
                return true;
            }
        }

        return false;
    }

    private static string DescribeSaveSlot(SaveGameSlot slot)
    {
        return JoinNonEmpty(", ", TextOf(slot._SlotName), TextOf(slot._Title), TextOf(slot._Date), TextOf(slot._Time));
    }

    private static string JoinNonEmpty(string separator, params string[] parts)
    {
        var kept = new List<string>();
        foreach (var part in parts)
        {
            if (!string.IsNullOrWhiteSpace(part))
            {
                kept.Add(part);
            }
        }

        return string.Join(separator, kept);
    }
}
