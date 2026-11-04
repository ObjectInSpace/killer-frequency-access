using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using GameInteractableBase = Il2Cpp.InteractableBase;
using GameRoom = Il2Cpp.Room;

namespace Killer_Frequency_Access;

/// <summary>
/// Object list (the Date Everything Access picker, adapted): rooms, then categories in the room, then
/// objects. Choosing an object sets the room and category that [ and ] cycle through.
///
/// Facts used:
/// - Rooms are the game's Il2Cpp.Room components (25 in SCN_Main). Room.volumes is empty at runtime
///   (first object-list run), so a room's space is the Unity OcclusionArea boxes on its children,
///   as the game's files define them. Offline check (tools/rip/room_check.py): 797 of 804 SCN_Main
///   interactables fall in a room. Names come from the hierarchy: floor folder plus room object,
///   for example GroundFloor/BreakRoom = "Ground Floor Break Room".
/// - Categories are the game's own component types on the interactable (see <see cref="Categories"/>).
/// - Keys: the game's InputActions asset binds WASD, the arrows, E, Q, R, F, P, Tab, 1 to 3, the numpad,
///   Ctrl, Escape, F4, F5 and F9. O, Page Up, Page Down, Enter and Backspace are unbound.
/// </summary>
public sealed partial class KillerFrequencyAccessMod
{
    private const string AllCategories = "all categories";

    /// <summary>
    /// Category per game component type, checked in this order; an object goes in the first that matches.
    /// Story items: the game's StoryObjectCheckpoint, or a PhysicsHoldable whose onHeldStoryEvents names a
    /// story event (32 in SCN_Main, for example FoundPoliceReport).
    /// </summary>
    private static readonly (string Label, Func<GameObject, bool> Matches)[] Categories =
    {
        ("story items", go => go.GetComponent<Il2Cpp.StoryObjectCheckpoint>() != null || HasStoryEvents(go)),
        ("collectables", go => go.GetComponent<Il2CppInteractables.CollectInteraction>() != null),
        ("doors", go => go.GetComponent<Il2Cpp.DoorOpenable>() != null),
        ("drawers", go => go.GetComponent<Il2Cpp.DrawerOpenable>() != null),
        ("buttons", go => go.GetComponent<Il2CppInteractables.Button>() != null),
        ("sliders", go => go.GetComponent<Il2Cpp.Slideable>() != null),
        ("cassettes", go => go.GetComponent<Il2CppInteractables.Cassette>() != null),
        ("floppy disks", go => go.GetComponent<Il2Cpp.FloppyDisk>() != null),
        ("records", go => go.GetComponent<Il2CppInteractables.Vinyl>() != null),
        ("fuses", go => go.GetComponent<Il2Cpp.Fuse>() != null),
        ("tray items", go => go.GetComponent<Il2CppInteractables.DockableInTray>() != null),
        ("pinboard items", go => go.GetComponent<Il2Cpp.BoardItem>() != null),
        ("sticky notes", go => go.GetComponent<Il2Cpp.StickyNote>() != null),
        ("holders", go => go.GetComponent<Il2CppInteractables.Holder>() != null),
        ("objects", go => go.GetComponent<Il2CppInteractables.PhysicsHoldable>() != null),
    };

    private const string OtherCategory = "other";

    /// <summary>Docks that accept the held item (PhysicsHoldableDock.CanDock), as a list category.</summary>
    private const string FitsHeldCategory = "places for what you hold";

    private HashSet<int> _placesForHeldIds = new();

    private void RefreshPlacesForHeld()
    {
        _placesForHeldIds = new HashSet<int>(FindPlacesForHeldItem().ConvertAll(t => t.GetInstanceID()));
    }

