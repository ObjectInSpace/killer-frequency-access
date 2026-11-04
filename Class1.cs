using System.Collections.Generic;
using System.Diagnostics;
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
    private const float RepeatCooldownSeconds = 0.75f;
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
    private const float AutoWalkWaypointRefreshSeconds = 0.18f;
    private const int MaxAutoWalkPathFailureFrames = 6;
    private const float CurrentRoomScanRadiusMeters = 24.0f;
    private const float CurrentRoomMaxVerticalDeltaMeters = 5.5f;
    private const int MaxCurrentRoomTargetCandidates = 40;
    private const float AutoWalkSpeed = 3.3f;
    private const float AutoWalkStopRange = 1.8f;
    private const float AutoTurnDegreesPerSecond = 300f;
    private const float InteractionHorizontalToleranceDegrees = 15f;
    private const float InteractionVerticalToleranceDegrees = 12f;
    private const float EnvironmentScanDistance = 20f;
    private const float EnvironmentScanUpdateIntervalSeconds = 0.08f;
    private const float EnvironmentScanCueGraceSeconds = 0.2f;
    private const float EnvironmentScanCueFadeSeconds = 0.12f;
    private const float PathStallSuppressSeconds = 0.18f;
    private const float PathStallSpeedThreshold = 0.12f;
    private const float PathStallGuideOverrideAngleDegrees = 22f;
    private const float PathGuidanceWaypointReachedDistance = 0.55f;
    private const float PathGuidanceHighToneDistance = 1.0f;
    private const float PathGuidanceMediumToneDistance = 2.1f;
    private const float PathGuidanceWaypointDriftDistance = 0.35f;
    private const float PathGuidanceStallDistanceEpsilon = 0.08f;
    private const int PathGuidanceMaxStalledUpdates = 10;
    private const int MaxRadialPathEvaluationsPerTick = 8;
    private const int UiScanFrameBudgetItems = 120;
    private const float UiScanIntervalMenuSeconds = 0.15f;
    private const float UiScanIntervalGameplaySeconds = 0.6f;
    private const float UiScanIntervalStudioSeconds = 1.0f;
    private const float UiCacheRefreshMenuSeconds = 0.5f;
    private const float UiCacheRefreshGameplaySeconds = 2.0f;
    private const float UiCacheRefreshStudioSeconds = 3.0f;
    private const float SceneLoadGraceSeconds = 2.0f;
    private const float UiVisibleEntryPruneSeconds = 5.0f;
    private const float TargetComponentCacheRefreshGameplaySeconds = 2.0f;
    private const float TargetComponentCacheRefreshStudioSeconds = 4.0f;
    private const float UiScanWarnThresholdMs = 4f;
    private const float TargetRefreshWarnThresholdMs = 12f;
    private const float PerfSummaryIntervalSeconds = 10f;
    private const float PerfWarnCooldownSeconds = 5f;
    private const float DialogInputBufferWindowSeconds = 0.45f;
    private const int WallToneFrequency = 250;
    private const int DoorToneFrequency = 500;
    private const int ObjectToneFrequency = 760;
    private const uint SoundFlagAsync = 0x0001;
    private const uint SoundFlagMemory = 0x0004;
    private const uint SoundFlagNoDefault = 0x0002;
    private static readonly Dictionary<string, string> ScanSummaryLabelOverrides = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<int, string> _lastVisibleTextByObjectId = new();
    private readonly Dictionary<int, float> _lastVisibleObjectSeenAt = new();
    private readonly Dictionary<string, float> _recentUtterances = new();
    private readonly List<ReachableTarget> _reachableTargets = new();
    private readonly Dictionary<int, ActiveScanCue> _activeEnvironmentScanCues = new();
    private readonly Dictionary<int, float> _temporarilyUnreachableTargetUntil = new();
    private readonly List<TMP_Text> _cachedTmpTextLabels = new();
    private readonly List<Text> _cachedLegacyTextLabels = new();
    private readonly List<MonoBehaviour> _cachedNavigationComponents = new();
    private readonly Collider[] _nearbyInteractionColliderBuffer = new Collider[768];
    private GCHandle _continuousScanFieldBufferHandle;
    private AudioClip? _environmentScanLoopedToneClip;
    private AudioSource? _environmentScanTemplateSource;
    private GameObject? _environmentScanCueRoot;
    private float _nextEnvironmentScanUpdateTime = -100f;
    private float _nextUiScanTime = -100f;
    private float _nextUiTextCacheRefreshTime = -100f;
    private float _nextNavigationComponentCacheRefreshTime = -100f;
    private float _nextTargetBackgroundRefreshTime = -100f;
    private float _lastTargetRefreshRequestTime = -100f;
    private float _nextSlowNavigationFallbackTime = -100f;
    private float _lastReachableTargetsRefreshTime = -100f;
    private float _lastNavigationInteractionTime = -100f;
    private float _sceneLoadGraceUntilTime = -100f;
    private float _nextPerfSummaryTime = -100f;
    private float _nextUiScanWarnTime = -100f;
    private float _nextTargetRefreshWarnTime = -100f;
    private float _nextDialogKeyboardBridgeWarmupTime = -100f;
    private bool _hasLastSelectionContext;
    private bool _isAutoWalking;
    private bool _isEnvironmentScanEnabled;
    private bool _hasPathGuidanceWaypoint;
    private int _pathGuidanceStalledUpdates;
    private bool _verticalNavPending;
    private int _verticalNavRetryScans;
    private bool _submitPending;
    private int _lastFocusedControlId;
    private float _lastFocusedControlY;
    private int _selectedTargetIndex = -1;
    private int _lastObservedInteractableObjectId = -1;
    private string _lastSelectionValue = string.Empty;
    private string _lastFocusedValue = string.Empty;
    private string _lastSelectionSpeechKey = string.Empty;
    private float _lastSelectionSpeechTime = -100f;
    private float _lastObservedInteractableSpeakTime = -100f;
    private float _pathGuidanceLastDistance = -1f;
    private float _pathStallElapsed;
    private float _pathStallLastSampleTime = -100f;
    private bool _hasPathStallSample;
    private Vector3 _pathStallLastPosition;
    private Vector3 _pathGuidanceWaypoint;
    private Vector3 _autoWalkWaypoint;
    private float _nextAutoWalkWaypointRefreshTime = -100f;
    private bool _hasAutoWalkWaypoint;
    private bool _autoWalkUseDirectSteering;
    private int _autoWalkPathFailureCount;
    private int _nextTmpUiScanIndex;
    private int _nextLegacyUiScanIndex;
    private int _lastKnownSceneBuildIndex = int.MinValue;
    private string _lastKnownSceneName = string.Empty;
    private int _uiScanPassCount;
    private float _uiScanTotalMilliseconds;
    private int _targetRefreshPassCount;
    private float _targetRefreshTotalMilliseconds;
    private int _findObjectsEnumerationCount;
    private Transform? _autoWalkTarget;
    private Gamepad? _dialogKeyboardVirtualGamepad;
    private float _dialogPendingInputUntilTime = -100f;
    private int _dialogPendingMoveSteps;
    private bool _dialogPendingSubmit;
    private int _dialogPendingOptionNumber;

    public override void OnInitializeMelon()
    {
        SpeechRouter.Initialize(LoggerInstance);
        LoggerInstance.Msg("Accessibility text scanning initialized.");
    }

    public override void OnUpdate()
    {
        UpdateSceneLoadState();
        EnsureDialogKeyboardBridgeReady();

        if (IsVerticalNavigationPressed())
        {
            _verticalNavPending = true;
            _verticalNavRetryScans = 1;
        }
        if (IsSubmitPressed())
        {
            _submitPending = true;
        }

        HandleDialogKeyboardChoiceInput();
        HandleObjectNavigationHotkeys();
        UpdateAutoWalk(Time.unscaledDeltaTime);
        UpdateObservedInteractableAnnouncement();

        var now = Time.unscaledTime;
        var perfMode = GetCurrentPerfMode();
        if (now < _nextUiScanTime)
        {
            PruneExpiredUtterances();
            MaybeLogPerfSummary(now);
            return;
        }

        _nextUiScanTime = now + ResolveUiScanIntervalSeconds(perfMode);
        var uiScanTimer = Stopwatch.StartNew();

        // Run focus speech first, then let bulk scanners honor recent-speech cooldown.
        if (IsSceneLoadGraceActive())
        {
            ScanCurrentSelectionMinimal();
        }
        else
        {
            ScanCurrentSelection();
        }

        var menuActive = IsMenuActive();
        if (!menuActive && !IsSceneLoadGraceActive())
        {
            ScanUiTextIncremental(perfMode, now, UiScanFrameBudgetItems);
            PruneMissingObjectsLazily(now);
        }

        PruneExpiredUtterances();
        uiScanTimer.Stop();
        RecordUiScanMetrics(uiScanTimer.Elapsed.TotalMilliseconds);
        MaybeLogPerfSummary(now);
    }

    public override void OnApplicationQuit()
    {
        StopContinuousEnvironmentScan(true);
        DisposeEnvironmentScanToneSources();
        ReleaseDialogKeyboardVirtualGamepad();
        SpeechRouter.Shutdown();
    }

    private enum ScanCueType
    {
        Wall = 1,
        Door = 2,
        Object = 3
    }

    private enum PerfMode
    {
        Menu,
        Gameplay,
        StudioLike
    }

    private readonly struct ActiveScanCue
    {
        public ActiveScanCue(
            int cueId,
            ScanCueType cueType,
            AudioSource source,
            Vector3 position,
            float distanceMeters,
            float sizeMeters,
            float lastSeenTime)
        {
            CueId = cueId;
            CueType = cueType;
            Source = source;
            Position = position;
            DistanceMeters = distanceMeters;
            SizeMeters = sizeMeters;
            LastSeenTime = lastSeenTime;
        }

        public int CueId { get; }
        public ScanCueType CueType { get; }
        public AudioSource Source { get; }
        public Vector3 Position { get; }
        public float DistanceMeters { get; }
        public float SizeMeters { get; }
        public float LastSeenTime { get; }
    }

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(IntPtr pszSound, IntPtr hmod, uint fdwSound);

    private void UpdateSceneLoadState()
    {
        var activeScene = SceneManager.GetActiveScene();
        if (activeScene.buildIndex == _lastKnownSceneBuildIndex
            && string.Equals(activeScene.name, _lastKnownSceneName, StringComparison.Ordinal))
        {
            return;
        }

        _lastKnownSceneBuildIndex = activeScene.buildIndex;
        _lastKnownSceneName = activeScene.name;
        _sceneLoadGraceUntilTime = Time.unscaledTime + SceneLoadGraceSeconds;
        _nextUiTextCacheRefreshTime = -100f;
        _nextNavigationComponentCacheRefreshTime = -100f;
        _nextTmpUiScanIndex = 0;
        _nextLegacyUiScanIndex = 0;
    }

    private bool IsSceneLoadGraceActive()
    {
        return Time.unscaledTime < _sceneLoadGraceUntilTime;
    }

    private void EndSceneLoadGrace()
    {
        _sceneLoadGraceUntilTime = -100f;
    }

    private PerfMode GetCurrentPerfMode()
    {
        if (IsMenuActive())
        {
            return PerfMode.Menu;
        }

        if (IsSceneLoadGraceActive())
        {
            return PerfMode.StudioLike;
        }

        if (_isAutoWalking
            || _isEnvironmentScanEnabled
            || Time.unscaledTime - _lastNavigationInteractionTime <= TargetNavigationActivityWindowSeconds)
        {
            return PerfMode.Gameplay;
        }

        return PerfMode.StudioLike;
    }

    private static float ResolveUiScanIntervalSeconds(PerfMode mode)
    {
        return mode switch
        {
            PerfMode.Menu => UiScanIntervalMenuSeconds,
            PerfMode.Gameplay => UiScanIntervalGameplaySeconds,
            _ => UiScanIntervalStudioSeconds
        };
    }

    private static float ResolveUiCacheRefreshIntervalSeconds(PerfMode mode)
    {
        return mode switch
        {
            PerfMode.Menu => UiCacheRefreshMenuSeconds,
            PerfMode.Gameplay => UiCacheRefreshGameplaySeconds,
            _ => UiCacheRefreshStudioSeconds
        };
    }

    private static float ResolveSelectionDetailRefreshIntervalSeconds(PerfMode mode)
    {
        return mode switch
        {
            PerfMode.Menu => 0.25f,
            PerfMode.Gameplay => 0.45f,
            _ => 0.75f
        };
    }


    private void ScanUiTextIncremental(PerfMode mode, float now, int totalBudget)
    {
        RefreshUiTextCachesIfNeeded(mode, now);
        if (totalBudget <= 0)
        {
            return;
        }

        var tmpBudget = Mathf.Max(1, totalBudget / 2);
        var legacyBudget = Mathf.Max(1, totalBudget - tmpBudget);
        ScanTmProTextIncremental(now, tmpBudget);
        ScanLegacyUiTextIncremental(now, legacyBudget);
    }

    private void RefreshUiTextCachesIfNeeded(PerfMode mode, float now)
    {
        if (_cachedTmpTextLabels.Count > 0
            && _cachedLegacyTextLabels.Count > 0
            && now < _nextUiTextCacheRefreshTime)
        {
            return;
        }

        _cachedTmpTextLabels.Clear();
        foreach (var label in Resources.FindObjectsOfTypeAll<TMP_Text>())
        {
            _cachedTmpTextLabels.Add(label);
        }

        _cachedLegacyTextLabels.Clear();
        foreach (var label in Resources.FindObjectsOfTypeAll<Text>())
        {
            _cachedLegacyTextLabels.Add(label);
        }

        _findObjectsEnumerationCount += 2;
        _nextUiTextCacheRefreshTime = now + ResolveUiCacheRefreshIntervalSeconds(mode);
        _nextTmpUiScanIndex = 0;
        _nextLegacyUiScanIndex = 0;
    }

    private void ScanTmProTextIncremental(float now, int itemBudget)
    {
        if (_cachedTmpTextLabels.Count == 0 || itemBudget <= 0)
        {
            return;
        }

        for (var processed = 0; processed < itemBudget; processed++)
        {
            if (_nextTmpUiScanIndex >= _cachedTmpTextLabels.Count)
            {
                _nextTmpUiScanIndex = 0;
            }

            var label = _cachedTmpTextLabels[_nextTmpUiScanIndex++];
            if (label == null || !IsVisible(label))
            {
                continue;
            }

            var cleanedText = NormalizeText(label.text);
            if (string.IsNullOrWhiteSpace(cleanedText))
            {
                continue;
            }

            var id = label.GetInstanceID();
            _lastVisibleObjectSeenAt[id] = now;
            if (_lastVisibleTextByObjectId.TryGetValue(id, out var existing) && existing == cleanedText)
            {
                continue;
            }

            _lastVisibleTextByObjectId[id] = cleanedText;
            SpeakIfNotRecent(cleanedText);
        }
    }

    private void ScanLegacyUiTextIncremental(float now, int itemBudget)
    {
        if (_cachedLegacyTextLabels.Count == 0 || itemBudget <= 0)
        {
            return;
        }

        for (var processed = 0; processed < itemBudget; processed++)
        {
            if (_nextLegacyUiScanIndex >= _cachedLegacyTextLabels.Count)
            {
                _nextLegacyUiScanIndex = 0;
            }

            var label = _cachedLegacyTextLabels[_nextLegacyUiScanIndex++];
            if (label == null || !IsVisible(label))
            {
                continue;
            }

            var cleanedText = NormalizeText(label.text);
            if (string.IsNullOrWhiteSpace(cleanedText))
            {
                continue;
            }

            var id = label.GetInstanceID();
            _lastVisibleObjectSeenAt[id] = now;
            if (_lastVisibleTextByObjectId.TryGetValue(id, out var existing) && existing == cleanedText)
            {
                continue;
            }

            _lastVisibleTextByObjectId[id] = cleanedText;
            SpeakIfNotRecent(cleanedText);
        }
    }

    private void PruneMissingObjectsLazily(float now)
    {
        if (_lastVisibleTextByObjectId.Count == 0)
        {
            return;
        }

        var staleIds = new List<int>();
        foreach (var pair in _lastVisibleTextByObjectId)
        {
            if (_lastVisibleObjectSeenAt.TryGetValue(pair.Key, out var seenAt)
                && now - seenAt <= UiVisibleEntryPruneSeconds)
            {
                continue;
            }

            staleIds.Add(pair.Key);
        }

        for (var i = 0; i < staleIds.Count; i++)
        {
            var staleId = staleIds[i];
            _lastVisibleTextByObjectId.Remove(staleId);
            _lastVisibleObjectSeenAt.Remove(staleId);
        }
    }

    private void RecordUiScanMetrics(double elapsedMs)
    {
        _uiScanPassCount++;
        _uiScanTotalMilliseconds += (float)elapsedMs;
        if (elapsedMs > UiScanWarnThresholdMs && Time.unscaledTime >= _nextUiScanWarnTime)
        {
            LoggerInstance.Warning($"UI scan cost {elapsedMs:0.00} ms");
            _nextUiScanWarnTime = Time.unscaledTime + PerfWarnCooldownSeconds;
        }
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

    private void MaybeLogPerfSummary(float now)
    {
        if (!UnityEngine.Debug.isDebugBuild || now < _nextPerfSummaryTime)
        {
            return;
        }

        _nextPerfSummaryTime = now + PerfSummaryIntervalSeconds;
        var avgUiMs = _uiScanPassCount > 0 ? _uiScanTotalMilliseconds / _uiScanPassCount : 0f;
        var avgTargetMs = _targetRefreshPassCount > 0 ? _targetRefreshTotalMilliseconds / _targetRefreshPassCount : 0f;
        LoggerInstance.Msg(
            $"Perf summary: ui_passes={_uiScanPassCount} ui_avg_ms={avgUiMs:0.00} " +
            $"target_passes={_targetRefreshPassCount} target_avg_ms={avgTargetMs:0.00} " +
            $"findobjects_calls={_findObjectsEnumerationCount}");
    }

    private void MarkNavigationInteractionActive()
    {
        _lastNavigationInteractionTime = Time.unscaledTime;
        EndSceneLoadGrace();
    }

    private bool ShouldRunNavigationBackgroundRefresh(float now)
    {
        if (now < _nextTargetBackgroundRefreshTime)
        {
            return false;
        }

        if (GetCurrentPerfMode() == PerfMode.StudioLike)
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


    private void SpeakIfNotRecent(string text)
    {
        if (IsMenuActive()
            && _hasLastSelectionContext
            && (string.Equals(text, _lastFocusedValue, System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, _lastSelectionValue, System.StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        if (_recentUtterances.TryGetValue(text, out var lastSpokenAt)
            && Time.unscaledTime - lastSpokenAt < RepeatCooldownSeconds)
        {
            return;
        }

        _recentUtterances[text] = Time.unscaledTime;
        SpeechRouter.Speak(text);
    }

    private void SpeakSelectionText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // Prevent bulk scanners from repeating selection speech in the same frame window.
        _recentUtterances[text] = Time.unscaledTime;
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

    private void MarkRecentSelectionValue(string valueText)
    {
        if (string.IsNullOrWhiteSpace(valueText))
        {
            return;
        }

        _recentUtterances[valueText] = Time.unscaledTime;
        _recentUtterances[NormalizeText(valueText)] = Time.unscaledTime;
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

    private void PruneExpiredUtterances()
    {
        if (_recentUtterances.Count == 0)
        {
            return;
        }

        var stale = new List<string>();
        foreach (var (text, timeSpoken) in _recentUtterances)
        {
            if (Time.unscaledTime - timeSpoken > RepeatCooldownSeconds)
            {
                stale.Add(text);
            }
        }

        foreach (var text in stale)
        {
            _recentUtterances.Remove(text);
        }

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

        var decoded = Regex.Replace(input, "<.*?>", " ");
        decoded = decoded.Replace("\r", " ").Replace("\n", " ");
        decoded = Regex.Replace(decoded, "\\s+", " ");
        return decoded.Trim();
    }
}

