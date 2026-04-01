using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using ResharperProfiler.Protocol;
using Silhouette;
using UiProfiler;

namespace ResharperProfiler;

[Profiler("687FB688-F002-4B64-9A95-567E5502230F")]
public unsafe class CorProfiler : CorProfilerCallback9Base
{
    private Client? _pipeClient;
    private uint _mainThreadId;
    private readonly CancellationTokenSource _shutdownToken = new();
    private readonly ManualResetEventSlim _solutionLoadMutex = new(false);
    private readonly ManualResetEventSlim _responsiveMutex = new(false);
    private readonly CancellationTokenSource _solutionLoaded = new();
    private long _sentInput;
    private long _receivedInput;
    private GCHandle _thisHandle;
    private readonly ConcurrentQueue<long> _keyTimestamps = new();
    private bool _isBackend;

    protected override HResult Initialize(int iCorProfilerInfoVersion)
    {
        if (iCorProfilerInfoVersion < 5)
        {
            return HResult.CORPROF_E_PROFILER_CANCEL_ACTIVATION;
        }

        var processName = Process.GetCurrentProcess().ProcessName;

        if (processName == "ReSharper.Backend64c")
        {
            _isBackend = true;
        }
        else if (processName != "devenv")
        {
            return HResult.CORPROF_E_PROFILER_CANCEL_ACTIVATION;
        }

        Log.Write($"Profiler loaded (process: {processName}, isBackend: {_isBackend})");

        // Disable profiling for child processes (unless OOP backend which inherits env vars)
        if (!_isBackend)
        {
            Environment.SetEnvironmentVariable("COR_ENABLE_PROFILING", "0");
            Environment.SetEnvironmentVariable("CORECLR_ENABLE_PROFILING", "0");
        }

        _pipeClient = CreatePipeClient();

        var result = ICorProfilerInfo5.SetEventMask2(
            COR_PRF_MONITOR.COR_PRF_MONITOR_MODULE_LOADS | COR_PRF_MONITOR.COR_PRF_ENABLE_REJIT,
            COR_PRF_HIGH_MONITOR.COR_PRF_HIGH_MONITOR_NONE);

        if (!result)
        {
            return HResult.E_FAIL;
        }

        _mainThreadId = NativeMethods.GetCurrentThreadId();
        _thisHandle = GCHandle.Alloc(this);

        // Input sending and UI freeze monitoring only apply to devenv.exe
        if (!_isBackend)
        {
            new Thread(LowLevelHookThread)
            {
                IsBackground = true,
                Name = "UI Profiler LL Hook"
            }.Start();

            new Thread(InputThread)
            {
                IsBackground = true,
                Name = "UI Profiler Monitor"
            }.Start();

            new Thread(MonitoringThread)
            {
                IsBackground = true,
                Name = "UI Responsiveness Monitor"
            }.Start();
        }

        return HResult.S_OK;
    }

    protected override HResult Shutdown()
    {
        _shutdownToken.Cancel();
        return HResult.S_OK;
    }

