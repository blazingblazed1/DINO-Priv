# DinoNet

Online co-op for **Diplomacy is Not an Option**: 2–8 players (default 4) run one shared kingdom.
The host's game is the reference; guests run the full game from the same save, player commands are
relayed, and the host corrects drift.

- Loader: none. Unity starts DinoNet itself from its start-up lists; Harmony patches the game (game: Unity 2021.3.45f2, Mono, Entities 0.51, version dno_1.0.187_r)
- Networking: NetKit (Rust, `netkit.dll`): Iroh join codes and Steam lobbies
- Current version: **0.2.5.5**

## Download
Download `download/DinoNet-0.2.5.5.zip` from this repository (open it, then "Download raw file"). It's a full drag-and-drop package
(`Diplomacy is Not an Option_Data` + `UserData` folders): open the zip and drag both folders into the game folder, replacing the 2 files Windows asks about.
See `README-DinoNet.txt` for player instructions.

## Source layout
| Folder | What |
|---|---|
| `src/Core` | mod entry, settings, logging, flight recorder / bug reports |
| `src/Net` | session, Steam lobbies/transport, message routing |
| `src/NetKit` | C# binding for the native NetKit library |
| `src/Game` | Harmony patches, command relay, construction, world sync, start/resync by save transfer, desync check |
| `src/UI` | co-op menu, lobby, toasts |

## Hooks (Harmony)
- `UIInputHandler.OnUpdateSimulation`: capture local commands (prefix) / inject friends' commands (postfix)
- `ConstructionSystem.OnUpdateSimulation`, `InitializeConstruction`, `ProcessBuildCommand`: two-step builds, orientation, results
- `SpellTargetSystem.OnUpdateSimulation`: friend's spell target point
- `SessionTimeUpdateSystem.ProcessTimeTask`: no window-pause in co-op
- `MainMenu` (Awake, ShowAsInitialState, UpdateProfileState, QuitToMenu, StartNewMissionFromGame), `SavedGames.LoadGame`: co-op menu, host-only restart/load
- `ComponentSystemGroup.OnUpdate` (TransformSystemGroup only): guests draw enemies at the host's position

## Building
`build.sh` compiles against the game's `Managed` assemblies and Harmony (net35)
(paths at the top of the script; set them for your machine). `package.sh` builds the drag-and-drop zip (`tools/addlists.py` adds DinoNet to the game's `ScriptingAssemblies.json` and `RuntimeInitializeOnLoads.json`). `src/Core/Boot.cs` is the start-up entry.
The game's own assemblies are not included in this repository.
