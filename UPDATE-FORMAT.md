# Game update ZIP format 1

Keep the official Godot 4.6.1 Windows runtime used by v1.4. Export a new OpenStreets.pck; do not rename the product's save directory when updating game content. Package these files at ZIP root or inside one wrapper folder:

```
CHECKSUMS.sha256
update-info.json
Game/OpenStreets.pck
Game/GAME-LICENSE.txt
Game/ENGINE-LICENSES.txt
```

`update-info.json` example:

```json
{"game":"Open Streets","version":"0.9 Example"}
```

CHECKSUMS.sha256 is UTF-8, with four lines of lowercase SHA-256, two ASCII spaces, then relative path. It identifies all four game files even though the engine EXE is omitted from the update. Use the following fixed engine line and generate the other hashes from the new data files:

```
6a0266cb7571aa4d437a32094acd353f020c77dcf7ff5a3305ae45d0609e5c20  Game/OpenStreets.exe
<64 hex characters>  Game/OpenStreets.pck
<64 hex characters>  Game/GAME-LICENSE.txt
<64 hex characters>  Game/ENGINE-LICENSES.txt
```

The engine is copied from the user's existing, hash-verified official runtime. The archive's engine EXE, if present in an older full Windows game ZIP, is ignored. Future engine versions require a new launcher. No launcher EXE, script, executable installer or network address is needed in an update ZIP.

The full v0.8 Citylight Windows ZIP is also accepted: it already contains the same four game manifest paths. Without update-info.json its filename becomes the displayed version label. A Godot source project ZIP is not a game update.

After exporting on an automated Windows build machine, verify the exported PCK by running it with the official release template. Set APPDATA, TEMP and TMP to writable build directories so project settings export successfully. Test the final ZIP with the separately compiled `LauncherTests.exe --verify-import` command. Checksums are unsigned integrity metadata; distribute updates through a source the player trusts.
