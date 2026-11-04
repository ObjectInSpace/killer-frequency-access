"""Parse a Unity YAML scene: build GameObjects, components, hierarchy, script names.
Usage: python -I scene_parse.py <scene.unity> <guidmap.txt> <out_prefix>
Writes <out_prefix>.pickle with dicts for later queries.
"""
import re, sys, pickle

scene, guidmap_path, out = sys.argv[1:4]

guidmap = {}
for line in open(guidmap_path, encoding="utf-8"):
    g, p = line.rstrip("\n").split(" ", 1)
    guidmap[g] = p

hdr = re.compile(r"^--- !u!(\d+) &(-?\d+)")
docs = {}  # fileID -> (classID, text lines)
cur = None
with open(scene, encoding="utf-8", errors="replace") as f:
    for line in f:
        m = hdr.match(line)
        if m:
            cur = int(m.group(2))
            docs[cur] = [int(m.group(1)), []]
            continue
        if cur is not None:
            docs[cur][1].append(line.rstrip("\n"))

def field(lines, name):
    pre = "  " + name + ":"
    for l in lines:
        if l.startswith(pre):
            return l[len(pre):].strip()
    return None

ref = re.compile(r"fileID: (-?\d+)")
gos, comps, transforms = {}, {}, {}
for fid, (cls, lines) in docs.items():
    if cls == 1:
        gos[fid] = {"name": field(lines, "m_Name") or "", "active": field(lines, "m_IsActive"), "comps": []}
    else:
        go = field(lines, "m_GameObject")
        gid = int(ref.search(go).group(1)) if go else None
        script = field(lines, "m_Script")
        sname = None
        if script:
            mm = re.search(r"fileID: (-?\d+), guid: ([0-9a-f]+)", script)
            if mm:
                p = guidmap.get(mm.group(2), mm.group(2))
                sname = p.rsplit("/", 1)[-1].replace(".cs", "")
                if p.endswith(".dll"):
                    sname += ":" + mm.group(1)
        comps[fid] = {"cls": cls, "go": gid, "script": sname, "lines": lines}
        if cls in (4, 224):  # Transform, RectTransform
            father = field(lines, "m_Father")
            pos = field(lines, "m_LocalPosition")
            transforms[fid] = {"go": gid, "father": int(ref.search(father).group(1)) if father else 0, "pos": pos}

for fid, c in comps.items():
    if c["go"] in gos:
        gos[c["go"]]["comps"].append(fid)

go2t = {t["go"]: fid for fid, t in transforms.items()}

def path(gid):
    parts = []
    t = go2t.get(gid)
    seen = 0
    while t and t in transforms and seen < 64:
        parts.append(gos.get(transforms[t]["go"], {}).get("name", "?"))
        t = transforms[t]["father"]
        seen += 1
    return "/".join(reversed(parts))

pickle.dump({"gos": gos, "comps": comps, "transforms": transforms, "go2t": go2t,
             "paths": {g: path(g) for g in gos}}, open(out + ".pickle", "wb"))
print("gameobjects", len(gos), "components", len(comps))