    private static readonly Dictionary<int, string> CategoryByInteractableId = new();

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
    }

    private enum ListLevel { Rooms, Categories, Objects }

    private enum RoomScope { CurrentRoom, AllRooms, FixedRoom }

    private readonly struct ListedObject
    {
        public ListedObject(Transform transform, string name, string category, RoomInfo? room, float distance)
        {
            Transform = transform;
            Name = name;
            Category = category;
            Room = room;
            Distance = distance;
        }

        public Transform Transform { get; }
        public string Name { get; }
        public string Category { get; }
        public RoomInfo? Room { get; }
        public float Distance { get; }
    }

    private readonly struct RoomChoice
    {
        public RoomChoice(RoomScope scope, RoomInfo? room, string label)
        {
            Scope = scope;
            Room = room;
            Label = label;
        }

        public RoomScope Scope { get; }
        public RoomInfo? Room { get; }
        public string Label { get; }
    }

    private static List<RoomInfo> _rooms = new();
    private static float _nextRoomRefreshTime = -100f;

    // The active filter, used by [ and ].
    private RoomScope _filterScope = RoomScope.CurrentRoom;
    private RoomInfo? _filterRoom;
    private string _filterCategory = AllCategories;
    private int _pendingSelectObjectId = -1;

    // Browsing state while the list is open.
    private bool _isObjectListOpen;
    private ListLevel _listLevel;
    private int _listIndex;
    private RoomChoice _browseRoom;
    private string _browseCategory = AllCategories;
    private List<RoomChoice> _roomChoices = new();
    private List<(string Label, int Count)> _categoryChoices = new();
    private List<ListedObject> _objectChoices = new();

    private string _lastPlayerRoomName = string.Empty;
    private float _nextPlayerRoomCheckTime;

    /// <summary>Handles the list keys. Returns true while the list is open, so other navigation keys are ignored.</summary>
    private bool HandleObjectListKeys()
    {
        if (IsKeyDownSafe(Key.O))
        {
            if (_isObjectListOpen)
            {
                CloseObjectList("Object list closed");
            }
            else
            {
                OpenObjectList();
            }

            return true;
        }

        if (!_isObjectListOpen)
        {
            return false;
        }

        if (IsKeyDownSafe(Key.PageDown))
        {
            MoveInObjectList(1);
        }
        else if (IsKeyDownSafe(Key.PageUp))
        {
            MoveInObjectList(-1);
        }
        else if (IsKeyDownSafe(Key.Enter, Key.NumpadEnter))
        {
            EnterInObjectList();
        }
        else if (IsKeyDownSafe(Key.Backspace))
        {
            BackInObjectList();
        }

        return true;
    }

    private void OpenObjectList()
    {
        _isObjectListOpen = true;
        _listLevel = ListLevel.Rooms;
        TakeListSnapshot();
        BuildRoomChoices();
        _listIndex = 0;
        SpeakSelectionText($"Object list, rooms. {DescribeListEntry()}");
    }

    private void CloseObjectList(string message)
    {
        _isObjectListOpen = false;
        if (message.Length > 0)
        {
            SpeakSelectionText(message);
        }
    }

    private int CurrentListCount()
    {
        return _listLevel switch
        {
            ListLevel.Rooms => _roomChoices.Count,
            ListLevel.Categories => _categoryChoices.Count,
            _ => _objectChoices.Count,
        };
    }

    private void MoveInObjectList(int direction)
    {
        var count = CurrentListCount();
        if (count == 0)
        {
            SpeakSelectionText("Empty");
            return;
        }

        _listIndex = (_listIndex + direction + count) % count;
        SpeakSelectionText(DescribeListEntry());
    }

    private void EnterInObjectList()
    {
        if (CurrentListCount() == 0)
        {
            SpeakSelectionText("Empty");
            return;
        }

        switch (_listLevel)
        {
            case ListLevel.Rooms:
                _browseRoom = _roomChoices[_listIndex];
                _listLevel = ListLevel.Categories;
                BuildCategoryChoices();
                _listIndex = 0;
                SpeakSelectionText($"{_browseRoom.Label}, categories. {DescribeListEntry()}");
                break;
            case ListLevel.Categories:
                _browseCategory = _categoryChoices[_listIndex].Label;
                _listLevel = ListLevel.Objects;
                BuildObjectChoices();
                _listIndex = 0;
                SpeakSelectionText($"{_browseCategory}. {DescribeListEntry()}");
                break;
            default:
                var chosen = _objectChoices[_listIndex];
                _filterScope = _browseRoom.Scope;
                _filterRoom = _browseRoom.Room;
                _filterCategory = _browseCategory;
                _pendingSelectObjectId = chosen.Transform.GetInstanceID();
                CloseObjectList(string.Empty);
                RefreshReachableTargets();
                SpeakSelectionText($"Selected {DescribeListedObject(chosen, includeRoom: true)}. Brackets cycle {_browseCategory} in {_browseRoom.Label}");
                break;
        }
    }

    private void BackInObjectList()
    {
        switch (_listLevel)
        {
            case ListLevel.Objects:
                _listLevel = ListLevel.Categories;
                BuildCategoryChoices();
                _listIndex = Math.Max(0, _categoryChoices.FindIndex(c => c.Label == _browseCategory));
                SpeakSelectionText($"Categories. {DescribeListEntry()}");
                break;
            case ListLevel.Categories:
                _listLevel = ListLevel.Rooms;
                BuildRoomChoices();
                _listIndex = 0;
                SpeakSelectionText($"Rooms. {DescribeListEntry()}");
                break;
            default:
                CloseObjectList("Object list closed");
                break;
        }
    }

    private string DescribeListEntry()
    {
        if (CurrentListCount() == 0)
        {
            return "Empty";
        }

        var position = $"{_listIndex + 1} of {CurrentListCount()}";
        switch (_listLevel)
        {
            case ListLevel.Rooms:
                var room = _roomChoices[_listIndex];
                return $"{room.Label}, {CountObjects(room, AllCategories)} objects, {position}";
            case ListLevel.Categories:
                var category = _categoryChoices[_listIndex];
                return $"{category.Label}, {category.Count}, {position}";
            default:
                return $"{DescribeListedObject(_objectChoices[_listIndex], includeRoom: _browseRoom.Scope == RoomScope.AllRooms)}, {position}";
        }
    }

    private string DescribeListedObject(ListedObject listed, bool includeRoom)
    {
        var text = listed.Name;
        if (TryGetNavigationOrigin(out var origin))
        {
            text += $", {DescribeWhere(origin, GetNearestPoint(listed.Transform, origin.position))}";
        }

        if (includeRoom)
        {
            text += $", {listed.Room?.Name ?? "no room"}";
        }

        return text;
    }

    private void BuildRoomChoices()
    {
        var rooms = GetRooms();
        _roomChoices = new List<RoomChoice>();
        var current = _snapshotPlayerRoom;
        _roomChoices.Add(new RoomChoice(RoomScope.CurrentRoom, null, current == null ? "current room, none" : $"current room, {current.Name}"));
        _roomChoices.Add(new RoomChoice(RoomScope.AllRooms, null, "all rooms"));
        var sorted = new List<RoomInfo>(rooms);
        sorted.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        foreach (var room in sorted)
        {
            _roomChoices.Add(new RoomChoice(RoomScope.FixedRoom, room, room.Name));
        }
    }

    private void BuildCategoryChoices()
    {
        var counts = new Dictionary<string, int>();
        var total = 0;
        foreach (var listed in GatherListedObjects(_browseRoom.Scope, _browseRoom.Room, AllCategories))
        {
            counts[listed.Category] = counts.TryGetValue(listed.Category, out var n) ? n + 1 : 1;
            total++;
        }

        _categoryChoices = new List<(string, int)> { (AllCategories, total) };
        var fits = GatherListedObjects(_browseRoom.Scope, _browseRoom.Room, FitsHeldCategory).Count;
        if (fits > 0)
        {
            _categoryChoices.Add((FitsHeldCategory, fits));
        }
        foreach (var category in Categories)
        {
            if (counts.TryGetValue(category.Label, out var n))
            {
                _categoryChoices.Add((category.Label, n));
            }
        }

        if (counts.TryGetValue(OtherCategory, out var other))
        {
            _categoryChoices.Add((OtherCategory, other));
        }
    }

    private void BuildObjectChoices()
    {
        _objectChoices = GatherListedObjects(_browseRoom.Scope, _browseRoom.Room, _browseCategory);
    }

    private int CountObjects(RoomChoice room, string category)
    {
        return GatherListedObjects(room.Scope, room.Room, category).Count;
    }

    /// <summary>All listable interactables with room and category, taken once when the list opens.</summary>
    private List<ListedObject> _listSnapshot = new();
    private RoomInfo? _snapshotPlayerRoom;

    private void TakeListSnapshot()
    {
        _listSnapshot = new List<ListedObject>();
        _snapshotPlayerRoom = null;
        if (!TryGetNavigationOrigin(out var origin))
        {
            return;
        }

        _snapshotPlayerRoom = RoomOf(origin.position);
        var candidates = new Dictionary<int, Transform>();
        CollectGameInteractables(candidates);
        RefreshPlacesForHeld();
        if (_placesForHeldIds.Count > 0)
        {
            // A dock that fits the held item is listed even when none of its own actions is available.
            foreach (var interactable in UnityEngine.Object.FindObjectsOfType<GameInteractableBase>())
            {
                var id = interactable == null ? 0 : interactable.transform.GetInstanceID();
                if (interactable != null && _placesForHeldIds.Contains(id))
                {
                    candidates[id] = interactable.transform;
                }
            }
        }
        foreach (var transform in candidates.Values)
        {
            if (!TryGetGameInteractable(transform, out var interactable))
            {
                continue;
            }

            var nearest = GetNearestPoint(transform, origin.position);
            _listSnapshot.Add(new ListedObject(
                transform,
                GetSpokenName(interactable),
                GetCategory(interactable),
                RoomOf(transform.position),
                Vector3.Distance(origin.position, nearest)));
        }

        _listSnapshot.Sort((a, b) => a.Distance.CompareTo(b.Distance));
    }

    /// <summary>Snapshot entries in the given room and category, nearest first.</summary>
    private List<ListedObject> GatherListedObjects(RoomScope scope, RoomInfo? fixedRoom, string category)
    {
        return _listSnapshot.FindAll(listed =>
            listed.Transform != null
            && RoomMatches(scope, fixedRoom, _snapshotPlayerRoom, listed.Room)
            && (category == AllCategories
                || (category == FitsHeldCategory ? _placesForHeldIds.Contains(listed.Transform.GetInstanceID()) : listed.Category == category)));
    }

    private static bool RoomMatches(RoomScope scope, RoomInfo? fixedRoom, RoomInfo? playerRoom, RoomInfo? objectRoom)
    {
        return scope switch
        {
            RoomScope.AllRooms => true,
            RoomScope.FixedRoom => objectRoom != null && fixedRoom != null && objectRoom.Room.GetInstanceID() == fixedRoom.Room.GetInstanceID(),
            _ => playerRoom == null
                ? objectRoom == null
                : objectRoom != null && objectRoom.Room.GetInstanceID() == playerRoom.Room.GetInstanceID(),
        };
    }

    /// <summary>Whether a target passes the active filter (the room and category chosen in the object list).</summary>
    private bool MatchesActiveFilter(Transform target, Vector3 playerPosition)
    {
        if (!TryGetGameInteractable(target, out var interactable))
        {
            return false;
        }

        if (_filterCategory == FitsHeldCategory)
        {
            if (!_placesForHeldIds.Contains(target.GetInstanceID()))
            {
                return false;
            }
        }
        else if (_filterCategory != AllCategories && GetCategory(interactable) != _filterCategory)
        {
            return false;
        }

        return RoomMatches(_filterScope, _filterRoom, RoomOf(playerPosition), RoomOf(target.position));
    }

    private string DescribeActiveFilter()
    {
        var room = _filterScope switch
        {
            RoomScope.AllRooms => "all rooms",
            RoomScope.FixedRoom => _filterRoom?.Name ?? "no room",
            _ => "current room",
        };
        return $"{_filterCategory} in {room}";
    }

    private static string GetCategory(GameInteractableBase interactable)
    {
        var id = interactable.GetInstanceID();
        if (CategoryByInteractableId.TryGetValue(id, out var cached))
        {
            return cached;
        }

        var category = OtherCategory;
        var go = interactable.gameObject;
        foreach (var candidate in Categories)
        {
            if (candidate.Matches(go))
            {
                category = candidate.Label;
                break;
            }
        }

        CategoryByInteractableId[id] = category;
        return category;
    }

    private static bool HasStoryEvents(GameObject go)
    {
        var holdable = go.GetComponent<Il2CppInteractables.PhysicsHoldable>();
        var events = holdable == null ? null : holdable.onHeldStoryEvents;
        if (events == null)
        {
            return false;
        }

        for (var i = 0; i < events.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(events[i]))
            {
                return true;
            }
        }

        return false;
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

        if (_filterScope == RoomScope.CurrentRoom)
        {
            RefreshReachableTargets();
        }
    }
}
