$env:COR_PROFILER='{687FB688-F002-4B64-9A95-567E5502230F}'
$env:COR_ENABLE_PROFILING=1
$env:COR_PROFILER_PATH_64='E:\source\ResharperProfiler\ResharperProfiler\bin\Release\net10.0\win-x64\publish\ResharperProfiler.dll'
$env:RESHARPER_PROFILER_LOG_FILE='E:\Jetbrains\profiler.txt'

if (-not (Test-Path function:global:__originalPrompt)) {
    $currentPrompt = (Get-Item function:prompt).ScriptBlock
    Set-Item -Path function:global:__originalPrompt -Value $currentPrompt
}

function global:prompt {
    $base = if (Test-Path function:global:__originalPrompt) {
        & global:__originalPrompt
    }
    else {
        "PS $($executionContext.SessionState.Path.CurrentLocation)> "
    }

    $enabled = ($env:COR_ENABLE_PROFILING -eq '1') -and -not [string]::IsNullOrWhiteSpace($env:COR_PROFILER)
    if ($enabled) { "[ReSharper profiler] $base" } else { $base }
}