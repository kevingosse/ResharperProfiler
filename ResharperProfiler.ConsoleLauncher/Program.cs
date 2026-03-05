using System.Diagnostics;
using System.Text;
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

    private static readonly ManualResetEventSlim _solutionListenerReady = new();
    private static readonly ManualResetEventSlim _solutionLoaded = new();

    private static readonly object _latencyLock = new();
    private static readonly List<double> _latencies = new(); // in ms

    static int Main(string[] args)
    {
        if (args is ["--debug", .. var rest])
        {
            _debug = true;
            args = rest;
        }

        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: ResharperProfiler.ConsoleLauncher [--debug] <executable> [args...]");
            return 1;
        }

        var profilerDll = Path.Combine(AppContext.BaseDirectory, "ResharperProfiler.dll");

        if (!File.Exists(profilerDll))
        {
            Console.Error.WriteLine($"Profiler DLL not found: {profilerDll}");
            Console.Error.WriteLine("Run Build.ps1 first.");
            return 1;
        }

        var pipeName = $"ResharperProfiler_{Guid.NewGuid():N}";

        // Start the pipe server (constructor blocks until the profiler connects)
        Server? server = null;
        Exception? serverError = null;
        var serverReady = new ManualResetEventSlim();

        new Thread(() =>
        {
            try
            {
                server = new Server(pipeName);
                server.MessageReceived += OnMessageReceived;
                server.Error += (context, ex) => Console.Error.WriteLine($"[PIPE ERROR] {context}: {ex}");
                server.Disconnected += () => Console.Error.WriteLine("[PIPE] Disconnected");
                server.Start();
            }
            catch (Exception ex)
            {
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

        for (var i = 1; i < args.Length; i++)
            psi.ArgumentList.Add(args[i]);

        psi.Environment["COR_PROFILER"] = ProfilerGuid;
        psi.Environment["COR_ENABLE_PROFILING"] = "1";
        psi.Environment["COR_PROFILER_PATH_64"] = profilerDll;
        psi.Environment["RESHARPER_PROFILER_PIPE_NAME"] = pipeName;

        var process = Process.Start(psi);

        if (process is null)
        {
            Console.Error.WriteLine($"Failed to start: {args[0]}");
            return 1;
        }

        _startupTime = Environment.TickCount64;

        if (_debug)
        {
            Console.WriteLine($"Started process {process.Id}, waiting for profiler to connect...");

            while (!serverReady.Wait(1000))
            {
                if (process.HasExited)
                {
                    Console.Error.WriteLine($"Process exited ({process.ExitCode}) before profiler connected.");
                    return process.ExitCode;
                }
            }

            if (serverError is not null)
            {
                Console.Error.WriteLine($"Pipe server error: {serverError.Message}");
                process.Kill();
                return 1;
            }

            Console.WriteLine("Profiler connected.");

            process.WaitForExit();
            server!.Dispose();
            return process.ExitCode;
        }

        // Startup phase
        var startupOk = true;

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Starting...", ctx =>
            {
                WriteStep($"Process [bold]{process.Id}[/] started");

                ctx.Status("Waiting for profiler to connect...");
                while (!serverReady.Wait(1000))
                {
                    if (process.HasExited)
                    {
                        Console.Error.WriteLine($"Process exited ({process.ExitCode}) before profiler connected.");
                        startupOk = false;
                        return;
                    }
                }

                if (serverError is not null)
                {
                    Console.Error.WriteLine($"Pipe server error: {serverError.Message}");
                    process.Kill();
                    startupOk = false;
                    return;
                }

                WriteStep("Profiler connected");

                ctx.Status("Waiting for solution listener...");
                WaitForAny(_solutionListenerReady, process);
                if (_solutionListenerReady.IsSet)
                    WriteStep("Solution listener ready");

                ctx.Status("Waiting for solution to load...");
                WaitForAny(_solutionLoaded, process);
            });

        if (!startupOk || process.HasExited)
        {
            server?.Dispose();
            return process.HasExited ? process.ExitCode : 1;
        }

        string? solutionSummary = null;

        if (_solutionLoaded.IsSet)
        {
            var totalTime = Environment.TickCount64 - _startupTime;
            var elapsed = (Environment.TickCount64 - _startupTime) / 1000.0;
            solutionSummary =
                $"  [dim]{elapsed,6:F1}s[/]  [green]✓[/] Solution loaded [dim]({totalTime / 1000.0:F1}s total)[/]\n" +
                $"           Total UI freeze: [bold]{_totalFreezeTime}[/] ms (max: [bold]{_maxFreezeTime}[/] ms)";
        }

        // Typing latency phase
        IRenderable BuildDisplay() => BuildLatencyDisplay(solutionSummary);

        AnsiConsole.Live(BuildDisplay())
            .AutoClear(false)
            .Start(ctx =>
            {
                while (!process.HasExited)
                {
                    Thread.Sleep(200);
                    ctx.UpdateTarget(BuildDisplay());
                }

                ctx.UpdateTarget(BuildDisplay());
            });

        server!.Dispose();
        return process.ExitCode;
    }

    static void WriteStep(string message)
    {
        var elapsed = (Environment.TickCount64 - _startupTime) / 1000.0;
        AnsiConsole.MarkupLine($"  [dim]{elapsed,6:F1}s[/]  [green]✓[/] {message}");
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

        lock (_latencyLock)
        {
            if (_latencies.Count == 0)
            {
                rows.Add(new Markup("  [dim]Waiting for keystrokes...[/]"));
            }
            else
            {
                var sorted = _latencies.OrderBy(x => x).ToList();
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
        }

        return new Rows(rows);
    }

    static double Percentile(List<double> sorted, int p)
    {
        var index = (int)Math.Ceiling(sorted.Count * p / 100.0) - 1;
        return sorted[Math.Max(0, index)];
    }

    static void OnMessageReceived(long timestamp, MessageType type, byte[] payload)
    {
        switch (type)
        {
            case MessageType.Log:
            {
                if (_debug)
                {
                    using var reader = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
                    Console.WriteLine($"[{timestamp}] LOG: {reader.ReadString()}");
                }

                break;
            }

            case MessageType.SolutionListenerReady:
                if (_debug)
                    Console.WriteLine($"[{timestamp}] Solution listener ready");
                _solutionListenerReady.Set();
                break;

            case MessageType.SolutionLoaded:
                if (_debug)
                    Console.WriteLine($"[{timestamp}] Solution loaded");
                _solutionLoaded.Set();
                break;

            case MessageType.UIFreeze:
            {
                using var reader = new BinaryReader(new MemoryStream(payload));
                var duration = reader.ReadInt64();
                _totalFreezeTime += duration;

                if (duration > _maxFreezeTime)
                    _maxFreezeTime = duration;

                if (_debug)
                    Console.WriteLine($"[{timestamp}] UI freeze: {duration} ms");

                break;
            }

            case MessageType.TypingLatency:
            {
                using var reader = new BinaryReader(new MemoryStream(payload));
                var latencyUs = reader.ReadInt64();

                if (_debug)
                    Console.WriteLine($"[{timestamp}] Typing latency: {latencyUs / 1000.0:F1} ms");

                lock (_latencyLock)
                {
                    _latencies.Add(latencyUs / 1000.0);
                }

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
