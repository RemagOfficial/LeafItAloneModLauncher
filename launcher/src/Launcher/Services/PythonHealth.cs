using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LeafItAlone.Launcher.Services;

/// <summary>
/// How good the resolved Python interpreter actually is. A file existing is not
/// enough: <c>apply.ps1</c> runs the UnityPy patcher under it, so a usable runtime
/// means the interpreter AND an importable <c>UnityPy</c>. We do not embed a Python
/// (L7 decision); when this is anything but <see cref="PythonStatus.Ok"/> we guide the
/// user to install the pinned interpreter themselves.
/// </summary>
public enum PythonStatus
{
    /// <summary>Not probed yet.</summary>
    Unknown,
    /// <summary>No interpreter found at all (PythonLocator resolved nothing).</summary>
    NotInstalled,
    /// <summary>Interpreter found and imports UnityPy, but is not the pinned 3.12.</summary>
    WrongVersion,
    /// <summary>Interpreter found but <c>UnityPy</c> is not importable.</summary>
    MissingUnityPy,
    /// <summary>Interpreter path exists yet running it failed (broken / not really Python).</summary>
    Broken,
    /// <summary>Interpreter found, is 3.12, and imports UnityPy - apply can run.</summary>
    Ok,
}

/// <summary>A probe outcome: the status plus the interpreter version we saw and a short detail.</summary>
public sealed record PythonHealthResult(PythonStatus Status, string VersionText, string Detail)
{
    /// <summary>True when a resolved interpreter can drive <c>apply.ps1</c> (UnityPy present).</summary>
    public bool ReadyForApply => Status is PythonStatus.Ok or PythonStatus.WrongVersion;
}

/// <summary>
/// Detects whether the machine has a Python that can run the mod engine. The
/// classification is pure (fed captured probe output) so it is unit-testable without a
/// real interpreter; <see cref="ProbeAsync"/> does the one cheap subprocess that asks
/// the interpreter for its version and whether UnityPy resolves.
/// </summary>
public static class PythonHealth
{
    /// <summary>The interpreter the toolchain is pinned to (3.14 breaks UnityPy's lz4/brotli).</summary>
    public const string PinnedVersion = "3.12";

    /// <summary>Where to send a user who has no suitable Python: the official Windows downloads.</summary>
    public const string DownloadUrl = "https://www.python.org/downloads/windows/";

    /// <summary>The one-liner a user runs (or we run) to add UnityPy to an interpreter.</summary>
    public const string PipCommand = "python -m pip install UnityPy";

    // Ask the interpreter for its version and whether UnityPy resolves, WITHOUT importing
    // it (find_spec is cheap and avoids loading the native deps just to answer a health poll).
    private const string ProbeStatement =
        "import sys, importlib.util as u; " +
        "print('LEAF_PYVER=%d.%d.%d' % sys.version_info[:3]); " +
        "print('LEAF_UNITYPY=' + ('yes' if u.find_spec('UnityPy') else 'no'))";

    /// <summary>Arguments for <c>python.exe</c> that run the probe. Kept public so it is testable.</summary>
    public static IReadOnlyList<string> BuildProbeArgs() => new[] { "-c", ProbeStatement };

    /// <summary>
    /// Run the probe against <paramref name="pythonPath"/> and classify the result.
    /// A null / empty path short-circuits to <see cref="PythonStatus.NotInstalled"/> (no spawn).
    /// Streams nothing to the log: the probe is noisy internals, not user output.
    /// </summary>
    public static async Task<PythonHealthResult> ProbeAsync(ProcessRunner runner, string? pythonPath)
    {
        if (string.IsNullOrWhiteSpace(pythonPath))
            return new PythonHealthResult(PythonStatus.NotInstalled, "", "no interpreter resolved");

        var lines = new List<string>();
        var gate = new object();
        void Collect(string l) { lock (gate) lines.Add(l); }

        runner.LineReceived += Collect;
        ProcessResult res;
        try
        {
            res = await runner.RunAsync(pythonPath!, BuildProbeArgs(), timeout: TimeSpan.FromSeconds(20));
        }
        finally
        {
            runner.LineReceived -= Collect;
        }

        List<string> captured;
        lock (gate) captured = new List<string>(lines);
        return Interpret(res, captured);
    }

    /// <summary>
    /// Turn a probe run's exit code + output lines into a status. Pure and public so the
    /// exact markers can be exercised headlessly with synthetic output.
    /// </summary>
    public static PythonHealthResult Interpret(ProcessResult res, IReadOnlyList<string> output)
    {
        string joined = string.Join("\n", output);

        // A failed spawn (Fault) or a non-zero exit means the interpreter did not run the
        // probe at all - treat as a broken/unusable install rather than silently passing.
        string? ver = Extract(output, "LEAF_PYVER=");
        string? unity = Extract(output, "LEAF_UNITYPY=");

        if (res.Fault is not null)
            return new PythonHealthResult(PythonStatus.Broken, ver ?? "", res.Fault);

        if (ver is null)
            return new PythonHealthResult(PythonStatus.Broken, "",
                $"interpreter exited {res.ExitCode} without reporting a version");

        // UnityPy missing is the actionable case: an installed interpreter that can't run the patcher.
        if (unity == "no" || res.ExitCode != 0 && joined.Contains("UnityPy"))
            return new PythonHealthResult(PythonStatus.MissingUnityPy, ver, "UnityPy is not importable");

        if (unity != "yes")
            return new PythonHealthResult(PythonStatus.Broken, ver, "probe did not report UnityPy state");

        bool pinned = ver.StartsWith(PinnedVersion, StringComparison.Ordinal);
        return pinned
            ? new PythonHealthResult(PythonStatus.Ok, ver, "ready")
            : new PythonHealthResult(PythonStatus.WrongVersion, ver, $"has UnityPy but is not {PinnedVersion}");
    }

    /// <summary>A short human phrase for the status, used in the Dashboard + the popup title.</summary>
    public static string Describe(PythonHealthResult r) => r.Status switch
    {
        PythonStatus.Ok => $"Python {r.VersionText} with UnityPy - ready",
        PythonStatus.WrongVersion => $"Python {r.VersionText} has UnityPy but is not the pinned 3.12 - may still work",
        PythonStatus.MissingUnityPy => $"Python {r.VersionText} found but UnityPy is not installed",
        PythonStatus.NotInstalled => "No Python interpreter found",
        PythonStatus.Broken => "The Python interpreter failed to run",
        _ => "Python not checked",
    };

    private static string? Extract(IReadOnlyList<string> lines, string marker)
    {
        foreach (var raw in lines)
        {
            int i = raw.IndexOf(marker, StringComparison.Ordinal);
            if (i >= 0)
            {
                string val = raw.Substring(i + marker.Length).Trim();
                int sp = val.IndexOfAny(new[] { ' ', '\t' });
                return sp < 0 ? val : val.Substring(0, sp);
            }
        }
        return null;
    }
}