    protected override HResult ModuleLoadFinished(ModuleId moduleId, HResult hrStatus)
    {
        if (!hrStatus)
        {
            return HResult.S_OK;
        }

        var moduleInfo = ICorProfilerInfo2.GetModuleInfo(moduleId).ThrowIfFailed();

        var fileName = Path.GetFileNameWithoutExtension(moduleInfo.ModuleName);

        if (!_isBackend && "JetBrains.Platform.VisualStudio.SinceVs17".Equals(fileName, StringComparison.OrdinalIgnoreCase))
        {
            Log.Write($"Entry point module loaded (id: {moduleId.Value:x2}), hooking startup");
            var result = HookMethod(moduleId, "JetBrains.Platform.VisualStudio.SinceVs17.Env.Package.VsAsyncPackage17", "Microsoft.VisualStudio.Shell.Interop.IAsyncLoadablePackageInitialize.Initialize", &OnStartupCallback, instrumentMethodStart: true);

            if (!result)
            {
                Log.Write($"Failed to hook startup: {result}");
                _pipeClient?.ReportPhase(Phase.Startup, success: false, statusMessage: $"Failed to hook startup method: {result}, UI freeze detection won't be available");
            }

            return HResult.S_OK;
        }

        if ("JetBrains.ReSharper.Feature.Services".Equals(fileName, StringComparison.OrdinalIgnoreCase))
        {
            Log.Write($"Feature services module loaded (id: {moduleId.Value:x2}), hooking daemon ready");

            var result = HookMethod(moduleId, "JetBrains.ReSharper.Feature.Services.Daemon.DaemonPerformanceCollector", "RegisterDaemonFinished", &OnDaemonFinishedCallback, instrumentMethodStart: true);

            if (!result)
            {
                Log.Write($"Failed to hook daemon ready: {result}");
                _pipeClient?.ReportPhase(Phase.DaemonFinished, success: false, statusMessage: $"Failed to hook DaemonFinished method: {result}");
            }

            return result;
        }

        if ("JetBrains.ReSharper.Psi".Equals(fileName, StringComparison.OrdinalIgnoreCase))
        {
            Log.Write($"ProjectModel module loaded (id: {moduleId.Value:x2}), hooking report phase");
            var result = HookMethod(moduleId, "JetBrains.ReSharper.Psi.Caches.Jobs.JobSaveCaches", "Do", &OnSaveCachesCallback, instrumentMethodStart: false);

            if (!result)
            {
                Log.Write($"Failed to hook save caches: {result}");
                _pipeClient?.ReportPhase(Phase.SaveCaches, success: false, statusMessage: $"Failed to hook SaveCaches method: {result}");
            }

            if (!_isBackend)
                _pipeClient?.ReportPhase(Phase.SolutionListenerReady);

            return result;
        }

        if (!_isBackend && "JetBrains.Platform.VisualStudio.Core".Equals(fileName, StringComparison.OrdinalIgnoreCase))
        {
            Log.Write($"VS Core module loaded (id: {moduleId.Value:x2}), hooking OOP detection");
            var result = HookMethod(moduleId, "JetBrains.VsIntegration.BackendInterop.BackendEntry", "CreateBackend", &OnOopDetectedCallback, instrumentMethodStart: true);

            if (!result)
            {
                Log.Write($"Failed to hook OOP detection: {result}");
            }

            return HResult.S_OK;
        }

        return HResult.S_OK;
    }



    [UnmanagedCallersOnly]
    private static void OnStartupCallback(IntPtr instance) => CallProfiler(instance, profiler => profiler.OnStartup());

    [UnmanagedCallersOnly]
    private static void OnSaveCachesCallback(IntPtr instance) => CallProfiler(instance, profiler => profiler.OnSaveCaches());

    [UnmanagedCallersOnly]
    private static void OnDaemonFinishedCallback(IntPtr instance) => CallProfiler(instance, profiler => profiler.OnDaemonFinished());

    [UnmanagedCallersOnly]
    private static void OnOopDetectedCallback(IntPtr instance) => CallProfiler(instance, profiler => profiler.OnOopDetected());

    private static void CallProfiler(IntPtr instance, Action<CorProfiler> action)
    {
        try
        {
            var target = GCHandle.FromIntPtr(instance).Target;

            if (target is CorProfiler profiler)
            {
                action(profiler);
            }
            else
            {
                Log.Write($"Failed to get CorProfiler instance from callback (ptr: {instance:x2})");
            }
        }
        catch (InvalidOperationException ex)
        {
            Log.Write($"Failed to get CorProfiler instance from callback (ptr: {instance:x2}): {ex}");
        }
    }

    private void OnStartup()
    {
        _solutionLoadMutex.Set();
        _pipeClient?.ReportPhase(Phase.Startup);
        Log.Write("Resharper startup detected");
    }

    private void OnSaveCaches()
    {
        _pipeClient?.ReportPhase(Phase.SaveCaches);

        _solutionLoaded.Cancel();
        Log.Write($"Total input events sent: {_sentInput}, received: {_receivedInput}");
    }

