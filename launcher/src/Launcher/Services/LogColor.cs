using System;

namespace LeafItAlone.Launcher.Services;

/// <summary>Coarse severity a single Output-log line carries, used only for colouring.</summary>
public enum LogSeverity { Info, Success, Warning, Error }

/// <summary>
/// Turns a raw log line into a <see cref="LogSeverity"/>. The launcher reads the
/// engine scripts' <b>redirected</b> stdout, and PowerShell strips <c>Write-Host</c>
/// colour when there is no real console — so every line arrives plain. This classifier
/// re-attaches meaning from the text itself (the scripts' and our own markers), keeping
/// the decision pure and unit-testable; the WPF converter just maps it to a brush.
///
/// <para>Signals are matched in priority order: hard failures first, then successes,
/// then warnings, so a line like "apply FAILED" never reads as OK and a healthy
/// "VERDICT: … active" reads green.</para>
/// </summary>
public static class LogColor
{
    public static LogSeverity Classify(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return LogSeverity.Info;
        var trimmed = line!.TrimStart();
        var u = line.ToUpperInvariant();

        // Hard failure markers (ours and the scripts').
        if (Any(u, "FAIL", "ERROR", "TIMEOUT", "EXCEPTION", "ABORT", "REFUSING", "DENIED",
                    "CRITICAL", "NO-VERIFY", "CORRUPT"))
            return LogSeverity.Error;
        if (trimmed.StartsWith("!!") || u.Contains(" !! ") || u.Contains("MISSING -"))
            return LogSeverity.Error;
        // "[launcher] exited with code N." for any non-zero exit is a failure signal.
        if (u.StartsWith("[LAUNCHER] EXITED WITH CODE") && !u.EndsWith("CODE 0."))
            return LogSeverity.Error;

        // Success markers.
        if (Any(u, "PASS", " OK", "OK ---", "APPLIED", "REVERTED", "INSTALLED OK",
                   "PRESENT, MATCHES", "STATUS REFRESHED", "SAFE-BASE INSTALLED")
            || u.StartsWith("VERDICT: SAFE-BASE")
            || u.Contains("+ M1 + M2 + M3 + M4 ACTIVE")
            || (u.StartsWith("--- ") && u.EndsWith(" OK ---")))
            return LogSeverity.Success;

        // Warning / partial markers.
        if (Any(u, "WARN", "PARTIAL", "NOT INSTALLED", "NO PYTHON", "NO VERDICT", "DIFFERS",
                   "NOT IN PLACE", "NOT ENABLED", "RECOMMENDED", "SKIP", "NOT A USABLE",
                   "ALREADY RUNNING", "CLOSE THE GAME", "NOT FOUND"))
            return LogSeverity.Warning;

        return LogSeverity.Info;
    }

    private static bool Any(string upper, params string[] tokens)
    {
        foreach (var t in tokens)
            if (upper.Contains(t, StringComparison.Ordinal)) return true;
        return false;
    }
}
