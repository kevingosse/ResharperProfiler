using System.Diagnostics;
using System.Text;
using ResharperProfiler.Protocol;

namespace ResharperProfiler.ConsoleLauncher;

static class Program
{
    private const string ProfilerGuid = "{687FB688-F002-4B64-9A95-567E5502230F}";
    private static long _totalFreezeTime;
    private static long _maxFreezeTime;
    private static long _startupTime;

    static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: ResharperProfiler.ConsoleLauncher <executable> [args...]");
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

        Console.WriteLine($"Started process {process.Id}, waiting for profiler to connect...");

        // Wait for the profiler to connect (or the process to exit first)
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
    static void OnMessageReceived(long timestamp, MessageType type, byte[] payload)
    {
        switch (type)
        {
            case MessageType.Log:
            {
                using var reader = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
                Console.WriteLine($"[{timestamp}] LOG: {reader.ReadString()}");
                break;
            }

            case MessageType.SolutionLoaded:
                var loadTime = Environment.TickCount64 - _startupTime;

                Console.WriteLine($"[{timestamp}] Solution loaded after {loadTime/1000.0} seconds");
                Console.WriteLine($"Total UI freeze time: {_totalFreezeTime} ms (max: {_maxFreezeTime} ms)");
                break;

            case MessageType.UIFreeze:
            {
                using var reader = new BinaryReader(new MemoryStream(payload));
                var duration = reader.ReadInt64();
                Console.WriteLine($"[{timestamp}] UI freeze: {duration} ms");
                _totalFreezeTime += duration;

                if (duration > _maxFreezeTime)
                {
                    _maxFreezeTime = duration;
                }

                break;
            }

            default:
                Console.WriteLine($"[{timestamp}] Unknown({type})");
                break;
        }
    }
}