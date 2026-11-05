using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.InputSystem;
using GameInteractableBase = Il2Cpp.InteractableBase;
using GameRoom = Il2Cpp.Room;

namespace Killer_Frequency_Access;

/// <summary>
/// Object list, always available outside menus (user design, 2026-11-05):
/// Home/End move between three levels, Page Up/Page Down move along the level, Backspace walks to the entry.
///   1. rooms (the first entry is the room the player is in, and follows them),
///   2. what stands on its own in the room: devices, drawers, doors, docks and loose objects,
///   3. what is in or on the level-2 entry: device parts, a drawer's slot and contents, a dock's item.
///
/// Facts used:
/// - Rooms are the game's Il2Cpp.Room components; a room's space is its OcclusionArea children (797 of 804
///   SCN_Main interactables fall in one, tools/rip/room_check.py).
/// - Doors between rooms: Il2Cpp.RoomPortalDoor names the DoorOpenable (_door), the two rooms it joins
///   (_frontRoom, _backRoom) and isOpen; Il2Cpp.RoomPortal without a door is an always-open doorway.
///   16 of the 59 DoorOpenables in SCN_Main are room doors; the rest belong to cabinets, the fridge, the safe
///   and similar. A door was listed in one room only because a single position decides an object's room:
///   the studio door's pivot lies 0.6 m inside the Green Room box (rip). Room doors now go in both rooms.
/// - Containment is the hierarchy: docked items are children of their dock at runtime (AttachPoint), handles
///   and keypads are children of their door or drawer, device parts are below the device component
///   (VinylPlayer, CassettePlayer, SoundBoard, ... the list in tools/rip/gen_names.py DEVICES, plus Board).
///   The outermost such ancestor is the level-2 entry. Loose items inside cabinets and the fridge are not
///   children of them (rip: no slots), so they stand on their own.
/// - Hidden: contents of a room the player is not in whose doorways are all closed (its doors stay listed),
///   and everything below a closed Openable (Openable.IsFullyClosed).
/// - Keys: the game's InputActions asset leaves Home, End, Page Up, Page Down and Backspace unbound.
/// </summary>
public sealed partial class KillerFrequencyAccessMod
{
    /// <summary>Device components that make a group of parts one entry, with their spoken names.</summary>
    private static readonly (Func<Transform, bool> Has, string Label)[] DeviceTypes =
    {
        (t => t.GetComponent<Il2Cpp.VinylPlayer>() != null, "record player"),
        (t => t.GetComponent<Il2CppInteractables.CassettePlayer>() != null, "cassette player"),
        (t => t.GetComponent<Il2Cpp.Dictaphone>() != null, "dictaphone"),
        (t => t.GetComponent<Il2Cpp.SoundBoard>() != null, "soundboard"),
        (t => t.GetComponent<Il2Cpp.FuseBox>() != null, "fuse box"),
        (t => t.GetComponent<Il2Cpp.NumericKeypad>() != null, "keypad"),
        (t => t.GetComponent<Il2Cpp.HandheldRadio>() != null, "handheld radio"),
        (t => t.GetComponent<Il2Cpp.FloppyDiskComputer>() != null, "computer"),
        (t => t.GetComponent<Il2Cpp.Lever>() != null, "lever"),
        (t => t.GetComponent<Il2Cpp.Board>() != null, "pinboard"),
    };

    private HashSet<int> _placesForHeldIds = new();

    private void RefreshPlacesForHeld()
    {
        _placesForHeldIds = new HashSet<int>(FindPlacesForHeldItem().ConvertAll(t => t.GetInstanceID()));
    }

    private sealed class RoomInfo
    {
        public RoomInfo(GameRoom room, string name, List<OcclusionArea> areas)
        {
            Room = room;
            Name = name;
            Areas = areas;
        }

        public GameRoom Room { get; }
        public string Name { get; }
        public List<OcclusionArea> Areas { get; }
        public int Id => Room.GetInstanceID();
    }

    private sealed class ListEntry
    {
        public Transform Root = null!;
        public string Name = string.Empty;
        public bool RootIsListed;
        public Il2Cpp.Openable? Openable;
        public bool IsRoomDoor;
        public bool RoomsResolved;
        public readonly HashSet<int> RoomIds = new();
        public readonly List<string> LeadsTo = new();
        public readonly List<Transform> Contents = new();
        public int Id => Root.GetInstanceID();
        public bool IsClosed => Openable != null && !IsRoomDoor && Openable.IsFullyClosed();
    }

