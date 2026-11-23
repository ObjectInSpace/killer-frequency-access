using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using GameInteractableBase = Il2Cpp.InteractableBase;

namespace Killer_Frequency_Access;

/// <summary>
/// Coverage sweep (Date Everything Access's diagnostic, built for KF): F8 starts and stops it. From where
/// the player stands, it auto-walks to the nearest object not yet reached, then the next nearest from
/// there, through every object the object list offers (active, not OculusOnly, with an action the game
/// says is available). Each walk's ending is recorded with its reason; an object that fails stays in the
/// pool and is tried once more later, from wherever the chain has got to. There is no teleporting: a
/// broken path shows up as failures, which is the point.
///
/// Key: F8 alone. Both Ctrl keys are the game's Crouch, F4/F5/F9 free camera, quicksave and quickload
/// (InputActions in the rip); F8 is bound by neither the game nor the mod.
///
/// Results: MelonLoader's UserData\KillerFrequencyAccess\sweeps\&lt;run&gt;\results.jsonl (one line per walk,
/// written as it happens) and summary.txt at the end (one outcome per object, arrival winning). Walk
/// speech and pointing announcements are silent while it runs; [sweep] lines go to the log.
/// It stops on F8, when a menu opens, or when the player starts a walk.
/// </summary>
public sealed partial class KillerFrequencyAccessMod
{
    private const float SweepLegBudgetSeconds = 90f;
    private const float SweepPauseSeconds = 0.5f;
    private const int SweepMaxAttempts = 2;

    private sealed class SweepItem
    {
        public Transform Target = null!;
        public string Name = string.Empty;
        public string Path = string.Empty;
        public int Attempts;
        public bool Arrived;
        public string LastOutcome = string.Empty;
        public string LastReason = string.Empty;
    }

    private bool _sweepActive;
    private readonly List<SweepItem> _sweepItems = new();
    private SweepItem? _sweepCurrent;
    private float _sweepLegStartTime;
    private Vector3 _sweepLegStartFeet;
    private int _sweepLegSpots;
    private int _sweepLegCorners;
    private float _sweepNextLegTime;
    private int _sweepLegCount;
    private string _sweepDirectory = string.Empty;
    private StreamWriter? _sweepWriter;
    private float _sweepStartTime;

    private void UpdateSweep()
    {
        try
        {
            if (IsKeyDownSafe(Key.F8))
            {
                if (_sweepActive)
                {
                    StopSweep("F8");
                }
                else
                {
                    StartSweep();
                }

                return;
            }

            if (!_sweepActive)
            {
                return;
            }

            if (IsMenuActive())
            {
                StopSweep("a menu opened");
                return;
            }

            var now = Time.unscaledTime;
            if (_sweepCurrent != null)
            {
                if (_isAutoWalking)
                {
                    if (now - _sweepLegStartTime > SweepLegBudgetSeconds)
                    {
                        StopAutoWalk("sweep budget");
                    }

                    return;
                }

                RecordSweepLeg(_sweepCurrent, now);
                _sweepCurrent = null;
                _sweepNextLegTime = now + SweepPauseSeconds;
                return;
            }

            if (now < _sweepNextLegTime)
            {
                return;
            }

            StartNextSweepLeg(now);
        }
        catch (Exception ex)
        {
            LoggerInstance.Warning($"[sweep] stopped by error: {ex}");
            StopSweep("error");
        }
    }

