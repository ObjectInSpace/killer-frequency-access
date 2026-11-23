using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using MelonLoader;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

[assembly: MelonInfo(typeof(Killer_Frequency_Access.KillerFrequencyAccessMod), "Killer Frequency Access", "0.95.0", "amock")]
[assembly: MelonGame(null, "Killer Frequency")]

namespace Killer_Frequency_Access;

public sealed partial class KillerFrequencyAccessMod : MelonMod
{
    private const float ObservedInteractableSpeakCooldownSeconds = 0.2f;
    private const float AutoWalkSpeed = 3.3f;
    private const float AutoTurnDegreesPerSecond = 300f;
    private const float InteractionHorizontalToleranceDegrees = 15f;
    private const float InteractionVerticalToleranceDegrees = 12f;

    private readonly List<MonoBehaviour> _cachedNavigationComponents = new();
    private readonly Collider[] _nearbyInteractionColliderBuffer = new Collider[768];
    private bool _isAutoWalking;
    private int _lastObservedInteractableObjectId = -1;
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
        ShutdownSpeech();
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