    private enum ListLevel { Rooms, Objects, Contents }

    private static List<RoomInfo> _rooms = new();
    private static float _nextRoomRefreshTime = -100f;

    private ListLevel _listLevel = ListLevel.Rooms;
    private int _roomIndex;
    private int _entryIndex;
    private int _itemIndex;
    private int _selectedRoomId = -1; // -1: the room the player is in
    private int _selectedEntryId = -1;
    private int _selectedItemId = -1;

    // Rebuilt on every list key, so the list always shows the world as it is now.
    private List<(RoomInfo? Room, string Label)> _roomChoices = new();
    private readonly Dictionary<int, ListEntry> _entriesById = new();
    private readonly HashSet<int> _accessibleRoomIds = new();
    private RoomInfo? _playerRoom;

    // Per key press: the current group's members, sorted once. Positions are only read for these.
    private List<ListEntry>? _entriesMemo;
    private int _entriesMemoRoomKey;
    private List<Transform>? _itemsMemo;
    private int _itemsMemoEntryId;

    private string _lastPlayerRoomName = string.Empty;
    private float _nextPlayerRoomCheckTime;

    /// <summary>Handles Home, End, Page Up and Page Down. Returns true when one was pressed.</summary>
    private bool HandleObjectListKeys()
    {
        var up = IsKeyDownSafe(Key.Home);
        var down = IsKeyDownSafe(Key.End);
        var previous = IsKeyDownSafe(Key.PageUp);
        var next = IsKeyDownSafe(Key.PageDown);
        if (!up && !down && !previous && !next)
        {
            return false;
        }

        RebuildObjectIndex();
        if (up)
        {
            MoveLevel(-1);
        }
        else if (down)
        {
            MoveLevel(1);
        }
        else
        {
            MoveInLevel(next ? 1 : -1);
        }

        return true;
    }

    private void MoveLevel(int direction)
    {
        if (direction < 0)
        {
            if (_listLevel == ListLevel.Rooms)
            {
                SpeakSelectionText($"Rooms. {DescribeRoomChoice()}");
                return;
            }

            _listLevel--;
            SpeakSelectionText(_listLevel == ListLevel.Rooms
                ? $"Rooms. {DescribeRoomChoice()}"
                : $"Objects in {SelectedRoomName()}. {DescribeEntry()}");
            return;
        }

        if (_listLevel == ListLevel.Rooms)
        {
            if (CurrentEntries().Count == 0)
            {
                SpeakSelectionText($"No objects listed in {SelectedRoomName()}");
                return;
            }

            _listLevel = ListLevel.Objects;
            SpeakSelectionText($"Objects in {SelectedRoomName()}. {DescribeEntry()}");
            return;
        }

        if (_listLevel == ListLevel.Objects)
        {
            var entry = SelectedEntry();
            if (entry == null)
            {
                SpeakSelectionText("No object selected");
                return;
            }

            if (entry.IsClosed)
            {
                SpeakSelectionText($"{entry.Name} is closed");
                return;
            }

            if (CurrentItems().Count == 0)
            {
                SpeakSelectionText($"Nothing in {entry.Name}");
                return;
            }

            _listLevel = ListLevel.Contents;
            SpeakSelectionText($"In {entry.Name}. {DescribeItem()}");
            return;
        }

        SpeakSelectionText($"In {SelectedEntry()?.Name ?? "nothing"}. {DescribeItem()}");
    }

    private void MoveInLevel(int direction)
    {
        switch (_listLevel)
        {
            case ListLevel.Rooms:
                _roomIndex = Wrap(_roomIndex + direction, _roomChoices.Count);
                _selectedRoomId = _roomIndex == 0 ? -1 : _roomChoices[_roomIndex].Room?.Id ?? -1;
                _selectedEntryId = -1;
                _selectedItemId = -1;
                _entriesMemo = null;
                _itemsMemo = null;
                SpeakSelectionText(DescribeRoomChoice());
                break;
            case ListLevel.Objects:
                var entries = CurrentEntries();
                if (entries.Count == 0)
                {
                    SpeakSelectionText("Empty");
                    return;
                }

                _entryIndex = Wrap(_entryIndex + direction, entries.Count);
                _selectedEntryId = entries[_entryIndex].Id;
                _selectedItemId = -1;
                _itemsMemo = null;
                SpeakSelectionText(DescribeEntry());
                break;
            default:
                var items = CurrentItems();
                if (items.Count == 0)
                {
                    SpeakSelectionText("Empty");
                    return;
                }

                _itemIndex = Wrap(_itemIndex + direction, items.Count);
                _selectedItemId = items[_itemIndex].GetInstanceID();
                SpeakSelectionText(DescribeItem());
                break;
        }
    }

