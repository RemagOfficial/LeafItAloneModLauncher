using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace LeafItAlone.Launcher.Services;

/// <summary>Outcome of a subprocess run.</summary>
public sealed record ProcessResult(int ExitCode, bool TimedOut, string? Fault = null)
{
    public bool Success => Fault is null && !TimedOut && ExitCode == 0;
}

/// <summary>
/// The launcher's ONLY way to run an external engine. Every apply / revert /
/// install / status call funnels through here, so patch logic is never duplicated
/// in C# (plan: "absorb the UX, reuse the engines").
///
/// <para>
/// Builds <c>powershell.exe -NoProfile -ExecutionPolicy Bypass -File &lt;script&gt; ...</c>,
/// quoting is handled by <see cref="ProcessStartInfo.ArgumentList"/> (the game and
/// profile live on different drives with spaces in the paths, so naive string
/// concatenation is unsafe). stdout/stderr are streamed line-by-line to
/// <see cref="LineReceived"/> for the log drawer, and a wall-clock timeout
/// terminates the whole process tree on the exact PID.
/// </para>
/// </summary>
public sealed class ProcessRunner
{
    /// <summary>One interleaved line of stdout or stderr, already newline-trimmed.</summary>
    public event Action<string>? LineReceived;

    /// <summary>Default per-call timeout; long UnityPy patches still finish well inside it.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private static readonly string PowerShellExe =
        Environment.GetFolderPath(Environment.SpecialFolder.System) + "\\WindowsPowerShell\\v1.0\\powershell.exe";

    /// <summary>
    /// Run a PowerShell <paramref name="scriptPath"/> with <paramref name="scriptArgs"/>
    /// (each becomes its own argv token). Example: apply.ps1 with <c>-Yes</c> and
    /// <c>-Python &lt;bundled&gt;</c> substituted by the caller.
    /// </summary>
    public Task<ProcessResult> RunPowerShellScriptAsync(
        string scriptPath,
        IEnumerable<string>? scriptArgs = null,
        string? workingDir = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var args = new List<string>
        {
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", scriptPath,   // ArgumentList quotes this, spaces and all
        };
        if (scriptArgs is not null) args.AddRange(scriptArgs);
        return RunAsync(PowerShellExe, args, workingDir, timeout, cancellationToken);
    }

    /// <summary>Run an arbitrary executable. Streams output; returns exit code / timeout status.</summary>
    public async Task<ProcessResult> RunAsync(
        string exe,
        IEnumerable<string> arguments,
        string? workingDir = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = workingDir ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,      // required for redirection + ArgumentList
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var limit = timeout ?? DefaultTimeout;
        if (limit > TimeSpan.Zero) cts.CancelAfter(limit);

        using var proc = new Process { StartInfo = psi };
        var stdoutDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) stdoutDone.TrySetResult(true);
            else LineReceived?.Invoke(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) stderrDone.TrySetResult(true);
            else LineReceived?.Invoke(e.Data);
        };

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, false, $"Failed to start {exe}: {ex.Message}");
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        bool timedOut = false;
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            KillTree(proc);
            // Give the killed process a moment so WaitForExit below flushes cleanly.
            try { await Task.WhenAll(stdoutDone.Task, stderrDone.Task).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch { /* best effort */ }
        }

        // Non-generic WaitForExit() ensures the async output handlers have drained.
        try { proc.WaitForExit(); } catch { /* already exited */ }

        if (timedOut)
        {
            LineReceived?.Invoke($"[launcher] timed out after {limit:hh\\:mm\\:ss}; terminated process tree (PID {SafePid(proc)}).");
            return new ProcessResult(-1, true);
        }

        int code = SafeExitCode(proc);
        LineReceived?.Invoke($"[launcher] exited with code {code}.");
        return new ProcessResult(code, false);
    }

    private static void KillTree(Process proc)
    {
        try { proc.Kill(entireProcessTree: true); }
        catch { /* process may already be gone */ }
    }

    private static int SafePid(Process proc) { try { return proc.Id; } catch { return -1; } }

    private static int SafeExitCode(Process proc) { try { return proc.ExitCode; } catch { return -1; } }
}
