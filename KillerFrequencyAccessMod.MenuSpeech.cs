using System.Collections.Generic;
using System.Text.RegularExpressions;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Killer_Frequency_Access;

public sealed partial class KillerFrequencyAccessMod
{
    private readonly List<SelectionTextCandidate> _cachedSelectionTextCandidates = new();
    private readonly List<Selectable> _cachedVisibleSelectables = new();
    private float _nextSelectionTextCandidatesRefreshTime = -100f;
    private float _nextVisibleSelectablesRefreshTime = -100f;
    private float _nextSelectionDetailRefreshTime = -100f;

    private void ScanCurrentSelection()
    {
        var verticalNavPressed = _verticalNavPending;
        _verticalNavPending = false;
        var submitPressed = _submitPending;
        _submitPending = false;

        var eventSystem = EventSystem.current;
        if (eventSystem == null)
        {
            ResetSelectionTracking();
            return;
        }

        var selectables = GetCachedVisibleSelectables();
        var selected = ResolveFocusAnchor(eventSystem, selectables);
        if (selected == null)
        {
            ResetSelectionTracking();
            return;
        }

        var focusedControlId = selected.GetInstanceID();
        var focusedControlChanged = !_hasLastSelectionContext || focusedControlId != _lastFocusedControlId;
        var now = Time.unscaledTime;
        if (!focusedControlChanged
            && !verticalNavPressed
            && !submitPressed
            && now < _nextSelectionDetailRefreshTime)
        {
            return;
        }

        _nextSelectionDetailRefreshTime = now + ResolveSelectionDetailRefreshIntervalSeconds(GetCurrentPerfMode());
        var focusedPosition = GetUiPosition(selected.transform);
        var candidates = GetCachedGlobalVisibleTextCandidates();
        var focusedText = FindStateTextInTransform(selected.transform, candidates);
        if (string.IsNullOrWhiteSpace(focusedText))
        {
            focusedText = FindPrimarySelectionText(selected.transform, focusedPosition, candidates);
        }
        if (string.IsNullOrWhiteSpace(focusedText))
        {
            focusedText = ReadableObjectName(selected.name);
        }
        if (string.IsNullOrWhiteSpace(focusedText))
        {
            ResetSelectionTracking();
            return;
        }

        var rowAnchorPosition = ResolveFocusedTextAnchorPosition(
            selected.transform,
            focusedPosition,
            candidates,
            focusedText);

        var rowTitle = string.Empty;
        var rowTitleFromFallbackLookup = false;
        if (verticalNavPressed)
        {
            var normalizedFocusedForRowLookup = NormalizeText(focusedText).ToLowerInvariant();
            var focusedLooksLikeBinding = IsLikelyBindingValueText(
                normalizedFocusedForRowLookup,
                NormalizeObjectName(selected.name));
            if (focusedLooksLikeBinding)
            {
                rowTitle = FindBindingRowTitleLeft(rowAnchorPosition, candidates, focusedText);
                rowTitleFromFallbackLookup = !string.IsNullOrWhiteSpace(rowTitle);
            }
            if (string.IsNullOrWhiteSpace(rowTitle))
            {
                rowTitle = FindSameRowLeftTitleFromHierarchy(
                    selected.transform,
                    rowAnchorPosition,
                    candidates,
                    focusedText);
            }
            if (string.IsNullOrWhiteSpace(rowTitle))
            {
                rowTitle = FindSameRowLeftTitle(rowAnchorPosition, candidates, focusedText);
            }
            if (string.IsNullOrWhiteSpace(rowTitle))
            {
                rowTitle = FindRowTitleAboveFocused(
                    selected.transform,
                    rowAnchorPosition,
                    candidates,
                    focusedText);
            }
            if (string.IsNullOrWhiteSpace(rowTitle))
            {
                rowTitle = FindFallbackLeftRowTitle(rowAnchorPosition, candidates, focusedText);
                rowTitleFromFallbackLookup = !string.IsNullOrWhiteSpace(rowTitle);
            }
            if (string.IsNullOrWhiteSpace(rowTitle))
            {
                rowTitle = FindBroadVerticalRowTitle(rowAnchorPosition, candidates, focusedText);
                rowTitleFromFallbackLookup = !string.IsNullOrWhiteSpace(rowTitle);
            }
        }
        if (!IsUsableChoiceRowTitle(rowTitle, focusedText))
        {
            rowTitle = string.Empty;
        }

        var focusedTextChanged = !_hasLastSelectionContext
            || !string.Equals(focusedText, _lastFocusedValue, StringComparison.Ordinal);
        var announceCheckedOnSubmit = submitPressed
            && ShouldAnnounceCheckedOnSubmit(selected, rowAnchorPosition, focusedText, selectables);

        // Navigation input can be observed before the UI selection actually changes.
        // Skip announcing in that stale frame so we don't speak previous + new items back-to-back.
        if (verticalNavPressed
            && !submitPressed
            && _hasLastSelectionContext
            && !focusedControlChanged)
        {
            if (_verticalNavRetryScans > 0)
            {
                _verticalNavPending = true;
                _verticalNavRetryScans--;
            }

            return;
        }
        _verticalNavRetryScans = 0;

        var includeRowTitlePrefix = verticalNavPressed
            && !string.IsNullOrWhiteSpace(rowTitle)
            && (!rowTitleFromFallbackLookup
                || HasPeerSelectableOnSameRow(rowAnchorPosition, focusedControlId, selectables)
                || IsSettingLikeFocus(selected, focusedText))
            && !IsTabOrHeaderFocus(selected, rowAnchorPosition, selectables)
            && !IsDropdownOptionPopupFocus(selected);

        // Ignore transient focus churn on navigation when spoken text did not change.
        // This avoids "old item, then new item" on top-level menu traversal.
        if (verticalNavPressed
            && !submitPressed
            && _hasLastSelectionContext
            && !focusedTextChanged
            && !includeRowTitlePrefix)
        {
            return;
        }

        var shouldSpeak = focusedControlChanged || focusedTextChanged || announceCheckedOnSubmit;

        if (shouldSpeak)
        {
            if (announceCheckedOnSubmit)
            {
                SpeakSelectionTextDedup(
                    $"submit-checked|{focusedControlId}|{NormalizeText(focusedText)}",
                    $"{focusedText} ✔");
            }
            else if (includeRowTitlePrefix)
            {
                SpeakSelectionText($"{rowTitle}, {focusedText}");
            }
            else
            {
                SpeakSelectionTextDedup(
                    $"focus|{focusedControlId}|{NormalizeText(focusedText)}",
                    focusedText);
            }

            MarkRecentSelectionValue(focusedText);
        }

        _hasLastSelectionContext = true;
        _lastSelectionValue = focusedText;
        _lastFocusedValue = focusedText;
        _lastFocusedControlId = focusedControlId;
        _lastFocusedControlY = focusedPosition.y;
    }

