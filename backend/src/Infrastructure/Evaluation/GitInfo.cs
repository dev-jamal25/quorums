using System.ComponentModel;
using System.Diagnostics;

namespace Backend.Infrastructure.Evaluation;

/// <summary>
/// Best-effort resolution of the current git HEAD sha for eval-run provenance (DL-051). Reproducibility
/// wants the commit a result was taken at; if git is unavailable (e.g. a packaged build) it degrades to
/// <c>"unknown"</c> rather than failing the run.
/// </summary>
public static class GitInfo
{
    public const string Unknown = "unknown";

    public static string HeadSha()
    {
        try
        {
            var info = new ProcessStartInfo("git", "rev-parse HEAD")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(info);
            if (process is null)
            {
                return Unknown;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(2000);
            return process.ExitCode == 0 && output.Length > 0 ? output : Unknown;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            // git not on PATH, not a repo, or the process could not be started — provenance degrades.
            return Unknown;
        }
    }
}
