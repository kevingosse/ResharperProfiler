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
    private readonly CancellationTokenSource _shutdownToken = new();
    private readonly ManualResetEventSlim _solutionLoadMutex = new(false);
    private readonly ManualResetEventSlim _responsiveMutex = new(false);
    private readonly CancellationTokenSource _solutionLoaded = new();
    private long _sentInput;
    private long _receivedInput;
    private GCHandle _thisHandle;
    private readonly ConcurrentQueue<long> _keyTimestamps = new();
    private NativeMethods.HookProc? _lowLevelHookProc;
    private NativeMethods.HookProc? _keyboardHookProc;
    private IntPtr _lowLevelHook;
    private IntPtr _keyboardHook;
    private long _lowLevelHookEvents;
    private long _keyboardHookEvents;
    private long _sendInputFailures;
    private long _mainWindowWaitIterations;
    private bool _isBackend;
    private volatile bool _lateLoadTaskFired;
    private volatile bool _daemonEverStarted;
    private volatile bool _daemonFinishedSeen;
    private int _solutionLoadedReported;

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

        // Disable profiling for child processes (OOP backend gets re-enabled via InvokeCore hook)
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

        var noFreezes = Environment.GetEnvironmentVariable("RESHARPER_PROFILER_NO_FREEZES") == "1";
        var forceFocus = Environment.GetEnvironmentVariable("RESHARPER_PROFILER_FORCE_FOCUS") == "1";
        Log.Debug($"Profiler initialized: mainThreadId={_mainThreadId}, noFreezes={noFreezes}, forceFocus={forceFocus}, userInteractive={Environment.UserInteractive}, sessionId={Process.GetCurrentProcess().SessionId}, logFile={Environment.GetEnvironmentVariable("RESHARPER_PROFILER_LOG_FILE") ?? "<none>"}");

        if (!_isBackend && forceFocus)
        {
            Log.Debug("Starting force-focus monitor thread");
            new Thread(ForceFocusThread)
            {
                IsBackground = true,
                Name = "UI Profiler Force Focus"
            }.Start();
        }

        // Input sending and UI freeze monitoring only apply to devenv.exe
        if (!_isBackend && !noFreezes)
        {
            Log.Debug("Starting UI freeze detection threads");
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
        else
        {
            Log.Debug($"UI freeze detection threads not started (isBackend={_isBackend}, noFreezes={noFreezes})");
        }

        return HResult.S_OK;
    }

    protected override HResult Shutdown()
    {
        Log.Debug($"Profiler shutdown requested. sentInput={_sentInput}, receivedInput={_receivedInput}, lowLevelHookEvents={_lowLevelHookEvents}, keyboardHookEvents={_keyboardHookEvents}, sendInputFailures={_sendInputFailures}, pendingKeyTimestamps={_keyTimestamps.Count}, lowLevelHook=0x{_lowLevelHook.ToInt64():x}, keyboardHook=0x{_keyboardHook.ToInt64():x}");
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

            var daemonHookResult = HookMethod(moduleId, "JetBrains.ReSharper.Feature.Services.Daemon.DaemonPerformanceCollector", "RegisterDaemonFinished", &OnDaemonFinishedCallback, instrumentMethodStart: true);

            if (!daemonHookResult)
            {
                Log.Write($"Failed to hook daemon ready: {daemonHookResult}");
                _pipeClient?.ReportPhase(Phase.DaemonFinished, success: false, statusMessage: $"Failed to hook DaemonFinished method: {daemonHookResult}");
            }

            return daemonHookResult;
        }

        if ("JetBrains.ReSharper.Daemon".Equals(fileName, StringComparison.OrdinalIgnoreCase))
        {
            Log.Write($"Daemon module loaded (id: {moduleId.Value:x2}), hooking daemon-process creation");

            // `CreateDaemonForDocumentImpl` is the sole instantiation site for `VisibleDocumentDaemonProcess`.
            // It only fires when ReSharper actually has a document to analyse. We use that as the
            // "any daemon will run" signal: if `OnSolutionLoadAsLateAsPossible` fires and we never
            // saw this hook fire, no document is open and there is nothing to wait for — fire
            // SolutionLoaded immediately. Otherwise wait for `RegisterDaemonFinished`.
            var startedHookResult = HookMethod(moduleId, "JetBrains.ReSharper.Daemon.Impl.DaemonImpl", "CreateDaemonForDocumentImpl", &OnDaemonStartedCallback, instrumentMethodStart: true);

            if (!startedHookResult)
            {
                Log.Write($"Failed to hook DaemonImpl.CreateDaemonForDocumentImpl: {startedHookResult}");
                _pipeClient?.ReportPhase(Phase.DaemonStarted, success: false, statusMessage: $"Failed to hook DaemonImpl.CreateDaemonForDocumentImpl: {startedHookResult}");
            }

            return startedHookResult;
        }

        if ("JetBrains.Platform.ProjectModel".Equals(fileName, StringComparison.OrdinalIgnoreCase))
        {
            Log.Write($"ProjectModel module loaded (id: {moduleId.Value:x2}), hooking solution-load completion");

            // Master: SolutionLoadListenersManager2 in JetBrains.ProjectModel.Tasks.Listeners.
            // 2025.1 baseline: SolutionLoadListenersManager in JetBrains.ProjectModel.Tasks (the v2 class doesn't exist yet,
            // and the v1 class is [Obsolete(error: true)] in master so it's never instantiated there).
            var result = HookMethod(moduleId, "JetBrains.ProjectModel.Tasks.Listeners.SolutionLoadListenersManager2", "OnSolutionLoadAsLateAsPossible", &OnLateLoadTaskCallback, instrumentMethodStart: true);

            if (result)
            {
                Log.Write("Hooked SolutionLoadListenersManager2.OnSolutionLoadAsLateAsPossible (master)");
            }
            else
            {
                result = HookMethod(moduleId, "JetBrains.ProjectModel.Tasks.SolutionLoadListenersManager", "OnSolutionLoadAsLateAsPossible", &OnLateLoadTaskCallback, instrumentMethodStart: true);

                if (result)
                {
                    Log.Write("Hooked SolutionLoadListenersManager.OnSolutionLoadAsLateAsPossible (2025.1 baseline)");
                }
                else
                {
                    Log.Write($"Failed to hook solution-load completion (tried v2 then v1): {result}");
                    _pipeClient?.ReportPhase(Phase.LateLoadTask, success: false, statusMessage: $"Failed to hook OnSolutionLoadAsLateAsPossible method: {result}");
                }
            }

            if (!_isBackend)
                _pipeClient?.ReportPhase(Phase.SolutionListenerReady);

            return result;
        }

        if (!_isBackend && "JetBrains.Platform.Util".Equals(fileName, StringComparison.OrdinalIgnoreCase))
        {
            Log.Write($"Platform.Util module loaded (id: {moduleId.Value:x2}), hooking InvokeCore for backend profiler injection");
            var result = HookInvokeCore(moduleId);

            if (!result)
            {
                Log.Write($"Failed to hook InvokeCore: {result}");
            }

            return HResult.S_OK;
        }

        return HResult.S_OK;
    }

    [UnmanagedCallersOnly]
    private static void OnStartupCallback(IntPtr instance) => CallProfiler(instance, profiler => profiler.OnStartup());

    [UnmanagedCallersOnly]
    private static void OnLateLoadTaskCallback(IntPtr instance) => CallProfiler(instance, profiler => profiler.OnLateLoadTask());

    [UnmanagedCallersOnly]
    private static void OnDaemonFinishedCallback(IntPtr instance) => CallProfiler(instance, profiler => profiler.OnDaemonFinished());

    [UnmanagedCallersOnly]
    private static void OnDaemonStartedCallback(IntPtr instance) => CallProfiler(instance, profiler => profiler.OnDaemonStarted());

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

    private void OnLateLoadTask()
    {
        _pipeClient?.ReportPhase(Phase.LateLoadTask);
        Log.Write("Late load task fired (OnSolutionLoadAsLateAsPossible)");

        _lateLoadTaskFired = true;

        if (TryFireSolutionLoaded())
        {
            return;
        }

        // If no daemon has appeared shortly after late-load, treat project-model load as
        // the end of startup. A daemon that starts before this timeout keeps the normal
        // "wait for daemon finished" path.
        Task.Delay(2000).ContinueWith(_ => TryFireSolutionLoaded(allowNoDaemonFallback: true));
    }

    private void OnDaemonStarted()
    {
        // Reports once for visibility, then keeps tracking silently — only the very first
        // `CreateDaemonForDocumentImpl` matters for the gate: it tells us at least one
        // document is being analysed, so a `RegisterDaemonFinished` will eventually fire.
        if (!_daemonEverStarted)
        {
            _daemonEverStarted = true;
            _pipeClient?.ReportPhase(Phase.DaemonStarted);
        }
    }

    private void OnDaemonFinished()
    {
        if (_solutionLoaded.IsCancellationRequested)
        {
            return;
        }

        _pipeClient?.ReportPhase(Phase.DaemonFinished);
        _daemonFinishedSeen = true;
        TryFireSolutionLoaded();
    }

    /// <summary>
    /// Fires <see cref="Phase.SolutionLoaded"/> when the gate conditions are satisfied:
    /// <list type="bullet">
    ///   <item>The late-load task has fired (project model is fully loaded).</item>
    ///   <item>Either a daemon was scheduled and has since finished, OR no daemon ever
    ///         scheduled (no document open → nothing to analyse → already settled).</item>
    /// </list>
    /// </summary>
    private bool TryFireSolutionLoaded(bool allowNoDaemonFallback = false)
    {
        if (!_lateLoadTaskFired)
        {
            return false;
        }

        if (_daemonFinishedSeen)
        {
            // The daemon gate is satisfied. This can happen before late-load.
        }
        else if (_daemonEverStarted)
        {
            return false;
        }
        else if (!allowNoDaemonFallback)
        {
            return false;
        }

        if (Interlocked.CompareExchange(ref _solutionLoadedReported, 1, 0) != 0)
        {
            return false;
        }

        _pipeClient?.ReportPhase(Phase.SolutionLoaded);
        _solutionLoaded.Cancel();
        Log.Write($"Solution fully loaded (daemonEverStarted={_daemonEverStarted}, daemonFinishedSeen={_daemonFinishedSeen}, allowNoDaemonFallback={allowNoDaemonFallback}). Total input events sent: {_sentInput}, received: {_receivedInput}");
        return true;
    }

    private HResult HookInvokeCore(ModuleId moduleId)
    {
        try
        {
            using var metaDataImport = ICorProfilerInfo.GetModuleMetaDataImport2(moduleId, CorOpenFlags.ofRead)
                .ThrowIfFailed("Failed to get IMetaDataImport2")
                .Wrap();

            var invokeTypeDef = metaDataImport.Value.FindTypeDefByName("JetBrains.Util.InvokeChildProcess", default)
                .ThrowIfFailed("Failed to find InvokeChildProcess type");

            var invokeCoreMethod = metaDataImport.Value.FindMethod(invokeTypeDef, "InvokeCore", default)
                .ThrowIfFailed("Failed to find InvokeCore method");

            var ilRewriter = Silhouette.IL.IlRewriter.Create(ICorProfilerInfo3);
            using var method = ilRewriter.Import(moduleId, invokeCoreMethod);

            var startInfoTypeDef = metaDataImport.Value.FindTypeDefByName("StartInfo", invokeTypeDef.Token)
                .ThrowIfFailed("Failed to find StartInfo");
            var getTargetMethod = metaDataImport.Value.FindMethod(startInfoTypeDef, "get_Target", default)
                .ThrowIfFailed("Failed to find get_Target");
            var additionalEnvVarsField = metaDataImport.Value.FindField(startInfoTypeDef, "AdditionalEnvironmentVariables", default)
                .ThrowIfFailed("Failed to find AdditionalEnvironmentVariables");

            var getTargetOp = method.Metadata.ResolveMethod(getTargetMethod);
            var envVarsFieldOp = method.Metadata.ResolveField(additionalEnvVarsField);

            var corLib = method.Metadata.CorLibTypes;
            var iDictTypeRef = corLib.GetTypeRef("System.Collections.Generic", "IDictionary`2");

            // Create MemberRefs
            var toStringOp = method.Metadata.GetMemberRef(corLib.Object.TypeDefOrRef, "ToString", MethodSig.CreateInstance(corLib.String));
            var containsOp = method.Metadata.GetMemberRef(corLib.String.TypeDefOrRef, "Contains", MethodSig.CreateInstance(corLib.Boolean, corLib.String));
            var getEnvVarOp = method.Metadata.GetMemberRef(corLib.GetTypeRef("System", "Environment"), "GetEnvironmentVariable", MethodSig.CreateStatic(corLib.String, corLib.String));

            // Create TypeSpec for IDictionary<string, string> and its set_Item MemberRef
            var iDictStringStringSpec = method.Metadata.GetTypeSpec(new GenericInstSig(new ClassSig(iDictTypeRef), corLib.String, corLib.String));
            var setItemOp = method.Metadata.GetMemberRef(iDictStringStringSpec, "set_Item", MethodSig.CreateInstance(corLib.Void, new GenericVar(0), new GenericVar(1)));

            // Target for the branch-skip: the original first instruction
            var skipTarget = method.Body.Instructions[0];

            int i = 0;

            // if (!startInfo.Target.ToString().Contains("ReSharper.Backend")) goto skip;
            method.Body.Instructions.Insert(i++, Instruction.Create(OpCodes.Ldarg_1));
            method.Body.Instructions.Insert(i++, Instruction.Create(OpCodes.Callvirt, getTargetOp));
            method.Body.Instructions.Insert(i++, Instruction.Create(OpCodes.Callvirt, toStringOp));
            method.Body.Instructions.Insert(i++, Instruction.Create(OpCodes.Ldstr, "ReSharper.Backend"));
            method.Body.Instructions.Insert(i++, Instruction.Create(OpCodes.Callvirt, containsOp));
            method.Body.Instructions.Insert(i++, Instruction.Create(OpCodes.Brfalse, skipTarget));

            // startInfo.AdditionalEnvironmentVariables["CORECLR_ENABLE_PROFILING"] = "1";
            method.Body.Instructions.Insert(i++, Instruction.Create(OpCodes.Ldarg_1));
            method.Body.Instructions.Insert(i++, Instruction.Create(OpCodes.Ldfld, envVarsFieldOp));
            method.Body.Instructions.Insert(i++, Instruction.Create(OpCodes.Ldstr, "CORECLR_ENABLE_PROFILING"));
            method.Body.Instructions.Insert(i++, Instruction.Create(OpCodes.Ldstr, "1"));
            method.Body.Instructions.Insert(i++, Instruction.Create(OpCodes.Callvirt, setItemOp));

            // startInfo.AdditionalEnvironmentVariables["CORECLR_PROFILER"] = "{687FB688-F002-4B64-9A95-567E5502230F}";
            method.Body.Instructions.Insert(i++, Instruction.Create(OpCodes.Ldarg_1));
            method.Body.Instructions.Insert(i++, Instruction.Create(OpCodes.Ldfld, envVarsFieldOp));
            method.Body.Instructions.Insert(i++, Instruction.Create(OpCodes.Ldstr, "CORECLR_PROFILER"));
            method.Body.Instructions.Insert(i++, Instruction.Create(OpCodes.Ldstr, "{687FB688-F002-4B64-9A95-567E5502230F}"));
            method.Body.Instructions.Insert(i++, Instruction.Create(OpCodes.Callvirt, setItemOp));

            // startInfo.AdditionalEnvironmentVariables["CORECLR_PROFILER_PATH_64"] = Environment.GetEnvironmentVariable("COR_PROFILER_PATH_64");
            method.Body.Instructions.Insert(i++, Instruction.Create(OpCodes.Ldarg_1));
            method.Body.Instructions.Insert(i++, Instruction.Create(OpCodes.Ldfld, envVarsFieldOp));
            method.Body.Instructions.Insert(i++, Instruction.Create(OpCodes.Ldstr, "CORECLR_PROFILER_PATH_64"));
            method.Body.Instructions.Insert(i++, Instruction.Create(OpCodes.Ldstr, "COR_PROFILER_PATH_64"));
            method.Body.Instructions.Insert(i++, Instruction.Create(OpCodes.Call, getEnvVarOp));
            method.Body.Instructions.Insert(i++, Instruction.Create(OpCodes.Callvirt, setItemOp));

            method.Body.UpdateInstructionOffsets();

            ilRewriter.Export(method);

            Log.Write($"Successfully hooked InvokeCore ({i} instructions injected)");
            return HResult.S_OK;
        }
        catch (Exception ex)
        {
            Log.Write($"Failed to rewrite InvokeCore method: {ex}");
            return HResult.E_FAIL;
        }
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
        Log.Debug("MonitoringThread started");
        bool isResponsive = true;

        var stopwatch = Stopwatch.StartNew();
        var lastIdleLog = Environment.TickCount64;

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
                    Log.Debug($"UI responsiveness restored after {freezeDuration} ms; pendingKeyTimestamps={_keyTimestamps.Count}, sentInput={_sentInput}, receivedInput={_receivedInput}, lowLevelHookEvents={_lowLevelHookEvents}, keyboardHookEvents={_keyboardHookEvents}");

                    if (freezeDuration >= 100)
                    {
                        _pipeClient?.SendUIFreeze(freezeDuration);
                        Log.Debug($"Reported UI freeze: {freezeDuration} ms");
                    }
                    else
                    {
                        Log.Debug($"Ignored short unresponsive period: {freezeDuration} ms");
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
                        var now = Environment.TickCount64;
                        if (now - lastIdleLog >= 5000)
                        {
                            lastIdleLog = now;
                            Log.Debug($"MonitoringThread idle: no pending key timestamps; sentInput={_sentInput}, receivedInput={_receivedInput}, lowLevelHookEvents={_lowLevelHookEvents}, keyboardHookEvents={_keyboardHookEvents}, sendInputFailures={_sendInputFailures}");
                        }
                        continue;
                    }

                    isResponsive = false;
                    stopwatch.Restart();
                    Log.Debug($"UI responsiveness wait started; pendingKeyTimestamps={_keyTimestamps.Count}, sentInput={_sentInput}, receivedInput={_receivedInput}, lowLevelHookEvents={_lowLevelHookEvents}, keyboardHookEvents={_keyboardHookEvents}");

                    // Now wait indefinitely
                    _responsiveMutex.Wait(_shutdownToken.Token);
                }
            }
        }

        Log.Debug("MonitoringThread exiting");
    }

    private long _suspendUntilTicks;

    private void InputThread()
    {
        // Wait until Resharper modules are loaded before sending the fake inputs
        // Old versions of Resharper don't start loading until VS is idle, so sending inputs keeps them waiting forever
        Log.Debug("InputThread started; waiting for startup signal before installing UI-thread hook and sending probe input");
        _solutionLoadMutex.Wait();
        Log.Debug("InputThread startup signal received");

        SetHook((int)_mainThreadId);

        try
        {
            var inputs = new NativeMethods.INPUT[2];
            var inputSize = Marshal.SizeOf<NativeMethods.INPUT>();
            var firstSuccessfulSendLogged = false;
            var lastNoReceiveLog = Environment.TickCount64;

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

                var sent = NativeMethods.SendInput(2, inputs, inputSize);

                if (sent != 2)
                {
                    var failures = Interlocked.Increment(ref _sendInputFailures);
                    Log.Write($"SendInput failed or partially succeeded: sent={sent}, expected=2, lastWin32Error={Marshal.GetLastWin32Error()}, failures={failures}, foreground=0x{NativeMethods.GetForegroundWindow().ToInt64():x}");
                    continue;
                }

                var totalSent = Interlocked.Add(ref _sentInput, 2);
                if (!firstSuccessfulSendLogged)
                {
                    firstSuccessfulSendLogged = true;
                    Log.Debug($"SendInput first success: totalSent={totalSent}, inputSize={inputSize}, foreground=0x{NativeMethods.GetForegroundWindow().ToInt64():x}");
                }
                else if (totalSent % 200 == 0)
                {
                    Log.Debug($"SendInput progress: totalSent={totalSent}, receivedInput={_receivedInput}, lowLevelHookEvents={_lowLevelHookEvents}, keyboardHookEvents={_keyboardHookEvents}, pendingKeyTimestamps={_keyTimestamps.Count}");
                }

                var now = Environment.TickCount64;
                if (totalSent >= 20 && _receivedInput == 0 && now - lastNoReceiveLog >= 1000)
                {
                    lastNoReceiveLog = now;
                    Log.Debug($"Probe input is being sent but no UI-thread hook events have been received yet: totalSent={totalSent}, lowLevelHookEvents={_lowLevelHookEvents}, keyboardHookEvents={_keyboardHookEvents}, pendingKeyTimestamps={_keyTimestamps.Count}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"InputThread failed: {ex}");
        }
    }

    private void LowLevelHookThread()
    {
        Log.Debug("LowLevelHookThread started");
        // LL hooks require hMod=0, dwThreadId=0 for global hook
        _lowLevelHookProc = LowLevelHookProc;
        _lowLevelHook = NativeMethods.SetWindowsHookEx(NativeMethods.HookType.WH_KEYBOARD_LL, _lowLevelHookProc!, 0, 0);

        if (_lowLevelHook == IntPtr.Zero)
        {
            Log.Write($"Failed to install WH_KEYBOARD_LL hook: {Marshal.GetLastWin32Error()}");
            return;
        }

        Log.Debug($"Installed WH_KEYBOARD_LL hook: handle=0x{_lowLevelHook.ToInt64():x}");

        // Message pump to keep the LL hook alive
        while (!_shutdownToken.IsCancellationRequested && NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }

        Log.Debug($"LowLevelHookThread exiting; lastWin32Error={Marshal.GetLastWin32Error()}, lowLevelHookEvents={_lowLevelHookEvents}");

        return;

        IntPtr LowLevelHookProc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                var count = Interlocked.Increment(ref _lowLevelHookEvents);
                _keyTimestamps.Enqueue(Stopwatch.GetTimestamp());
                if (count == 1 || count % 200 == 0)
                {
                    Log.Debug($"WH_KEYBOARD_LL event: count={count}, code={code}, wParam=0x{wParam.ToInt64():x}, pendingKeyTimestamps={_keyTimestamps.Count}");
                }
            }

            return NativeMethods.CallNextHookEx(0, code, wParam, lParam);
        }
    }

    private void SetHook(int threadId)
    {
        _keyboardHookProc = HookProc;
        _keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.HookType.WH_KEYBOARD, _keyboardHookProc!, 0, threadId);

        if (_keyboardHook == IntPtr.Zero)
        {
            Log.Write($"Failed to install WH_KEYBOARD hook for thread {threadId}: {Marshal.GetLastWin32Error()}");
        }
        else
        {
            Log.Debug($"Installed WH_KEYBOARD hook for thread {threadId}: handle=0x{_keyboardHook.ToInt64():x}");
        }

        IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                var hookEvents = Interlocked.Increment(ref _keyboardHookEvents);
                var isProbeKey = (int)wParam == 0xFC;

                if (isProbeKey)
                {
                    var received = Interlocked.Increment(ref _receivedInput);
                    if (received == 1 || received % 200 == 0)
                    {
                        Log.Debug($"WH_KEYBOARD probe event: received={received}, hookEvents={hookEvents}, lParam=0x{lParam.ToInt64():x}, pendingKeyTimestamps={_keyTimestamps.Count}");
                    }
                }
                else
                {
                    Interlocked.Exchange(ref _suspendUntilTicks, Environment.TickCount64 + 3000);
                    Log.Debug($"WH_KEYBOARD real key event: wParam=0x{wParam.ToInt64():x}, lParam=0x{lParam.ToInt64():x}; suspending probe input for 3000 ms");
                }

                // Dequeue the LL timestamp and signal responsiveness
                if (_keyTimestamps.TryDequeue(out var llTimestamp))
                {
                    _responsiveMutex.Set();
                    if (hookEvents == 1 || hookEvents % 200 == 0)
                    {
                        Log.Debug($"Responsive signal set from WH_KEYBOARD event: hookEvents={hookEvents}, isProbeKey={isProbeKey}, pendingKeyTimestamps={_keyTimestamps.Count}");
                    }

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

    private void WaitForMainWindow()
    {
        while (true)
        {
            var foreground = NativeMethods.GetForegroundWindow();

            if (foreground != IntPtr.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(foreground, out var pid);

                if (pid == Environment.ProcessId)
                {
                    var waited = Interlocked.Exchange(ref _mainWindowWaitIterations, 0);
                    if (waited > 0)
                    {
                        Log.Debug($"WaitForMainWindow succeeded after {waited} iterations; foreground=0x{foreground.ToInt64():x}");
                    }
                    return;
                }
            }

            var iterations = Interlocked.Increment(ref _mainWindowWaitIterations);
            if (iterations == 1 || iterations % 50 == 0)
            {
                var mainWindow = GetMainWindowHandle();
                var foregroundPid = 0u;
                if (foreground != IntPtr.Zero)
                    NativeMethods.GetWindowThreadProcessId(foreground, out foregroundPid);

                Log.Debug($"WaitForMainWindow waiting: iterations={iterations}, foreground=0x{foreground.ToInt64():x}, foregroundPid={foregroundPid}, currentPid={Environment.ProcessId}, mainWindow=0x{mainWindow.ToInt64():x}");
            }

            Thread.Sleep(100);
        }
    }

    private void ForceFocusThread()
    {
        try
        {
            Log.Debug("Force-focus monitor started");
            var missingWindowLogs = 0;
            var focusAttempts = 0;

            while (!_shutdownToken.IsCancellationRequested)
            {
                Thread.Sleep(100);

                var mainWindow = GetMainWindowHandle();

                if (mainWindow == IntPtr.Zero)
                {
                    missingWindowLogs++;
                    if (missingWindowLogs == 1 || missingWindowLogs % 50 == 0)
                    {
                        Log.Debug($"Force-focus waiting for main window: attempts={missingWindowLogs}");
                    }
                    continue;
                }

                var foreground = NativeMethods.GetForegroundWindow();

                if (foreground == mainWindow)
                {
                    continue;
                }

                if (foreground != IntPtr.Zero)
                {
                    NativeMethods.GetWindowThreadProcessId(foreground, out var pid);

                    if (pid != Environment.ProcessId)
                    {
                        focusAttempts++;
                        if (focusAttempts == 1 || focusAttempts % 20 == 0)
                        {
                            Log.Debug($"Force-focus attempt {focusAttempts}: mainWindow=0x{mainWindow.ToInt64():x}, foreground=0x{foreground.ToInt64():x}, foregroundPid={pid}");
                        }
                        FocusWindow(mainWindow, foreground);
                    }
                }
                else
                {
                    focusAttempts++;
                    Log.Debug($"Force-focus attempt {focusAttempts}: no foreground window, mainWindow=0x{mainWindow.ToInt64():x}");
                    FocusWindow(mainWindow, IntPtr.Zero);
                }

            }
        }
        catch (Exception ex)
        {
            Log.Write($"ForceFocusThread failed: {ex}");
        }
    }

    private static IntPtr GetMainWindowHandle()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();

        if (process.MainWindowHandle != IntPtr.Zero)
        {
            return process.MainWindowHandle;
        }

        IntPtr result = IntPtr.Zero;

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);

            if (pid == Environment.ProcessId && NativeMethods.IsWindowVisible(hWnd) && NativeMethods.GetWindow(hWnd, 4 /* GW_OWNER */) == IntPtr.Zero)
            {
                result = hWnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return result;
    }

    private static void FocusWindow(IntPtr window, IntPtr foreground)
    {
        if (NativeMethods.IsIconic(window))
        {
            var restored = NativeMethods.ShowWindowAsync(window, 9 /* SW_RESTORE */);
            Log.Debug($"FocusWindow restored iconic window=0x{window.ToInt64():x}, result={restored}");
        }

        var currentThread = NativeMethods.GetCurrentThreadId();
        var foregroundThread = foreground == IntPtr.Zero
            ? 0
            : NativeMethods.GetWindowThreadProcessId(foreground, out _);

        var attached = foregroundThread != 0 && foregroundThread != currentThread &&
                       NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
        if (foregroundThread != 0 && foregroundThread != currentThread)
        {
            Log.Debug($"AttachThreadInput attach result={attached}, lastWin32Error={Marshal.GetLastWin32Error()}, currentThread={currentThread}, foregroundThread={foregroundThread}");
        }

        try
        {
            var result = NativeMethods.SetForegroundWindow(window);
            Log.Debug($"SetForegroundWindow result={result}, lastWin32Error={Marshal.GetLastWin32Error()}, window=0x{window.ToInt64():x}, foreground=0x{foreground.ToInt64():x}, currentThread={currentThread}, foregroundThread={foregroundThread}, attached={attached}");
        }
        finally
        {
            if (attached)
            {
                var detached = NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
                Log.Debug($"AttachThreadInput detach result={detached}, lastWin32Error={Marshal.GetLastWin32Error()}, currentThread={currentThread}, foregroundThread={foregroundThread}");
            }
        }
    }
}