    private void ResetSelectionTracking()
    {
        _hasLastSelectionContext = false;
        _lastSelectionValue = string.Empty;
        _lastFocusedValue = string.Empty;
        _lastFocusedControlId = 0;
        _lastFocusedControlY = 0f;
        _nextSelectionDetailRefreshTime = -100f;
    }

    private void ScanCurrentSelectionMinimal()
    {
        _verticalNavPending = false;
        _submitPending = false;
        _verticalNavRetryScans = 0;

        var eventSystem = EventSystem.current;
        if (eventSystem == null)
        {
            ResetSelectionTracking();
            return;
        }

        var selected = eventSystem.currentSelectedGameObject ?? ResolveFocusAnchor(eventSystem, GetCachedVisibleSelectables());
        if (selected == null)
        {
            ResetSelectionTracking();
            return;
        }

        var focusedText = ReadableObjectName(selected.name);
        if (string.IsNullOrWhiteSpace(focusedText))
        {
            ResetSelectionTracking();
            return;
        }

        var focusedControlId = selected.GetInstanceID();
        var focusedTextChanged = !_hasLastSelectionContext
            || focusedControlId != _lastFocusedControlId
            || !string.Equals(focusedText, _lastFocusedValue, StringComparison.Ordinal);
        if (focusedTextChanged)
        {
            SpeakSelectionTextDedup(
                $"focus-min|{focusedControlId}|{NormalizeText(focusedText)}",
                focusedText);
            MarkRecentSelectionValue(focusedText);
        }

        _hasLastSelectionContext = true;
        _lastSelectionValue = focusedText;
        _lastFocusedValue = focusedText;
        _lastFocusedControlId = focusedControlId;
        _lastFocusedControlY = GetUiPosition(selected.transform).y;
        _nextSelectionDetailRefreshTime = Time.unscaledTime + ResolveSelectionDetailRefreshIntervalSeconds(GetCurrentPerfMode());
    }

    private List<SelectionTextCandidate> GetCachedGlobalVisibleTextCandidates()
    {
        var now = Time.unscaledTime;
        if (_cachedSelectionTextCandidates.Count > 0 && now < _nextSelectionTextCandidatesRefreshTime)
        {
            return _cachedSelectionTextCandidates;
        }

        _cachedSelectionTextCandidates.Clear();
        _cachedSelectionTextCandidates.AddRange(BuildGlobalVisibleTextCandidates());
        _findObjectsEnumerationCount += 2;
        _nextSelectionTextCandidatesRefreshTime = now + ResolveUiCacheRefreshIntervalSeconds(GetCurrentPerfMode());
        return _cachedSelectionTextCandidates;
    }

    private List<Selectable> GetCachedVisibleSelectables()
    {
        var now = Time.unscaledTime;
        if (_cachedVisibleSelectables.Count > 0 && now < _nextVisibleSelectablesRefreshTime)
        {
            return _cachedVisibleSelectables;
        }

        _cachedVisibleSelectables.Clear();
        foreach (var selectable in Resources.FindObjectsOfTypeAll<Selectable>())
        {
            if (IsSelectableVisible(selectable))
            {
                _cachedVisibleSelectables.Add(selectable);
            }
        }

        _findObjectsEnumerationCount++;
        _nextVisibleSelectablesRefreshTime = now + ResolveUiCacheRefreshIntervalSeconds(GetCurrentPerfMode());
        return _cachedVisibleSelectables;
    }

