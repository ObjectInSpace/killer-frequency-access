using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using MelonLoader;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

[assembly: MelonInfo(typeof(Killer_Frequency_Access.KillerFrequencyAccessMod), "Killer Frequency Access", "0.1.0", "amock")]
[assembly: MelonGame(null, "Killer Frequency")]

namespace Killer_Frequency_Access;

public sealed partial class KillerFrequencyAccessMod : MelonMod
{
    private const float SelectionSpeakDebounceSeconds = 0.18f;
    private const float ObservedInteractableSpeakCooldownSeconds = 0.2f;
    private const float TargetRefreshDebounceSeconds = 0.25f;
    private const float SlowNavigationFallbackCooldownSeconds = 0.9f;
    private const float UnreachableTargetRetrySeconds = 2.0f;
    private const float TargetBackgroundRefreshSeconds = 8.0f;
    private const float TargetNavigationActivityWindowSeconds = 12.0f;
    private const float TargetListFreshnessSeconds = 1.25f;
    private const int MaxWalkableTargetsPerRefresh = 14;
    private const int MaxPathValidationAttemptsPerRefresh = 24;
    private const float CurrentRoomMaxVerticalDeltaMeters = 5.5f;
    private const int MaxCurrentRoomTargetCandidates = 40;
    private const float AutoWalkSpeed = 3.3f;
    private const float AutoTurnDegreesPerSecond = 300f;
    private const float InteractionHorizontalToleranceDegrees = 15f;
    private const float InteractionVerticalToleranceDegrees = 12f;
    private const float TargetComponentCacheRefreshGameplaySeconds = 2.0f;
    private const float TargetComponentCacheRefreshStudioSeconds = 4.0f;
    private const float TargetRefreshWarnThresholdMs = 12f;
    private const float PerfWarnCooldownSeconds = 5f;

    private readonly List<ReachableTarget> _reachableTargets = new();
    private readonly Dictionary<int, float> _temporarilyUnreachableTargetUntil = new();
    private readonly List<MonoBehaviour> _cachedNavigationComponents = new();
    private readonly Collider[] _nearbyInteractionColliderBuffer = new Collider[768];
    private float _nextNavigationComponentCacheRefreshTime = -100f;
    private float _nextTargetBackgroundRefreshTime = -100f;
    private float _lastTargetRefreshRequestTime = -100f;
    private float _nextSlowNavigationFallbackTime = -100f;
    private float _lastReachableTargetsRefreshTime = -100f;
    private float _lastNavigationInteractionTime = -100f;
    private float _nextTargetRefreshWarnTime = -100f;
    private bool _isAutoWalking;
    private int _selectedTargetIndex = -1;
    private int _lastObservedInteractableObjectId = -1;
    private string _lastSelectionSpeechKey = string.Empty;
    private float _lastSelectionSpeechTime = -100f;
    private float _lastObservedInteractableSpeakTime = -100f;
    private int _targetRefreshPassCount;
    private float _targetRefreshTotalMilliseconds;
    private Transform? _autoWalkTarget;

    public override void OnInitializeMelon()
    {
        SpeechRouter.Initialize(LoggerInstance);
        LoggerInstance.Msg("Killer Frequency Access initialized.");
    }

    public override void OnUpdate()
    {
        HandleObjectNavigationHotkeys();
        UpdateAutoWalk(Time.unscaledDeltaTime);
        UpdateObservedInteractableAnnouncement();
        UpdatePlayerRoomAnnouncement();
        UpdateHeldItemAnnouncement();
        LogPerformingInteractions();
        UpdateDialogArrows();
        UpdateRangeCue();
        // World text first, so a conversation's question is spoken before its first answer.
        UpdateWorldText();
        UpdateMenuSpeech();
    }

    public override void OnApplicationQuit()
    {
        SpeechRouter.Shutdown();
    }

    private void RecordTargetRefreshMetrics(double elapsedMs)
    {
        _targetRefreshPassCount++;
        _targetRefreshTotalMilliseconds += (float)elapsedMs;
        if (elapsedMs > TargetRefreshWarnThresholdMs && Time.unscaledTime >= _nextTargetRefreshWarnTime)
        {
            LoggerInstance.Warning($"Target refresh cost {elapsedMs:0.00} ms");
            _nextTargetRefreshWarnTime = Time.unscaledTime + PerfWarnCooldownSeconds;
        }
    }

    private void MarkNavigationInteractionActive()
    {
        _lastNavigationInteractionTime = Time.unscaledTime;
    }

    private bool ShouldRunNavigationBackgroundRefresh(float now)
    {
        if (now < _nextTargetBackgroundRefreshTime)
        {
            return false;
        }

        return now - _lastNavigationInteractionTime <= TargetNavigationActivityWindowSeconds;
    }

    private bool CanRunOnDemandTargetRefresh(float now)
    {
        if (now - _lastTargetRefreshRequestTime < TargetRefreshDebounceSeconds)
        {
            return false;
        }

        _lastTargetRefreshRequestTime = now;
        return true;
    }

    private bool CanRunSlowNavigationFallback(float now)
    {
        if (now < _nextSlowNavigationFallbackTime)
        {
            return false;
        }

        _nextSlowNavigationFallbackTime = now + SlowNavigationFallbackCooldownSeconds;
        return true;
    }


    private void SpeakSelectionText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        SpeechRouter.Speak(text);
    }

    private void SpeakSelectionTextDedup(string dedupeKey, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (string.Equals(_lastSelectionSpeechKey, dedupeKey, System.StringComparison.Ordinal)
            && Time.unscaledTime - _lastSelectionSpeechTime < SelectionSpeakDebounceSeconds)
        {
            return;
        }

        _lastSelectionSpeechKey = dedupeKey;
        _lastSelectionSpeechTime = Time.unscaledTime;
        SpeakSelectionText(text);
    }

    private static bool IsKeyDownSafe(Key primaryKey, Key? secondaryKey = null)
    {
        var keyboard = Keyboard.current;
        if (keyboard == null)
        {
            return false;
        }

        if (keyboard[primaryKey].wasPressedThisFrame)
        {
            return true;
        }

        if (secondaryKey.HasValue && keyboard[secondaryKey.Value].wasPressedThisFrame)
        {
            return true;
        }

        return false;
    }

    private static bool IsVisible(MaskableGraphic graphic)
    {
        if (graphic == null || !graphic.isActiveAndEnabled || graphic.canvasRenderer == null || graphic.canvasRenderer.cull)
        {
            return false;
        }

        if (graphic.color.a <= 0.001f)
        {
            return false;
        }

        return HasVisibleCanvasGroupChain(graphic.transform);
    }

    private static bool HasVisibleCanvasGroupChain(Transform start)
    {
        var current = start;
        while (current != null)
        {
            var groups = current.GetComponents<CanvasGroup>();
            foreach (var group in groups)
            {
                if (!group.enabled)
                {
                    continue;
                }

                if (group.alpha <= 0.001f)
                {
                    return false;
                }

                if (group.ignoreParentGroups)
                {
                    return true;
                }
            }

            current = current.parent;
        }

        return true;
    }

    private static string NormalizeText(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        // Button glyphs are sprite tags; speak the control they show before other tags are stripped.
        var decoded = Regex.Replace(SpeakGlyphs(input), "<.*?>", " ");
        decoded = decoded.Replace("\r", " ").Replace("\n", " ");
        decoded = Regex.Replace(decoded, "\\s+", " ");
        return decoded.Trim();
    }
}

