using System;
using System.IO;

namespace LeafItAlone.Launcher.Services;

/// <summary>
/// Finds a Python for <c>apply.ps1</c>. Priority: a <c>LEAF_PYTHON</c> override (developer
/// machines), then the stock <c>C:\Python312\python.exe</c> the toolchain is pinned to. It
/// only locates a file - existence is NOT validity. <see cref="PythonHealth"/> then probes the
/// found interpreter for "is 3.12 AND UnityPy importable"; if that fails the launcher guides
/// the user to install it (L7 chose detect-and-guide over bundling a runtime). A path from an
/// embedded <c>runtime/py</c> is still honoured first if someone drops one in, but we ship none.
/// </summary>
public static class PythonLocator
{
    public static string SystemDefault => @"C:\Python312\python.exe";

    public static string Resolve(string? appRoot)
    {
        // Optional drop-in embedded runtime (we do not ship one; kept so a portable install works).
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
