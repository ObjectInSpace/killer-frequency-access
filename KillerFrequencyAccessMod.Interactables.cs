using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using GameActionStatus = Il2Cpp.ActionStatus;
using GameInteractableItem = Il2Cpp.InteractableItem;

namespace Killer_Frequency_Access;

/// <summary>
/// Finds interactable objects by asking the game, not by guessing from names.
/// Every interactable in Killer Frequency carries an Il2Cpp.InteractableItem (see docs/game-api.md).
/// </summary>
public sealed partial class KillerFrequencyAccessMod
{
    /// <summary>Spoken label for each target, keyed by Transform instance id. Filled by <see cref="CollectGameInteractables"/>.</summary>
    private static readonly Dictionary<int, string> GameTargetLabels = new();

    private bool _loggedMissingOrigin;
    private string _lastInteractableLogSignature = "<never>";

    /// <summary>
    /// Adds every InteractableItem that currently has an available action to <paramref name="candidates"/>.
    /// Items that are inactive, stored, collider-less or have nothing available are skipped, and every
    /// decision is written to the log (once per change) so a play session shows what the game reported.
    /// </summary>
    private void CollectGameInteractables(Dictionary<int, Transform> candidates)
    {
        GameTargetLabels.Clear();

        var items = UnityEngine.Object.FindObjectsOfType<GameInteractableItem>();
        var everything = Resources.FindObjectsOfTypeAll<GameInteractableItem>();
        var log = new StringBuilder();
        var signature = new StringBuilder();

        foreach (var item in items)
        {
            if (item == null)
            {
                continue;
            }

            var itemTransform = item.transform;
            var scopeName = DescribeScope(item);
            var objectName = HumanizeEnum(item._stringHandle.ToString(), item.gameObject.name);

            string skipReason = string.Empty;
            var availableActions = new List<string>();
            var allActions = new List<string>();

            if (!item.gameObject.activeInHierarchy)
            {
                skipReason = "inactive";
            }
            else if (item.stored)
            {
                skipReason = "stored";
            }
            else if (item.InteractableCollider == null)
            {
                skipReason = "no collider";
            }

            try
            {
                item.GetInteractionStatuses(out var ids, out var statuses);
                var count = ids == null ? 0 : ids.Count;
                for (var i = 0; i < count && statuses != null && i < statuses.Count; i++)
                {
                    var actionName = HumanizeEnum(ids![i].ToString(), string.Empty);
                    allActions.Add($"{actionName}={statuses[i]}");
                    if (statuses[i] == GameActionStatus.AVAILABLE)
                    {
                        availableActions.Add(actionName);
                    }
                }
            }
            catch (Exception ex)
            {
                if (skipReason.Length == 0)
                {
                    skipReason = $"status error: {ex.Message}";
                }
            }

            if (skipReason.Length == 0 && allActions.Count == 0)
            {
                skipReason = "no actions";
            }

            var id = itemTransform.GetInstanceID();
            var picked = skipReason.Length == 0;
            if (picked)
            {
                candidates[id] = itemTransform;
                GameTargetLabels[id] = availableActions.Count > 0
                    ? $"{objectName}, {availableActions[0]}"
                    : $"{objectName}, not available now";
            }

            signature.Append(id).Append(picked ? '+' : '-').Append(availableActions.Count).Append(';');
            log.Append("  ")
                .Append(picked ? "[+] " : "[-] ")
                .Append(objectName)
                .Append(" (").Append(itemTransform.name).Append(')')
                .Append(" scope=").Append(scopeName)
                .Append(" actions=[").Append(string.Join(", ", allActions)).Append(']');
            if (!picked)
            {
                log.Append(" skipped: ").Append(skipReason);
            }
            log.AppendLine();
        }

        var newSignature = signature.ToString();
        if (!string.Equals(newSignature, _lastInteractableLogSignature, StringComparison.Ordinal))
        {
            _lastInteractableLogSignature = newSignature;
            LoggerInstance.Msg($"[targets] {GameTargetLabels.Count} usable of {items.Length} InteractableItems:\n{log}");
        }
    }

    private static string DescribeScope(GameInteractableItem item)
    {
        try
        {
            var scope = item.Scope;
            return scope == null ? "none" : scope.GetType().Name;
        }
        catch
        {
            return "?";
        }
    }

    /// <summary>"PICK_UP" becomes "pick up". NONE (or empty) falls back to <paramref name="fallback"/>.</summary>
    private static string HumanizeEnum(string enumName, string fallback)
    {
        if (string.IsNullOrWhiteSpace(enumName) || string.Equals(enumName, "NONE", StringComparison.Ordinal))
        {
            return string.IsNullOrWhiteSpace(fallback) ? "object" : ReadableObjectName(fallback);
        }

        return enumName.Replace('_', ' ').ToLowerInvariant();
    }
}
