# Open Streets Launcher

Windows launcher 1.7.1 supports both native Unreal City Routes and legacy Godot releases.

Players: use **Update Launcher** first, then **Update Game**. New installations should download the full game bundle at https://github.com/koko12356780/open-world-sanboxcgame/releases/latest . The small launcher update contains no game files.

The launcher verifies every manifest hash, installs immutable game versions, and supports Restore Previous. Saves remain separate. Keep Game beside the launcher; a short folder such as C:\Games\OpenStreets is recommended because the Unreal executable has a Windows path-length limit. Native Discord controls are inside the game.

The original game baseline is pinned only for launcher regression tests. The release job can package the latest tested native Unreal game from its public, hash-checked full-game asset; the licensed Unreal engine build itself runs locally.

Original launcher source/art uses MIT. Engine and game notices ship separately. There are no passwords or bot tokens in the launcher. The Discord Application ID is public and presence is optional.
