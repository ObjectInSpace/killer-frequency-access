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
/// a tree. End goes into the current entry, Home comes back out, Page Up/Page Down move along a level,
/// Backspace walks to the entry.
///   rooms (the first entry is the room the player is in, and follows them),
///   then what stands on its own in the room: devices, drawers, doors, docks and loose objects,
///   then, for every container at any depth, its direct contents: a device's parts, a drawer's slot,
///   the item in a slot.
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
///   An object's parent in the list is its nearest such ancestor, at any depth. Loose items inside cabinets and the fridge are not
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

    private sealed class ListNode
    {
        public Transform Transform = null!;
        public string Name = string.Empty;
        public bool Listed;
        public Il2Cpp.Openable? Openable;
        public bool IsRoomDoor;
        public bool RoomsResolved;
        public ListNode? Parent;
        public readonly List<ListNode> Children = new();
        public readonly HashSet<int> RoomIds = new();
        public readonly List<string> LeadsTo = new();
        public int Id => Transform.GetInstanceID();
        public bool IsClosed => Openable != null && !IsRoomDoor && Openable.IsFullyClosed();
    }

    // Kept for callers outside the list (auto-walk): whether the list is at the rooms level.
    private enum ListLevel { Rooms, Objects }

    private static List<RoomInfo> _rooms = new();
    private static float _nextRoomRefreshTime = -100f;

    // Depth 0 is rooms, 1 the room's own objects, d >= 2 the children of the node selected at d - 1.
    // _path[d - 1] is the selected node at depth d.
    private int _depth;
    private readonly List<int> _path = new();
    private int _roomIndex;
    private int _selectedRoomId = -1; // -1: the room the player is in

    // Rebuilt on every list key, so the list always shows the world as it is now.
    private List<(RoomInfo? Room, string Label)> _roomChoices = new();
    private readonly Dictionary<int, ListNode> _nodes = new();
    private readonly List<ListNode> _topNodes = new();
    private readonly HashSet<int> _accessibleRoomIds = new();
    private RoomInfo? _playerRoom;

    // Per key press: a group's members, filtered and sorted once. Positions are only read for these.
    private readonly Dictionary<(int Depth, int Owner), List<ListNode>> _groupMemo = new();

    // Player context (user request 2026-11-05), applied at the next list key: entering a room lists its
    // objects; looking at an object selects it, and looking at a container lists its contents.
    private enum ListContext { None, EnteredRoom, LookedAt }
    private ListContext _pendingContext;
    private int _contextNodeId;

    private string _lastPlayerRoomName = string.Empty;
    private float _nextPlayerRoomCheckTime;

    private ListLevel _listLevel => _depth == 0 ? ListLevel.Rooms : ListLevel.Objects;

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
        var atStart = ApplyPendingContext();
        if (atStart && (previous || next))
        {
            // The context just reset the list to the start of a group: read that entry instead of skipping it.
            SpeakSelectionText($"{GroupTitle(_depth)}. {DescribeSelected(_depth)}");
            return true;
        }

        if (up)
        {
            if (_depth > 0)
            {
                _depth--;
            }

            SpeakSelectionText($"{GroupTitle(_depth)}. {DescribeSelected(_depth)}");
        }
        else if (down)
        {
            GoIn();
        }
        else
        {
            MoveInGroup(next ? 1 : -1);
        }

        return true;
    }

    private void GoIn()
    {
        if (_depth == 0)
        {
            if (Group(1).Count == 0)
            {
                SpeakSelectionText($"No objects listed in {SelectedRoomName()}");
                return;
            }

            _depth = 1;
            SpeakSelectionText($"{GroupTitle(1)}. {DescribeSelected(1)}");
            return;
        }

        var node = SelectedNode(_depth);
        if (node == null)
        {
            SpeakSelectionText("Nothing selected");
            return;
        }

        if (node.IsClosed)
        {
            SpeakSelectionText($"{node.Name} is closed");
            return;
        }

        if (Group(_depth + 1).Count == 0)
        {
            SpeakSelectionText($"Nothing in {node.Name}");
            return;
        }

        _depth++;
        SpeakSelectionText($"{GroupTitle(_depth)}. {DescribeSelected(_depth)}");
    }

    private void MoveInGroup(int direction)
    {
        if (_depth == 0)
        {
            _roomIndex = Wrap(_roomIndex + direction, _roomChoices.Count);
            _selectedRoomId = _roomIndex == 0 ? -1 : _roomChoices[_roomIndex].Room?.Id ?? -1;
            _path.Clear();
            _groupMemo.Clear();
            SpeakSelectionText(DescribeSelected(0));
            return;
        }

        var group = Group(_depth);
        if (group.Count == 0)
        {
            SpeakSelectionText("Empty");
            return;
        }

        var index = Wrap(SelectedIndex(_depth, group) + direction, group.Count);
        SetSelected(_depth, group[index].Id);
        SpeakSelectionText(DescribeSelected(_depth));
    }

    private static int Wrap(int index, int count)
    {
        return count == 0 ? 0 : ((index % count) + count) % count;
    }

    /// <summary>Selects a node at a depth and forgets the selections below it.</summary>
    private void SetSelected(int depth, int nodeId)
    {
        while (_path.Count >= depth)
        {
            _path.RemoveAt(_path.Count - 1);
        }

        while (_path.Count < depth - 1)
        {
            _path.Add(-1);
        }

        _path.Add(nodeId);
    }

    private int SelectedIndex(int depth, List<ListNode> group)
    {
        var id = depth - 1 < _path.Count ? _path[depth - 1] : -1;
        return Math.Max(0, group.FindIndex(n => n.Id == id));
    }

    private ListNode? SelectedNode(int depth)
    {
        var group = Group(depth);
        if (group.Count == 0)
        {
            return null;
        }

        var node = group[SelectedIndex(depth, group)];
        if (depth - 1 >= _path.Count || _path[depth - 1] != node.Id)
        {
            SetSelected(depth, node.Id);
        }

        return node;
    }

    /// <summary>
    /// The members of the group at a depth, nearest first: the room's own objects at depth 1 (a closed room
    /// shows only its doors), otherwise the children of the node selected one level up (none while it is closed).
    /// </summary>
    private List<ListNode> Group(int depth)
    {
        if (depth <= 0)
        {
            return new List<ListNode>();
        }

        if (depth == 1)
        {
            var room = SelectedRoom();
            var key = (1, room?.Id ?? 0);
            if (!_groupMemo.TryGetValue(key, out var members))
            {
                var accessible = IsRoomAccessible(room);
                members = new List<ListNode>();
                foreach (var node in _topNodes)
                {
                    ResolveNodeRooms(node);
                    var inRoom = room == null ? node.RoomIds.Count == 0 : node.RoomIds.Contains(room.Id);
                    if (inRoom && (accessible || node.IsRoomDoor))
                    {
                        members.Add(node);
                    }
                }

                SortNearest(members, n => n.Transform);
                _groupMemo[key] = members;
            }

            return members;
        }

        var owner = SelectedNode(depth - 1);
        if (owner == null || owner.IsClosed)
        {
            return new List<ListNode>();
        }

        var ownerKey = (depth, owner.Id);
        if (!_groupMemo.TryGetValue(ownerKey, out var children))
        {
            children = new List<ListNode>(owner.Children);
            SortNearest(children, n => n.Transform);
            _groupMemo[ownerKey] = children;
        }

        return children;
    }

    private string GroupTitle(int depth)
    {
        return depth switch
        {
            0 => "Rooms",
            1 => $"Objects in {SelectedRoomName()}",
            _ => $"In {SelectedNode(depth - 1)?.Name ?? "nothing"}",
        };
    }

    private string DescribeSelected(int depth)
    {
        if (depth == 0)
        {
            if (_roomChoices.Count == 0)
            {
                return "No rooms";
            }

            var room = SelectedRoom();
            var closed = IsRoomAccessible(room) ? string.Empty : ", closed";
            return $"{_roomChoices[_roomIndex].Label}{closed}, {Group(1).Count} objects, {_roomIndex + 1} of {_roomChoices.Count}";
        }

        var group = Group(depth);
        if (group.Count == 0)
        {
            return "Empty";
        }

        var index = SelectedIndex(depth, group);
        var node = group[index];
        if (depth - 1 >= _path.Count || _path[depth - 1] != node.Id)
        {
            SetSelected(depth, node.Id);
        }

        var parts = new List<string> { node.Name };
        if (node.IsRoomDoor)
        {
            var here = SelectedRoom()?.Name;
            var others = node.LeadsTo.FindAll(name => name != here);
            if (others.Count > 0)
            {
                parts.Add($"to {string.Join(" and ", others)}");
            }
        }

        if (node.IsClosed)
        {
            parts.Add("closed");
        }
        else if (node.Children.Count > 0)
        {
            parts.Add(node.Children.Count == 1 ? "1 item" : $"{node.Children.Count} items");
        }

        if (TryGetNavigationOrigin(out var origin))
        {
            parts.Add(DescribeWhere(origin, GetNearestPoint(node.Transform, origin.position)));
        }

        parts.Add($"{index + 1} of {group.Count}");
        return string.Join(", ", parts);
    }

    /// <summary>The object Backspace walks to: the selected node, or for a container the game cannot use, its first usable part (breadth first).</summary>
    private Transform? GetListWalkTarget()
    {
        RebuildObjectIndex();
        if (_depth == 0)
        {
            return null;
        }

        var node = SelectedNode(_depth);
        if (node == null)
        {
            return null;
        }

        var queue = new Queue<ListNode>();
        queue.Enqueue(node);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current.Listed)
            {
                return current.Transform;
            }

            foreach (var child in current.Children)
            {
                queue.Enqueue(child);
            }
        }

        return null;
    }

    /// <summary>Called when the game starts reporting a new observed interactable.</summary>
    private void SetListContextLookedAt(Transform observed)
    {
        _pendingContext = ListContext.LookedAt;
        _contextNodeId = observed.GetInstanceID();
    }

    /// <summary>Applies the latest context. Returns true when it left the list at the start of a group.</summary>
    private bool ApplyPendingContext()
    {
        var context = _pendingContext;
        _pendingContext = ListContext.None;
        if (context == ListContext.None)
        {
            return false;
        }

        _selectedRoomId = -1;
        _roomIndex = 0;
        _groupMemo.Clear();
        if (context == ListContext.EnteredRoom)
        {
            _path.Clear();
            _depth = 1;
            return true;
        }

        // Looked at: the chain from the room's own object down to the node, which must be in the player's room.
        if (!_nodes.TryGetValue(_contextNodeId, out var target))
        {
            return false;
        }

        var chain = new List<ListNode>();
        for (var node = target; node != null; node = node.Parent)
        {
            chain.Insert(0, node);
        }

        if (!Group(1).Exists(n => n.Id == chain[0].Id))
        {
            return false;
        }

        _path.Clear();
        foreach (var node in chain)
        {
            _path.Add(node.Id);
        }

        _depth = chain.Count;
        if (!target.IsClosed && target.Children.Count > 0)
        {
            // A container: list what is in it.
            _depth++;
            return true;
        }

        return false;
    }

    private string SelectedRoomName()
    {
        return SelectedRoom()?.Name ?? "no room";
    }

    private RoomInfo? SelectedRoom()
    {
        return _roomIndex == 0 || _roomIndex >= _roomChoices.Count ? _playerRoom : _roomChoices[_roomIndex].Room;
    }

    private bool IsRoomAccessible(RoomInfo? room)
    {
        return room == null || _accessibleRoomIds.Contains(room.Id);
    }

    /// <summary>A top node's room from its position, read only when its room's members are needed.</summary>
    private static void ResolveNodeRooms(ListNode node)
    {
        if (node.RoomsResolved)
        {
            return;
        }

        node.RoomsResolved = true;
        var room = RoomOf(node.Transform.position);
        if (room != null)
        {
            node.RoomIds.Add(room.Id);
        }
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

    /// <summary>Rebuilds rooms, doorways and the object tree from the game's current state. No positions are read.</summary>
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
            if (doorPortal == null || door == null || doorPortal.isOpen)
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

        // The tree: every active interactable with an action the game says is available now, linked to its
        // nearest container ancestor. No count cap and no distance limit. OculusOnly marks the 98 VR grab
        // handles (rip), which flat-screen play cannot use.
        _nodes.Clear();
        _topNodes.Clear();
        _groupMemo.Clear();
        var listedItems = new List<Transform>();
        foreach (var interactable in UnityEngine.Object.FindObjectsOfType<GameInteractableBase>())
        {
            if (interactable == null || !interactable.isActiveAndEnabled || interactable.OculusOnly
                || GetAvailableVerbs(interactable).Count == 0)
            {
                continue;
            }

            listedItems.Add(interactable.transform);
        }

        foreach (var item in listedItems)
        {
            GetOrCreateNode(item, doorRooms).Listed = true;
        }

        foreach (var item in listedItems)
        {
            var node = _nodes[item.GetInstanceID()];
            while (node.Parent == null)
            {
                var ancestor = FindContainerAncestor(node.Transform);
                if (ancestor == null)
                {
                    break;
                }

                var parent = GetOrCreateNode(ancestor, doorRooms);
                node.Parent = parent;
                parent.Children.Add(node);
                node = parent;
            }
        }

        foreach (var node in _nodes.Values)
        {
            if (node.Parent == null)
            {
                _topNodes.Add(node);
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

    private ListNode GetOrCreateNode(Transform transform, Dictionary<int, List<RoomInfo>> doorRooms)
    {
        var id = transform.GetInstanceID();
        if (_nodes.TryGetValue(id, out var existing))
        {
            return existing;
        }

        var node = new ListNode
        {
            Transform = transform,
            Openable = transform.GetComponent<Il2Cpp.Openable>(),
        };
        node.Name = DeviceLabel(transform) ?? GetSpokenName(transform);
        if (doorRooms.TryGetValue(id, out var joined))
        {
            node.IsRoomDoor = true;
            node.RoomsResolved = true;
            foreach (var room in joined)
            {
                node.RoomIds.Add(room.Id);
                node.LeadsTo.Add(room.Name);
            }
        }

        _nodes[id] = node;
        return node;
    }

    // Nearest container ancestor per object, keyed by the object and its current parent (docking re-parents).
    private static readonly Dictionary<(int Item, int Parent), Transform?> ContainerAncestorCache = new();

    /// <summary>The nearest ancestor that is an interactable, an Openable or a device; null if none.</summary>
    private static Transform? FindContainerAncestor(Transform item)
    {
        var key = (item.GetInstanceID(), item.parent == null ? 0 : item.parent.GetInstanceID());
        if (ContainerAncestorCache.TryGetValue(key, out var cached) && (cached is null || cached != null))
        {
            return cached;
        }

        Transform? found = null;
        for (var current = item.parent; current != null; current = current.parent)
        {
            if (current.GetComponent<GameInteractableBase>() != null
                || current.GetComponent<Il2Cpp.Openable>() != null
                || DeviceLabel(current) != null)
            {
                found = current;
                break;
            }
        }

        ContainerAncestorCache[key] = found;
        return found;
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
            _pendingContext = ListContext.EnteredRoom;
        }
    }
}
