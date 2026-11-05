## Killer Frequency Access: a mod for Killer Frequency to make it playable by the blind

### What this does:
- Speaks game text through your screen reader (via Prism), or the Windows voice when none is running.
- Plays a rising tone when an object or a conversation's answers come into range, so you can use them, and a falling tone when they go out of range.
- Arrow keys move through conversation answers while they are on screen. WASD still walks.
- Object list: rooms, the objects in a room, and what is in or on each object, as deep as things are nested. End goes into the current entry, Home comes back out, Page Up and Page Down move through the current level. The list follows you: walking into a room lists its objects, and looking at a container lists what is in it. Backspace walks to the selected object; you hear its name and a tone once you can use it.

Note that the mod is incomplete and as of yet does not work. In particular, the objects in the studio are not being included in the list of scannable objects.

## How to play:
- Grab the game from https://www.team17.com › games › killer-frequency or from steam/epic games
- Grab Mellon Loader from https://melonloader.app/
- Build a release DLL or grab one from the releases page. Put `prism.dll` (from `native\win-x64`) next to the mod in the Mods folder: it speaks through NVDA, JAWS, Narrator and other screen readers, or the Windows voice when none is running.
- Install Mellon Loader and point it to your game folder.
- Copy the mod to the mods folder.
- Run the game.

Pull requests welcome!
Donations:
https://paypal.me/justsendyourcash
