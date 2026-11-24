## Killer Frequency Access: a mod for Killer Frequency to make it playable by the blind

### What this does:
- Speaks game text through your screen reader (via Prism), or the Windows voice when none is running.
- Plays a rising tone when an object or a conversation's answers come into range, so you can use them, and a falling tone when they go out of range.
- Arrow keys move through conversation answers while they are on screen and in range. WASD still walks.
- Reads subtitles, the conversation question with each answer, menus and settings, and text on items (including the inside pages of inspected items).
- Describes pictures after their text: what the clippings on the alibi boards show, where every place and street is on the Gallows Creek map, the maize maze map with its way out, and the Save Clive polaroids, map markers and pin tacks when you inspect them.
- When you pick something up, says what you hold and every action you can take with it, with its keys.
- Object list: rooms, the objects in a room, and what is in or on each object, as deep as things are nested. End goes into the current entry, Home comes back out, Page Up and Page Down move through the current level. The list follows you: walking into a room lists its objects, and looking at a container lists what is in it. Closed cabinets, drawers and rooms show what is inside; only locked ones are hidden, and a locked safe still shows its keypad. Backspace walks to the selected object; you hear its name and a tone once you can use it. On the way it opens unlocked doors and cabinets, crouches through low gaps, stands you up to walk and crouches or stands at the end as the object needs, and goes around things in the way.
- Coverage sweep (for testing auto-walk): F8 walks to every object in turn, nearest first, and writes how each walk ended to `UserData\KillerFrequencyAccess\sweeps` in the game folder. F8 again, or opening a menu, stops it.

Version 1.0: menus, conversations, item text, picture descriptions, the object list and auto-walk all work. Please report anything that is silent or wrong, with MelonLoader\Latest.log.

## How to play:
- Grab the game from https://www.team17.com › games › killer-frequency or from steam/epic games
- Grab MelonLoader (0.7.1) from https://melonloader.app/
- Build a release DLL or grab one from the releases page. Put `prism.dll` (from `native\win-x64`) next to the mod in the Mods folder: it speaks through NVDA, JAWS, Narrator and other screen readers, or the Windows voice when none is running.
- Install MelonLoader and point it to your game folder.
- Copy the mod to the mods folder.
- Run the game.

Pull requests welcome!
Donations:
https://paypal.me/justsendyourcash