    private static int Wrap(int index, int count)
    {
        return count == 0 ? 0 : ((index % count) + count) % count;
    }

    /// <summary>The object Backspace walks to: the selected level-3 item, or the level-2 entry (its first part for a device).</summary>
    private Transform? GetListWalkTarget()
    {
        RebuildObjectIndex();
        if (_listLevel == ListLevel.Contents)
        {
            var items = CurrentItems();
            return items.Count > 0 ? items[_itemIndex] : null;
        }

        if (_listLevel == ListLevel.Objects)
        {
            var entry = SelectedEntry();
            if (entry == null)
            {
                return null;
            }

            if (entry.RootIsListed)
            {
                return entry.Root;
            }

            var items = CurrentItems();
            return items.Count > 0 ? items[0] : null;
        }

        return null;
    }

    private string SelectedRoomName()
    {
        var room = SelectedRoom();
        return room?.Name ?? "no room";
    }

    private RoomInfo? SelectedRoom()
    {
        return _roomIndex == 0 ? _playerRoom : _roomChoices[_roomIndex].Room;
    }

    private bool IsRoomAccessible(RoomInfo? room)
    {
        return room == null || _accessibleRoomIds.Contains(room.Id);
    }

    /// <summary>Level-2 entries in the selected room, nearest first. A closed room shows only its doors.</summary>
    private List<ListEntry> CurrentEntries()
    {
        var room = SelectedRoom();
        var roomKey = room?.Id ?? 0;
        if (_entriesMemo == null || _entriesMemoRoomKey != roomKey)
        {
            var accessible = IsRoomAccessible(room);
            var result = new List<ListEntry>();
            foreach (var entry in _entriesById.Values)
            {
                ResolveEntryRooms(entry);
                var inRoom = room == null ? entry.RoomIds.Count == 0 : entry.RoomIds.Contains(room.Id);
                if (inRoom && (accessible || entry.IsRoomDoor))
                {
                    result.Add(entry);
                }
            }

            SortNearest(result, e => e.Root);
            _entriesMemo = result;
            _entriesMemoRoomKey = roomKey;
        }

        _entryIndex = Math.Max(0, _entriesMemo.FindIndex(e => e.Id == _selectedEntryId));
        if (_entriesMemo.Count > 0)
        {
            _selectedEntryId = _entriesMemo[_entryIndex].Id;
        }

        return _entriesMemo;
    }

    /// <summary>An entry's room from its position, read only when its room's members are needed.</summary>
    private static void ResolveEntryRooms(ListEntry entry)
    {
        if (entry.RoomsResolved)
        {
            return;
        }

        entry.RoomsResolved = true;
        var room = RoomOf(entry.Root.position);
        if (room != null)
        {
            entry.RoomIds.Add(room.Id);
        }
    }

    private ListEntry? SelectedEntry()
    {
        var entries = CurrentEntries();
        return entries.Count > 0 ? entries[_entryIndex] : null;
    }

    /// <summary>Level-3 items of the selected entry, nearest first; none while the entry is closed.</summary>
    private List<Transform> CurrentItems()
    {
        var entry = SelectedEntry();
        if (entry == null || entry.IsClosed)
        {
            return new List<Transform>();
        }

        if (_itemsMemo == null || _itemsMemoEntryId != entry.Id)
        {
            var result = new List<Transform>();
            foreach (var item in entry.Contents)
            {
                if (item != null && !IsBelowClosedOpenable(item, entry.Root))
                {
                    result.Add(item);
                }
            }

            SortNearest(result, t => t);
            _itemsMemo = result;
            _itemsMemoEntryId = entry.Id;
        }

        _itemIndex = Math.Max(0, _itemsMemo.FindIndex(t => t.GetInstanceID() == _selectedItemId));
        if (_itemsMemo.Count > 0)
        {
            _selectedItemId = _itemsMemo[_itemIndex].GetInstanceID();
        }

        return _itemsMemo;
    }