    private void StartSweep()
    {
        if (IsMenuActive() || !TryGetNavigationOrigin(out _))
        {
            SpeakSelectionText("Sweep needs the player in the world");
            return;
        }

        _sweepItems.Clear();
        foreach (var interactable in UnityEngine.Object.FindObjectsOfType<GameInteractableBase>())
        {
            if (interactable == null || !interactable.isActiveAndEnabled || interactable.OculusOnly
                || GetAvailableVerbs(interactable).Count == 0)
            {
                continue;
            }

            _sweepItems.Add(new SweepItem
            {
                Target = interactable.transform,
                Name = GetSpokenName(interactable.transform),
                Path = BuildHierarchyPath(interactable.transform),
            });
        }

        if (_sweepItems.Count == 0)
        {
            SpeakSelectionText("Sweep: no objects to visit");
            return;
        }

        var run = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        _sweepDirectory = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "KillerFrequencyAccess", "sweeps", run);
        Directory.CreateDirectory(_sweepDirectory);
        _sweepWriter = new StreamWriter(System.IO.Path.Combine(_sweepDirectory, "results.jsonl"), false, new UTF8Encoding(false)) { AutoFlush = true };
        _sweepActive = true;
        _sweepCurrent = null;
        _sweepLegCount = 0;
        _sweepStartTime = Time.unscaledTime;
        _sweepNextLegTime = Time.unscaledTime;
        var held = TryGetHeld(out var heldItem, out _) && heldItem != null ? GetSpokenName(heldItem) : "nothing";
        MelonLoader.MelonLogger.Msg($"[sweep] start {run}: {_sweepItems.Count} objects, holding {held}, {(IsCrouching() ? "crouching" : "standing")}, results in {_sweepDirectory}");
        SpeakSelectionText($"Sweep started, {_sweepItems.Count} objects. F8 stops it");
    }

    private void StartNextSweepLeg(float now)
    {
        if (!TryGetNavigationOrigin(out var origin))
        {
            StopSweep("no player");
            return;
        }

        var feet = GetFeet(origin);
        SweepItem? next = null;
        var nextDistance = float.MaxValue;
        foreach (var item in _sweepItems)
        {
            if (item.Arrived || item.Attempts >= SweepMaxAttempts || item.Target == null || !item.Target.gameObject.activeInHierarchy)
            {
                continue;
            }

            var distance = Vector3.Distance(feet, item.Target.position);
            if (distance < nextDistance)
            {
                next = item;
                nextDistance = distance;
            }
        }

        if (next == null)
        {
            StopSweep("every object tried");
            return;
        }

        next.Attempts++;
        _sweepCurrent = next;
        _sweepLegStartTime = now;
        _sweepLegStartFeet = feet;
        _sweepLegCount++;
        _lastWalkStopReason = string.Empty;
        MelonLoader.MelonLogger.Msg($"[sweep] walk {_sweepLegCount}: {next.Name} (attempt {next.Attempts}, {nextDistance:0.0} m away) {next.Path}");
        StartAutoWalkTo(next.Target);
        _sweepLegSpots = _walkStandpoints.Count;
        _sweepLegCorners = _walkCorners.Count;
    }

    private static string SweepOutcome(string reason)
    {
        if (reason.StartsWith("arrived", StringComparison.Ordinal)) return "arrived";
        if (reason.StartsWith("no path", StringComparison.Ordinal) || reason.StartsWith("no route", StringComparison.Ordinal)) return "no_path";
        if (reason.StartsWith("blocked by", StringComparison.Ordinal)) return "blocked";
        if (reason.StartsWith("door", StringComparison.Ordinal)) return "door";
        if (reason.StartsWith("container", StringComparison.Ordinal)) return "container";
        if (reason.StartsWith("no spot worked", StringComparison.Ordinal)) return "aim_failed";
        if (reason == "stuck") return "stalled";
        if (reason == "sweep budget") return "budget";
        if (reason == "target gone") return "target_gone";
        if (reason.Length == 0) return "unknown";
        return "other";
    }

    private void RecordSweepLeg(SweepItem item, float now)
    {
        var reason = _lastWalkStopReason;
        var outcome = SweepOutcome(reason);
        item.LastOutcome = outcome;
        item.LastReason = reason;
        if (outcome == "arrived")
        {
            item.Arrived = true;
        }

        var feet = TryGetNavigationOrigin(out var origin) ? GetFeet(origin) : _sweepLegStartFeet;
        var targetPosition = item.Target != null ? item.Target.position : Vector3.zero;
        var line = new StringBuilder("{");
        AppendJson(line, "walk", _sweepLegCount.ToString(CultureInfo.InvariantCulture), raw: true);
        AppendJson(line, "object", item.Name);
        AppendJson(line, "path", item.Path);
        AppendJson(line, "attempt", item.Attempts.ToString(CultureInfo.InvariantCulture), raw: true);
        AppendJson(line, "outcome", outcome);
        AppendJson(line, "reason", reason);
        AppendJson(line, "seconds", (now - _sweepLegStartTime).ToString("0.0", CultureInfo.InvariantCulture), raw: true);
        AppendJson(line, "start", FormatVector(_sweepLegStartFeet));
        AppendJson(line, "end", FormatVector(feet));
        AppendJson(line, "target", FormatVector(targetPosition));
        AppendJson(line, "moved_m", Vector3.Distance(_sweepLegStartFeet, feet).ToString("0.00", CultureInfo.InvariantCulture), raw: true);
        AppendJson(line, "end_to_target_m", Vector3.Distance(feet, targetPosition).ToString("0.00", CultureInfo.InvariantCulture), raw: true);
        AppendJson(line, "spots", _sweepLegSpots.ToString(CultureInfo.InvariantCulture), raw: true);
        AppendJson(line, "corners", _sweepLegCorners.ToString(CultureInfo.InvariantCulture), raw: true);
        AppendJson(line, "detours", _walkDetours.ToString(CultureInfo.InvariantCulture), raw: true);
        AppendJson(line, "doors_opened", _walkDoorsOpened.ToString(CultureInfo.InvariantCulture), raw: true);
        AppendJson(line, "crouched_at_end", IsCrouching() ? "true" : "false", raw: true, last: true);
        line.Append('}');
        _sweepWriter?.WriteLine(line.ToString());
        MelonLoader.MelonLogger.Msg($"[sweep] walk {_sweepLegCount}: {item.Name} {outcome} ({reason}) in {now - _sweepLegStartTime:0.0} s");

        if (_sweepLegCount % 10 == 0)
        {
            var arrived = _sweepItems.FindAll(i => i.Arrived).Count;
            SpeakSelectionText($"Sweep: {_sweepLegCount} walks, {arrived} of {_sweepItems.Count} objects reached");
        }
    }

    private void StopSweep(string why)
    {
        if (!_sweepActive)
        {
            return;
        }

        _sweepActive = false;
        if (_sweepCurrent != null && _isAutoWalking)
        {
            StopAutoWalk($"sweep stopped: {why}");
        }

        _sweepCurrent = null;
        var minutes = (Time.unscaledTime - _sweepStartTime) / 60f;
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var unreached = new List<SweepItem>();
        var untried = 0;
        foreach (var item in _sweepItems)
        {
            if (item.Attempts == 0)
            {
                untried++;
                continue;
            }

            var key = item.Arrived ? "arrived" : item.LastOutcome;
            counts[key] = counts.TryGetValue(key, out var c) ? c + 1 : 1;
            if (!item.Arrived)
            {
                unreached.Add(item);
            }
        }

        var summary = new StringBuilder();
        summary.AppendLine($"Sweep stopped ({why}) after {_sweepLegCount} walks, {minutes:0.0} minutes. {_sweepItems.Count} objects, {untried} not tried.");
        foreach (var pair in counts)
        {
            summary.AppendLine($"  {pair.Key}: {pair.Value}");
        }

        summary.AppendLine();
        summary.AppendLine("Not reached (last outcome, reason, path):");
        unreached.Sort((a, b) => string.CompareOrdinal(a.LastOutcome + a.Name, b.LastOutcome + b.Name));
        foreach (var item in unreached)
        {
            summary.AppendLine($"  {item.LastOutcome,-12} {item.Name}: {item.LastReason} | {item.Path}");
        }

        try
        {
            File.WriteAllText(System.IO.Path.Combine(_sweepDirectory, "summary.txt"), summary.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            LoggerInstance.Warning($"[sweep] could not write the summary: {ex.Message}");
        }

        _sweepWriter?.Dispose();
        _sweepWriter = null;
        foreach (var text in summary.ToString().Split('\n'))
        {
            if (text.Trim().Length > 0)
            {
                MelonLoader.MelonLogger.Msg($"[sweep] {text.TrimEnd()}");
            }
        }

        var reached = counts.TryGetValue("arrived", out var a) ? a : 0;
        SpeakSelectionText($"Sweep stopped, {why}. {reached} of {_sweepItems.Count - untried} objects tried were reached. Results in the UserData folder");
    }

    private static string FormatVector(Vector3 v)
    {
        return string.Format(CultureInfo.InvariantCulture, "{0:0.00} {1:0.00} {2:0.00}", v.x, v.y, v.z);
    }

    private static void AppendJson(StringBuilder line, string key, string value, bool raw = false, bool last = false)
    {
        line.Append('"').Append(key).Append("\":");
        if (raw)
        {
            line.Append(value);
        }
        else
        {
            line.Append('"');
            foreach (var ch in value)
            {
                switch (ch)
                {
                    case '"': line.Append("\\\""); break;
                    case '\\': line.Append("\\\\"); break;
                    case '\n': line.Append("\\n"); break;
                    case '\r': break;
                    default:
                        if (ch < ' ')
                        {
                            line.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            line.Append(ch);
                        }

                        break;
                }
            }

            line.Append('"');
        }

        if (!last)
        {
            line.Append(',');
        }
    }
}
