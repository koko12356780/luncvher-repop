param(
    [string]$GameVersion="0.11 Street Explorer",
    [string]$GameFolder=(Join-Path $PSScriptRoot '..\WindowsGame-v11'),
    [string]$OfficialRuntime=(Join-Path $PSScriptRoot '..\..\tools\godot\windows_release_x86_64.exe'),
    [string]$Destination=(Join-Path $PSScriptRoot '..\OpenStreets-GitHub-v1.6')
)
$ErrorActionPreference='Stop'
if($GameVersion -notmatch '^[0-9]+\.[0-9]+(?:\.[0-9]+)?(?: [A-Za-z0-9 ]+)?$'){throw 'Invalid game version.'}
$null=New-Item -ItemType Directory -Force -Path (Join-Path $Destination 'Game')
$gameTarget=Join-Path $Destination 'Game'
$names=@('OpenStreets.exe','OpenStreets.pck','GAME-LICENSE.txt','ENGINE-LICENSES.txt')
Copy-Item -LiteralPath $OfficialRuntime -Destination (Join-Path $gameTarget 'OpenStreets.exe')
foreach($name in @('OpenStreets.pck','GAME-LICENSE.txt','ENGINE-LICENSES.txt')) {
    Copy-Item -LiteralPath (Join-Path $GameFolder $name) -Destination (Join-Path $gameTarget $name)
}
$entries=foreach($name in $names) {
    $hash=(Get-FileHash -LiteralPath (Join-Path $gameTarget $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    '{"'+$name+'","'+$hash+'"}'
}
$officialHash=(Get-FileHash -LiteralPath $OfficialRuntime -Algorithm SHA256).Hash.ToLowerInvariant()
$entryText=$entries -join ",`n"
$info=@"
using System.Collections.Generic;
namespace OpenStreetsLauncher { static class BuildInfo {
public const string GameVersion="$GameVersion";
public const string DiscordApplicationId="1561040455100145775";
public const string OfficialRuntimeHash="$officialHash";
public static readonly Dictionary<string,string> Files=new Dictionary<string,string> {
$entryText
};
}}
"@
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'BuildInfo.cs'),$info)
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$common=@('/nologo','/target:winexe','/platform:x64','/optimize+',
    "/win32icon:$PSScriptRoot\app.ico", "/win32manifest:$PSScriptRoot\launcher.manifest",
    '/reference:System.Windows.Forms.dll,System.Drawing.dll,System.Web.Extensions.dll,System.IO.Compression.dll,System.IO.Compression.FileSystem.dll,System.Net.Http.dll',
    "/resource:$PSScriptRoot\hero.png,hero.png", "/resource:$PSScriptRoot\app.ico,app.ico")
$common+=@('Launcher.cs','GamePackage.cs','UpdatePackage.cs','DiscordPresence.cs','OnlineUpdates.cs','ReleasePublisher.cs','SelfUpdater.cs','BuildInfo.cs','AssemblyInfo.cs')|ForEach-Object{Join-Path $PSScriptRoot $_}
& $compiler "/out:$Destination\OpenStreetsLauncher.exe" @common
if($LASTEXITCODE -ne 0){throw 'Portable launcher build failed.'}
# Tests compile into a separate executable; no test routines ship to players.
& $compiler /define:TEST "/out:$Destination\LauncherTests.exe" @common (Join-Path $PSScriptRoot 'Verification.cs') (Join-Path $PSScriptRoot 'PresenceVerification.cs') (Join-Path $PSScriptRoot 'UpdateVerification.cs') (Join-Path $PSScriptRoot 'OnlineVerification.cs') (Join-Path $PSScriptRoot 'SelfUpdateVerification.cs')
if($LASTEXITCODE -ne 0){throw 'Test build failed.'}
Write-Output "Built portable release: $Destination"

& $compiler /nologo /target:winexe /platform:x64 "/out:$Destination\OldLauncherFixture.exe" (Join-Path $PSScriptRoot "SelfUpdateFixture.cs")
if($LASTEXITCODE -ne 0){throw "Self-update fixture build failed."}