    private static bool IsBelowClosedOpenable(Transform item, Transform root)
    {
        for (var current = item.parent; current != null && current != root.parent; current = current.parent)
        {
            var openable = current.GetComponent<Il2Cpp.Openable>();
            if (openable != null && openable.IsFullyClosed())
            {
                return true;
            }
        }

        return false;
    }

    private void SortNearest<T>(List<T> list, Func<T, Transform> transformOf)
    {
        if (!TryGetNavigationOrigin(out var origin))
        {
            return;
        }

        var position = origin.position;
        var distances = new Dictionary<int, float>();
        float DistanceOf(T value)
        {
            var transform = transformOf(value);
            var id = transform.GetInstanceID();
            if (!distances.TryGetValue(id, out var distance))
            {
                distance = Vector3.Distance(position, GetNearestPoint(transform, position));
                distances[id] = distance;
            }

            return distance;
        }

        list.Sort((a, b) => DistanceOf(a).CompareTo(DistanceOf(b)));
    }

    private string DescribeRoomChoice()
    {
        if (_roomChoices.Count == 0)
        {
            return "No rooms";
        }

        var room = SelectedRoom();
        var count = CurrentEntries().Count;
        var closed = IsRoomAccessible(room) ? string.Empty : ", closed";
        return $"{_roomChoices[_roomIndex].Label}{closed}, {count} objects, {_roomIndex + 1} of {_roomChoices.Count}";
    }

    private string DescribeEntry()
    {
        var entries = CurrentEntries();
        if (entries.Count == 0)
        {
            return "Empty";
        }

        var entry = entries[_entryIndex];
        var parts = new List<string> { entry.Name };
        if (entry.IsRoomDoor && entry.LeadsTo.Count > 0)
        {
            var room = SelectedRoom();
            var others = entry.LeadsTo.FindAll(name => name != room?.Name);
            if (others.Count > 0)
            {
                parts.Add($"to {string.Join(" and ", others)}");
            }
        }

        if (entry.IsClosed)
        {
            parts.Add("closed");
        }
        else
        {
            var visible = entry.Contents.FindAll(t => t != null && !IsBelowClosedOpenable(t, entry.Root)).Count;
            if (visible > 0)
            {
                parts.Add(visible == 1 ? "1 item" : $"{visible} items");
            }
        }

        if (TryGetNavigationOrigin(out var origin))
        {
            parts.Add(DescribeWhere(origin, GetNearestPoint(entry.Root, origin.position)));
        }

        parts.Add($"{_entryIndex + 1} of {entries.Count}");
        return string.Join(", ", parts);
    }

    private string DescribeItem()
    {
        var items = CurrentItems();
        if (items.Count == 0)
        {
            return "Empty";
        }

        var item = items[_itemIndex];
        var text = GetSpokenName(item);
        if (TryGetNavigationOrigin(out var origin))
        {
            text += $", {DescribeWhere(origin, GetNearestPoint(item, origin.position))}";
        }

        return $"{text}, {_itemIndex + 1} of {items.Count}";
    }

