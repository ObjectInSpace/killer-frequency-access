using System.Collections.Generic;
using UnityEngine;

namespace Killer_Frequency_Access;

/// <summary>
/// The record box (Il2Cpp.RecordBox, one in SCN_Main on ".../P_RecordBox (1)/Functionality", with the
/// box's Interactable and TakeInteraction). While the player points at it, small pointer movements flip
/// through the records (OnHovering -> CalculateSelectionFromPoint); Take dispenses the selected one.
/// The sleeves carry no text component (rip: P_HeroProp_LPRecord_Sleeve_01..12 have only a mesh and
/// LPRecordSleeve), so the record's name comes from its data: LPRecordSleeve.recordData.title.
///
/// Spoken: the selected record when the box becomes the observed interactable (after the box's own
/// announcement), and again each time the selection changes while it stays observed. A change is
/// spoken once it has held for RecordSettleSeconds, since speech queues and a quick flick would
/// otherwise queue every record passed over.
///
/// Unverified (rip has no method bodies, and LPRecordProperties fields are not serialised in it):
/// that _currentSelection indexes _availableRecords, and what title holds. [records] logs the
/// selection, both lists' sizes, the sleeve at the index, _activeRecord.properties and _isHovered
/// on every change, so the first run settles both.
/// </summary>
public sealed partial class KillerFrequencyAccessMod
{
    private const float RecordSettleSeconds = 0.25f;

    private int _recordBoxId = -1;
    private int _recordSelection = int.MinValue;
    private int _recordSpokenSelection = int.MinValue;
    private float _recordSelectionChangedTime;

    private void UpdateRecordBox()
    {
        try
        {
            Il2Cpp.RecordBox? box = null;
            if (!IsMenuActive() && TryGetObservedInteractable(out var observed))
            {
                box = observed.GetComponent<Il2Cpp.RecordBox>();
            }

            if (box == null)
            {
                _recordBoxId = -1;
                return;
            }

            var now = Time.unscaledTime;
            var selection = box._currentSelection;
            if (box.GetInstanceID() != _recordBoxId)
            {
                // Focus just arrived: speak right away.
                _recordBoxId = box.GetInstanceID();
                _recordSelection = selection;
                _recordSpokenSelection = int.MinValue;
                _recordSelectionChangedTime = now - RecordSettleSeconds;
                LogRecordSelection(box, "focused");
            }
            else if (selection != _recordSelection)
            {
                _recordSelection = selection;
                _recordSelectionChangedTime = now;
                LogRecordSelection(box, "changed");
            }

            if (_recordSpokenSelection == _recordSelection || now - _recordSelectionChangedTime < RecordSettleSeconds || _sweepActive)
            {
                return;
            }

            _recordSpokenSelection = _recordSelection;
            var title = SelectedRecordTitle(box);
            if (title.Length > 0)
            {
                SpeakSelectionText(title);
            }
        }
        catch (System.Exception ex)
        {
            LogOnce($"records-error|{ex.GetType().Name}|{ex.Message}", $"[records] error: {ex}");
        }
    }

    /// <summary>The selected sleeve's record title, or empty (logged) when there is none to read.</summary>
    private string SelectedRecordTitle(Il2Cpp.RecordBox box)
    {
        var records = box._availableRecords;
        var selection = box._currentSelection;
        if (records == null || selection < 0 || selection >= records.Count)
        {
            LogOnce($"records-no-sleeve|{selection}|{records?.Count ?? -1}",
                $"[records] selection {selection} has no sleeve in _availableRecords ({records?.Count ?? -1} records)");
            return string.Empty;
        }

        var data = records[selection]?.recordData;
        var title = data != null ? NormalizeText(data.title) : string.Empty;
        if (title.Length == 0)
        {
            LogOnce($"records-no-title|{data?.name}", $"[records] record {data?.name ?? "(no data)"} has no title");
        }

        return title;
    }

    private void LogRecordSelection(Il2Cpp.RecordBox box, string what)
    {
        var records = box._availableRecords;
        var sleeves = box._sleeveObjects;
        var selection = box._currentSelection;
        var titles = new List<string>();
        for (var i = 0; records != null && i < records.Count; i++)
        {
            var data = records[i]?.recordData;
            titles.Add($"{i}={data?.name}:\"{data?.title}\"{(records[i] != null && records[i]._tilted ? " tilted" : string.Empty)}");
        }

        var active = box._activeRecord?.properties;
        LoggerInstance.Msg($"[records] {what}: selection {selection} (previous {box._previousSelection}), hovered {box._isHovered}, "
            + $"{records?.Count ?? -1} available of {sleeves?.Count ?? -1} sleeves, max {box._maxCapacity}; "
            + $"active disc {active?.name}:\"{active?.title}\" id {active?.id}; records [{string.Join(", ", titles)}]");
    }
}
