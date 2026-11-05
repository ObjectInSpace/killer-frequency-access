using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Killer_Frequency_Access;

// Range cue: a rising tone when something becomes usable, a falling tone when it stops being usable.
// "Usable" is the game's own test, not a distance the mod picks:
// - objects: ControllerBaseInteractor.observedInteractable is set (the game's reach + aim check) and at
//   least one of its flat-screen interactions passes Available && CanPerform (the game's own test), or it
//   is a dock whose CanDock accepts the held item (spoken as "Fits ..."). It
//   flickers to none between frames (object-list run), so "out" waits for 0.5 s with nothing observed;
//   moving straight from one object to another stays "in" and only the name is spoken.
// - conversation answers: WorldUIManager.IsChoicesUiInRange() while answers are on screen (the box stays
//   at the speaker: UIAnchorDialog.switchToPlayerWhenOutOfRange is 0 in SCN_Main).
// Unity audio is disabled in this game (ProjectSettings/AudioManager.asset m_DisableAudio: 1), so the
// tones are WAVs played from memory through winmm PlaySound.
public sealed partial class KillerFrequencyAccessMod
{
    private const float ObservedOutOfRangeDelaySeconds = 0.5f;
    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndMemory = 0x0004;

    private static readonly byte[] InRangeWav = BuildToneWav(660, 990);
    private static readonly byte[] OutOfRangeWav = BuildToneWav(990, 660);
    // PlaySound reads the buffer while it plays; pinned for the life of the process.
    private static readonly GCHandle InRangeHandle = GCHandle.Alloc(InRangeWav, GCHandleType.Pinned);
    private static readonly GCHandle OutOfRangeHandle = GCHandle.Alloc(OutOfRangeWav, GCHandleType.Pinned);

    private bool _objectInRange;
    private float _lastObservedForRangeTime = -100f;
    private bool _choicesInRange;

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(IntPtr pszSound, IntPtr hmod, uint fdwSound);

    private void UpdateRangeCue()
    {
        try
        {
            if (IsMenuActive())
            {
                return;
            }

            var now = Time.unscaledTime;
            if (TryGetObservedInteractable(out var observed)
                && (HasPerformableInteraction(observed.transform)
                    || (_placesForHeldIds.Contains(observed.transform.GetInstanceID()) && TryGetHeld(out var held, out _) && held != null)))
            {
                _lastObservedForRangeTime = now;
                if (!_objectInRange)
                {
                    _objectInRange = true;
                    PlayRangeCue(true);
                }
            }
            else if (_objectInRange && now - _lastObservedForRangeTime > ObservedOutOfRangeDelaySeconds)
            {
                _objectInRange = false;
                PlayRangeCue(false);
            }

            var ui = TryGetWorldUi();
            var choicesInRange = ui != null && CountActiveAnswers() > 0 && ui.IsChoicesUiInRange();
            if (choicesInRange != _choicesInRange)
            {
                _choicesInRange = choicesInRange;
                LoggerInstance.Msg($"[range] conversation choices {(choicesInRange ? "in range" : "out of range")}");
                PlayRangeCue(choicesInRange);
            }
        }
        catch (Exception ex)
        {
            LogOnce($"range-error|{ex.GetType().Name}|{ex.Message}", $"[range] error: {ex}");
        }
    }

    private static void PlayRangeCue(bool inRange)
    {
        var handle = inRange ? InRangeHandle : OutOfRangeHandle;
        PlaySound(handle.AddrOfPinnedObject(), IntPtr.Zero, SndAsync | SndMemory | SndNoDefault);
    }

    /// <summary>Two 70 ms sine notes (first, then second frequency), 16-bit mono 22050 Hz, short fades.</summary>
    private static byte[] BuildToneWav(int firstHz, int secondHz)
    {
        const int sampleRate = 22050;
        const int noteSamples = sampleRate * 70 / 1000;
        const int fadeSamples = sampleRate * 8 / 1000;
        const double volume = 0.35;
        var samples = new short[noteSamples * 2];
        for (var note = 0; note < 2; note++)
        {
            var hz = note == 0 ? firstHz : secondHz;
            for (var i = 0; i < noteSamples; i++)
            {
                var envelope = Math.Min(1.0, Math.Min(i, noteSamples - 1 - i) / (double)fadeSamples);
                samples[note * noteSamples + i] = (short)(Math.Sin(2 * Math.PI * hz * i / sampleRate) * envelope * volume * short.MaxValue);
            }
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var dataBytes = samples.Length * 2;
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        foreach (var sample in samples)
        {
            writer.Write(sample);
        }

        writer.Flush();
        return stream.ToArray();
    }
}