    /// <summary>Rebuilds rooms, doorways and entries from the game's current state.</summary>
    private void RebuildObjectIndex()
    {
        var rooms = GetRooms();
        _playerRoom = TryGetNavigationOrigin(out var origin) ? RoomOf(origin.position) : null;
        var roomsById = new Dictionary<int, RoomInfo>();
        foreach (var room in rooms)
        {
            roomsById[room.Id] = room;
        }

        // Doorways: which rooms are reachable, and which doors join which rooms.
        _accessibleRoomIds.Clear();
        if (_playerRoom != null)
        {
            _accessibleRoomIds.Add(_playerRoom.Id);
        }

        var doorRooms = new Dictionary<int, List<RoomInfo>>();
        foreach (var portal in UnityEngine.Object.FindObjectsOfType<Il2Cpp.RoomPortal>())
        {
            if (portal == null)
            {
                continue;
            }

            var front = portal._frontRoom != null && roomsById.TryGetValue(portal._frontRoom.GetInstanceID(), out var f) ? f : null;
            var back = portal._backRoom != null && roomsById.TryGetValue(portal._backRoom.GetInstanceID(), out var b) ? b : null;
            var doorPortal = portal.TryCast<Il2Cpp.RoomPortalDoor>();
            var door = doorPortal?._door;
            var open = doorPortal == null || door == null || doorPortal.isOpen;
            if (open)
            {
                if (front != null) _accessibleRoomIds.Add(front.Id);
                if (back != null) _accessibleRoomIds.Add(back.Id);
            }

            if (door != null)
            {
                var list = new List<RoomInfo>();
                if (front != null) list.Add(front);
                if (back != null) list.Add(back);
                doorRooms[door.transform.GetInstanceID()] = list;
            }
        }

        // Entries: every active interactable with an action the game says is available now, under its
        // outermost container. No count cap and no distance limit; no positions are read here. OculusOnly
        // marks the 98 VR grab handles (rip), which flat-screen play cannot use.
        _entriesById.Clear();
        _entriesMemo = null;
        _itemsMemo = null;
        var listed = new HashSet<int>();
        var items = new List<Transform>();
        foreach (var interactable in UnityEngine.Object.FindObjectsOfType<GameInteractableBase>())
        {
            if (interactable == null || !interactable.isActiveAndEnabled || interactable.OculusOnly
                || GetAvailableVerbs(interactable).Count == 0)
            {
                continue;
            }

            listed.Add(interactable.transform.GetInstanceID());
            items.Add(interactable.transform);
        }

        foreach (var item in items)
        {
            var root = FindContainerRoot(item);
            if (!_entriesById.TryGetValue(root.GetInstanceID(), out var entry))
            {
                entry = CreateEntry(root, listed, doorRooms);
                _entriesById[entry.Id] = entry;
            }

            if (root != item)
            {
                entry.Contents.Add(item);
            }
        }

        // Rooms: the player's room first (it follows them), then every room by name.
        _roomChoices = new List<(RoomInfo?, string)> { (null, _playerRoom == null ? "current room, none" : $"current room, {_playerRoom.Name}") };
        var sorted = new List<RoomInfo>(rooms);
        sorted.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        foreach (var room in sorted)
        {
            _roomChoices.Add((room, room.Name));
        }

        _roomIndex = _selectedRoomId == -1 ? 0 : Math.Max(0, _roomChoices.FindIndex(c => c.Room != null && c.Room.Id == _selectedRoomId));
    }

    private ListEntry CreateEntry(Transform root, HashSet<int> listed, Dictionary<int, List<RoomInfo>> doorRooms)
    {
        var entry = new ListEntry
        {
            Root = root,
            RootIsListed = listed.Contains(root.GetInstanceID()),
            Openable = root.GetComponent<Il2Cpp.Openable>(),
        };
        entry.Name = DeviceLabel(root) ?? GetSpokenName(root);
        if (doorRooms.TryGetValue(root.GetInstanceID(), out var joined))
        {
            entry.IsRoomDoor = true;
            entry.RoomsResolved = true;
            foreach (var room in joined)
            {
                entry.RoomIds.Add(room.Id);
                entry.LeadsTo.Add(room.Name);
            }
        }

        return entry;
    }

    // Container root per item, keyed by the item and its current parent (docking re-parents an item).
    private static readonly Dictionary<(int Item, int Parent), Transform> ContainerRootCache = new();

    /// <summary>The outermost ancestor that is an interactable, an Openable or a device; the item itself if none.</summary>
    private static Transform FindContainerRoot(Transform item)
    {
        var key = (item.GetInstanceID(), item.parent == null ? 0 : item.parent.GetInstanceID());
        if (ContainerRootCache.TryGetValue(key, out var cached) && cached != null)
        {
            return cached;
        }

        var found = FindContainerRootUncached(item);
        ContainerRootCache[key] = found;
        return found;
    }

    private static Transform FindContainerRootUncached(Transform item)
    {
        var root = item;
        for (var current = item.parent; current != null; current = current.parent)
        {
            if (current.GetComponent<GameInteractableBase>() != null
                || current.GetComponent<Il2Cpp.Openable>() != null
                || DeviceLabel(current) != null)
            {
                root = current;
            }
        }

        return root;
    }

