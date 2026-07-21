using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static EzyImageViewer.Imaging.Codecs.Isolation.IsolationNativeMethods;

namespace EzyImageViewer.Imaging.Codecs.Isolation;

/// <summary>
/// Launches one codec request in the selected AppContainer identity. The child is created
/// suspended, assigned to an unnamed non-breakaway job, and only then resumed.
/// </summary>
internal sealed class ClassicAppContainerProcessLauncher(
    TimeProvider timeProvider,
    Action? requestSent = null)
    : IIsolatedCodecProcessLauncher
{
    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));
    // Optional internal observer lets installed-boundary tests start cancellation after stdin is closed.
    private readonly Action? _requestSent = requestSent;

    public async Task<IsolatedCodecProcessResult> ExecuteAsync(
        IsolatedCodecProcessRequest request,
        IsolatedCodecProcessPolicy policy,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        policy.Validate();

        using var inheritedSourceHandle = DuplicateInheritedReadHandle(request.InheritedSource);
        var standardInput = await CreateStandardInputAsync(
                request,
                inheritedSourceHandle,
                policy.MaxStandardInputBytes,
                cancellationToken)
            .ConfigureAwait(false);
        using var appContainerSid = AppContainerProfileAccess.OpenIdentitySid(policy);
        using var launched = Launch(
            request,
            policy,
            appContainerSid,
            inheritedSourceHandle);
        inheritedSourceHandle?.Dispose();

        var writeTask = WriteInputAndCloseAsync(launched.StandardInput, standardInput);
        var outputTask = BoundedPipeReader.ReadAsync(
            launched.StandardOutput,
            policy.MaxStandardOutputBytes);
        var errorTask = BoundedPipeReader.ReadAsync(
            launched.StandardError,
            policy.MaxStandardErrorBytes);
        var waitTask = launched.WaitForExitAsync();
        var operationTask = CompleteOperationAsync(writeTask, outputTask, errorTask, waitTask);
        var faultSignal = CreateFaultSignal(writeTask, outputTask, errorTask, waitTask);

        using var deadlineCancellation = new CancellationTokenSource();
        var deadlineTask = Task.Delay(
            policy.WallClockDeadline,
            _timeProvider,
            deadlineCancellation.Token);
        var cancellationSignal = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationRegistration = cancellationToken.UnsafeRegister(
            static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            cancellationSignal);

        try
        {
            var completed = await Task.WhenAny(
                    operationTask,
                    faultSignal,
                    deadlineTask,
                    cancellationSignal.Task)
                .ConfigureAwait(false);

            if (operationTask.IsCompleted)
            {
                await operationTask.ConfigureAwait(false);
            }
            else if (completed == cancellationSignal.Task)
            {
                await TerminateAndDrainAsync(
                        launched,
                        policy.ForcedTerminationExitCode,
                        writeTask,
                        outputTask,
                        errorTask,
                        waitTask)
                    .ConfigureAwait(false);
                throw new OperationCanceledException(cancellationToken);
            }
            else if (completed == deadlineTask)
            {
                await TerminateAndDrainAsync(
                        launched,
                        policy.ForcedTerminationExitCode,
                        writeTask,
                        outputTask,
                        errorTask,
                        waitTask)
                    .ConfigureAwait(false);
                throw new TimeoutException(
                    $"The isolated codec exceeded its {policy.WallClockDeadline} wall-clock deadline.");
            }
            else
            {
                var failure = await faultSignal.ConfigureAwait(false);
                await TerminateAndDrainAsync(
                        launched,
                        policy.ForcedTerminationExitCode,
                        writeTask,
                        outputTask,
                        errorTask,
                        waitTask)
                    .ConfigureAwait(false);
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            return new IsolatedCodecProcessResult(
                await waitTask.ConfigureAwait(false),
                await outputTask.ConfigureAwait(false),
                await errorTask.ConfigureAwait(false));
        }
        catch
        {
            if (!waitTask.IsCompleted)
            {
                await TerminateAndDrainAsync(
                        launched,
                        policy.ForcedTerminationExitCode,
                        writeTask,
                        outputTask,
                        errorTask,
                        waitTask)
                    .ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            deadlineCancellation.Cancel();
        }
    }

    private static LaunchedProcess Launch(
        IsolatedCodecProcessRequest request,
        IsolatedCodecProcessPolicy policy,
        SafeSidHandle appContainerSid,
        SafeFileHandle? inheritedSourceHandle)
    {
        using var inputPipe = AnonymousPipeEnds.Create(parentReads: false);
        using var outputPipe = AnonymousPipeEnds.Create(parentReads: true);
        using var errorPipe = AnonymousPipeEnds.Create(parentReads: true);
        using var attributeList = CreateAttributeList(attributeCount: 2);
        var inheritedHandleCount = inheritedSourceHandle is null ? 3 : 4;
        using var inheritedHandles = HGlobalBuffer.Allocate(
            checked(inheritedHandleCount * IntPtr.Size));
        using var capabilitySet = AppContainerCapabilitySet.Create(policy.Capabilities);
        using var securityCapabilities = HGlobalBuffer.Allocate(
            Marshal.SizeOf<SecurityCapabilities>());
        using var environment = BuildEnvironmentBlock(request.Environment);

        Marshal.WriteIntPtr(inheritedHandles.DangerousGetHandle(), 0, inputPipe.ChildHandle);
        Marshal.WriteIntPtr(inheritedHandles.DangerousGetHandle(), IntPtr.Size, outputPipe.ChildHandle);
        Marshal.WriteIntPtr(inheritedHandles.DangerousGetHandle(), 2 * IntPtr.Size, errorPipe.ChildHandle);
        if (inheritedSourceHandle is not null)
        {
            Marshal.WriteIntPtr(
                inheritedHandles.DangerousGetHandle(),
                3 * IntPtr.Size,
                inheritedSourceHandle.DangerousGetHandle());
        }
        AddAttribute(
            attributeList,
            ProcThreadAttributeHandleList,
            inheritedHandles.DangerousGetHandle(),
            checked((nuint)(inheritedHandleCount * IntPtr.Size)));

        var capabilities = new SecurityCapabilities
        {
            AppContainerSid = appContainerSid.DangerousGetHandle(),
            Capabilities = capabilitySet.Attributes,
            CapabilityCount = capabilitySet.Count,
            Reserved = 0,
        };
        Marshal.StructureToPtr(
            capabilities,
            securityCapabilities.DangerousGetHandle(),
            fDeleteOld: false);
        AddAttribute(
            attributeList,
            ProcThreadAttributeSecurityCapabilities,
            securityCapabilities.DangerousGetHandle(),
            checked((nuint)Marshal.SizeOf<SecurityCapabilities>()));

        var startupInfo = new StartupInfoEx
        {
            StartupInfo = new StartupInfo
            {
                Cb = Marshal.SizeOf<StartupInfoEx>(),
                Flags = StartfUseStdHandles,
                StandardInput = inputPipe.ChildHandle,
                StandardOutput = outputPipe.ChildHandle,
                StandardError = errorPipe.ChildHandle,
            },
            AttributeList = attributeList.DangerousGetHandle(),
        };

        var job = CreateConfiguredJob(policy);
        SafeProcessHandle? process = null;
        SafeThreadHandle? thread = null;
        try
        {
            var commandLine = BuildCommandLine(request.ExecutablePath, request.Arguments);
            var creationFlags = CreateSuspended
                | CreateNoWindow
                | ExtendedStartupInfoPresent
                | CreateUnicodeEnvironment;
            if (!CreateProcessW(
                    request.ExecutablePath,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: true,
                    creationFlags,
                    environment.DangerousGetHandle(),
                    request.WorkingDirectory,
                    ref startupInfo,
                    out var processInformation))
            {
                throw LastWin32Exception("CreateProcessW");
            }

            process = new SafeProcessHandle(processInformation.Process, ownsHandle: true);
            thread = new SafeThreadHandle(processInformation.Thread);
            inputPipe.DisposeChild();
            outputPipe.DisposeChild();
            errorPipe.DisposeChild();

            if (!AssignProcessToJobObject(job, process))
            {
                var failure = LastWin32Exception("AssignProcessToJobObject");
                TerminateProcessBestEffort(process, policy.ForcedTerminationExitCode);
                throw failure;
            }

            if (ResumeThread(thread) == ResumeThreadFailed)
            {
                var failure = LastWin32Exception("ResumeThread");
                TerminateJobBestEffort(job, process, policy.ForcedTerminationExitCode);
                throw failure;
            }
            thread.Dispose();
            thread = null;

            var standardInput = new FileStream(
                inputPipe.TakeParent(), FileAccess.Write, 64 * 1024, isAsync: false);
            var standardOutput = new FileStream(
                outputPipe.TakeParent(), FileAccess.Read, 64 * 1024, isAsync: false);
            var standardError = new FileStream(
                errorPipe.TakeParent(), FileAccess.Read, 16 * 1024, isAsync: false);
            var launched = new LaunchedProcess(
                job,
                process,
                standardInput,
                standardOutput,
                standardError);
            job = null!;
            process = null;
            return launched;
        }
        catch
        {
            thread?.Dispose();
            if (process is not null && !process.IsInvalid && !process.IsClosed)
                TerminateJobBestEffort(job, process, policy.ForcedTerminationExitCode);
            process?.Dispose();
            job.Dispose();
            throw;
        }
    }

    private static SafeFileHandle? DuplicateInheritedReadHandle(InheritedReadOnlySource? source)
    {
        if (source is null)
            return null;

        var currentProcess = GetCurrentProcess();
        if (!DuplicateHandle(
                currentProcess,
                source.Handle,
                currentProcess,
                out var duplicate,
                FileGenericRead,
                inheritHandle: true,
                options: 0))
        {
            duplicate.Dispose();
            throw LastWin32Exception("DuplicateHandle");
        }
        if (duplicate.IsInvalid)
        {
            duplicate.Dispose();
            throw new InvalidOperationException("DuplicateHandle returned an invalid file handle.");
        }
        return duplicate;
    }

    private static async ValueTask<ReadOnlyMemory<byte>> CreateStandardInputAsync(
        IsolatedCodecProcessRequest request,
        SafeFileHandle? inheritedSourceHandle,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadOnlyMemory<byte> standardInput;
        if (request.InheritedSource is null)
        {
            standardInput = request.StandardInput;
        }
        else
        {
            standardInput = await request.InheritedSource.CreateStandardInputAsync(
                    inheritedSourceHandle!.DangerousGetHandle(),
                    cancellationToken)
                .ConfigureAwait(false);
            standardInput = standardInput.ToArray();
        }

        if (standardInput.Length > maximumBytes)
        {
            throw new ArgumentException(
                $"The codec control message exceeds its {maximumBytes:N0}-byte input limit.",
                nameof(request));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return standardInput;
    }

    private static SafeJobHandle CreateConfiguredJob(IsolatedCodecProcessPolicy policy)
    {
        var job = CreateJobObjectW(IntPtr.Zero, name: null);
        if (job.IsInvalid)
        {
            job.Dispose();
            throw LastWin32Exception("CreateJobObjectW");
        }

        try
        {
            var information = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    PerProcessUserTimeLimit = policy.PerProcessUserTimeLimit.Ticks,
                    LimitFlags = JobObjectLimitKillOnJobClose
                        | JobObjectLimitActiveProcess
                        | JobObjectLimitProcessTime
                        | JobObjectLimitProcessMemory,
                    ActiveProcessLimit = 1,
                },
                ProcessMemoryLimit = new UIntPtr(
                    checked((ulong)policy.ProcessMemoryLimitBytes)),
            };
            if (!SetInformationJobObject(
                    job,
                    JobObjectExtendedLimitInformationClass,
                    ref information,
                    checked((uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>())))
            {
                throw LastWin32Exception("SetInformationJobObject");
            }
            return job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    private static SafeProcThreadAttributeList CreateAttributeList(int attributeCount)
    {
        nuint size = 0;
        _ = InitializeProcThreadAttributeList(
            IntPtr.Zero, attributeCount, flags: 0, ref size);
        if (size == 0)
            throw LastWin32Exception("InitializeProcThreadAttributeList(size)");

        var buffer = Marshal.AllocHGlobal(checked((nint)size));
        try
        {
            if (!InitializeProcThreadAttributeList(
                    buffer, attributeCount, flags: 0, ref size))
            {
                throw LastWin32Exception("InitializeProcThreadAttributeList");
            }
            return new SafeProcThreadAttributeList(buffer);
        }
        catch
        {
            Marshal.FreeHGlobal(buffer);
            throw;
        }
    }

    private static void AddAttribute(
        SafeProcThreadAttributeList attributeList,
        nuint attribute,
        IntPtr value,
        nuint size)
    {
        if (!UpdateProcThreadAttribute(
                attributeList.DangerousGetHandle(),
                flags: 0,
                attribute,
                value,
                size,
                previousValue: IntPtr.Zero,
                returnSize: IntPtr.Zero))
        {
            throw LastWin32Exception("UpdateProcThreadAttribute");
        }
    }

    private static HGlobalBuffer BuildEnvironmentBlock(
        IReadOnlyDictionary<string, string> environment)
    {
        ValidateEnvironment(environment);
        var builder = new StringBuilder();
        foreach (var entry in environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(entry.Key).Append('=').Append(entry.Value).Append('\0');
        }
        if (builder.Length == 0)
            builder.Append('\0');
        return HGlobalBuffer.FromUnicodeString(builder.ToString());
    }

    private static void ValidateEnvironment(IReadOnlyDictionary<string, string> environment)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in environment)
        {
            if (string.IsNullOrEmpty(entry.Key)
                || entry.Key.Contains('=')
                || entry.Key.Contains('\0')
                || entry.Value is null
                || entry.Value.Contains('\0'))
            {
                throw new ArgumentException(
                    "The child environment contains an invalid entry.",
                    nameof(environment));
            }
            if (!names.Add(entry.Key))
            {
                throw new ArgumentException(
                    "The child environment contains duplicate case-insensitive names.",
                    nameof(environment));
            }
        }
    }

    private static StringBuilder BuildCommandLine(
        string executablePath,
        IReadOnlyList<string> arguments)
    {
        var commandLine = new StringBuilder(QuoteArgument(executablePath));
        foreach (var argument in arguments)
            commandLine.Append(' ').Append(QuoteArgument(argument));
        if (commandLine.Length >= 32_767)
            throw new ArgumentOutOfRangeException(nameof(arguments), "The Windows command line is too long.");
        return commandLine;
    }

    private static string QuoteArgument(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Contains('\0'))
            throw new ArgumentException("Command-line arguments cannot contain NUL.", nameof(argument));
        if (argument.Length > 0
            && !argument.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            return argument;
        }

        var quoted = new StringBuilder(argument.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                quoted.Append('\\', checked(backslashes * 2 + 1)).Append('"');
                backslashes = 0;
                continue;
            }
            quoted.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        quoted.Append('\\', checked(backslashes * 2)).Append('"');
        return quoted.ToString();
    }

    private static void ValidateRequest(IsolatedCodecProcessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkingDirectory);
        ArgumentNullException.ThrowIfNull(request.Arguments);
        ArgumentNullException.ThrowIfNull(request.Environment);
        ValidateEnvironment(request.Environment);

        if (!Path.IsPathFullyQualified(request.ExecutablePath))
            throw new ArgumentException("The codec executable path must be absolute.", nameof(request));
        if (!File.Exists(request.ExecutablePath))
            throw new FileNotFoundException("The codec executable was not found.", request.ExecutablePath);
        if (!Path.IsPathFullyQualified(request.WorkingDirectory))
            throw new ArgumentException("The codec working directory must be absolute.", nameof(request));
        if (!Directory.Exists(request.WorkingDirectory))
            throw new DirectoryNotFoundException(request.WorkingDirectory);
        if (request.Arguments.Any(argument => argument is null))
            throw new ArgumentException("Codec arguments cannot contain null.", nameof(request));
        if (request.InheritedSource is not null)
        {
            if (!request.StandardInput.IsEmpty)
            {
                throw new ArgumentException(
                    "Inherited source requests must create standard input from the child handle.",
                    nameof(request));
            }
            ArgumentNullException.ThrowIfNull(request.InheritedSource.Handle);
            ArgumentNullException.ThrowIfNull(request.InheritedSource.CreateStandardInputAsync);
            if (request.InheritedSource.Handle.IsInvalid || request.InheritedSource.Handle.IsClosed)
                throw new ArgumentException("The inherited source handle is not open.", nameof(request));
        }
    }

    private async Task WriteInputAndCloseAsync(
        FileStream stream,
        ReadOnlyMemory<byte> input)
    {
        try
        {
            await stream.WriteAsync(input, CancellationToken.None).ConfigureAwait(false);
            await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        _requestSent?.Invoke();
    }

    private static async Task CompleteOperationAsync(params Task[] tasks) =>
        await Task.WhenAll(tasks).ConfigureAwait(false);

    private static Task<Exception> CreateFaultSignal(params Task[] tasks)
    {
        var signal = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var task in tasks)
        {
            _ = task.ContinueWith(
                static (failed, state) => ((TaskCompletionSource<Exception>)state!).TrySetResult(
                    failed.Exception!.GetBaseException()),
                signal,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
        return signal.Task;
    }

    private static async Task TerminateAndDrainAsync(
        LaunchedProcess launched,
        uint exitCode,
        params Task[] tasks)
    {
        launched.Terminate(exitCode);
        foreach (var task in tasks)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // The initiating cancellation, timeout, or boundary failure is reported by caller.
            }
        }
    }

    private static void TerminateJobBestEffort(
        SafeJobHandle job,
        SafeProcessHandle process,
        uint exitCode)
    {
        if (process.IsInvalid || process.IsClosed || IsExited(process))
            return;
        if (!job.IsInvalid && !job.IsClosed && TerminateJobObject(job, exitCode))
            return;
        TerminateProcessBestEffort(process, exitCode);
    }

    private static void TerminateProcessBestEffort(SafeProcessHandle process, uint exitCode)
    {
        if (!process.IsInvalid && !process.IsClosed && !IsExited(process))
            _ = TerminateProcess(process, exitCode);
    }

    private static bool IsExited(SafeProcessHandle process)
    {
        var result = WaitForSingleObject(process, milliseconds: 0);
        if (result == WaitFailed)
            throw LastWin32Exception("WaitForSingleObject");
        return result == WaitObject0;
    }

    private static Win32Exception LastWin32Exception(string operation)
    {
        var error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"{operation} failed with Win32 error {error}.");
    }

    private sealed class LaunchedProcess : IDisposable
    {
        private readonly SafeJobHandle _job;
        private readonly SafeProcessHandle _process;

        internal LaunchedProcess(
            SafeJobHandle job,
            SafeProcessHandle process,
            FileStream standardInput,
            FileStream standardOutput,
            FileStream standardError)
        {
            _job = job;
            _process = process;
            StandardInput = standardInput;
            StandardOutput = standardOutput;
            StandardError = standardError;
        }

        internal FileStream StandardInput { get; }
        internal FileStream StandardOutput { get; }
        internal FileStream StandardError { get; }

        internal Task<int> WaitForExitAsync() => Task.Run(() =>
        {
            var result = WaitForSingleObject(_process, Infinite);
            if (result == WaitFailed)
                throw LastWin32Exception("WaitForSingleObject");
            if (result != WaitObject0)
                throw new InvalidOperationException($"Unexpected process wait result 0x{result:X8}.");
            if (!GetExitCodeProcess(_process, out var exitCode))
                throw LastWin32Exception("GetExitCodeProcess");
            return unchecked((int)exitCode);
        });

        internal void Terminate(uint exitCode)
        {
            if (IsExited(_process))
                return;
            if (TerminateJobObject(_job, exitCode))
                return;

            var jobError = Marshal.GetLastWin32Error();
            if (!TerminateProcess(_process, exitCode) && !IsExited(_process))
            {
                throw new Win32Exception(
                    jobError,
                    "TerminateJobObject failed and the process could not be terminated.");
            }
        }

        public void Dispose()
        {
            StandardInput.Dispose();
            StandardOutput.Dispose();
            StandardError.Dispose();
            _job.Dispose();
            _process.Dispose();
        }
    }

    private sealed class AnonymousPipeEnds : IDisposable
    {
        private SafeFileHandle? _parent;
        private SafeFileHandle? _child;

        private AnonymousPipeEnds(SafeFileHandle parent, SafeFileHandle child)
        {
            _parent = parent;
            _child = child;
        }

        internal IntPtr ChildHandle => (_child
            ?? throw new ObjectDisposedException(nameof(AnonymousPipeEnds))).DangerousGetHandle();

        internal static AnonymousPipeEnds Create(bool parentReads)
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                InheritHandle = true,
            };
            if (!CreatePipe(out var read, out var write, ref attributes, size: 0))
                throw LastWin32Exception("CreatePipe");

            var parent = parentReads ? read : write;
            var child = parentReads ? write : read;
            try
            {
                if (!SetHandleInformation(parent, HandleFlagInherit, flags: 0))
                    throw LastWin32Exception("SetHandleInformation");
                return new AnonymousPipeEnds(parent, child);
            }
            catch
            {
                read.Dispose();
                write.Dispose();
                throw;
            }
        }

        internal SafeFileHandle TakeParent()
        {
            var parent = _parent
                ?? throw new ObjectDisposedException(nameof(AnonymousPipeEnds));
            _parent = null;
            return parent;
        }

        internal void DisposeChild()
        {
            _child?.Dispose();
            _child = null;
        }

        public void Dispose()
        {
            _parent?.Dispose();
            _child?.Dispose();
            _parent = null;
            _child = null;
        }
    }

    private sealed class HGlobalBuffer : SafeHandleZeroOrMinusOneIsInvalid
    {
        private HGlobalBuffer(IntPtr handle) : base(ownsHandle: true) => SetHandle(handle);

        internal static HGlobalBuffer Allocate(int bytes)
        {
            if (bytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(bytes));
            return new HGlobalBuffer(Marshal.AllocHGlobal(bytes));
        }

        internal static HGlobalBuffer FromUnicodeString(string value) =>
            new(Marshal.StringToHGlobalUni(value));

        protected override bool ReleaseHandle()
        {
            Marshal.FreeHGlobal(handle);
            return true;
        }
    }
}
