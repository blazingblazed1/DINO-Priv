DinoNet 0.2.5.7 - online co-op for Diplomacy is Not an Option
==============================================================

One shared town: everyone controls the same faction - same buildings, army, resources and research. Each player has their own camera. The host runs
the game; friends run it too and follow the host.

INSTALL (drag and drop, no mod loader)
--------------------------------------
Made for game version dno_1.0.187_r (the current Steam version).
1. Open the game folder: Steam > right-click Diplomacy is Not an Option > Manage > Browse local files.
2. Open this zip and drag BOTH folders from it into the game folder (next to "Diplomacy is Not an Option.exe"):
   "Diplomacy is Not an Option_Data" and "UserData". When Windows asks, choose "Replace the files" (2 small files are replaced).
3. Start the game normally. The main menu now has MULTIPLAYER.
Everyone who plays needs the same DinoNet version.

Had an older DinoNet with MelonLoader? Delete these from the game folder first: version.dll, the MelonLoader folder,
and Mods\DinoNet.dll (and Mods if it is empty). Otherwise DinoNet stays off.

After a game update, if MULTIPLAYER is gone from the menu: drag the zip in again.

UNINSTALL: Steam > right-click the game > Properties > Installed Files > "Verify integrity of game files". That restores the
2 replaced files; the extra DinoNet files are then simply ignored (delete them if you like).

LINUX / STEAM DECK (Proton)
---------------------------
Same install. No launch options needed.

HOW TO PLAY
-----------
Main menu: SINGLE PLAYER opens the normal game menu. MULTIPLAYER opens the co-op screen.

Make sure you have a game profile first (play once in single player, or press New Game there).

IROH (left panel): HOST GAME gives you a join code - press COPY JOIN CODE in the lobby and send it to your friends. Friends press JOIN GAME, paste the
code (it is pasted automatically if it is on the clipboard) and press CONNECT. Tick "LAN / direct only" for LAN or Hamachi.

STEAM (right panel): HOST GAME creates a Steam lobby. Friends can right-click you in their Steam friends list and pick "Join Game", or find your game
in the list on the Multiplayer screen (friends' games are always listed; set PUBLIC so anyone with the mod can see yours).

LOBBY: friends press READY. The host presses START (hold it for 2 seconds to start without everyone ready) and then picks ANY game in the normal menu:
campaign mission, endless, sandbox, special mission, or load a saved game. As soon as the host's game has loaded, it is sent to everyone and all
players load it together. Everyone stays frozen
for a few more seconds ("Matching units with friends") until each friend's game has matched the host's units, then the game starts.

IN GAME
-------
- Your own orders, building, training, research, market deals, spells and game speed are shared with everyone.
- MIDDLE-CLICK (a quick click) pings the map: everyone sees a pulsing ring in your color with your name, and hears a short chime.
  The chime plays at most once every 5 seconds per player; up to 3 rings per 5 seconds. Holding the middle button still moves the camera.
- The top-right panel shows the players, their ping and a connection word (good / ok / slow / lagging / lost). Small name tags show where your friends
  are pointing on the map.
- F9: host = re-sync everyone from the host's game. Friend = ask the host to re-sync you. Use it if things look different on your screen.
- F8: show sync statistics (how many of the host's units/buildings your game matched, Steam connection quality and queues).
- Every 10 seconds your game is compared with the host's (units, buildings, enemies, clock). If it stays different for 30 seconds,
  a bug report is saved automatically and a message tells you to press F9.
- F10: save a bug report right now (use it right after you see a desync).
- Unit orders (move, attack, patrol, hold, abilities), research, market deals, training units, building upgrades/repairs and game
  speed are decided by the host: when a friend
  clicks them, the host's game does it first and everyone follows (a friend sees the result after their ping).
- Opening the menu does not pause the game in co-op. Pause / speed buttons work for everyone.
- Friends who join or come back during a game get the host's game automatically (everyone is frozen for a moment while they load).
- When the host quits to the menu, everyone returns to the lobby. A friend who quits leaves the co-op session.
- Only the host can restart, change the mission or load a save.

SETTINGS
--------
UserData\DinoNet\settings.txt: name, max_players (2-8), lan_only, steam_public, host_upload_kbps (0 = automatic, 2400),
verbose_log (1 = extra logging), steam_transport (messages = new Steam networking, p2p = the older one used up to 0.0.3.4),
reports_on_desktop (1 = also put bug reports in a "DinoNet Reports" folder on the desktop, 0 = off),
auto_resync (1 = when your game stays out of sync, ask the host for a re-sync automatically; 0 = just tell me, default).


KNOWN LIMITS IN THIS VERSION
----------------------------
- In-game co-op is still early: expect desyncs. Every report helps.
- Placing buildings and unit orders still run on your own PC right away (so they feel instant). If your game and the host's
  disagree about resources, a building can appear for you but not for the others - F9 fixes it.
- Custom maps: friends need the same custom map installed.
- Direct control of a single unit, squads (Ctrl+number groups), section walls and drawn trap lines are local to each player.
- Quick save / quick load keys are off in co-op. The host can still load any save from the menu (everyone gets it).
- If a friend casts a spell while you are aiming one yourself, your aiming is cancelled - just pick the spell again.
- If two players place a building at the same moment, your own placement may be cancelled - just pick the building again.

IF SOMETHING GOES WRONG (crash, freeze, desync)
-----------------------------------------------
DinoNet keeps the last 6 minutes of what it saw in memory (orders, network messages, sync numbers, frame times, CPU and memory use,
Steam connection quality) and saves a bug report when something goes wrong: an error, a freeze of 8+ seconds, a lost connection or
a failed sync. Press F10 to save one by hand. If the game crashed outright, the report is saved the next time you start the game.
Each report starts with the PC's CPU, GPU, RAM, Windows version and graphics settings.

Where: Desktop\DinoNet Reports\<your player name>\ - one folder per player name, with the reports, a copy of the game's Unity log next to each
report, DinoNet.log and Player.log (kept up to date every minute in co-op, when a session ends and when you quit). Running several copies of the game on one
PC? Give each copy its own name (MULTIPLAYER menu name box, or name= in UserData\DinoNet\settings.txt) and each gets its own folder.
Also kept in UserData\DinoNet\Recordings\ in the game folder. Send the whole player folder. Old reports are deleted automatically.
