$ErrorActionPreference = 'Stop'

dotnet publish ResharperProfiler/ResharperProfiler.csproj -c Release -r win-x64
dotnet publish ResharperProfiler.ConsoleLauncher/ResharperProfiler.ConsoleLauncher.csproj -c Release -r win-x64

$launcherDir = "artifacts/publish/ResharperProfiler.ConsoleLauncher/release_win-x64"
Copy-Item "artifacts/publish/ResharperProfiler/release_win-x64/ResharperProfiler.dll" $launcherDir
