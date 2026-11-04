# Killer Frequency Access: project status

Last updated: 2026-11-04

## State

Unreleased. Loads in MelonLoader, speaks through NVDA, reads menus. World navigation
(`KillerFrequencyAccessMod.WorldNavigation.cs`, about 5,600 lines) finds interactables by name
keywords and reflection guesses, and targets the wrong objects. It is being rebuilt, not patched.
The current build finds interactables through `Il2Cpp.InteractableItem`, which is the wrong class
(see Findings), so it reports "no nearby objects".

## Findings (2026-11-04)

- The game is a radio station the player walks around in first person, with desk scopes (mixer,
  pinboard, record drawer). See `docs\game-api.md`.
- The old mod never referenced the game's own assemblies, so it could not ask the game what is
  interactable.
- **Discovery solved from the rip.** The game has two interaction systems in code. The one the
  scenes use is Radio.Actions: every interactable carries `Il2CppActions.Interactables.Interactable`
  (a `ControllerBaseInteractable` / `Il2Cpp.InteractableBase`), with actions from
  `Il2CppInteractables.*` (`PickUpInteraction`, `UseInteraction`, `GraspInteraction`,
  `FsBoundedMotionInteraction`, ...), all deriving from `Il2CppActions.InteractionBase`.
  `Il2Cpp.InteractableItem` and the `RootScope`/`DeskChairScope`/... scopes are never placed in any
  scene or prefab. That is why the runtime count was 0.
- SCN_Main has 804 interactables; SCN_Intro 51, SCN_Epilogue 46, SCN_Menu 6. Full lists with
  each object's scripts: `docs\rip\interactables_*.txt`.
- Every `interactionScope` field in the scenes is null, so scope filtering has to come from the
  runtime (`ControllerActionManager.currentScope`, `InteractionBase.Scope`, `CanPerform`).
- Items have no on-screen name (the reticle is an icon). Names must come from cleaned-up
  GameObject names plus a hand table, or `InteractionBase.Name`/`Description` if they hold text.
- Hover hooks: `ControllerBaseInteractor.observedInteractable`,
  `OnStartHoverOverPrimaryInteraction`, `ControllerActionManager.hoveredPrimaryInteraction`.
- NavMesh: yes. SCN_Main uses `NavMesh_0.asset`, Intro and Menu `NavMesh_1.asset`, Epilogue
  `NavMesh.asset`. No walkable bake of our own is needed.
- SCN_Main has 25 `Room`s, 17 `RoomPortalDoor`s, 59 `DoorOpenable`s.

## Plan

1. Decompiled reference and `docs\game-api.md`. **Done.**
2. Reference `Assembly-CSharp`, `Il2CppRadio.Actions`, `Il2CppRadio.Actions.Core` in the csproj.
   **Done.**
3. Replace discovery with `Il2CppActions.Interactables.Interactable` and its `Interactions`
   (`Available`, `CanPerform` with the player's `ControllerBaseInteractor`). Drop the
   `InteractableItem` code. Name items from cleaned GameObject names plus a table.
4. Announce what the player is pointing at from the interactor's hover
   (`observedInteractable` / `OnStartHoverOverPrimaryInteraction`).
5. Rooms and doors from `Room` / `RoomPortalDoor`.
6. Rip and roster of interactables. **Done** (`docs\rip\`). Positions are not in the roster yet.
7. Logging and a visit-every-object sweep, as in Date Everything Access.
8. Audio guidance, after targeting works. Model it on Date Everything Access (`ObjectTracker.cs`,
   `AccessibilityWatcher.AimCoach.cs`), not Before the Storm (third-person, speech-only clock
   directions): one continuous tone for the selected target. Walking: pulse rate = distance,
   stereo pan = heading error, one-shot chirp at landmarks (doors). Aiming (steady tone): pan =
   horizontal error, pitch = vertical error, chirp when the game's own selection is the target.
   Spoken aim coach as the alternative ("a little up", "far left"). Settings: tone or spoken.
   Replaces the old environment-scan tone code. First-person desk scopes (mixer, pinboard) may
   want aiming help more than route guidance. Paths can use the game's NavMesh.

Commit straight to master; no branches while the mod is unreleased.

## Test log

- 2026-11-04, first InteractableItem build: "no nearby objects", and no `[targets]` log lines.
  Zero results logged nothing (fixed). Rebuilt to always log counts (active and inactive items), the
  missing-origin case, and to list items with any action even when none is AVAILABLE.
- 2026-11-04, rebuilt build: 0 `InteractableItem`s at runtime. Explained by the rip (wrong class).

## Next step

Do step 3: switch discovery to `Il2CppActions.Interactables.Interactable`, with logging kept in.
The user will run test builds when one is needed. Answer anything the rip or decompiled code can
answer offline first; ask for a run only when the answer exists only at runtime.

- Rip: `D:\KFRip\out\ExportedProject\Assets` (17 GB, from the trimmed copy `D:\KFRip\game-lite`).
  `C:` has little free space, so use `D:`.
- Scene tools: `C:\Users\amock\RipWork\kf_scene_tools` (see `docs\game-api.md`, "Scenes").

## Open questions

- What `InteractionBase.Name` and `Description` return (text, an ID, or empty).
- Free walking is the normal state (observed 2026-11-04); a dialog choice blocks it until answered. Which
  other locations set `LocationBase.allowMovement` false is still unknown.
- Which hover hook fires reliably for the focused item.

## Reference mods

- `C:\Users\amock\mod template`: Date Everything Access (rip, nav bake, sweep). Read its `HANDOFF.md`.
- `C:\Users\amock\Before The Storm Access`: Life Is Strange: Before the Storm.