    private void OnDaemonFinished()
    {
        if (_solutionLoaded.IsCancellationRequested)
        {
            return;
        }

        _pipeClient?.ReportPhase(Phase.DaemonFinished);
    }

    private void OnOopDetected()
    {
        Log.Write("ReSharper is running out-of-process, enabling profiler for backend process");
        Environment.SetEnvironmentVariable("CORECLR_ENABLE_PROFILING", "1");
        Environment.SetEnvironmentVariable("CORECLR_PROFILER", "{687FB688-F002-4B64-9A95-567E5502230F}");
        Environment.SetEnvironmentVariable("CORECLR_PROFILER_PATH_64",
            Environment.GetEnvironmentVariable("COR_PROFILER_PATH_64"));
    }

    private static Client? CreatePipeClient()
    {
        var pipeName = Environment.GetEnvironmentVariable("RESHARPER_PROFILER_PIPE_NAME");

        if (string.IsNullOrEmpty(pipeName))
        {
            return null;
        }

        try
        {
            var client = new Client(pipeName);
            Log.SetPipeClient(client);
            Log.Write($"Connected to pipe '{pipeName}'");
            return client;
        }
        catch (Exception ex)
        {
            Log.Write($"Failed to connect to pipe '{pipeName}': {ex.Message}");
            return null;
        }
    }

    private HResult HookMethod(ModuleId moduleId, string typeName, string methodName, delegate* unmanaged<IntPtr, void> callback, bool instrumentMethodStart)
    {
        using var metadataImport = ICorProfilerInfo.GetModuleMetaDataImport2(moduleId, CorOpenFlags.ofRead)
            .ThrowIfFailed()
            .Wrap();

        var (result, typeDef) = metadataImport.Value.FindTypeDefByName(typeName, default);

        if (!result)
        {
            Log.Write($"Failed to find type {typeName}");
            return HResult.E_FAIL;
        }

        (result, var methodDef) = metadataImport.Value.FindMethod(typeDef, methodName, default);

        if (!result)
        {
            Log.Write($"Failed to find method {methodName} in type {typeName}");
            return HResult.E_FAIL;
        }

        var ilRewriter = Silhouette.IL.IlRewriter.Create(ICorProfilerInfo3);
        using var method = ilRewriter.Import(moduleId, methodDef);

        var sig = MethodSig.CreateStatic(method.Metadata.CorLibTypes.Void, method.Metadata.CorLibTypes.IntPtr);
        sig.CallingConvention = dnlib.DotNet.CallingConvention.Unmanaged;

        int index = 0;
        int InstructionIndex() => instrumentMethodStart ? index++ : method.Body.Instructions.Count - 1;

        method.Body.Instructions.Insert(InstructionIndex(), Instruction.Create(OpCodes.Ldc_I8, GCHandle.ToIntPtr(_thisHandle)));
        method.Body.Instructions.Insert(InstructionIndex(), Instruction.Create(OpCodes.Conv_I));
        method.Body.Instructions.Insert(InstructionIndex(), Instruction.Create(OpCodes.Ldc_I8, (nint)callback));
        method.Body.Instructions.Insert(InstructionIndex(), Instruction.Create(OpCodes.Conv_I));
        method.Body.Instructions.Insert(InstructionIndex(), Instruction.Create(OpCodes.Calli, sig));

        method.Body.UpdateInstructionOffsets();

        ilRewriter.Export(method);

        return HResult.S_OK;
    }

