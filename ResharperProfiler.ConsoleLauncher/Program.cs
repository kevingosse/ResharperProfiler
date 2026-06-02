using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ResharperProfiler.Protocol;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ResharperProfiler.ConsoleLauncher;

static class Program
{
    private const string ProfilerGuid = "{687FB688-F002-4B64-9A95-567E5502230F}";
    private static long _totalFreezeTime;
    private static long _maxFreezeTime;
    private static long _startupTime;
    private static bool _debug;
    private static string? _debugLogPath;
    private static string? _debugLogRootPath;
    private static bool _debugLogPathIsDirectory;
    private static readonly object DebugLogLock = new();

    private record RunResult(long SolutionLoad, long TotalFreezeTime, long MaxFreezeTime, long CloseTime);

    private static readonly ManualResetEventSlim SolutionListenerReady = new();
    private static readonly ManualResetEventSlim SolutionLoaded = new();
    private static volatile bool _backendConnected;
    private static long _backendLateLoadTime;
    private static volatile bool _backendDaemonSeen;
    private static int _lateLoadTaskAnnounced;
    private static int _daemonFinishedAnnounced;
    private static int _daemonStartedAnnounced;

    private static readonly ConcurrentQueue<double> Latencies = new(); // in ms
    private static readonly List<Server> Servers = new();

    static int Main(string[] args)
    {
        string? outputPath = null;
        string? benchmarkOutputPath = null;
        string? productVersion = null;
        string benchmarkDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
        int runs = 1;
        int shutdownTimeoutSeconds = 60;
        bool noFreezes = false;
        bool forceFocus = false;

        var i = 0;
        for (; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--debug":
                    _debug = true;
                    break;
                case "--debug-log":
                    _debugLogPath = args[++i];
                    break;
                case "--output":
                    outputPath = args[++i];
                    break;
                case "--benchmark-output":
                    benchmarkOutputPath = args[++i];
                    break;
                case "--product-version":
                    productVersion = args[++i];
                    break;
                case "--date":
                    benchmarkDate = args[++i];
                    break;
                case "--runs":
                    runs = int.Parse(args[++i]);
                    if (runs < 1)
                    {
                        Console.Error.WriteLine("--runs must be at least 1");
                        return 1;
                    }
                    break;
                case "--timeout":
                    shutdownTimeoutSeconds = int.Parse(args[++i]);
                    if (shutdownTimeoutSeconds < 1)
                    {
                        Console.Error.WriteLine("--timeout must be at least 1");
                        return 1;
                    }
                    break;
                case "--no-freezes":
                    noFreezes = true;
                    break;
                case "--force-focus":
                    forceFocus = true;
                    break;
                case "--":
                    i++;
                    goto done;
                default:
                    goto done;
            }
        }
    done:

