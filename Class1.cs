using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using MelonLoader;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

[assembly: MelonInfo(typeof(Killer_Frequency_Access.KillerFrequencyAccessMod), "Killer Frequency Access", "0.9.0", "amock")]
[assembly: MelonGame(null, "Killer Frequency")]

namespace Killer_Frequency_Access;

public sealed partial class KillerFrequencyAccessMod : MelonMod
{
    private const float SelectionSpeakDebounceSeconds = 0.18f;
    private const float ObservedInteractableSpeakCooldownSeconds = 0.2f;
    private const float AutoWalkSpeed = 3.3f;
    private const float AutoTurnDegreesPerSecond = 300f;
    private const float InteractionHorizontalToleranceDegrees = 15f;
    private const float InteractionVerticalToleranceDegrees = 12f;

    private readonly List<MonoBehaviour> _cachedNavigationComponents = new();
    private readonly Collider[] _nearbyInteractionColliderBuffer = new Collider[768];
    private bool _isAutoWalking;
    private int _lastObservedInteractableObjectId = -1;
    private string _lastSelectionSpeechKey = string.Empty;
    private float _lastSelectionSpeechTime = -100f;
    private float _lastObservedInteractableSpeakTime = -100f;
    private Transform? _autoWalkTarget;

    public override void OnInitializeMelon()
    {
        InitializeSpeech();
        LoggerInstance.Msg("Killer Frequency Access initialized.");
    }

    public override void OnUpdate()
    {
        HandleObjectNavigationHotkeys();
        UpdateAutoWalk(Time.unscaledDeltaTime);
        UpdateSweep();
        UpdateObservedInteractableAnnouncement();
        // After the observed object's announcement, so the record box is named before its record.
        UpdateRecordBox();
        UpdatePlayerRoomAnnouncement();
        UpdateHeldItemAnnouncement();
        LogPerformingInteractions();
        LogPlaceHandler();
        UpdateDialogArrows();
        UpdateRangeCue();
        // World text first, so a conversation's question is spoken before its first answer.
        UpdateWorldText();
        UpdateMenuSpeech();
    }

    public override void OnApplicationQuit()
    {
        try
        {
            _speechBackend?.Dispose();
            _speechBackend = null;
            _speechContext?.Dispose();
            _speechContext = null;
        }
        catch (System.Exception ex)
        {
            LoggerInstance.Warning($"Prism: shutdown failed: {ex.Message}");
        }
    }

    // Speech and braille through Prism (github.com/ethindp/prism, MPL-2.0, v0.18.4 in native\) by way of
    // the Prism.Net bindings (third_party\Prism.Net, compiled into the mod). Prism drives NVDA, JAWS, ZDSR,
    // PC-Talker, ZoomText, System Access and Narrator, and falls back to the OS voice (SAPI/OneCore).
    // - Loading: the bindings P/Invoke "prism"; a DllImport resolver on this assembly loads the prism.dll
    //   next to the mod in Mods\, which the default search would not find.
    // - Backend: the best one Prism can create. Prism's availability polling (a background thread, every
    //   second, a change confirmed on two samples) reports a screen reader starting or stopping; the next
    //   line spoken re-selects the best backend.
    // - Output: Prism's output (speech and braille). Lines queue in the order spoken unless a caller asks
    //   to interrupt. A failed line is logged, not retried.
    private Prism.Context? _speechContext;
    private Prism.Backend? _speechBackend;
    private int _speechAvailabilityChanged;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _speechAvailabilityLog = new();

    private void InitializeSpeech()
    {
        try
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(KillerFrequencyAccessMod).Assembly.Location) ?? string.Empty, "prism.dll");
            if (!System.IO.File.Exists(path))
            {
                LoggerInstance.Error($"Prism: {path} is missing; speech is disabled.");
                return;
            }

            System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(typeof(KillerFrequencyAccessMod).Assembly,
                (name, _, _) => name == "prism" ? System.Runtime.InteropServices.NativeLibrary.Load(path) : System.IntPtr.Zero);

            var config = Prism.Primitives.PrismConfig.Create();
            config.AvailabilityCallback = (_, name, available) =>
            {
                // Prism's poll thread: only record the change for the main thread.
                _speechAvailabilityLog.Enqueue($"Prism: {name} {(available ? "became available" : "is no longer available")}");
                System.Threading.Interlocked.Exchange(ref _speechAvailabilityChanged, 1);
            };
            _speechContext = new Prism.Context(config);
            LoggerInstance.Msg($"Prism {Prism.PrismInfo.VersionString} loaded from {path}");
            _speechBackend = _speechContext.CreateBest();
            LoggerInstance.Msg($"Speech backend: {_speechBackend.Name}");
        }
        catch (System.Exception ex)
        {
            LoggerInstance.Error($"Prism: initialization failed: {ex}");
        }
    }

    private void SpeakSelectionText(string text) => Speak(text);

    /// <summary>Speaks through Prism; every line also goes to the log as [speech].</summary>
    private void Speak(string text, bool interrupt = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        LoggerInstance.Msg(interrupt ? $"[speech] (interrupt) {text}" : $"[speech] {text}");
        try
        {
            ApplySpeechAvailabilityChanges();
            _speechBackend?.Output(text, interrupt);
        }
        catch (System.Exception ex)
        {
            LogOnce($"speech-error|{ex.GetType().Name}", $"Prism: {_speechBackend?.Name} output failed: {ex.Message}");
        }
    }

    /// <summary>After a screen reader starts or stops, switches to the best backend if that changed it.</summary>
    private void ApplySpeechAvailabilityChanges()
    {
        while (_speechAvailabilityLog.TryDequeue(out var line))
        {
            LoggerInstance.Msg(line);
        }

        if (_speechContext == null || System.Threading.Interlocked.Exchange(ref _speechAvailabilityChanged, 0) == 0)
        {
            return;
        }

        var candidate = _speechContext.CreateBest();
        if (_speechBackend != null && candidate.Name == _speechBackend.Name)
        {
            candidate.Dispose();
            return;
        }

        var previous = _speechBackend?.Name ?? "none";
        _speechBackend?.Dispose();
        _speechBackend = candidate;
        LoggerInstance.Msg($"Speech backend switched: {previous} -> {candidate.Name}");
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

