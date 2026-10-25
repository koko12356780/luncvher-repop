# Open Streets Launcher

Windows launcher for the Open Streets driving/walking game.

## Players
Download the full game bundle from this repository's latest release, extract the complete folder, then open OpenStreetsLauncher.exe. The Game folder stays beside it.

- **Update Game** checks the latest game release, downloads it, verifies the transfer and installs it in one click.
- **Update Launcher** checks this repository, downloads a newer launcher, restarts and keeps the previous EXE as a backup. If the new window does not open successfully, the helper restores the old one.
- Saves, cars, coins, upgrades and preferences remain separate from both updates.

Game source/releases: https://github.com/koko12356780/open-world-sanboxcgame

## Development
Standard Windows .NET Framework C#, no third-party launcher runtime. build.ps1 accepts -GameFolder, -OfficialRuntime, -Destination and -GameVersion. Test code compiles into LauncherTests.exe and is excluded from player downloads.

Increase AssemblyVersion and AssemblyFileVersion in AssemblyInfo.cs, then push to main. GitHub Actions downloads the latest game data, verifies the official engine, builds/tests the launcher and publishes launcher-latest.json, OpenStreetsLauncher-update.zip and a complete first-install game bundle. No manual ZIP upload is needed. Existing releases are never overwritten.

OnlineUpdates.cs handles bounded HTTPS downloads and the fixed-file game importer. SelfUpdater.cs stages the verified launcher archive, starts a local copy of the current launcher as the replacement helper, waits for exit, atomically replaces only the launcher EXE and retains a backup. The restarted window acknowledges startup before the helper reports success. No engine executable is accepted through the game-update channel.

Unsigned local binaries may be subject to Windows download/reputation checks; no antivirus exceptions or security-setting changes are made by this project. Original source/art uses MIT. The engine's separate notices accompany the full game.
