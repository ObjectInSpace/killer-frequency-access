# Killer Frequency Access: project status

Last updated: 2026-11-04

## State

Unreleased. Loads in MelonLoader, speaks through NVDA, reads menus. World navigation
(`KillerFrequencyAccessMod.WorldNavigation.cs`, about 5,600 lines) finds interactables by name
keywords and reflection guesses, and targets the wrong objects. It is being rebuilt, not patched.

## Findings (2026-11-04)

- The game has 16 object types and a small set of scopes (walking, seated, mixer, pinboard,
  record drawer). It is a desk, not an open world. See `docs\game-api.md`.
- The old mod never referenced the game's own assemblies, so it could not ask the game what is
  interactable.

## Plan

1. Decompiled reference and `docs\game-api.md`. **Done.**
2. Reference `Assembly-CSharp`, `Il2CppRadio.Actions`, `Il2CppRadio.Actions.Core` in the csproj.
3. Replace the keyword and collider guessing with `InteractableItem` plus
   `GetInteractionStatuses`, filtered by the current scope. Name items from the game's strings.
4. Hook what the game shows on screen so the focused item is announced as the game labels it.
5. Rooms and doors from `Room` / `RoomPortalDoor`.
6. Rip `SCN_Main` with AssetRipper (pipeline in `C:\Users\amock\RipWork`) for a complete roster.
7. Logging and a visit-every-object sweep, as in Date Everything Access.
8. Audio guidance, after targeting works. Model it on Date Everything Access (`ObjectTracker.cs`,
   `AccessibilityWatcher.AimCoach.cs`), not Before the Storm (third-person, speech-only clock
   directions): one continuous tone for the selected target. Walking: pulse rate = distance,
   stereo pan = heading error, one-shot chirp at landmarks (doors). Aiming (steady tone): pan =
   horizontal error, pitch = vertical error, chirp when the game's own selection is the target.
   Spoken aim coach as the alternative ("a little up", "far left"). Settings: tone or spoken.
   Replaces the old environment-scan tone code. First-person desk scopes (mixer, pinboard) may
   want aiming help more than route guidance.

Commit straight to master; no branches while the mod is unreleased.

## Test log

- 2026-11-04, first InteractableItem build: "no nearby objects", and no `[targets]` log lines.
  Zero results logged nothing (fixed). Rebuilt to always log counts (active and inactive items), the
  missing-origin case, and to list items with any action even when none is AVAILABLE.

## Open questions

- Does `SCN_Main` have a NavMesh? Decides whether pathing needs our own walkable bake.
- Free walking is the normal state (observed 2026-11-04); a dialog choice blocks it until answered. Which
  other locations set `LocationBase.allowMovement` false is still unknown.
- Which UI hook fires reliably for the focused item.

## Reference mods

- `C:\Users\amock\mod template`: Date Everything Access (rip, nav bake, sweep). Read its `HANDOFF.md`.
- `C:\Users\amock\Before The Storm Access`: Life Is Strange: Before the Storm.
