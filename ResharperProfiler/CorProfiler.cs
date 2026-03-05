using System.Collections.Concurrent;
using System.ComponentModel;
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
    private readonly ManualResetEventSlim _responsiveMutex = new(false);
    private readonly CancellationTokenSource _solutionLoaded = new();
    private long _sentInput;
    private long _receivedInput;
    private GCHandle _thisHandle;
    private readonly ConcurrentQueue<long> _keyTimestamps = new();

    protected override HResult Initialize(int iCorProfilerInfoVersion)
    {
        if (iCorProfilerInfoVersion < 5)
        {
            return HResult.CORPROF_E_PROFILER_CANCEL_ACTIVATION;
        }

        var processName = Process.GetCurrentProcess().ProcessName;

        if (processName != "devenv")
        {
            return HResult.CORPROF_E_PROFILER_CANCEL_ACTIVATION;
        }

        Log.Write("Profiler loaded");

        // Disable profiling for child processes
        Environment.SetEnvironmentVariable("COR_ENABLE_PROFILING", "0");

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

        if ("JetBrains.ReSharper.Feature.Services".Equals(fileName, StringComparison.OrdinalIgnoreCase))
        {
            return HookSolutionLoad(moduleId);
        }

        return HResult.S_OK;
    }

    private static IEnumerable<(string TypeName, string? ParentTypeName, string MethodName)> GetSolutionDoneCandidates()
    {
        yield return ("JetBrains.ReSharper.Feature.Services.Daemon.DaemonPerformanceCollector", null, "RegisterDaemonFinished");
    }

    [UnmanagedCallersOnly]
    private static void OnSolutionLoadedCallback(IntPtr instance)
    {
        try
        {
            var target = GCHandle.FromIntPtr(instance).Target;

            if (target is CorProfiler profiler)
            {
                profiler.OnSolutionLoaded();
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

    private void OnSolutionLoaded()
    {
        if (_solutionLoaded.IsCancellationRequested)
        {
            return;
        }

        Log.Write("Solution loaded");
        _pipeClient?.SendSolutionLoaded();
        _solutionLoaded.Cancel();
        Log.Write($"Total input events sent: {_sentInput}, received: {_receivedInput}");
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

    private HResult HookSolutionLoad(ModuleId projectModelModuleId)
    {
        Log.Write("Hooking solution load");

        try
        {
            using var metadataImport = ICorProfilerInfo.GetModuleMetaDataImport2(projectModelModuleId, CorOpenFlags.ofRead)
                .ThrowIfFailed()
                .Wrap();

            foreach (var (typeName, parentTypeName, methodName) in GetSolutionDoneCandidates())
            {
                HResult result;
                MdToken enclosingClass = default;

                if (parentTypeName != null)
                {
                    (result, var parentTypeDef) = metadataImport.Value.FindTypeDefByName(parentTypeName, default);

                    if (!result)
                    {
                        Log.Write($"Failed to find parent type {parentTypeName} for candidate {typeName}.{methodName}");
                        continue;
                    }

                    enclosingClass = new(parentTypeDef.Value);
                }

                (result, var typeDef) = metadataImport.Value.FindTypeDefByName(typeName, enclosingClass);

                if (!result)
                {
                    Log.Write($"Failed to find type {typeName} for candidate {typeName}.{methodName}");
                    continue;
                }

                (result, var methodDef) = metadataImport.Value.FindMethod(typeDef, methodName, default);

                if (!result)
                {
                    Log.Write($"Failed to find method {methodName} in type {typeName} for candidate {typeName}.{methodName}");
                    continue;
                }

                Log.Write($"Hooking {typeName}.{methodName}");

                var ilRewriter = Silhouette.IL.IlRewriter.Create(ICorProfilerInfo3);
                using var method = ilRewriter.Import(projectModelModuleId, methodDef);

                var ptr = (nint)(delegate* unmanaged<IntPtr, void>)&OnSolutionLoadedCallback;

                var sig = MethodSig.CreateStatic(method.Metadata.CorLibTypes.Void, method.Metadata.CorLibTypes.IntPtr);
                sig.CallingConvention = dnlib.DotNet.CallingConvention.Unmanaged;

                method.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Ldc_I8, GCHandle.ToIntPtr(_thisHandle)));
                method.Body.Instructions.Insert(1, Instruction.Create(OpCodes.Conv_I));
                method.Body.Instructions.Insert(2, Instruction.Create(OpCodes.Ldc_I8, ptr));
                method.Body.Instructions.Insert(3, Instruction.Create(OpCodes.Conv_I));
                method.Body.Instructions.Insert(4, Instruction.Create(OpCodes.Calli, sig));

                ilRewriter.Export(method);

                Log.Write($"Successfully hooked {typeName}.{methodName}");

                return HResult.S_OK;
            }

            Log.Write("Failed to find solution load method to hook");
            return HResult.E_FAIL;
        }
        catch (Win32Exception ex)
        {
            Log.Write($"Failed to hook solution load: {ex}");
            return ex.HResult;
        }
        catch (Exception ex)
        {
            Log.Write($"Failed to hook solution load: {ex}");
            return HResult.E_FAIL;
        }
    }

    private void MonitoringThread()
    {
        bool isResponsive = true;

        var stopwatch = Stopwatch.StartNew();

        while (true)
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
                    _responsiveMutex.Wait();
                }
            }
        }
    }

    private long _suspendUntilTicks;

    private void InputThread()
    {
        SetHook((int)_mainThreadId);

        try
        {
            var inputs = new NativeMethods.INPUT[2];

            var inputSize = Marshal.SizeOf<NativeMethods.INPUT>();

            while (true)
            {
                Thread.Sleep(10);

                if (Environment.TickCount64 < Interlocked.Read(ref _suspendUntilTicks))
                    continue;

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
        IntPtr LowLevelHookProc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                _keyTimestamps.Enqueue(Stopwatch.GetTimestamp());
            }

            return NativeMethods.CallNextHookEx(0, code, wParam, lParam);
        }

        // LL hooks require hMod=0, dwThreadId=0 for global hook
        var hook = NativeMethods.SetWindowsHookEx(NativeMethods.HookType.WH_KEYBOARD_LL, LowLevelHookProc, 0, 0);

        if (hook == IntPtr.Zero)
        {
            Log.Write($"Failed to install WH_KEYBOARD_LL hook: {Marshal.GetLastWin32Error()}");
            return;
        }

        Log.Write("WH_KEYBOARD_LL hook installed");

        // Message pump to keep the LL hook alive
        while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }
    }

    private void SetHook(int threadId)
    {
        NativeMethods.SetWindowsHookEx(NativeMethods.HookType.WH_KEYBOARD, HookProc, 0, threadId);

        IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                if ((int)wParam == 0xFC)
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