    /// <summary>Spoken device name with the number at the end of its object name, as gen_names.py does; null if not a device.</summary>
    private static string? DeviceLabel(Transform transform)
    {
        foreach (var (has, label) in DeviceTypes)
        {
            if (has(transform))
            {
                var match = Regex.Match(transform.name, @"_(\d+)$");
                return match.Success ? $"{label} {int.Parse(match.Groups[1].Value)}" : label;
            }
        }

        return null;
    }

    /// <summary>The game's rooms, rebuilt every few seconds so scene changes are picked up.</summary>
    private static List<RoomInfo> GetRooms()
    {
        var now = Time.unscaledTime;
        var stale = now >= _nextRoomRefreshTime || _rooms.Exists(r => r.Room == null);
        if (!stale)
        {
            return _rooms;
        }

        _nextRoomRefreshTime = now + 5f;
        var rooms = new List<RoomInfo>();
        foreach (var room in UnityEngine.Object.FindObjectsOfType<GameRoom>())
        {
            if (room == null)
            {
                continue;
            }

            var name = BuildRoomName(room.transform);
            var areas = new List<OcclusionArea>();
            for (var i = 0; i < room.transform.childCount; i++)
            {
                var area = room.transform.GetChild(i).GetComponent<OcclusionArea>();
                if (area != null)
                {
                    areas.Add(area);
                }
            }

            if (areas.Count == 0)
            {
                LogOnce($"room-no-areas|{name}", $"[rooms] {name} has no OcclusionArea children; nothing can be placed in it");
            }

            rooms.Add(new RoomInfo(room, name, areas));
        }

        if (rooms.Count != _rooms.Count)
        {
            MelonLoader.MelonLogger.Msg($"[rooms] {rooms.Count} rooms: {string.Join(", ", rooms.ConvertAll(r => $"{r.Name} ({r.Areas.Count})"))}");
        }

        _rooms = rooms;
        return _rooms;
    }

    /// <summary>
    /// The room whose OcclusionArea box contains <paramref name="point"/>. The box is centre and size in
    /// the area's local space. Where boxes overlap (13 objects in SCN_Main, at door frames), the
    /// smallest box wins.
    /// </summary>
    private static RoomInfo? RoomOf(Vector3 point)
    {
        RoomInfo? best = null;
        var bestVolume = float.MaxValue;
        foreach (var room in GetRooms())
        {
            foreach (var area in room.Areas)
            {
                if (area == null)
                {
                    continue;
                }

                var local = area.transform.InverseTransformPoint(point) - area.center;
                var half = area.size * 0.5f;
                if (Mathf.Abs(local.x) > half.x || Mathf.Abs(local.y) > half.y || Mathf.Abs(local.z) > half.z)
                {
                    continue;
                }

                var scale = area.transform.lossyScale;
                var volume = Mathf.Abs(area.size.x * scale.x * area.size.y * scale.y * area.size.z * scale.z);
                if (volume < bestVolume)
                {
                    bestVolume = volume;
                    best = room;
                }
            }
        }

        return best;
    }

    /// <summary>Floor folder plus room object, for example GroundFloor/BreakRoom = "Ground Floor Break Room".</summary>
    private static string BuildRoomName(Transform roomTransform)
    {
        var parts = new List<string>();
        if (roomTransform.parent != null)
        {
            parts.Add(ReadableObjectName(roomTransform.parent.name));
        }

        parts.Add(ReadableObjectName(roomTransform.name));
        var words = new List<string>();
        foreach (var word in string.Join(" ", parts).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!words.Exists(w => string.Equals(w, word, StringComparison.OrdinalIgnoreCase)))
            {
                words.Add(word);
            }
        }

        return string.Join(" ", words);
    }

    /// <summary>Speaks the room name when the player walks into a different room.</summary>
    private void UpdatePlayerRoomAnnouncement()
    {
        var now = Time.unscaledTime;
        if (now < _nextPlayerRoomCheckTime || IsMenuActive())
        {
            return;
        }

        _nextPlayerRoomCheckTime = now + 0.5f;
        if (!TryGetNavigationOrigin(out var origin))
        {
            return;
        }

        var room = RoomOf(origin.position);
        var name = room?.Name ?? string.Empty;
        if (name == _lastPlayerRoomName)
        {
            return;
        }

        _lastPlayerRoomName = name;
        if (name.Length > 0)
        {
            SpeakSelectionText(name);
        }
    }
}
