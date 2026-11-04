"""List GameObjects that carry ControllerBaseInteractable, with sibling scripts."""
import pickle, sys, collections
d = pickle.load(open(sys.argv[1], "rb"))
gos, comps, paths = d["gos"], d["comps"], d["paths"]
KEY = "Radio.Actions.dll:273980569"
hits = [g for g, o in gos.items() if any(comps[c]["script"] == KEY for c in o["comps"])]
combo = collections.Counter()
rows = []
for g in hits:
    s = sorted({comps[c]["script"] for c in gos[g]["comps"] if comps[c]["script"] and comps[c]["script"] != KEY})
    combo[tuple(x for x in s if x.endswith("Interaction") or x in ("Button", "Handle", "DoorOpenable", "Holder", "BoardItem"))] += 1
    rows.append((paths[g], gos[g]["active"], ",".join(s)))
print("count", len(hits))
for k, v in combo.most_common(40):
    print(v, k)
print("=====")
for r in sorted(rows):
    print(" | ".join(map(str, r)))
