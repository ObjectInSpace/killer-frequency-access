"""Build the spoken-name table for every Interactable in the ripped scenes.

Usage: python -I gen_names.py <out.cs> <review.txt> <scene.pickle>...

Each name comes from rip facts only: the object's own name and its parents' names (as the level
designers wrote them) plus a role taken from the components on the object (DoorOpenable -> door,
Handle -> handle, Button -> button, Holder -> holder, BoardItem -> board item). The C# file is keyed
by the full hierarchy path, which is what the mod rebuilds at runtime from Transform names.
"""
import pickle, re, sys

KEY = "Radio.Actions.dll:273980569"  # Actions.Interactables.Interactable

# Names that say nothing about the object; the walk up the hierarchy skips them.
GENERIC = {
    "general", "item", "item dock", "graspable handle", "door body", "door body left", "door body right",
    "door body01", "door body02", "door body 01", "door body 02", "top door body", "body door", "drawer",
    "container slot", "interactable button", "button", "movable", "movable docks", "docks", "functionality",
    "default orentation", "keypad", "board", "openable drawer", "dock", "handle", "visuals", "root",
    "general set dressing", "set dressing", "dressing", "hero assets", "hero props", "props", "functional",
    "interactables", "door", "safe", "container base single door",
}

DROP_TOKENS = {"p", "m", "sm", "hero", "prop", "props", "heroprop", "heroprops", "interactable",
               "set", "dressing", "setdressing", "core", "fs", "base", "openable"}

# Designer category words in front of a prop name ("OfficeClutter_Pen").
DROP_PREFIXES = ("office clutter ", "cleaning supplies ")


def unquote(name):
    if len(name) >= 2 and name[0] == "'" and name[-1] == "'":
        return name[1:-1].replace("''", "'")
    if len(name) >= 2 and name[0] == '"' and name[-1] == '"':
        return name[1:-1]
    return name


def clean(raw):
    s = re.sub(r"\s*\(\d+\)$", "", raw)           # Unity duplicate suffix "(1)"
    s = re.sub(r"([A-Z]+)([A-Z][a-z])", r"\1 \2", s)  # acronym then word: LPRecord -> LP Record
    s = re.sub(r"([a-z])([A-Z])", r"\1 \2", s)     # camelCase
    s = re.sub(r"([A-Za-z])(\d)", r"\1 \2", s)     # Button0 -> Button 0
    s = re.sub(r"[_\-]+", " ", s)
    toks = []
    for t in s.lower().split():
        if t not in DROP_TOKENS and t not in toks:  # drop noise and repeats ("Door_BaseDoor")
            toks.append(t)
    out = " ".join(toks)
    for pre in DROP_PREFIXES:
        if out.startswith(pre):
            out = out[len(pre):]
    return out


def role(scripts):
    if "DoorOpenable" in scripts:
        return "door"
    if "Handle" in scripts:
        return "handle"
    if "Button" in scripts:
        return "button"
    if "BoardItem" in scripts:
        return "board item"
    if "Holder" in scripts:
        return "holder"
    return ""


# Game components that mark a device. A holder, button or slider below one is a part of it and is
# named after it ("record player play button"). Found by tallying the components above every part
# in SCN_Main.
DEVICES = {
    "VinylPlayer": "record player",
    "CassettePlayer": "cassette player",
    "Dictaphone": "dictaphone",
    "SoundBoard": "soundboard",
    "FuseBox": "fuse box",
    "NumericKeypad": "keypad",
    "HandheldRadio": "handheld radio",
    "FloppyDiskComputer": "computer",
    "Lever": "lever",
}

PART_ROLES = (("Holder", "slot"), ("Button", "button"), ("Slideable", "slider"))

# Words in a part's own name that only repeat its role.
PART_NOISE = {"dock", "holder", "button", "slot", "slider", "functional", "fs"}


def part_role(scripts):
    for script, word in PART_ROLES:
        if script in scripts:
            return word
    return ""


def main():
    out_cs, review, *pickles = sys.argv[1:]
    table = {}
    rows = []
    for pk in pickles:
        d = pickle.load(open(pk, "rb"))
        gos, comps, transforms, go2t = d["gos"], d["comps"], d["transforms"], d["go2t"]

        def chain(gid):
            names, t, n = [], go2t.get(gid), 0
            while t and t in transforms and n < 64:
                names.append(unquote(gos[transforms[t]["go"]]["name"]))
                t = transforms[t]["father"]
                n += 1
            return names  # self first, root last

        def device_of(gid):
            """Spoken device name from the nearest ancestor carrying a DEVICES component, plus its number."""
            t, n = transforms[go2t[gid]]["father"], 0
            while t and t in transforms and n < 8:
                g = transforms[t]["go"]
                scripts = {comps[c]["script"] for c in gos[g]["comps"] if comps[c]["script"]}
                for script, label in DEVICES.items():
                    if script in scripts:
                        m = re.search(r"_(\d+)$", unquote(gos[g]["name"]))
                        return f"{label} {int(m.group(1))}" if m else label
                t = transforms[t]["father"]
                n += 1
            return ""

        for gid, o in gos.items():
            scripts = {comps[c]["script"] for c in o["comps"] if comps[c]["script"]}
            if KEY not in scripts:
                continue
            names = chain(gid)
            path = "/".join(reversed(names))
            device = device_of(gid)
            pr = part_role(scripts)
            if device and pr:
                own = [t for t in clean(names[0]).split() if t not in PART_NOISE and t not in device.split()]
                if not own:
                    # The distinguishing number can sit on a parent: Keypad/..._Button_0/InteractableButton.
                    for parent in names[1:4]:
                        m = re.search(r"_(\d+)$", parent)
                        if m:
                            own = [m.group(1)]
                            break
                spoken = " ".join([device] + own + [pr])
                table[path] = spoken
                rows.append((pk, spoken, path))
                continue
            r = role(scripts)
            desc = ""
            for n in names:
                c = clean(n)
                if c and c not in GENERIC:
                    desc = c
                    break
            if not desc:
                desc = clean(names[0]) or names[0]
            spoken = desc if (not r or r in desc) else f"{desc} {r}"
            if path in table and table[path] != spoken:
                print("CONFLICT", path, table[path], "|", spoken)
            table[path] = spoken
            rows.append((pk, spoken, path))

    with open(out_cs, "w", encoding="utf-8", newline="\n") as f:
        f.write("// <auto-generated>\n")
        f.write("// Generated by tools\\rip\\gen_names.py from the AssetRipper rip of the game's scenes.\n")
        f.write("// Do not edit by hand; fix the generator and rerun it.\n")
        f.write("// </auto-generated>\n")
        f.write("using System.Collections.Generic;\n\nnamespace Killer_Frequency_Access;\n\n")
        f.write("internal static class GameObjectNames\n{\n")
        f.write("    /// <summary>Spoken name per Interactable, keyed by full hierarchy path (root/.../object).</summary>\n")
        f.write("    internal static readonly Dictionary<string, string> ByPath = new()\n    {\n")
        for path in sorted(table):
            p = path.replace("\\", "\\\\").replace('"', '\\"')
            n = table[path].replace('"', '\\"')
            f.write(f'        ["{p}"] = "{n}",\n')
        f.write("    };\n}\n")
    with open(review, "w", encoding="utf-8") as f:
        for pk, spoken, path in sorted(rows):
            f.write(f"{spoken}\t{path}\n")
    print("names", len(table))


main()