        args = args[i..];

        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: ResharperProfiler.ConsoleLauncher [options] -- <executable> [args...]");
            Console.Error.WriteLine("Options: --debug, --debug-log <path>, --output <path>, --benchmark-output <path>, --product-version <ver>,");
            Console.Error.WriteLine("         --date <yyyy-MM-dd>, --runs <n>, --timeout <seconds>, --no-freezes, --force-focus");
            return 1;
        }

        if (_debugLogPath is not null)
        {
            PrepareDebugLog(_debugLogPath);
            WriteDebugLog("Launcher debug logging enabled");
            WriteDebugLog($"Command line: {Environment.CommandLine}");
            WriteDebugLog($"Working directory: {Environment.CurrentDirectory}");
            WriteDebugLog($"Process: {Environment.ProcessPath ?? "unknown"} (pid {Environment.ProcessId})");
            WriteDebugLog($"UserInteractive={Environment.UserInteractive}, SessionId={Process.GetCurrentProcess().SessionId}");
        }

        var profilerDll = Path.Combine(AppContext.BaseDirectory, "ResharperProfiler.dll");

        if (!File.Exists(profilerDll))
        {
            Console.Error.WriteLine($"Profiler DLL not found: {profilerDll}");
            Console.Error.WriteLine("Run Build.ps1 first.");
            return 1;
        }

        var allResults = new List<RunResult>();
        var totalStopwatch = Stopwatch.StartNew();

        for (var run = 0; run < runs; run++)
        {
            if (runs > 1)
                AnsiConsole.MarkupLine($"\n[bold]═══ Run {run + 1}/{runs} ═══[/]\n");

            var result = ExecuteRun(profilerDll, args, shutdownTimeoutSeconds, noFreezes, forceFocus, run);

            if (result is null)
                return 1;

            allResults.Add(result);
        }

        totalStopwatch.Stop();

        if (runs > 1)
            PrintSummary(allResults, totalStopwatch.Elapsed, noFreezes);

        var lastResult = allResults[^1];

        if (outputPath is not null)
        {
            var results = new Dictionary<string, long>
            {
                ["solutionLoad"] = lastResult.SolutionLoad,
                ["closeTime"] = lastResult.CloseTime,
            };

            if (!noFreezes)
            {
                results["totalFreezeTime"] = lastResult.TotalFreezeTime;
                results["maxFreezeTime"] = lastResult.MaxFreezeTime;
            }

            File.WriteAllText(outputPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        }

        if (benchmarkOutputPath is not null)
        {
            var benchmarkResults = new Dictionary<string, object>
            {
                ["version"] = productVersion ?? "unknown",
                ["date"] = benchmarkDate,
                ["solutionLoad"] = lastResult.SolutionLoad,
                ["closeTime"] = lastResult.CloseTime,
            };

            if (!noFreezes)
                benchmarkResults["totalFreezeTime"] = lastResult.TotalFreezeTime;

            File.WriteAllText(benchmarkOutputPath, JsonSerializer.Serialize(benchmarkResults, new JsonSerializerOptions { WriteIndented = true }));
        }

        return 0;
    }

    static RunResult? ExecuteRun(string profilerDll, string[] args, int shutdownTimeoutSeconds, bool noFreezes, bool forceFocus, int runIndex)
    {
        WriteDebugLog($"Run {runIndex + 1} starting");

        // Reset per-run state
        _totalFreezeTime = 0;
        _maxFreezeTime = 0;
        SolutionListenerReady.Reset();
        SolutionLoaded.Reset();
        _backendConnected = false;
        Interlocked.Exchange(ref _backendLateLoadTime, 0);
        _backendDaemonSeen = false;
        _lateLoadTaskAnnounced = 0;
        _daemonFinishedAnnounced = 0;
        _daemonStartedAnnounced = 0;
        Latencies.Clear();
        Servers.Clear();

        var pipeName = $"ResharperProfiler_{Guid.NewGuid():N}";
        WriteDebugLog($"Run {runIndex + 1}: pipeName={pipeName}, noFreezes={noFreezes}, forceFocus={forceFocus}, target={args[0]}");

        // Start the pipe server (constructor blocks until the profiler connects)
        Server? server;
        Exception? serverError = null;
        var serverReady = new ManualResetEventSlim();

        new Thread(() =>
        {
            try
            {
                WriteDebugLog($"Run {runIndex + 1}: waiting for devenv profiler pipe connection");
                server = new Server(pipeName);
                server.MessageReceived += (ts, type, payload) => OnMessageReceived(ts, type, payload, isBackend: false);
                server.Error += (context, ex) =>
                {
                    WriteDebugLog($"Run {runIndex + 1}: [PIPE ERROR] devenv: {context}: {ex}");
                    Console.Error.WriteLine($"[PIPE ERROR] devenv: {context}: {ex}");
                };
                server.Disconnected += () =>
                {
                    WriteDebugLog($"Run {runIndex + 1}: [PIPE] devenv disconnected; SolutionLoaded={SolutionLoaded.IsSet}");
                    if (_debug || !SolutionLoaded.IsSet)
                        Console.Error.WriteLine("[PIPE] devenv disconnected");
                };

                Servers.Add(server);
                server.Start();

                // Start a second server instance for the OOP backend
                AcceptBackendConnection(pipeName);
            }
            catch (Exception ex)
            {
                WriteDebugLog($"Run {runIndex + 1}: devenv pipe server failed: {ex}");
                serverError = ex;
            }
            finally
            {
                serverReady.Set();
            }
        })
        { IsBackground = true }.Start();

        // Launch the target process with profiler environment variables
        var psi = new ProcessStartInfo(args[0]) { UseShellExecute = false };

        for (var j = 1; j < args.Length; j++)
            psi.ArgumentList.Add(args[j]);

        psi.Environment["COR_PROFILER"] = ProfilerGuid;
        psi.Environment["COR_ENABLE_PROFILING"] = "1";
        psi.Environment["COR_PROFILER_PATH_64"] = profilerDll;
        psi.Environment["CORECLR_PROFILER"] = ProfilerGuid;
        psi.Environment["CORECLR_ENABLE_PROFILING"] = "0";
        psi.Environment["CORECLR_PROFILER_PATH_64"] = profilerDll;
        psi.Environment["RESHARPER_PROFILER_PIPE_NAME"] = pipeName;
        if (noFreezes)
            psi.Environment["RESHARPER_PROFILER_NO_FREEZES"] = "1";
        if (forceFocus)
            psi.Environment["RESHARPER_PROFILER_FORCE_FOCUS"] = "1";
        var profilerLogPath = GetProfilerLogPath(runIndex);
        if (profilerLogPath is not null)
        {
            File.WriteAllText(profilerLogPath, string.Empty);
            psi.Environment["RESHARPER_PROFILER_LOG_FILE"] = profilerLogPath;
            psi.Environment["RESHARPER_PROFILER_LOG_APPEND"] = "1";
            psi.Environment["RESHARPER_PROFILER_DEBUG_LOG"] = "1";
            WriteDebugLog($"Run {runIndex + 1}: profiler log path={profilerLogPath}");
        }

        WriteDebugLog($"Run {runIndex + 1}: launching target");
        var process = Process.Start(psi);

        if (process is null)
        {
            WriteDebugLog($"Run {runIndex + 1}: Process.Start returned null");
            Console.Error.WriteLine($"Failed to start: {args[0]}");
            return null;
        }

        _startupTime = Environment.TickCount64;
        WriteDebugLog($"Run {runIndex + 1}: started process {process.Id}, SessionId={GetProcessSessionId(process)}");

        if (_debug)
        {
            Console.WriteLine($"Started process {process.Id}, waiting for profiler to connect...");

            while (!serverReady.Wait(1000))
            {
                if (process.HasExited)
                {
                    WriteDebugLog($"Run {runIndex + 1}: process exited before profiler connected, exitCode={process.ExitCode}");
                    Console.Error.WriteLine($"Process exited ({process.ExitCode}) before profiler connected.");
                    return null;
                }
            }

            if (serverError is not null)
            {
                WriteDebugLog($"Run {runIndex + 1}: pipe server error before connect: {serverError}");
                Console.Error.WriteLine($"Pipe server error: {serverError.Message}");
                process.Kill();
                return null;
            }

            WriteDebugLog($"Run {runIndex + 1}: profiler connected (devenv)");
            Console.WriteLine("Profiler connected (devenv).");

            process.WaitForExit();
            WriteDebugLog($"Run {runIndex + 1}: debug run process exited with code {process.ExitCode}; totalFreeze={_totalFreezeTime}, maxFreeze={_maxFreezeTime}");
            DisposeAllServers();
            return new RunResult(0, _totalFreezeTime, _maxFreezeTime, 0);
        }

        // Startup phase
        var startupOk = true;
        var ungracefulExit = false;
        long totalTime = 0;
        long closeTime = 0;

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Starting...", ctx =>
            {
                WriteStep($"Process [bold]{process.Id}[/] started", true);

                ctx.Status("Waiting for profiler to connect...");
                while (!serverReady.Wait(1000))
                {
                    WriteDebugLog($"Run {runIndex + 1}: still waiting for profiler connection; processExited={process.HasExited}");
                    if (process.HasExited)
                    {
                        WriteDebugLog($"Run {runIndex + 1}: process exited before profiler connected, exitCode={process.ExitCode}");
                        Console.Error.WriteLine($"Process exited ({process.ExitCode}) before profiler connected.");
                        startupOk = false;
                        return;
                    }
                }

                if (serverError is not null)
                {
                    WriteDebugLog($"Run {runIndex + 1}: pipe server error before connect: {serverError}");
                    Console.Error.WriteLine($"Pipe server error: {serverError.Message}");
                    process.Kill();
                    startupOk = false;
                    return;
                }

                WriteStep("Profiler connected (devenv)", true);
                WriteDebugLog($"Run {runIndex + 1}: profiler connected (devenv)");

                ctx.Status("Waiting for solution listener...");
                WaitForAny(SolutionListenerReady, process);
                if (SolutionListenerReady.IsSet)
                    WriteStep("Solution listener ready", true);

                ctx.Status("Waiting for solution to load...");
                WaitForAny(SolutionLoaded, process);

                if (!SolutionLoaded.IsSet)
                {
                    WriteDebugLog($"Run {runIndex + 1}: solution did not load before process exit; processExited={process.HasExited}");
                    startupOk = false;
                    return;
                }

                var backendLlt = Interlocked.Read(ref _backendLateLoadTime);
                totalTime = (_backendConnected && backendLlt > 0 && !_backendDaemonSeen)
                    ? backendLlt - _startupTime
                    : Environment.TickCount64 - _startupTime;
                var freezeLine = noFreezes
                    ? "           Total UI freeze: [yellow]disabled (--no-freezes)[/]"
                    : $"           Total UI freeze: [bold]{_totalFreezeTime}[/] ms (max: [bold]{_maxFreezeTime}[/] ms)";
                AnsiConsole.MarkupLine(
                    $"  [dim]{totalTime / 1000.0,6:F1}s[/]  [green]✓[/] Solution loaded [dim]({totalTime / 1000.0:F1}s total)[/]\n" +
                    freezeLine);
                WriteDebugLog($"Run {runIndex + 1}: solution loaded; totalTime={totalTime}, totalFreeze={_totalFreezeTime}, maxFreeze={_maxFreezeTime}, backendConnected={_backendConnected}, backendLateLoadTime={backendLlt}, backendDaemonSeen={_backendDaemonSeen}");

                var closeStartTime = Environment.TickCount64;
                ctx.Status("Waiting for process to exit...");
                CloseProcessWindows(process.Id);

                if (!process.WaitForExit(shutdownTimeoutSeconds * 1000))
                {
                    WriteDebugLog($"Run {runIndex + 1}: VS did not exit within {shutdownTimeoutSeconds}s; killing");
                    AnsiConsole.MarkupLine("  [yellow]⚠[/] VS did not exit gracefully, killing...");
                    process.Kill();
                    process.WaitForExit();
                    ungracefulExit = true;
                }

                closeTime = Environment.TickCount64 - closeStartTime;
                WriteDebugLog($"Run {runIndex + 1}: process exited; exitCode={process.ExitCode}, closeTime={closeTime}");
                AnsiConsole.MarkupLine($"  [dim]{(Environment.TickCount64 - _startupTime) / 1000.0:F1}s[/]  [green]✓[/] Process exited in {closeTime / 1000.0,6:F1}s with code {process.ExitCode}");
            });

        if (!startupOk)
        {
            DisposeAllServers();
            return null;
        }

        if (ungracefulExit)
        {
            DisposeAllServers();
            return null;
        }

        DisposeAllServers();
        WriteDebugLog($"Run {runIndex + 1}: completed; totalTime={totalTime}, totalFreeze={_totalFreezeTime}, maxFreeze={_maxFreezeTime}, closeTime={closeTime}");
        return new RunResult(totalTime, _totalFreezeTime, _maxFreezeTime, closeTime);
    }

    static void AcceptBackendConnection(string pipeName)
    {
        new Thread(() =>
        {
            try
            {
                WriteDebugLog("Waiting for backend profiler pipe connection");
                var backend = new Server(pipeName);
                _backendConnected = true;
                backend.MessageReceived += (ts, type, payload) => OnMessageReceived(ts, type, payload, isBackend: true);
                backend.Error += (context, ex) =>
                {
                    WriteDebugLog($"[PIPE ERROR] backend: {context}: {ex}");
                    Console.Error.WriteLine($"[PIPE ERROR] backend: {context}: {ex}");
                };
                backend.Disconnected += () =>
                {
                    WriteDebugLog($"[PIPE] backend disconnected; SolutionLoaded={SolutionLoaded.IsSet}");
                    if (_debug || !SolutionLoaded.IsSet)
                        Console.Error.WriteLine("[PIPE] backend disconnected");
                };
                backend.Start();
                Servers.Add(backend);

                if (_debug)
                    Console.WriteLine("Backend profiler connected.");
                else
                    WriteStep("Profiler connected (backend)", true);
                WriteDebugLog("Backend profiler connected");
            }
            catch (Exception ex)
            {
                // Backend may never connect if OOP is not enabled - this is expected
                WriteDebugLog($"Backend pipe server ended: {ex}");
                if (_debug)
                    Console.WriteLine($"Backend pipe server ended: {ex.Message}");
            }
        })
        { IsBackground = true, Name = "BackendPipeServer" }.Start();
    }

    static void DisposeAllServers()
    {
        foreach (var s in Servers)
            s.Dispose();
        Servers.Clear();
    }

    static void PrintSummary(List<RunResult> results, TimeSpan totalDuration, bool noFreezes)
    {
        AnsiConsole.MarkupLine("\n[bold]═══ Summary ═══[/]\n");

        if (noFreezes)
            AnsiConsole.MarkupLine("[yellow]UI freeze detection disabled (--no-freezes)[/]\n");

        var table = new Table();
        table.AddColumn("Metric");
        table.AddColumn(new TableColumn("Min").RightAligned());
        table.AddColumn(new TableColumn("Max").RightAligned());
        table.AddColumn(new TableColumn("Avg").RightAligned());
        table.AddColumn(new TableColumn("Median").RightAligned());

        AddMetricRow(table, "Solution Load (ms)", results.Select(r => r.SolutionLoad).ToList());
        if (!noFreezes)
        {
            AddMetricRow(table, "Total Freeze (ms)", results.Select(r => r.TotalFreezeTime).ToList());
            AddMetricRow(table, "Max Freeze (ms)", results.Select(r => r.MaxFreezeTime).ToList());
        }
        AddMetricRow(table, "Close Time (ms)", results.Select(r => r.CloseTime).ToList());

        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine($"\n  [dim]Runs: {results.Count}  |  Total duration: {totalDuration.TotalSeconds:F1}s[/]");

        // Per-run details
        var detailTable = new Table();
        detailTable.AddColumn("Run");
        detailTable.AddColumn(new TableColumn("Solution Load").RightAligned());
        if (!noFreezes)
        {
            detailTable.AddColumn(new TableColumn("Total Freeze").RightAligned());
            detailTable.AddColumn(new TableColumn("Max Freeze").RightAligned());
        }
        detailTable.AddColumn(new TableColumn("Close Time").RightAligned());

        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            if (noFreezes)
            {
                detailTable.AddRow(
                    $"{i + 1}",
                    $"{r.SolutionLoad} ms",
                    $"{r.CloseTime} ms");
            }
            else
            {
                detailTable.AddRow(
                    $"{i + 1}",
                    $"{r.SolutionLoad} ms",
                    $"{r.TotalFreezeTime} ms",
                    $"{r.MaxFreezeTime} ms",
                    $"{r.CloseTime} ms");
            }
        }

        AnsiConsole.Write(detailTable);
    }

    static void AddMetricRow(Table table, string label, List<long> values)
    {
        var sorted = values.OrderBy(x => x).ToList();
        var min = sorted[0];
        var max = sorted[^1];
        var avg = sorted.Average();
        var median = sorted[sorted.Count / 2];

        table.AddRow(label, $"{min}", $"{max}", $"{avg:F0}", $"{median}");
    }

    static void PrepareDebugLog(string path)
    {
        _debugLogPathIsDirectory = Directory.Exists(path) || string.IsNullOrEmpty(Path.GetExtension(path));

        if (_debugLogPathIsDirectory)
        {
            Directory.CreateDirectory(path);
            _debugLogRootPath = path;
            _debugLogPath = Path.Combine(path, "launcher.log");
        }
        else
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            _debugLogPath = path;
            _debugLogRootPath = directory ?? Environment.CurrentDirectory;
        }

        File.WriteAllText(_debugLogPath, string.Empty);
    }

    static string? GetProfilerLogPath(int runIndex)
    {
        if (_debugLogPath is null)
            return null;

        if (_debugLogPathIsDirectory)
            return Path.Combine(_debugLogRootPath!, $"profiler-run-{runIndex + 1}.log");

        var directory = Path.GetDirectoryName(Path.GetFullPath(_debugLogPath));
        var fileName = Path.GetFileNameWithoutExtension(_debugLogPath);
        var extension = Path.GetExtension(_debugLogPath);
        return Path.Combine(directory ?? Environment.CurrentDirectory, $"{fileName}.profiler-run-{runIndex + 1}{extension}");
    }

    static void WriteDebugLog(string message)
    {
        if (_debugLogPath is null)
            return;

        lock (DebugLogLock)
        {
            File.AppendAllText(_debugLogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
    }

    static string GetProcessSessionId(Process process)
    {
        try
        {
            return process.SessionId.ToString();
        }
        catch (Exception ex)
        {
            return $"unknown ({ex.GetType().Name}: {ex.Message})";
        }
    }

    static void WriteStep(string message, bool success)
    {
        var elapsed = (Environment.TickCount64 - _startupTime) / 1000.0;

        var symbol = success ? "[green]✓[/]" : "[red]X[/]";

        AnsiConsole.MarkupLine($"  [dim]{elapsed,6:F1}s[/]  {symbol} {message}");
        WriteDebugLog($"STEP success={success}: {message}");
    }

    static void WaitForAny(ManualResetEventSlim signal, Process process)
    {
        while (!signal.Wait(1000))
        {
            if (process.HasExited)
                return;
        }
    }

    static IRenderable BuildLatencyDisplay(string? summary)
    {
        var rows = new List<IRenderable>();

        if (summary is not null)
        {
            rows.Add(new Markup(summary));
            rows.Add(new Text(""));
        }

        rows.Add(new Markup("  [bold]Typing Latency Test[/] - type in the editor"));
        rows.Add(new Text(""));

        if (Latencies.Count == 0)
        {
            rows.Add(new Markup("  [dim]Waiting for keystrokes...[/]"));
        }
        else
        {
            var sorted = Latencies.OrderBy(x => x).ToList();
            var count = sorted.Count;
            var avg = sorted.Average();
            var median = Percentile(sorted, 50);
            var p95 = Percentile(sorted, 95);
            var max = sorted[^1];

            rows.Add(new Markup(
                $"  Keys: [bold]{count}[/]   " +
                $"Avg: [bold]{avg:F1}[/] ms   " +
                $"Median: [bold]{median:F1}[/] ms   " +
                $"P95: [bold]{p95:F1}[/] ms   " +
                $"Max: [bold]{max:F1}[/] ms"));
            rows.Add(new Text(""));

            var buckets = new (string Label, double Min, double Max, Color Color)[]
            {
                ("  0-10 ms", 0, 10, Color.Green),
                (" 10-25 ms", 10, 25, Color.Green),
                (" 25-50 ms", 25, 50, Color.Yellow),
                ("50-100 ms", 50, 100, Color.Red),
                ("  >100 ms", 100, double.MaxValue, Color.Red),
            };

            var chart = new BarChart().Width(60);

            foreach (var (label, min, max2, color) in buckets)
            {
                var bucketCount = sorted.Count(x => x >= min && x < max2);
                chart.AddItem(label, bucketCount, color);
            }

            rows.Add(chart);
        }

        return new Rows(rows);
    }

    static double Percentile(List<double> sorted, int p)
    {
        var index = (int)Math.Ceiling(sorted.Count * p / 100.0) - 1;
        return sorted[Math.Max(0, index)];
    }

    private const uint WM_CLOSE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    static void CloseProcessWindows(int processId)
    {
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == (uint)processId && IsWindowVisible(hWnd))
                PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
    }

    static void OnMessageReceived(long timestamp, MessageType type, byte[] payload, bool isBackend)
    {
        WriteDebugLog($"Message received: source={(isBackend ? "backend" : "devenv")}, timestamp={timestamp}, type={type}, payloadBytes={payload.Length}");

        switch (type)
        {
            case MessageType.Log:
            {
                using var reader = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
                var message = reader.ReadString();
                WriteDebugLog($"Profiler log ({(isBackend ? "backend" : "devenv")}): {message}");
                if (_debug)
                {
                    Console.WriteLine($"[{timestamp}] LOG: {message}");
                }

                break;
            }

            case MessageType.Phase:
            {
                using var reader = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
                var phase = (Phase)reader.ReadByte();
                var success = reader.ReadBoolean();
                var statusMessage = reader.ReadString();
                WriteDebugLog($"Phase ({(isBackend ? "backend" : "devenv")}): {phase}, success={success}, status={statusMessage}");
                if (_debug)
                    Console.WriteLine($"[{timestamp}] Phase: {phase} (success: {success}{(statusMessage.Length > 0 ? $", {statusMessage}" : "")})");
                if (!success)
                {
                    var detail = statusMessage.Length > 0 ? $" - {statusMessage}" : "";
                    WriteStep($"{phase}{detail}", false);
                    break;
                }
                var source = isBackend ? " (backend)" : "";
                switch (phase)
                {
                    case Phase.SolutionListenerReady:
                        SolutionListenerReady.Set();
                        break;
                    case Phase.LateLoadTask:
                        if (isBackend)
                            Interlocked.CompareExchange(ref _backendLateLoadTime, Environment.TickCount64, 0);
                        if (Interlocked.CompareExchange(ref _lateLoadTaskAnnounced, 1, 0) == 0)
                            WriteStep($"Project model loaded{source}", true);
                        break;
                    case Phase.DaemonStarted:
                        if (isBackend)
                            _backendDaemonSeen = true;
                        if (Interlocked.CompareExchange(ref _daemonStartedAnnounced, 1, 0) == 0)
                            WriteStep($"Daemon scheduled (first document){source}", true);
                        break;
                    case Phase.DaemonFinished:
                        if (Interlocked.CompareExchange(ref _daemonFinishedAnnounced, 1, 0) == 0)
                            WriteStep($"Daemon finished (first file){source}", true);
                        break;
                    case Phase.SolutionLoaded:
                        if (isBackend || !_backendConnected)
                            SolutionLoaded.Set();
                        break;
                }
                break;
            }

            case MessageType.UIFreeze:
            {
                using var reader = new BinaryReader(new MemoryStream(payload));
                var duration = reader.ReadInt64();
                WriteDebugLog($"UIFreeze received: duration={duration}, counted={!SolutionLoaded.IsSet}, totalBefore={_totalFreezeTime}, maxBefore={_maxFreezeTime}");

                if (!SolutionLoaded.IsSet)
                {
                    _totalFreezeTime += duration;

                    if (duration > _maxFreezeTime)
                        _maxFreezeTime = duration;
                }

                if (_debug)
                    Console.WriteLine($"[{timestamp}] UI freeze: {duration} ms");

                break;
            }

            case MessageType.TypingLatency:
            {
                using var reader = new BinaryReader(new MemoryStream(payload));
                var latencyUs = reader.ReadInt64();
                WriteDebugLog($"TypingLatency received: latencyUs={latencyUs}");

                if (_debug)
                    Console.WriteLine($"[{timestamp}] Typing latency: {latencyUs / 1000.0:F1} ms");

                Latencies.Enqueue(latencyUs / 1000.0);

                break;
            }

            default:
                if (_debug)
                    Console.WriteLine($"[{timestamp}] Unknown({type})");
                else
                    AnsiConsole.MarkupLine($"  [dim]Unknown({type})[/]");
                break;
        }
    }
}
