"""Offline check: which Room (by its OcclusionArea children) contains each Interactable.
Usage: python -I room_check.py <scene.pickle>
Uses the scene's Transform data (local position/rotation/scale) to get world matrices, and
OcclusionArea m_Center/m_Size in the area's local space, which is how Unity defines the box.
"""
import pickle, re, sys, collections

d = pickle.load(open(sys.argv[1], "rb"))
gos, comps, transforms, go2t, paths = d["gos"], d["comps"], d["transforms"], d["go2t"], d["paths"]
KEY = "Radio.Actions.dll:273980569"
vec = re.compile(r"\{x: ([-\d.eE]+), y: ([-\d.eE]+), z: ([-\d.eE]+)(?:, w: ([-\d.eE]+))?\}")


def field(lines, name):
    for l in lines:
        if l.startswith("  " + name + ":"):
            m = vec.search(l)
            return tuple(float(x) for x in m.groups() if x is not None) if m else None
    return None


def qmat(q):
    x, y, z, w = q
    return [[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
            [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
            [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]]


def local_matrix(t):
    lines = comps[t]["lines"]
    p = field(lines, "m_LocalPosition") or (0, 0, 0)
    q = field(lines, "m_LocalRotation") or (0, 0, 0, 1)
    s = field(lines, "m_LocalScale") or (1, 1, 1)
    r = qmat(q)
    m = [[r[i][j] * s[j] for j in range(3)] + [p[i]] for i in range(3)]
    m.append([0, 0, 0, 1])
    return m


def mul(a, b):
    return [[sum(a[i][k] * b[k][j] for k in range(4)) for j in range(4)] for i in range(4)]


cache = {}


def world(t):
    if t in cache:
        return cache[t]
    m = local_matrix(t)
    f = transforms[t]["father"]
    if f and f in transforms:
        m = mul(world(f), m)
    cache[t] = m
    return m


def inv(m):
    # affine inverse via 3x3 inverse
    a = [row[:3] for row in m[:3]]
    det = (a[0][0] * (a[1][1] * a[2][2] - a[1][2] * a[2][1]) - a[0][1] * (a[1][0] * a[2][2] - a[1][2] * a[2][0])
           + a[0][2] * (a[1][0] * a[2][1] - a[1][1] * a[2][0]))
    c = [[(a[(j + 1) % 3][(i + 1) % 3] * a[(j + 2) % 3][(i + 2) % 3] - a[(j + 1) % 3][(i + 2) % 3] * a[(j + 2) % 3][(i + 1) % 3]) / det
          for j in range(3)] for i in range(3)]
    t = [m[0][3], m[1][3], m[2][3]]
    it = [-sum(c[i][k] * t[k] for k in range(3)) for i in range(3)]
    return [c[0] + [it[0]], c[1] + [it[1]], c[2] + [it[2]], [0, 0, 0, 1]]


def apply(m, p):
    return [sum(m[i][k] * p[k] for k in range(3)) + m[i][3] for i in range(3)]


# rooms -> boxes
rooms = []
for c in comps.values():
    if c["script"] != "Room":
        continue
    rt = go2t[c["go"]]
    name = paths[c["go"]].split("/")[-2] + "/" + gos[c["go"]]["name"]
    boxes = []
    for k, tt in transforms.items():
        if tt["father"] != rt:
            continue
        for x in gos[tt["go"]]["comps"]:
            if comps[x]["cls"] == 192:
                ctr = field(comps[x]["lines"], "m_Center")
                size = field(comps[x]["lines"], "m_Size")
                boxes.append((inv(world(k)), ctr, size))
    rooms.append((name, boxes))

counts = collections.Counter()
multi = 0
for g, o in gos.items():
    if KEY not in {comps[c]["script"] for c in o["comps"]}:
        continue
    p = apply(world(go2t[g]), (0, 0, 0))
    inside = []
    for name, boxes in rooms:
        for invm, ctr, size in boxes:
            lp = apply(invm, p)
            if all(abs(lp[i] - ctr[i]) <= size[i] / 2 for i in range(3)):
                inside.append(name)
                break
    if len(inside) > 1:
        multi += 1
    counts[inside[0] if inside else "<none>"] += 1
for k, v in counts.most_common():
    print(v, k)
print("in more than one room:", multi)
for g, o in gos.items():
    if paths[g].endswith("P_HeroProp_RecordPlayer"):
        p = apply(world(go2t[g]), (0, 0, 0))
        print("record player at", [round(x, 2) for x in p])
