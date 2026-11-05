using System;
using System.IO;

namespace LeafItAlone.Launcher.Services;

/// <summary>
/// Finds a Python for <c>apply.ps1</c>. Priority: an embedded runtime shipped beside the app
/// (<c>runtime/py/python.exe</c>, L7), then a <c>LEAF_PYTHON</c> override, then the stock
/// <c>C:\Python312\python.exe</c> the existing toolchain already used. <c>apply.ps1</c> imports
/// <c>UnityPy</c> (installed into whatever interpreter we name) and re-execs itself under it with
/// <c>-Python</c>, so this must be a Python that has UnityPy — the system default on the user's
/// machine does; a fresh install without it relies on apply.ps1's own pip-install fallback.
/// </summary>
public static class PythonLocator
{
    public static string SystemDefault => @"C:\Python312\python.exe";

    public static string Resolve(string? appRoot)
    {
        // 1. Embedded runtime, so the launcher works on a machine with no Python at all (L7).
        if (!string.IsNullOrEmpty(appRoot))
        {
            string embedded = Path.Combine(appRoot!, "runtime", "py", "python.exe");
            if (File.Exists(embedded)) return embedded;
        }

        // 2. Explicit override for developers with Python elsewhere.
        string? env = Environment.GetEnvironmentVariable("LEAF_PYTHON");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env!;

        // 3. The interpreter the existing toolchain used.
        if (File.Exists(SystemDefault)) return SystemDefault;

        return null!; // caller decides how to report a missing Python
    }

    /// <summary>True if <see cref="Resolve"/> found a real interpreter (not null).</summary>
    public static bool TryResolve(string? appRoot, out string python)
    {
        python = Resolve(appRoot);
        return !string.IsNullOrEmpty(python);
    }
}
