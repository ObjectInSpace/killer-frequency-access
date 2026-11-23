# Rip tools

Offline tools that read the AssetRipper export of the game (`D:\KFRip\out\ExportedProject\Assets`).
Run Python with `-I`. Scene pickles are large; keep them in a scratch folder, not in the repo.

- `scene_parse.py <scene.unity> guidmap.txt <out_prefix>`: parse a scene into `<out_prefix>.pickle`
  (GameObjects, components, hierarchy, script names).
- `q_interactables.py <pickle>`: list every `Interactable` with its scripts (`docs\rip\interactables_*.txt`).
- `gen_names.py <out.cs> <review.txt> <pickle>...`: build `Generated\GameObjectNames.cs` and
  `docs\rip\spoken_names.txt`. Pass the SCN_Main, SCN_Intro, SCN_Epilogue and SCN_Menu pickles.
- `gen_navmesh.py <NavMesh.asset> <out.navbin.gz>`: the walking graph (NavMesh polygons and their
  neighbour links) the mod routes over. Assets are in `NavMeshData\`: `NavMesh_0` (SCN_Main),
  `NavMesh_1` (SCN_Intro, SCN_Menu), `NavMesh` (SCN_Epilogue). Write them to `Generated\Nav\` with the
  asset's name; they are embedded in the DLL.
- `fileid.py <Namespace.Class>...`: the `m_Script` fileID Unity uses for a class from a DLL.
- `guidmap.txt`: script GUID to script file, from the rip's `.meta` files.

These read the game's own files with UnityPy (the rip has no textures and no string values):

- `extract_textures.py <KillerFrequency_Data> <out_dir> <Material,...>`: save the named materials'
  base textures as PNG (to describe pictures by hand for `ImageDescriptions.cs`).
- `dump_strings.py <StreamingAssets/aa/StandaloneWindows64> <out.json>`: every English string as
  `Collection:Key` -> text (dialogue, GameText panels).
- `gen_gametext_keys.py <StreamingAssets/aa/StandaloneWindows64> <out.cs>`: `Generated\GameTextKeys.cs`,
  GameText key id -> entry name.

After a game update: re-rip, rerun `scene_parse.py` for the four scenes, then `gen_names.py` and `gen_navmesh.py`.
