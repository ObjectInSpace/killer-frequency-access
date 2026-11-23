"""Save the textures of the named materials as PNG, straight from the game's data files.
The AssetRipper rip has no textures (Texture2D is empty), so this reads the game with UnityPy.
Usage: python -I extract_textures.py <KillerFrequency_Data> <out_dir> <Material1,Material2,...>
"""
import UnityPy, sys, os, glob

data, out, names = sys.argv[1], sys.argv[2], set(sys.argv[3].split(','))
files = [f for f in glob.glob(os.path.join(data, '*')) if os.path.isfile(f) and
         (f.endswith('.assets') or os.path.basename(f).startswith('level') or f.endswith('globalgamemanagers'))]
for obj in UnityPy.load(*files).objects:
    if obj.type.name != 'Material':
        continue
    m = obj.read()
    if m.m_Name not in names:
        continue
    for slot, env in m.m_SavedProperties.m_TexEnvs:
        if slot != '_BaseMap' or env.m_Texture.m_PathID == 0:
            continue
        t = env.m_Texture.read()
        t.image.save(os.path.join(out, f'{m.m_Name}__{t.m_Name}.png'))
        print(m.m_Name, t.m_Name, t.m_Width, t.m_Height)