    private void MonitoringThread()
    {
        bool isResponsive = true;

        var stopwatch = Stopwatch.StartNew();

        while (!_shutdownToken.IsCancellationRequested)
        {
            if (_responsiveMutex.Wait(20))
            {
                _responsiveMutex.Reset();

                if (!isResponsive)
                {
                    isResponsive = true;
                    stopwatch.Stop();

                    var freezeDuration = stopwatch.ElapsedMilliseconds;

                    if (freezeDuration >= 100)
                    {
                        _pipeClient?.SendUIFreeze(stopwatch.ElapsedMilliseconds);
                    }
                }
            }
            else
            {
                if (isResponsive)
                {
                    // No pending keys = idle, not frozen
                    if (_keyTimestamps.IsEmpty)
                    {
                        continue;
                    }

                    isResponsive = false;
                    stopwatch.Restart();

                    // Now wait indefinitely
                    _responsiveMutex.Wait(_shutdownToken.Token);
                }
            }
        }
    }

    private long _suspendUntilTicks;

    private void InputThread()
    {
        // Wait until Resharper modules are loaded before sending the fake inputs
        // Old versions of Resharper don't start loading until VS is idle, so sending inputs keeps them waiting forever
        _solutionLoadMutex.Wait();

        SetHook((int)_mainThreadId);

        try
        {
            var inputs = new NativeMethods.INPUT[2];

            var inputSize = Marshal.SizeOf<NativeMethods.INPUT>();

            while (!_shutdownToken.IsCancellationRequested)
            {
                Thread.Sleep(10);

                if (Environment.TickCount64 < Interlocked.Read(ref _suspendUntilTicks))
                {
                    continue;
                }

                WaitForMainWindow();

                const ushort key = 0xFC; // VK_NONAME
                const uint type = 0x1; // INPUT_KEYBOARD

                inputs[0] = new()
                {
                    u = new() { ki = new() { wVk = key } },
                    type = type
                };

                inputs[1] = new()
                {
                    u = new() { ki = new() { wVk = key, dwFlags = 0x2 /* KEYEVENTF_KEYUP */ } },
                    type = type
                };

                NativeMethods.SendInput(2, inputs, inputSize);

                _sentInput += 2;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"InputThread failed: {ex}");
        }
    }

    private void LowLevelHookThread()
    {
        // LL hooks require hMod=0, dwThreadId=0 for global hook
        var hook = NativeMethods.SetWindowsHookEx(NativeMethods.HookType.WH_KEYBOARD_LL, LowLevelHookProc, 0, 0);

        if (hook == IntPtr.Zero)
        {
            Log.Write($"Failed to install WH_KEYBOARD_LL hook: {Marshal.GetLastWin32Error()}");
            return;
        }

        // Message pump to keep the LL hook alive
        while (!_shutdownToken.IsCancellationRequested && NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }

        return;

        IntPtr LowLevelHookProc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                _keyTimestamps.Enqueue(Stopwatch.GetTimestamp());
            }

            return NativeMethods.CallNextHookEx(0, code, wParam, lParam);
        }
    }

    private void SetHook(int threadId)
    {
        NativeMethods.SetWindowsHookEx(NativeMethods.HookType.WH_KEYBOARD, HookProc, 0, threadId);

        IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                var isProbeKey = (int)wParam == 0xFC;

                if (isProbeKey)
                {
                    _receivedInput++;
                }
                else
                {
                    Interlocked.Exchange(ref _suspendUntilTicks, Environment.TickCount64 + 3000);
                }

                // Dequeue the LL timestamp and signal responsiveness
                if (_keyTimestamps.TryDequeue(out var llTimestamp))
                {
                    _responsiveMutex.Set();

                    // Report typing latency for real key-down events after solution is loaded
                    if (!isProbeKey && _solutionLoaded.IsCancellationRequested && (int)lParam >= 0)
                    {
                        var latencyUs = (long)Stopwatch.GetElapsedTime(llTimestamp).TotalMicroseconds;
                        _pipeClient?.SendTypingLatency(latencyUs);
                    }
                }
            }

            return NativeMethods.CallNextHookEx(0, code, wParam, lParam);
        }
    }

    private static void WaitForMainWindow()
    {
        while (true)
        {
            var foreground = NativeMethods.GetForegroundWindow();

            if (foreground != IntPtr.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(foreground, out var pid);

                if (pid == Environment.ProcessId)
                {
                    return;
                }
            }

            Thread.Sleep(100);
        }
    }
}
