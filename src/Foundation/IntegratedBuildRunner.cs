using System.Diagnostics;
using System.Text.Json;

namespace AutoFollower;

/// <summary>Runs explicit integration stages with unique outputs and owned-child cancellation.</summary>
public static class IntegratedBuildRunner
{
    public static int Run(string root, string request, string output, string python, CancellationToken token)
    {
        root = Path.GetFullPath(root); output = Path.GetFullPath(output); request = Path.GetFullPath(request);
        string taskRoot = Path.Combine(root, "runs", "T09") + Path.DirectorySeparatorChar;
        if (!output.StartsWith(taskRoot, StringComparison.OrdinalIgnoreCase) || Directory.Exists(output))
            throw new InvalidDataException("Integration requires a unique output below runs/T09");
        foreach (var path in new[] { request, python, Path.Combine(root, "prototypes", "T09", "integrate.py") }) SafePaths.NoLinks(path);
        var info = new ProcessStartInfo(python) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var value in new[] { Path.Combine(root, "prototypes", "T09", "integrate.py"), request, output }) info.ArgumentList.Add(value);
        token.ThrowIfCancellationRequested();
        using var process = Process.Start(info) ?? throw new IOException("Could not start integration worker");
        var stdout = Task.Run(async () => { while (await process.StandardOutput.ReadLineAsync() is { } line) Console.Out.WriteLine(line); });
        var stderr = Task.Run(async () => { while (await process.StandardError.ReadLineAsync() is { } line) Console.Error.WriteLine(line); });
        using var cancellation = token.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        process.WaitForExit();
        Task.WaitAll(stdout, stderr);
        if (token.IsCancellationRequested)
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "runner-cancellation.json"), JsonSerializer.Serialize(new {
                status = "cancelled", workerExited = process.HasExited, pid = process.Id,
                outputPreserved = true, originalInputsUntouchedByRunner = true,
                retry = "Use a new output path; never reuse a partial attempt"
            }, Wire.Options));
            return 130;
        }
        return process.ExitCode;
    }
}
