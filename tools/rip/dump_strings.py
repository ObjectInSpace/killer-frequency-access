"""Dump the game's English string tables as "Collection:Key" -> text (JSON).
Usage: python -I dump_strings.py <StreamingAssets/aa/StandaloneWindows64> <out.json>
Reads localization-string-tables-english(en)_*.bundle (values) and
localization-assets-shared_*.bundle (key names) with UnityPy.
"""
import UnityPy, sys, json, glob, os

aa, out = sys.argv[1:3]

def monos(bundle):
    for obj in UnityPy.load(bundle).objects:
        if obj.type.name == 'MonoBehaviour':
            try:
                yield obj.read_typetree()
            except Exception:
                pass

en = glob.glob(os.path.join(aa, 'localization-string-tables-english(en)_*.bundle'))[0]
shared = glob.glob(os.path.join(aa, 'localization-assets-shared_*.bundle'))[0]
tables = {t['m_Name']: t for t in monos(en) if 'm_TableData' in t}
keys = {t['m_TableCollectionName']: {e['m_Id']: e['m_Key'] for e in t['m_Entries']}
        for t in monos(shared) if 'm_Entries' in t and 'm_TableCollectionName' in t}

strings = {}
for coll, ids in keys.items():
    t = tables.get(coll + '_en')
    if t:
        for e in t['m_TableData']:
            strings[coll + ':' + ids.get(e['m_Id'], str(e['m_Id']))] = e['m_Localized']
json.dump(strings, open(out, 'w', encoding='utf-8'), indent=1, ensure_ascii=False)
print(len(strings), 'strings')