    private static bool IsVerticalNavigationPressed()
    {
        if (IsKeyDownSafe(Key.UpArrow)
            || IsKeyDownSafe(Key.DownArrow)
            || IsKeyDownSafe(Key.W)
            || IsKeyDownSafe(Key.S))
        {
            return true;
        }

        var gamepads = Gamepad.all;
        if (gamepads == null || gamepads.Count == 0)
        {
            return false;
        }

        for (var i = 0; i < gamepads.Count; i++)
        {
            var gamepad = gamepads[i];
            if (gamepad == null)
            {
                continue;
            }

            if (gamepad.dpad.up.wasPressedThisFrame || gamepad.dpad.down.wasPressedThisFrame)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSubmitPressed()
    {
        return IsKeyDownSafe(Key.Enter, Key.NumpadEnter);
    }

    private void EnsureDialogKeyboardBridgeReady()
    {
        if (IsMenuActive())
        {
            ClearBufferedDialogInput();
            ReleaseDialogKeyboardVirtualGamepad();
            return;
        }

        if (!IsDialogChoiceContextActive())
        {
            return;
        }

        if (_dialogKeyboardVirtualGamepad != null && _dialogKeyboardVirtualGamepad.added)
        {
            return;
        }

        var now = Time.unscaledTime;
        if (now < _nextDialogKeyboardBridgeWarmupTime)
        {
            return;
        }

        _nextDialogKeyboardBridgeWarmupTime = now + 5f;
        _ = GetOrCreateDialogKeyboardVirtualGamepad();
    }

    private bool IsDialogChoiceContextActive()
    {
        if (IsMenuActive())
        {
            return false;
        }

        var eventSystem = EventSystem.current;
        if (eventSystem == null)
        {
            return false;
        }

        var selectedObject = eventSystem.currentSelectedGameObject;
        if (selectedObject == null)
        {
            return false;
        }

        var selectedSelectable = selectedObject.GetComponentInParent<Selectable>();
        if (selectedSelectable == null || !IsSelectableVisible(selectedSelectable))
        {
            return false;
        }

        return IsLikelyDialogChoiceContext(selectedSelectable);
    }

    private void HandleDialogKeyboardChoiceInput()
    {
        if (IsMenuActive())
        {
            ClearBufferedDialogInput();
            return;
        }

        var now = Time.unscaledTime;
        var moveUpPressed = IsKeyDownSafe(Key.UpArrow);
        var moveDownPressed = IsKeyDownSafe(Key.DownArrow);
        var submitPressed = IsKeyDownSafe(Key.Enter, Key.NumpadEnter);
        var directOptionNumber = GetPressedDialogOptionNumberKey();
        var hasLiveInput = moveUpPressed || moveDownPressed || submitPressed || directOptionNumber > 0;
        var hasBufferedInput = HasBufferedDialogInput(now);
        if (!hasLiveInput && !hasBufferedInput)
        {
            return;
        }

        var eventSystem = EventSystem.current;
        if (eventSystem == null)
        {
            if (hasLiveInput)
            {
                BufferDialogInput(now, moveUpPressed, moveDownPressed, submitPressed, directOptionNumber);
            }

            return;
        }

        var selectedObject = eventSystem.currentSelectedGameObject;
        var selectedSelectable = selectedObject != null ? selectedObject.GetComponentInParent<Selectable>() : null;
        if (selectedSelectable == null || !IsSelectableVisible(selectedSelectable))
        {
            var selectables = GetCachedVisibleSelectables();
            for (var i = 0; i < selectables.Count; i++)
            {
                var candidate = selectables[i];
                if (!IsSelectableVisible(candidate))
                {
                    continue;
                }

                if (!IsLikelyDialogChoiceContext(candidate))
                {
                    continue;
                }

                selectedSelectable = candidate;
                eventSystem.SetSelectedGameObject(candidate.gameObject);
                break;
            }
        }

        if (selectedSelectable == null || !IsSelectableVisible(selectedSelectable))
        {
            if (hasLiveInput)
            {
                BufferDialogInput(now, moveUpPressed, moveDownPressed, submitPressed, directOptionNumber);
            }

            return;
        }

        if (!IsLikelyDialogChoiceContext(selectedSelectable))
        {
            if (hasLiveInput)
            {
                BufferDialogInput(now, moveUpPressed, moveDownPressed, submitPressed, directOptionNumber);
            }

            return;
        }

        if (!hasLiveInput && hasBufferedInput)
        {
            moveUpPressed = _dialogPendingMoveSteps < 0;
            moveDownPressed = _dialogPendingMoveSteps > 0;
            submitPressed = _dialogPendingSubmit;
            directOptionNumber = _dialogPendingOptionNumber;
        }

        var requestedMoveSteps = 0;
        if (moveUpPressed)
        {
            requestedMoveSteps = hasLiveInput ? -1 : _dialogPendingMoveSteps;
        }
        else if (moveDownPressed)
        {
            requestedMoveSteps = hasLiveInput ? 1 : _dialogPendingMoveSteps;
        }

        var handledWithVirtualGamepad = false;
        if (directOptionNumber > 0)
        {
            handledWithVirtualGamepad = TryQueueDialogOptionNumberWithVirtualGamepad(eventSystem, selectedSelectable, directOptionNumber);
            if (handledWithVirtualGamepad)
            {
                ClearBufferedDialogInput();
                return;
            }

            if (TrySelectDialogOptionByNumber(eventSystem, selectedSelectable, directOptionNumber, out var selectedOption))
            {
                TryDispatchDialogSubmit(eventSystem, selectedOption);
                ClearBufferedDialogInput();
            }

            return;
        }

        if (requestedMoveSteps < 0)
        {
            for (var i = 0; i < Mathf.Abs(requestedMoveSteps); i++)
            {
                handledWithVirtualGamepad = QueueDialogVirtualGamepadTap(DialogVirtualInput.Up) || handledWithVirtualGamepad;
            }
        }
        else if (requestedMoveSteps > 0)
        {
            for (var i = 0; i < Mathf.Abs(requestedMoveSteps); i++)
            {
                handledWithVirtualGamepad = QueueDialogVirtualGamepadTap(DialogVirtualInput.Down) || handledWithVirtualGamepad;
            }
        }

        if (submitPressed)
        {
            handledWithVirtualGamepad = QueueDialogVirtualGamepadTap(DialogVirtualInput.Submit) || handledWithVirtualGamepad;
        }

        if (handledWithVirtualGamepad)
        {
            ClearBufferedDialogInput();
            return;
        }

        if (requestedMoveSteps < 0)
        {
            for (var i = 0; i < Mathf.Abs(requestedMoveSteps); i++)
            {
                TryDispatchDialogMove(eventSystem, selectedSelectable.gameObject, MoveDirection.Up);
            }
        }
        else if (requestedMoveSteps > 0)
        {
            for (var i = 0; i < Mathf.Abs(requestedMoveSteps); i++)
            {
                TryDispatchDialogMove(eventSystem, selectedSelectable.gameObject, MoveDirection.Down);
            }
        }

        if (submitPressed)
        {
            var focused = eventSystem.currentSelectedGameObject ?? selectedSelectable.gameObject;
            TryDispatchDialogSubmit(eventSystem, focused);
        }

        ClearBufferedDialogInput();
    }

    private bool HasBufferedDialogInput(float now)
    {
        if (now > _dialogPendingInputUntilTime)
        {
            ClearBufferedDialogInput();
            return false;
        }

        return _dialogPendingMoveSteps != 0 || _dialogPendingSubmit || _dialogPendingOptionNumber > 0;
    }

    private void BufferDialogInput(
        float now,
        bool moveUpPressed,
        bool moveDownPressed,
        bool submitPressed,
        int directOptionNumber)
    {
        if (directOptionNumber > 0)
        {
            _dialogPendingOptionNumber = directOptionNumber;
            _dialogPendingMoveSteps = 0;
        }
        else
        {
            if (moveUpPressed)
            {
                _dialogPendingMoveSteps--;
            }

            if (moveDownPressed)
            {
                _dialogPendingMoveSteps++;
            }
        }

        if (submitPressed)
        {
            _dialogPendingSubmit = true;
        }

        _dialogPendingInputUntilTime = now + DialogInputBufferWindowSeconds;
        _ = GetOrCreateDialogKeyboardVirtualGamepad();
    }

    private void ClearBufferedDialogInput()
    {
        _dialogPendingInputUntilTime = -100f;
        _dialogPendingMoveSteps = 0;
        _dialogPendingSubmit = false;
        _dialogPendingOptionNumber = 0;
    }

    private enum DialogVirtualInput
    {
        Up,
        Down,
        Submit
    }

    private bool QueueDialogVirtualGamepadTap(DialogVirtualInput input)
    {
        var gamepad = GetOrCreateDialogKeyboardVirtualGamepad();
        if (gamepad == null)
        {
            return false;
        }

        try
        {
            switch (input)
            {
                case DialogVirtualInput.Up:
                    InputSystem.QueueDeltaStateEvent(gamepad.dpad.up, 1f);
                    InputSystem.QueueDeltaStateEvent(gamepad.dpad.up, 0f);
                    break;
                case DialogVirtualInput.Down:
                    InputSystem.QueueDeltaStateEvent(gamepad.dpad.down, 1f);
                    InputSystem.QueueDeltaStateEvent(gamepad.dpad.down, 0f);
                    break;
                case DialogVirtualInput.Submit:
                    InputSystem.QueueDeltaStateEvent(gamepad.buttonSouth, 1f);
                    InputSystem.QueueDeltaStateEvent(gamepad.buttonSouth, 0f);
                    break;
                default:
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryQueueDialogOptionNumberWithVirtualGamepad(EventSystem eventSystem, Selectable selected, int optionNumber)
    {
        if (optionNumber <= 0 || eventSystem == null || selected == null)
        {
            return false;
        }

        if (!TryCollectDialogOptions(selected, out var options))
        {
            return false;
        }

        var targetIndex = optionNumber - 1;
        if (targetIndex < 0 || targetIndex >= options.Count)
        {
            return false;
        }

        var selectedObject = eventSystem.currentSelectedGameObject;
        var currentId = selectedObject != null
            ? selectedObject.GetInstanceID()
            : selected.gameObject.GetInstanceID();
        var currentIndex = -1;
        for (var i = 0; i < options.Count; i++)
        {
            if (options[i] != null && options[i].gameObject.GetInstanceID() == currentId)
            {
                currentIndex = i;
                break;
            }
        }

        if (currentIndex < 0)
        {
            return false;
        }

        var delta = targetIndex - currentIndex;
        var steps = Mathf.Abs(delta);
        var handled = false;
        for (var i = 0; i < steps; i++)
        {
            handled = QueueDialogVirtualGamepadTap(delta > 0 ? DialogVirtualInput.Down : DialogVirtualInput.Up) || handled;
        }

        return QueueDialogVirtualGamepadTap(DialogVirtualInput.Submit) || handled;
    }

    private Gamepad? GetOrCreateDialogKeyboardVirtualGamepad()
    {
        if (_dialogKeyboardVirtualGamepad != null && _dialogKeyboardVirtualGamepad.added)
        {
            return _dialogKeyboardVirtualGamepad;
        }

        try
        {
            _dialogKeyboardVirtualGamepad = InputSystem.AddDevice<Gamepad>("KFAccessDialogKeyboardBridge");
            return _dialogKeyboardVirtualGamepad;
        }
        catch
        {
            return Gamepad.current;
        }
    }

    private void ReleaseDialogKeyboardVirtualGamepad()
    {
        if (_dialogKeyboardVirtualGamepad == null)
        {
            return;
        }

        try
        {
            if (_dialogKeyboardVirtualGamepad.added)
            {
                InputSystem.RemoveDevice(_dialogKeyboardVirtualGamepad);
            }
        }
        catch
        {
            // Ignore shutdown failures.
        }
        finally
        {
            _dialogKeyboardVirtualGamepad = null;
        }
    }

    private static int GetPressedDialogOptionNumberKey()
    {
        var mapping = new (Key Main, Key Numpad, int Value)[]
        {
            (Key.Digit1, Key.Numpad1, 1),
            (Key.Digit2, Key.Numpad2, 2),
            (Key.Digit3, Key.Numpad3, 3),
            (Key.Digit4, Key.Numpad4, 4),
            (Key.Digit5, Key.Numpad5, 5),
            (Key.Digit6, Key.Numpad6, 6),
            (Key.Digit7, Key.Numpad7, 7),
            (Key.Digit8, Key.Numpad8, 8),
            (Key.Digit9, Key.Numpad9, 9)
        };

        for (var i = 0; i < mapping.Length; i++)
        {
            if (IsKeyDownSafe(mapping[i].Main, mapping[i].Numpad))
            {
                return mapping[i].Value;
            }
        }

        return 0;
    }

    private static bool TryDispatchDialogMove(EventSystem eventSystem, GameObject selected, MoveDirection direction)
    {
        if (eventSystem == null || selected == null)
        {
            return false;
        }

        var axisData = new AxisEventData(eventSystem)
        {
            moveDir = direction,
            moveVector = direction == MoveDirection.Up ? Vector2.up : Vector2.down
        };
        ExecuteEvents.Execute(selected, axisData, ExecuteEvents.moveHandler);
        if (axisData.used)
        {
            return true;
        }

        var selectable = selected.GetComponentInParent<Selectable>();
        if (selectable == null)
        {
            return false;
        }

        var fallback = direction == MoveDirection.Up
            ? selectable.FindSelectableOnUp()
            : selectable.FindSelectableOnDown();
        if (fallback == null || !IsSelectableVisible(fallback))
        {
            return false;
        }

        eventSystem.SetSelectedGameObject(fallback.gameObject);
        return true;
    }

    private static void TryDispatchDialogSubmit(EventSystem eventSystem, GameObject selected)
    {
        if (eventSystem == null || selected == null)
        {
            return;
        }

        var submitData = new BaseEventData(eventSystem);
        ExecuteEvents.Execute(selected, submitData, ExecuteEvents.submitHandler);
        if (!submitData.used)
        {
            ExecuteEvents.ExecuteHierarchy(selected, submitData, ExecuteEvents.submitHandler);
        }
    }

    private static bool IsLikelyDialogChoiceContext(Selectable selected)
    {
        if (selected == null || selected.gameObject == null || !selected.gameObject.activeInHierarchy)
        {
            return false;
        }

        if (selected.GetComponentInParent<Dropdown>() != null)
        {
            return false;
        }

        if (TryCollectDialogOptions(selected, out var options) && options.Count >= 2)
        {
            return true;
        }

        var current = selected.transform;
        for (var depth = 0; current != null && depth < 6; depth++)
        {
            var normalizedName = NormalizeObjectName(current.name);
            if (normalizedName.Contains("dialog")
                || normalizedName.Contains("choice")
                || normalizedName.Contains("response")
                || normalizedName.Contains("conversation"))
            {
                return true;
            }

            current = current.parent;
        }

        return false;
    }

    private static bool TrySelectDialogOptionByNumber(
        EventSystem eventSystem,
        Selectable selected,
        int optionNumber,
        out GameObject selectedOption)
    {
        selectedOption = null!;
        if (optionNumber <= 0 || eventSystem == null || selected == null)
        {
            return false;
        }

        if (!TryCollectDialogOptions(selected, out var options))
        {
            return false;
        }

        var index = optionNumber - 1;
        if (index < 0 || index >= options.Count)
        {
            return false;
        }

        var target = options[index];
        if (target == null || target.gameObject == null || !target.gameObject.activeInHierarchy)
        {
            return false;
        }

        selectedOption = target.gameObject;
        eventSystem.SetSelectedGameObject(selectedOption);
        return true;
    }

    private static bool TryCollectDialogOptions(Selectable selected, out List<Selectable> options)
    {
        options = new List<Selectable>();
        if (selected == null)
        {
            return false;
        }

        if (TryCollectDialogOptionsFromContainer(selected.transform.parent, options)
            || TryCollectDialogOptionsFromContainer(selected.transform.parent != null ? selected.transform.parent.parent : null, options))
        {
            options.Sort(CompareDialogOptions);
            return options.Count > 0;
        }

        return false;
    }

    private static bool TryCollectDialogOptionsFromContainer(Transform? container, List<Selectable> options)
    {
        if (container == null)
        {
            return false;
        }

        var seen = new HashSet<int>();
        var childCount = container.childCount;
        for (var i = 0; i < childCount; i++)
        {
            var child = container.GetChild(i);
            if (child == null)
            {
                continue;
            }

            var selectable = child.GetComponentInChildren<Selectable>(true);
            if (selectable == null || !IsSelectableVisible(selectable))
            {
                continue;
            }

            var id = selectable.GetInstanceID();
            if (!seen.Add(id))
            {
                continue;
            }

            options.Add(selectable);
        }

        return options.Count >= 2;
    }

    private static int CompareDialogOptions(Selectable? a, Selectable? b)
    {
        if (a == null && b == null)
        {
            return 0;
        }

        if (a == null)
        {
            return 1;
        }

        if (b == null)
        {
            return -1;
        }

        var posA = GetUiPosition(a.transform);
        var posB = GetUiPosition(b.transform);
        var byY = posB.y.CompareTo(posA.y);
        if (byY != 0)
        {
            return byY;
        }

        var byX = posA.x.CompareTo(posB.x);
        if (byX != 0)
        {
            return byX;
        }

        return a.transform.GetSiblingIndex().CompareTo(b.transform.GetSiblingIndex());
    }

    private static bool IsMenuActive()
    {
        var eventSystem = EventSystem.current;
        if (eventSystem == null)
        {
            return false;
        }

        var selected = eventSystem.currentSelectedGameObject;
        if (selected == null)
        {
            return false;
        }

        var selectable = selected.GetComponentInParent<Selectable>();
        if (selectable == null)
        {
            return false;
        }

        var canvas = selectable.GetComponentInParent<Canvas>();
        return canvas != null && canvas.isActiveAndEnabled;
    }

    private static GameObject? ResolveFocusAnchor(EventSystem eventSystem, IReadOnlyList<Selectable> selectables)
    {
        var selected = eventSystem.currentSelectedGameObject;
        if (selected != null)
        {
            var selectedSelectable = selected.GetComponentInParent<Selectable>();
            if (selectedSelectable != null && IsSelectableVisible(selectedSelectable))
            {
                return selectedSelectable.gameObject;
            }
        }

        Selectable? best = null;
        var bestRank = int.MaxValue;

        foreach (var selectable in selectables)
        {
            if (!IsSelectableVisible(selectable))
            {
                continue;
            }

            var rank = selectable.currentSelectionState switch
            {
                Selectable.SelectionState.Selected => 0,
                Selectable.SelectionState.Pressed => 1,
                Selectable.SelectionState.Highlighted => 2,
                _ => 99
            };

            if (rank >= 99)
            {
                continue;
            }

            if (rank < bestRank)
            {
                best = selectable;
                bestRank = rank;
            }
        }

        if (best != null)
        {
            return best.gameObject;
        }

        return selected;
    }

    private static bool IsSelectableVisible(Selectable selectable)
    {
        if (selectable == null || !selectable.IsActive() || !selectable.gameObject.activeInHierarchy)
        {
            return false;
        }

        var graphic = selectable.targetGraphic as MaskableGraphic;
        if (graphic != null)
        {
            return IsVisible(graphic);
        }

        return true;
    }

    private static bool IsUsableChoiceRowTitle(string title, string focusedValue)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        if (IsLikelyStateText(title, string.Empty))
        {
            return false;
        }

        var normalizedTitle = NormalizeText(title);
        if (!string.IsNullOrWhiteSpace(focusedValue)
            && string.Equals(normalizedTitle, NormalizeText(focusedValue), System.StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static Vector2 ResolveFocusedTextAnchorPosition(
        Transform focusedTransform,
        Vector2 fallbackPosition,
        List<SelectionTextCandidate> candidates,
        string focusedText)
    {
        if (string.IsNullOrWhiteSpace(focusedText))
        {
            return fallbackPosition;
        }

        SelectionTextCandidate? best = null;
        var bestScore = float.MaxValue;
        var normalizedFocused = NormalizeText(focusedText);
        foreach (var candidate in candidates)
        {
            if (candidate.SourceTransform == null
                || string.IsNullOrWhiteSpace(candidate.Text)
                || !string.Equals(
                    NormalizeText(candidate.Text),
                    normalizedFocused,
                    System.StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var dy = Mathf.Abs(candidate.Position.y - fallbackPosition.y);
            var dx = Mathf.Abs(candidate.Position.x - fallbackPosition.x);
            var score = dy * 2.0f + dx * 0.35f;
            if (candidate.SourceTransform.IsChildOf(focusedTransform))
            {
                score -= 120f;
            }

            if (score < bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best?.Position ?? fallbackPosition;
    }

    private static string FindStateTextInTransform(Transform root, List<SelectionTextCandidate> candidates)
    {
        SelectionTextCandidate? best = null;
        foreach (var candidate in candidates)
        {
            if (candidate.SourceTransform == null
                || !candidate.SourceTransform.IsChildOf(root)
                || !IsLikelyStateText(candidate.Text, candidate.ObjectName))
            {
                continue;
            }

            if (best == null || candidate.Text.Length < best.Text.Length)
            {
                best = candidate;
            }
        }

        return best?.Text ?? string.Empty;
    }

    private static string FindPrimarySelectionText(Transform selectedTransform, Vector2 selectedPosition, List<SelectionTextCandidate> candidates)
    {
        SelectionTextCandidate? best = null;
        var bestScore = float.MaxValue;

        foreach (var candidate in candidates)
        {
            if (candidate.SourceTransform == null || !candidate.SourceTransform.IsChildOf(selectedTransform))
            {
                continue;
            }

            var dx = Mathf.Abs(candidate.Position.x - selectedPosition.x);
            var dy = Mathf.Abs(candidate.Position.y - selectedPosition.y);
            var score = dy * 2.2f + dx * 0.5f;

            if (IsLikelyStateText(candidate.Text, candidate.ObjectName))
            {
                score -= 25f;
            }

            if (score < bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best?.Text ?? string.Empty;
    }

    private static string FindSameRowLeftTitle(
        Vector2 selectedPosition,
        List<SelectionTextCandidate> candidates,
        string focusedValue)
    {
        SelectionTextCandidate? best = null;
        var bestScore = float.MaxValue;

        foreach (var candidate in candidates)
        {
            if (!IsPotentialRowTitleCandidate(candidate, focusedValue))
            {
                continue;
            }

            var dy = Mathf.Abs(candidate.Position.y - selectedPosition.y);
            if (dy > 34f)
            {
                continue;
            }

            var dx = selectedPosition.x - candidate.Position.x;
            if (dx < 60f || dx > 1500f)
            {
                continue;
            }

            var score = dy * 3.2f + Mathf.Abs(dx - 380f) * 0.08f;
            if (HasRowTitleNameHint(candidate.ObjectName))
            {
                score -= 12f;
            }

            if (score < bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best?.Text ?? string.Empty;
    }

    private static string FindSameRowLeftTitleFromHierarchy(
        Transform focusedTransform,
        Vector2 focusedPosition,
        List<SelectionTextCandidate> candidates,
        string focusedValue)
    {
        var current = focusedTransform;
        for (var depth = 0; current != null && depth < 8; depth++)
        {
            SelectionTextCandidate? best = null;
            var bestScore = float.MaxValue;

            foreach (var candidate in candidates)
            {
                if (candidate.SourceTransform == null
                    || !candidate.SourceTransform.IsChildOf(current)
                    || !IsPotentialRowTitleCandidate(candidate, focusedValue))
                {
                    continue;
                }

                var dy = Mathf.Abs(candidate.Position.y - focusedPosition.y);
                if (dy > 34f)
                {
                    continue;
                }

                var dx = focusedPosition.x - candidate.Position.x;
                if (dx < 40f || dx > 1600f)
                {
                    continue;
                }

                var score = dy * 3.3f + Mathf.Abs(dx - 380f) * 0.08f + depth * 6f;
                if (HasRowTitleNameHint(candidate.ObjectName))
                {
                    score -= 12f;
                }

                if (score < bestScore)
                {
                    best = candidate;
                    bestScore = score;
                }
            }

            if (best != null)
            {
                return best.Text;
            }

            current = current.parent;
        }

        return string.Empty;
    }

    private static string FindRowTitleAboveFocused(
        Transform focusedTransform,
        Vector2 anchorPosition,
        List<SelectionTextCandidate> candidates,
        string focusedValue)
    {
        var current = focusedTransform;
        for (var depth = 0; current != null && depth < 8; depth++)
        {
            SelectionTextCandidate? best = null;
            var bestScore = float.MaxValue;
            foreach (var candidate in candidates)
            {
                if (candidate.SourceTransform == null
                    || !candidate.SourceTransform.IsChildOf(current)
                    || string.IsNullOrWhiteSpace(candidate.Text)
                    || !IsPotentialRowTitleCandidate(candidate, focusedValue))
                {
                    continue;
                }

                var dy = candidate.Position.y - anchorPosition.y;
                if (dy < 8f || dy > 180f)
                {
                    continue;
                }

                var dx = candidate.Position.x - anchorPosition.x;
                if (candidate.Position.x > anchorPosition.x - 20f || Mathf.Abs(dx) > 900f)
                {
                    continue;
                }

                var score = dy * 1.8f + Mathf.Abs(dx) * 0.12f + depth * 8f;
                if (HasRowTitleNameHint(candidate.ObjectName))
                {
                    score -= 18f;
                }

                if (WordCount(candidate.Text) > 6)
                {
                    score += 20f;
                }

                if (score < bestScore)
                {
                    best = candidate;
                    bestScore = score;
                }
            }

            if (best != null)
            {
                return best.Text;
            }

            current = current.parent;
        }

        return string.Empty;
    }

    private static string FindFallbackLeftRowTitle(
        Vector2 focusedPosition,
        List<SelectionTextCandidate> candidates,
        string focusedValue)
    {
        SelectionTextCandidate? best = null;
        var bestScore = float.MaxValue;

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Text)
                || IsLikelyTabOrHeaderObjectName(candidate.ObjectName)
                || IsLikelyDescriptionText(candidate.Text))
            {
                continue;
            }

            if (!IsUsableChoiceRowTitle(candidate.Text, focusedValue))
            {
                continue;
            }

            var dy = Mathf.Abs(candidate.Position.y - focusedPosition.y);
            if (dy > 34f)
            {
                continue;
            }

            var dx = focusedPosition.x - candidate.Position.x;
            if (dx < 20f || dx > 2200f)
            {
                continue;
            }

            var score = dy * 2.4f + Mathf.Abs(dx - 420f) * 0.06f;
            if (IsLikelyStateText(candidate.Text, candidate.ObjectName))
            {
                score += 45f;
            }

            if (HasRowTitleNameHint(candidate.ObjectName))
            {
                score -= 10f;
            }

            if (score < bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best?.Text ?? string.Empty;
    }

    private static string FindBindingRowTitleLeft(
        Vector2 focusedPosition,
        List<SelectionTextCandidate> candidates,
        string focusedValue)
    {
        SelectionTextCandidate? best = null;
        var bestScore = float.MaxValue;

        foreach (var candidate in candidates)
        {
            if (!IsPotentialRowTitleCandidate(candidate, focusedValue))
            {
                continue;
            }

            if (IsLikelyBindingTokenCandidate(candidate.Text))
            {
                continue;
            }

            var dy = Mathf.Abs(candidate.Position.y - focusedPosition.y);
            if (dy > 90f)
            {
                continue;
            }

            var dx = focusedPosition.x - candidate.Position.x;
            if (dx < 120f || dx > 2400f)
            {
                continue;
            }

            var score = dy * 3.0f + Mathf.Abs(dx - 520f) * 0.04f;
            var words = WordCount(candidate.Text);
            if (words <= 1 && dy > 20f)
            {
                score += 40f;
            }
            else if (words >= 2)
            {
                score -= 6f;
            }

            if (HasRowTitleNameHint(candidate.ObjectName))
            {
                score -= 8f;
            }

            if (score < bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best?.Text ?? string.Empty;
    }

    private static bool IsLikelyBindingTokenCandidate(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = NormalizeText(text).ToLowerInvariant();
        if (IsLikelyBindingValueText(normalized, string.Empty))
        {
            return true;
        }

        if (normalized is "esc" or "escape"
            or "del" or "delete"
            or "ins" or "insert"
            or "home" or "end")
        {
            return true;
        }

        if (Regex.IsMatch(normalized, "^(num|numpad)\\s*[/\\-*+]$"))
        {
            return true;
        }

        if (Regex.IsMatch(normalized, "^page\\s*(up|down)$") || Regex.IsMatch(normalized, "^pg\\s*(up|dn)$"))
        {
            return true;
        }

        return false;
    }

    private static string FindBroadVerticalRowTitle(
        Vector2 focusedPosition,
        List<SelectionTextCandidate> candidates,
        string focusedValue)
    {
        SelectionTextCandidate? best = null;
        var bestScore = float.MaxValue;
        var normalizedFocused = NormalizeText(focusedValue);

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Text)
                || IsLikelyTabOrHeaderObjectName(candidate.ObjectName))
            {
                continue;
            }

            var normalizedCandidate = NormalizeText(candidate.Text);
            if (!string.IsNullOrWhiteSpace(normalizedFocused)
                && string.Equals(normalizedCandidate, normalizedFocused, System.StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var dy = Mathf.Abs(candidate.Position.y - focusedPosition.y);
            if (dy > 42f)
            {
                continue;
            }

            // Prefer labels to the left, but allow slight right offsets for odd layouts.
            var dx = focusedPosition.x - candidate.Position.x;
            if (dx < -90f || dx > 2600f)
            {
                continue;
            }

            var score = dy * 1.9f + Mathf.Abs(dx - 420f) * 0.05f;

            if (IsLikelyStateText(candidate.Text, candidate.ObjectName))
            {
                score += 70f;
            }

            if (IsLikelyDescriptionText(candidate.Text))
            {
                score += 60f;
            }

            if (HasRowTitleNameHint(candidate.ObjectName))
            {
                score -= 18f;
            }

            if (score < bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best?.Text ?? string.Empty;
    }

    private static bool IsPotentialRowTitleCandidate(SelectionTextCandidate candidate, string focusedValue)
    {
        if (string.IsNullOrWhiteSpace(candidate.Text)
            || IsLikelyDescriptionText(candidate.Text)
            || IsLikelyTabOrHeaderObjectName(candidate.ObjectName))
        {
            return false;
        }

        if (IsLikelyBindingTokenCandidate(candidate.Text))
        {
            return false;
        }

        // Keep obvious single-value tokens out, but allow meaningful multi-word labels
        // (for example: "Text Panels") even when object metadata looks value-like.
        if (IsLikelyStateText(candidate.Text, candidate.ObjectName))
        {
            var normalized = NormalizeText(candidate.Text);
            var lower = normalized.ToLowerInvariant();
            var words = WordCount(normalized);
            var looksBindingValue = IsLikelyBindingValueText(lower, candidate.ObjectName);
            var looksNumeric = Regex.IsMatch(lower, "^\\d{1,3}%$") || Regex.IsMatch(lower, "^\\d+(\\.\\d+)?$");
            if (looksBindingValue || looksNumeric || words <= 1)
            {
                return false;
            }
        }

        if (!IsUsableChoiceRowTitle(candidate.Text, focusedValue))
        {
            return false;
        }

        return true;
    }

    private static bool ShouldAnnounceCheckedOnSubmit(
        GameObject selected,
        Vector2 focusedPosition,
        string focusedText,
        IReadOnlyList<Selectable> selectables)
    {
        if (selected == null
            || string.IsNullOrWhiteSpace(focusedText)
            || IsTabOrHeaderFocus(selected, focusedPosition, selectables))
        {
            return false;
        }

        if (selected.GetComponentInParent<Slider>() != null)
        {
            return false;
        }

        var normalizedFocused = NormalizeText(focusedText).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedFocused)
            || IsLikelyBindingValueText(normalizedFocused, NormalizeObjectName(selected.name))
            || Regex.IsMatch(normalizedFocused, "^\\d"))
        {
            return false;
        }

        return true;
    }

    private static bool HasPeerSelectableOnSameRow(
        Vector2 focusedPosition,
        int focusedControlId,
        IReadOnlyList<Selectable> selectables)
    {
        foreach (var selectable in selectables)
        {
            if (!IsSelectableVisible(selectable))
            {
                continue;
            }

            if (selectable.GetInstanceID() == focusedControlId)
            {
                continue;
            }

            var position = GetUiPosition(selectable.transform);
            if (Mathf.Abs(position.y - focusedPosition.y) > 32f)
            {
                continue;
            }

            if (Mathf.Abs(position.x - focusedPosition.x) < 80f)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool IsSettingLikeFocus(GameObject selected, string focusedText)
    {
        if (selected == null)
        {
            return false;
        }

        if (selected.GetComponentInParent<Slider>() != null
            || selected.GetComponentInParent<Scrollbar>() != null
            || selected.GetComponentInParent<Toggle>() != null
            || selected.GetComponentInParent<TMP_Dropdown>() != null)
        {
            return true;
        }

        return IsLikelyStateText(focusedText, NormalizeObjectName(selected.name));
    }

    private static bool IsDropdownOptionPopupFocus(GameObject selected)
    {
        if (selected == null)
        {
            return false;
        }

        var current = selected.transform;
        var hasDropdownHint = false;
        var hasListOrTemplateHint = false;
        var hasItemHint = false;

        for (var depth = 0; current != null && depth < 10; depth++)
        {
            var name = NormalizeObjectName(current.name);
            if (name.Contains("dropdown"))
            {
                hasDropdownHint = true;
            }

            if (name.Contains("list") || name.Contains("template"))
            {
                hasListOrTemplateHint = true;
            }

            if (name.Contains("item"))
            {
                hasItemHint = true;
            }

            current = current.parent;
        }

        return hasDropdownHint && hasListOrTemplateHint && hasItemHint;
    }

    private static bool IsTabOrHeaderFocus(
        GameObject selected,
        Vector2 focusedPosition,
        IReadOnlyList<Selectable> selectables)
    {
        if (selected == null)
        {
            return false;
        }

        if (selected.GetComponentInParent<Slider>() != null || selected.GetComponentInParent<Toggle>() != null)
        {
            return false;
        }

        if (HasTabOrHeaderNameInAncestors(selected.transform))
        {
            return true;
        }

        var sameRowSelectables = 0;
        var hintMatches = 0;
        foreach (var selectable in selectables)
        {
            if (!IsSelectableVisible(selectable))
            {
                continue;
            }

            var dy = Mathf.Abs(GetUiPosition(selectable.transform).y - focusedPosition.y);
            if (dy <= 28f)
            {
                sameRowSelectables++;
                if (HasTabOrHeaderNameInAncestors(selectable.transform))
                {
                    hintMatches++;
                }
            }
        }

        if (sameRowSelectables < 4)
        {
            return false;
        }

        // A dense row of selectables with multiple tab/header-like object-name hints
        // is treated as top navigation regardless of localized text content.
        if (hintMatches >= 2)
        {
            return true;
        }

        return false;
    }

    private static bool HasTabOrHeaderNameInAncestors(Transform node)
    {
        var current = node;
        for (var depth = 0; current != null && depth < 7; depth++)
        {
            if (IsLikelyTabOrHeaderObjectName(current.name))
            {
                return true;
            }

            current = current.parent;
        }

        return false;
    }

    private static bool HasRowTitleNameHint(string objectName)
    {
        if (string.IsNullOrWhiteSpace(objectName))
        {
            return false;
        }

        return objectName.Contains("title")
            || objectName.Contains("label")
            || objectName.Contains("name")
            || objectName.Contains("action")
            || objectName.Contains("binding");
    }

    private static bool IsLikelyDescriptionText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Length >= 26 && WordCount(text) >= 5;
    }

    private static bool IsLikelyTabOrHeaderObjectName(string objectName)
    {
        if (string.IsNullOrWhiteSpace(objectName))
        {
            return false;
        }

        var normalized = NormalizeObjectName(objectName);
        return normalized.Contains("tab")
            || normalized.Contains("header")
            || normalized.Contains("category")
            || normalized.Contains("navigation")
            || normalized.Contains("navbar")
            || normalized.Contains("topbar")
            || normalized.Contains("backbutton")
            || normalized.Contains("restoredefault")
            || normalized.Contains("restore_defaults");
    }

    private static int WordCount(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var count = 0;
        foreach (var token in text.Split(' '))
        {
            if (!string.IsNullOrWhiteSpace(token))
            {
                count++;
            }
        }

        return count;
    }

    private static List<SelectionTextCandidate> BuildGlobalVisibleTextCandidates()
    {
        var candidates = new List<SelectionTextCandidate>(128);

        var tmpLabels = Resources.FindObjectsOfTypeAll<TMP_Text>();
        foreach (var label in tmpLabels)
        {
            if (!IsVisible(label))
            {
                continue;
            }

            var text = NormalizeText(label.text);
            if (string.IsNullOrWhiteSpace(text) || LooksLikeOptionList(text))
            {
                continue;
            }

            candidates.Add(new SelectionTextCandidate(
                text,
                NormalizeObjectName(label.gameObject.name),
                fromSelectedRoot: false,
                GetUiPosition(label.transform),
                label.transform));
        }

        var legacyLabels = Resources.FindObjectsOfTypeAll<Text>();
        foreach (var label in legacyLabels)
        {
            if (!IsVisible(label))
            {
                continue;
            }

            var text = NormalizeText(label.text);
            if (string.IsNullOrWhiteSpace(text) || LooksLikeOptionList(text))
            {
                continue;
            }

            candidates.Add(new SelectionTextCandidate(
                text,
                NormalizeObjectName(label.gameObject.name),
                fromSelectedRoot: false,
                GetUiPosition(label.transform),
                label.transform));
        }

        return candidates;
    }

    private static bool IsLikelyStateText(string text, string objectName)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalizedText = text.Trim().ToLowerInvariant();
        if (Regex.IsMatch(normalizedText, "^\\d{1,3}%$"))
        {
            return true;
        }

        if (Regex.IsMatch(normalizedText, "^\\d+\\s*x$"))
        {
            return true;
        }

        if (Regex.IsMatch(normalizedText, "^\\d+(\\.\\d+)?$"))
        {
            return true;
        }

        if (normalizedText is "on" or "off" or "yes" or "no" or "enabled" or "disabled"
            or "low" or "medium" or "high" or "ultra" or "windowed" or "fullscreen")
        {
            return true;
        }

        // Keybind/value entries in control settings (for example: "Numpad 0", "F", "Mouse 1").
        if (IsLikelyBindingValueText(normalizedText, objectName))
        {
            return true;
        }

        if (objectName.Contains("value")
            || objectName.Contains("state")
            || objectName.Contains("current")
            || objectName.Contains("toggle"))
        {
            return true;
        }

        if (LooksLikeOptionList(text))
        {
            return true;
        }

        return false;
    }

    private static bool IsLikelyBindingValueText(string normalizedText, string objectName)
    {
        if (Regex.IsMatch(normalizedText, "^f\\d{1,2}$")
            || Regex.IsMatch(normalizedText, "^numpad\\s*\\d+$")
            || Regex.IsMatch(normalizedText, "^mouse\\s*\\d+$")
            || Regex.IsMatch(normalizedText, "^(num|numpad)\\s*[/\\-*+]$"))
        {
            return true;
        }

        if (normalizedText.Contains("+")
            && normalizedText.Length <= 24
            && WordCount(normalizedText) <= 4)
        {
            return true;
        }

        if (normalizedText is "tab" or "enter" or "space" or "shift" or "ctrl" or "alt"
            or "left shift" or "right shift"
            or "left ctrl" or "right ctrl"
            or "left alt" or "right alt"
            or "esc" or "escape"
            or "delete" or "insert" or "home" or "end"
            or "page up" or "page down" or "pg up" or "pg dn"
            or "up arrow" or "down arrow" or "left arrow" or "right arrow")
        {
            return true;
        }

        if (normalizedText.Length == 1 && char.IsLetterOrDigit(normalizedText[0]))
        {
            return true;
        }

        return false;
    }

    private static bool LooksLikeOptionList(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (!(text.Contains(",") || text.Contains("|")))
        {
            return false;
        }

        var parts = Regex.Split(text, "[,|]");
        var count = 0;
        foreach (var part in parts)
        {
            var token = NormalizeText(part);
            if (token.Length == 0)
            {
                continue;
            }

            if (token.Length > 14)
            {
                return false;
            }

            count++;
        }

        return count >= 2;
    }

    private static string NormalizeObjectName(string objectName)
    {
        if (string.IsNullOrWhiteSpace(objectName))
        {
            return string.Empty;
        }

        return objectName.Trim().ToLowerInvariant();
    }

    private static Vector2 GetUiPosition(Transform transform)
    {
        if (transform is RectTransform rect)
        {
            return rect.position;
        }

        return transform.position;
    }

    private sealed class SelectionTextCandidate
    {
        public SelectionTextCandidate(string text, string objectName, bool fromSelectedRoot, Vector2 position, Transform sourceTransform)
        {
            Text = text;
            ObjectName = objectName;
            FromSelectedRoot = fromSelectedRoot;
            Position = position;
            SourceTransform = sourceTransform;
        }

        public string Text { get; }
        public string ObjectName { get; }
        public bool FromSelectedRoot { get; }
        public Vector2 Position { get; }
        public Transform SourceTransform { get; }
    }
}

