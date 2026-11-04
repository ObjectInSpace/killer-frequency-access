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

Commit straight to master; no branches while the mod is unreleased.

## Open questions

- Does `SCN_Main` have a NavMesh? Decides whether pathing needs our own walkable bake.
- Free walking is the normal state (observed 2026-11-04); a dialog choice blocks it until answered. Which
  other locations set `LocationBase.allowMovement` false is still unknown.
- Which UI hook fires reliably for the focused item.

## Reference mods

- `C:\Users\amock\mod template`: Date Everything Access (rip, nav bake, sweep). Read its `HANDOFF.md`.
- `C:\Users\amock\Before The Storm Access`: Life Is Strange: Before the Storm.
