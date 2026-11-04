# Killer Frequency: game API notes

What the mod can rely on in the game. Everything here comes from the interop assemblies in
`<GameDir>\MelonLoader\Il2CppAssemblies`, decompiled with `ilspycmd -p` into `decompiled\`
(git-ignored; regenerate after a game update, see "Sources of truth"). These assemblies give
types, fields and signatures only. They do not contain method bodies, so **behaviour below is
marked "unverified" until it has been seen in game.**

Game: Unity 2021.3.5f1, IL2CPP, game version 454, MelonLoader 0.7.1.
Interop namespaces carry an `Il2Cpp` prefix (`Il2Cpp.InteractableItem`, `Il2CppUI.Interactable.UIData`).

## How interaction works

The game is one first-person radio station desk, not an open world. Interaction is built from a
few parts:

- **`Il2Cpp.InteractableItem`** (MonoBehaviour) is the one component every interactable carries.
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

## Names

`ObjectStringHandle` has only **16 values**: BALL, BOX, CARTRIDGE, CARTRIDGE_PLAYER,
CARTRIDGE_RACK, CHAIR, DOOR, KEY, MIXER, PHONE, PIN, PIN_BOARD, RECORD, RECORD_DRAWER,
RECORD_PLAYER, SLIDER. `ActionStringHandle` has about 60 (ANSWER_LINE_1..3, ADJUST_VOLUME_*,
CHANGE_CARTRIDGE, CHECK_OUT_PINBOARD, ...).

Both are plain enums, so the enum name is a stable key. Turning it into the on-screen text goes
through Unity Localization string tables (`localization-string-tables-english(en)` bundle).
**Unverified:** which table and key format the UI uses. The reliable route is to read the text the
game itself puts on screen (below) and keep an enum-name fallback table in the mod.

## What the game shows on screen

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
  `DoorLocked`. The door's interaction is `OpenDoorAndEnterInteraction`.
- `RoomAmbienceZone`, `DoorRoomSelector` exist and may help name the current room (unverified).

## Scenes

Build scenes: `SCN_Boot`, `SCN_Ident`, `SCN_Transition`, `SCN_Menu`, `SCN_Main` (the station),
`SCN_Intro`, `SCN_Epilogue`, plus `TestScenes/SCN_Testing_*`. The addressables catalog holds only
localization tables and audio banks. The scene itself is in the data files, so ripping it needs
AssetRipper on the game's data folder. Whether the scene has a baked NavMesh is **unknown**; check
the rip for `NavMeshData`.

## Sources of truth

- Regenerate `decompiled\` after a game update (game version is in `MelonLoader\Latest.log`):
  `ilspycmd -p -o decompiled\Assembly-CSharp "<GameDir>\MelonLoader\Il2CppAssemblies\Assembly-CSharp.dll"`,
  likewise for `Il2CppRadio.Actions`, `Il2CppRadio.Actions.Core`, `Il2CppRadio.Core`.
- Log: `<GameDir>\MelonLoader\Latest.log`. Older runs are in `MelonLoader\Logs`.
