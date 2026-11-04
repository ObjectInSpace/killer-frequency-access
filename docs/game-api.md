# Killer Frequency: game API notes

What the mod can rely on in the game. Everything here comes from the interop assemblies in
`<GameDir>\MelonLoader\Il2CppAssemblies`, decompiled with `ilspycmd -p` into `decompiled\`
(git-ignored; regenerate after a game update, see "Sources of truth"). These assemblies give
types, fields and signatures only. They do not contain method bodies, so **behaviour below is
marked "unverified" until it has been seen in game.**

Game: Unity 2021.3.5f1, IL2CPP, game version 454, MelonLoader 0.7.1.
Interop namespaces carry an `Il2Cpp` prefix (`Il2CppActions.Interactables.Interactable`,
`Il2CppInteractables.PickUpInteraction`).

## How interaction works (verified from the rip, 2026-11-04)

The game has **two** interaction systems in its code. Only one is used.

**Used: the Radio.Actions system.** Every interactable in every scene is this one (see
`docs\rip\interactables_*.txt` for the full lists: SCN_Main 804, SCN_Intro 51, SCN_Epilogue 46,
SCN_Menu 6).

- **`Il2CppActions.Interactables.Interactable`** (Radio.Actions, derives from
  `ControllerBaseInteractable` -> `Il2Cpp.InteractableBase` in Radio.Actions.Core) is the component
  every interactable carries. Find them with `FindObjectsOfType<Il2CppActions.Interactables.Interactable>()`
  or `InteractableBase`.
  - Fields: `m_Colliders` (list), `fsMaxReachDistance` (1.2 on almost everything), `FsOnly`,
    `OculusOnly`, `highlightRootOverride`. `Interactions` is its `List<Il2CppActions.InteractionBase>`.
- **`Il2CppActions.InteractionBase`** (Radio.Actions.Core) is one action. Members: `Name`,
  `Description`, `Priority`, `Available`, `Highlightable`, `Interactable`, `Scope`,
  `interactionScope` (an `InteractionScope` ScriptableObject; **null on every interaction in the
  scenes**), `fsCustomReticle` (an icon sprite only, no text), `CanPerform(InteractorBase)`,
  `Perform(InteractorBase)`, `IsPerforming()`.
- The concrete actions are in **`Il2CppInteractables`** (Assembly-CSharp), for example
  `PickUpInteraction`, `FsInspectInteraction`, `FsPlaceInteraction`, `DropInteraction`,
  `FsThrowInteraction`, `FsSwapInHandsInteraction`, `DockInteraction`/`UndockInteraction` (on
  `Holder`s), `GraspInteraction`/`ReleaseInteraction` (on `Handle`s), `FsBoundedMotionInteraction`
  (doors with `DoorOpenable`, drawers, sliders), `UseInteraction` (with `Button`),
  `TouchInteraction`, `CollectInteraction` (keys, record sleeves), `RedockInteraction` (pinboard
  `BoardItem`s), `TakeInteraction`, `FsPeekInteraction`.
- Combinations in SCN_Main: 364 physics props (pick up, inspect, place, drop, throw, swap),
  107 holders (dock/undock), 98 handles (grasp/release), 61 board items, 58 doors, 44 buttons,
  37 other bounded-motion objects, 18 touch, 10 collectables.
- The player side: **`ControllerBaseInteractor`** (`observedInteractable`, `heldInteractables`,
  `GetPrimaryHeldInteractable()`, `PerformingInteractions`, `OnStartHoverOverPrimaryInteraction`/
  `OnEndHoverOverPrimaryInteraction(InteractionBase)`, `interactorCamera`, `raycastHitDistance`)
  and **`Il2CppActions.ControllerActionManager`** (`hoveredPrimaryInteraction`, `cachedInteractions`,
  `currentScope`, `controllerInteractor`). These are the hooks for "what is the player pointing at".
  `PlayerInteractionHighlighter.Highlight` also takes the Radio types.
- **Names:** nothing on screen names an item; the reticle is an icon, and the rip's localization
  tables are UI text only. The only names are the GameObject names
  (`P_HeroProp_Interactable_SaveClive_PoliceReport`, `Button_Play_FS`). The mod speaks names from
  `Generated\GameObjectNames.cs`, generated from the rip by `tools\rip\gen_names.py`.
  `InteractionBase.Name` / `Description`: unverified what they return; the mod logs them.
- **Player:** one `PlayerController` GameObject per scene carries the `CharacterController`, the
  `ControllerBaseInteractor`, `ControllerActionManager`, `FlatScreenPlayer` and the Fs*Handler
  components.

**Unused: the `Il2Cpp.InteractableItem` system** described below. It is in Assembly-CSharp, but no
scene or prefab in the rip contains `InteractableItem`, `RootScope`, `DeskChairScope` or any other
`InteractionScopeBase`, and at runtime `FindObjectsOfType<Il2Cpp.InteractableItem>()` returned 0.
Do not build on it. Kept for reference:

- **`Il2Cpp.InteractableItem`** (MonoBehaviour), meant as the component every interactable carries.
  - `InteractableCollider` is its collider; `Scope` is the `InteractionScopeBase` it currently
    belongs to; `stored` marks items put away.
  - `_stringHandle` (`ObjectStringHandle`) is the item's name. See "Names" below.
  - `GetInteractionsCount()`, `GetInteraction(int)` and `GetInteraction(ActionId)` list its actions.
  - `GetInteractionStatuses(out List<ActionId>, out List<ActionStatus>)` gives what is possible
    right now. `ActionStatus` is `GREYED_OUT`, `UNAVAILABLE`, `AVAILABLE`.
- **`Il2Cpp.InteractionBase`** (MonoBehaviour) is one action on an item. `GetProperties()` returns
  an `ActionProperties` (`Id`, `InputCategory`, `InputType`, `StringHandle`). `IsVisible()`,
  `IsAvailable()`, `GetStatus()`, `Trigger()`.
- **`ActionId`** values: PLACE, DROP, THROW, ENTER/EXIT_CONSOLE_MIXER, ENTER/EXIT_PINBOARD,
  ENTER/EXIT_RECORD_DRAWER, INSERT_CARTRIDGE_INTO_PLAYER, INSERT_RECORD_INTO_PLAYER, INSPECT,
  INVENTORY, KICK, OPEN_DOOR_AND_ENTER, PICK_UP, PRESS_BUTTON, SIT_DOWN, STAND_UP, STOW,
  SWITCH_OFF, SWITCH_ON, TAKE_OUT_CARTRIDGE/RECORD_FROM_PLAYER, UNLOCK, UNSTOW, USE_SLIDER, ZOOM.
- **Scopes** (`InteractionScopeBase`, with `_scopeItems`, `_actionFilter`, `_exitInteraction`) limit
  which items and actions are live. The player moves between scopes: `RootScope` (walking),
  `DeskChairScope` (seated), `ConsoleMixerScope`, `PinboardScope`, `RecordDrawerScope`. Entering one
  is an interaction (`EnterPinboardInteraction`, `SitDownInteraction`, ...), leaving is another.
  **This is why a flat "everything near me" list is wrong: items outside the current scope are not
  usable even when they are close.**
- **Docks** (`InteractionDockBase`: `TableDock`, `PinboardDock`, `RecordDrawerDock`,
  `SingleItemDock`, `CartridgeSingleItemDock`, `RecordSleeveSingleItemDock`) are the places items
  are put.
- **`Il2Cpp.InteractionHandler`** is the player's scanner (`Instance()`, `ScanForInteractableItem()`).
  `IsObjectWithinInterationDistance(Collider)` is spelled with one "c". Held items:
  `AddHeldItem`, `RemoveHeldItem`, `GetCarriedItem`.

## Names (unused system)

`ObjectStringHandle` has only **16 values**: BALL, BOX, CARTRIDGE, CARTRIDGE_PLAYER,
CARTRIDGE_RACK, CHAIR, DOOR, KEY, MIXER, PHONE, PIN, PIN_BOARD, RECORD, RECORD_DRAWER,
RECORD_PLAYER, SLIDER. `ActionStringHandle` has about 60 (ANSWER_LINE_1..3, ADJUST_VOLUME_*,
CHANGE_CARTRIDGE, CHECK_OUT_PINBOARD, ...).

Both are plain enums, so the enum name is a stable key. Turning it into the on-screen text goes
through Unity Localization string tables (`localization-string-tables-english(en)` bundle).
**Unverified:** which table and key format the UI uses. The reliable route is to read the text the
game itself puts on screen (below) and keep an enum-name fallback table in the mod.

## What the game shows on screen

For the system the game uses, see the interactor and action manager hooks above. The UI below
belongs to the `InteractableItem` system (its `UIData` uses the same string handles), although one
`InteractableUIManager` is present in SCN_Main; whether it is ever fed data is unverified.

`Il2CppUI.Interactable.InteractableUIManager.SetUIData(ref UIData)` is called when the highlighted
item changes. `UIData` has `itemTitles` (`UIItemTitle`: `stringHandle`, `valid`) and `actions`
(`UIAction`: `stringHandle`, `inputCategory`, `inputType`, `greyedOut`, `valid`). Panels:
`_leftPanel`, `_middlePanel`, `_rightPanel`; `ToggleUI(bool)`, `Hidden`.
`PlayerInteractionHighlighter.Highlight(InteractionBase, InteractorBase)` / `UnHighlight()` mark
the focused item. Either is a candidate hook for "what is the player pointing at".
**Unverified:** which of these fires reliably in game; patching `SetUIData` involves a `ref` struct.

## Player location and movement

`Il2CppPlayer.Movement.LocationBase` (`debugName`, `interactionScope`, `allowMovement`;
`RootLocation` static) and `MoveTarget.FixedLocation` (`_id`, `_position`, `_interactionScope`)
tie a position to an interaction scope. `MoveTarget` is a MonoBehaviour that wraps a location;
`MoveToLocationInteraction` (`target`) moves the player to it. So the player can be in a location
that disallows free walking (probably seated at the desk, in the mixer, at the pinboard).
**Observed in game (user, 2026-11-04):** the player starts able to move around freely. The
current save begins on a dialog choice, and walking is only allowed after it. So free walking is
the normal state, and dialogs (and probably seated or console scopes) suspend it.
**Unverified:** which other locations set `allowMovement` false.

## Rooms and doors

- `Il2Cpp.Room` (`name`, `root`, `volumes` bounds array), `RoomPortal` (`_frontRoom`, `_backRoom`,
  `_size`, `_plane`), `RoomPortalDoor : RoomPortal` (`_door`, `active`, `isOpen`, `openness`).
- Doors: `DoorOpenable`, `HingedDoor`, `SelfClosingDoor`, `VerticalSlidingDoor`, `ADoorLock`,
  `DoorLocked`. In the scenes a door is a `DoorOpenable` plus `FsBoundedMotionInteraction` (the
  player drags it); `OpenDoorAndEnterInteraction` belongs to the unused system.
- `RoomAmbienceZone`, `DoorRoomSelector` exist and may help name the current room (unverified).

## Scenes

Build scenes: `SCN_Boot`, `SCN_Ident`, `SCN_Transition`, `SCN_Menu`, `SCN_Main` (the station),
`SCN_Intro`, `SCN_Epilogue`, plus `TestScenes/SCN_Testing_*`. The addressables catalog holds only
localization tables and audio banks. The scene itself is in the data files, so ripping it needs
AssetRipper on the game's data folder.

**Rip (`D:\KFRip\out\ExportedProject\Assets`):** each walkable scene has a baked NavMesh, linked from
its `NavMeshSettings`: SCN_Main uses `NavMeshData\NavMesh_0.asset`, SCN_Intro and SCN_Menu use
`NavMesh_1.asset`, SCN_Epilogue uses `NavMesh.asset`. Boot, Ident, Transition and Engagement have
none. SCN_Main also has 25 `Room`s, 9 `RoomPortal`s, 17 `RoomPortalDoor`s and 59 `DoorOpenable`s.

Scene tooling is in `tools\rip` (see its README; `scene_parse.py` builds a pickle of a
scene, `q_interactables.py` lists interactables, `fileid.py` turns a DLL class name into the
`m_Script` fileID used in the scene files, `guidmap.txt` maps script GUIDs to files).

## Sources of truth

- Regenerate `decompiled\` after a game update (game version is in `MelonLoader\Latest.log`):
  `ilspycmd -p -o decompiled\Assembly-CSharp "<GameDir>\MelonLoader\Il2CppAssemblies\Assembly-CSharp.dll"`,
  likewise for `Il2CppRadio.Actions`, `Il2CppRadio.Actions.Core`, `Il2CppRadio.Core`.
- Log: `<GameDir>\MelonLoader\Latest.log`. Older runs are in `MelonLoader\Logs`.
