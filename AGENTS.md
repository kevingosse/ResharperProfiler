# AGENTS.md

## Scope

These instructions apply to the entire repository.

## Project Overview

This repository contains a .NET 10 ReSharper/Visual Studio profiling tool:

- `ResharperProfiler/` contains the profiler implementation. It is published AOT, allows unsafe code, and uses Silhouette/dnlib to hook CLR profiler callbacks and instrument methods.
- `ResharperProfiler.Protocol/` contains the named-pipe protocol shared by the profiler and launcher.
- `ResharperProfiler.ConsoleLauncher/` contains the executable launcher and result reporting logic.

The solution file is `ResharperProfiler.slnx`. `Directory.Build.props` enables `UseArtifactsOutput`, so normal build and publish outputs also appear under `artifacts/`.

## Build and Run

Use PowerShell from the repository root.

- Build the solution:
  ```powershell
  dotnet build --no-restore
  ```
- Publish the runnable profiler bundle:
  ```powershell
  .\Build.ps1
  ```
- Launch the published console launcher:
  ```powershell
  .\Launch.ps1 -- <executable> [args...]
  ```

`Build.ps1` publishes `ResharperProfiler` and `ResharperProfiler.ConsoleLauncher` for `win-x64`, then copies `ResharperProfiler.dll` next to the launcher in `artifacts/publish/ResharperProfiler.ConsoleLauncher/release_win-x64`.

There is currently no dedicated test project in the solution. For changes that affect behavior, at minimum run a build; for launcher/profiler changes, prefer a publish because AOT and runtime identifier issues may not show up in a plain build.

## Generated Files

Do not edit generated or build output files directly. In particular, treat these as disposable output:

- `artifacts/`
- `bin/`
- `obj/`
- `_ReSharper.Caches/`
- `.vs/`

Keep changes focused on source files, project files, scripts, and documentation unless the user explicitly asks otherwise.

## Coding Conventions

- Keep protocol changes synchronized across `ResharperProfiler.Protocol`, `ResharperProfiler`, and `ResharperProfiler.ConsoleLauncher`.
- When adding launcher options, update the usage text in `ResharperProfiler.ConsoleLauncher/Program.cs`.
- Keep comments useful and sparse. Existing comments document profiler hook intent and version-specific ReSharper behavior; maintain that level of context for similar code.

## Environment Notes

- This is Windows-oriented code. Profiler activation, `win-x64` publish output, and User32 process-window APIs are expected parts of the workflow.
- `EnableProfiler.ps1` contains local absolute paths and environment variables for manual profiler activation. Do not assume those paths are portable.
