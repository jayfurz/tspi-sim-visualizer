using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace TspiViewer
{
    public sealed class CliResult
    {
        public bool Ok;
        public int ExitCode = -1;
        public string Stdout = "";
        public string Stderr = "";
        public string Command = "";
        public double ElapsedMs;
    }

    /// <summary>
    /// Runs the headless tspi CLI as a child process, off the main thread. This is how
    /// the editor previews trajectories: the same binary, math, and determinism as the
    /// campaign runs — Unity itself never simulates. Desktop editor/player only
    /// (process spawn is unavailable on mobile/web targets).
    /// </summary>
    public sealed class TspiCliRunner
    {
        /// <summary>Self-contained tspi executable, or "dotnet".</summary>
        public string Executable = "tspi";
        /// <summary>When Executable is "dotnet": absolute path to tspi.dll.</summary>
        public string DllPath = "";
        /// <summary>Working directory for the CLI — the repo root, so ./models resolves.</summary>
        public string WorkingDirectory = "";
        /// <summary>Optional --models override.</summary>
        public string ModelsDir = "";

        /// <summary>`tspi run` (which validates the manifest first) into outTspiPath.</summary>
        public Task<CliResult> RunScenario(string scenarioPath, string outTspiPath)
        {
            string models = string.IsNullOrEmpty(ModelsDir) ? "" : " --models \"" + ModelsDir + "\"";
            string args = "run \"" + scenarioPath + "\" -o \"" + outTspiPath + "\" --quiet" + models;
            return Task.Run(() => Exec(args));
        }

        private CliResult Exec(string args)
        {
            if (!string.IsNullOrEmpty(DllPath)) args = "\"" + DllPath + "\" " + args;
            var result = new CliResult { Command = Executable + " " + args };
            var sw = Stopwatch.StartNew();
            try
            {
                using (var p = new Process())
                {
                    p.StartInfo = new ProcessStartInfo
                    {
                        FileName = Executable,
                        Arguments = args,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    };
                    if (!string.IsNullOrEmpty(WorkingDirectory))
                        p.StartInfo.WorkingDirectory = WorkingDirectory;
                    p.Start();
                    // Drain both pipes concurrently so neither can fill and deadlock.
                    Task<string> so = p.StandardOutput.ReadToEndAsync();
                    Task<string> se = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(120_000))
                    {
                        try { p.Kill(); } catch { /* already exited */ }
                        result.Stderr = "tspi timed out after 120 s";
                        return result;
                    }
                    result.Stdout = so.Result;
                    result.Stderr = se.Result;
                    result.ExitCode = p.ExitCode;
                    result.Ok = p.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                result.Stderr = ex.Message; // e.g. executable not found
            }
            finally
            {
                result.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            }
            return result;
        }
    }
}
