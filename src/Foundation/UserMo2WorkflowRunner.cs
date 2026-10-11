using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AutoFollower;

public sealed record UserMo2WorkflowRequest(string Selection, string Mo2Executable,
    string Worker, string WorkerSha256, string Mode, int TimeoutSeconds = 60, string[]? AdditionalProtectedRoots = null,
    int ProbeHoldSeconds = 0, string? SelectedVfsSnapshot = null);

/// <summary>Entry invoked inside the user's MO2. The server is observed, never supervised.</summary>
public static class UserMo2WorkflowRunner
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern nint GetModuleHandle(string module);
    public static int Run(string root, string requestPath, string output, CancellationToken token)
    {
        root = Path.GetFullPath(root); output = Path.GetFullPath(output); requestPath = Path.GetFullPath(requestPath);
        if (!output.StartsWith(Path.Combine(root, "runs", "T09") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || Directory.Exists(output)) throw new InvalidDataException("User-MO2 workflow needs a unique task output");
        SafePaths.NoLinks(root); SafePaths.NoLinks(requestPath);
        var request = JsonSerializer.Deserialize<UserMo2WorkflowRequest>(File.ReadAllText(requestPath), Wire.Options)
            ?? throw new InvalidDataException("Missing user-MO2 request");
        if (request.Mode is not ("inspect" or "inventory" or "probe" or "guard-checks" or "relay-diagnostic" or "route-probe" or "route-diagnostic") || request.TimeoutSeconds is < 10 or > 900)
            throw new InvalidDataException("Unsupported workflow mode/deadline");
        if (request.ProbeHoldSeconds is < 0 or > 30 || request.ProbeHoldSeconds > 0 && request.Mode is not ("probe" or "relay-diagnostic"))
            throw new InvalidDataException("Unsupported explicit probe hold");
        if (GetModuleHandle("usvfs_x64.dll") == 0) throw new InvalidDataException("Launch this application through the selected MO2 instance");
        foreach (var p in new[] { request.Selection, request.Mo2Executable, request.Worker }) SafePaths.NoLinks(p);
        if (Hashing.File(request.Worker) != request.WorkerSha256) throw new InvalidDataException("Worker helper is stale");
        var selection = JsonSerializer.Deserialize<ResolveRequest>(File.ReadAllText(request.Selection), Wire.Options)
            ?? throw new InvalidDataException("Missing selected save/environment");
        var ini = EnvironmentResolver.Ini(selection.InstanceIni);
        if (ini.GetValueOrDefault("General/selected_profile") != selection.Profile ||
            !string.Equals(Path.GetFullPath(ini.GetValueOrDefault("General/gamePath") ?? ""), Path.GetFullPath(selection.Game), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Selected instance/profile differs from request");
        string baseDirectory = ini.GetValueOrDefault("Settings/base_directory") ?? throw new InvalidDataException("Explicit instance base required");
        string profiles = ini.GetValueOrDefault("Settings/profiles_directory") ?? Path.Combine(baseDirectory, "profiles");
        if (!string.Equals(Path.GetFullPath(ini.GetValueOrDefault("Settings/mod_directory") ?? Path.Combine(baseDirectory, "mods")), Path.GetFullPath(selection.Mods), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFullPath(ini.GetValueOrDefault("Settings/overwrite_directory") ?? Path.Combine(baseDirectory, "overwrite")), Path.GetFullPath(selection.Overwrite), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Selected provider roots differ from MO2 configuration");
        string profile = SafePaths.Below(profiles, VirtualPath.Normalize(selection.Profile));
        var candidates = Process.GetProcessesByName("ModOrganizer").Where(p =>
        {
            try { return string.Equals(p.MainModule?.FileName, request.Mo2Executable, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }).ToArray();
        if (candidates.Length != 1) throw new InvalidDataException("Unique selected MO2 server identity unavailable");
        using var server = candidates[0];
        long serverCreation = server.StartTime.ToUniversalTime().ToFileTimeUtc();
        Directory.CreateDirectory(output); SafePaths.NoLinks(output);
        void Write(string name, object value) => File.WriteAllText(Path.Combine(output, name), JsonSerializer.Serialize(value, Wire.Options));
        File.WriteAllText(Path.Combine(output, "owned.marker"), Guid.NewGuid().ToString("N"));
        var config = new Dictionary<string, string>
        {
            ["schema"] = "user-mo2/v1", ["root"] = output, ["probeHoldSeconds"] = request.ProbeHoldSeconds.ToString(),
            ["markerSha256"] = Hashing.File(Path.Combine(output, "owned.marker")),
            ["mo2Pid"] = server.Id.ToString(), ["mo2Creation"] = serverCreation.ToString(),
            ["mo2Image"] = request.Mo2Executable, ["workerSha256"] = request.WorkerSha256,
            ["usvfsImage"] = Path.Combine(Path.GetDirectoryName(request.Mo2Executable)!, "usvfs_x64.dll"),
            ["reference"] = Path.Combine(selection.Game, "Data", "meshes", "actors", "character", "character assets female", "skeleton_female.nif")
        };
        config["usvfsSha256"] = Hashing.File(config["usvfsImage"]);
        if (request.Mode is "route-probe" or "route-diagnostic")
        {
            string snapshotPath = Path.GetFullPath(request.SelectedVfsSnapshot ?? throw new InvalidDataException("Selected mapping snapshot required"));
            SafePaths.NoLinks(snapshotPath);
            var snapshot = JsonDocument.Parse(File.ReadAllText(snapshotPath)).RootElement;
            if (snapshot.GetProperty("selectionSha256").GetString() != Hashing.File(request.Selection)
                || snapshot.GetProperty("selectedGame").GetString() != selection.Game
                || snapshot.GetProperty("selectedProfile").GetString() != selection.Profile)
                throw new InvalidDataException("Selected mapping snapshot belongs to another environment");
            string files = Path.Combine(Path.GetDirectoryName(snapshotPath)!, "selected-files.tsv"); SafePaths.NoLinks(files);
            if (Hashing.File(files) != snapshot.GetProperty("fileListSha256").GetString()) throw new InvalidDataException("Selected mapping file list changed");
            config["selectedFileList"] = files; config["selectedFileListSha256"] = Hashing.File(files);
            config["selectedMappingSha256"] = snapshot.GetProperty("mappingSha256").GetString()!;
            config["selectedFiles"] = snapshot.GetProperty("files").GetInt64().ToString();
            config["routeProbe"] = "1";
            config["documentsGame"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Skyrim Special Edition");
            config["localGame"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Skyrim Special Edition");
            Directory.CreateDirectory(Path.Combine(output, "profile"));
            foreach (string name in new[] { "Skyrim.ini", "SkyrimPrefs.ini", "SkyrimCustom.ini" })
                File.Copy(Path.Combine(profile, name), Path.Combine(output, "profile", name));
            config["privateIni"] = Path.Combine(output, "profile", "Skyrim.ini");
            Write("route-inputs.json", new { snapshot = snapshotPath, snapshotSha256 = Hashing.File(snapshotPath),
                actualSelectedMappingMustMatchBeforeUse = true, payloadCopies = "Selected saves and profile INIs only; source meshes, textures, archives and plugins remain read-through" });
        }
        if (request.Mode is "probe" or "inventory" or "relay-diagnostic" or "route-probe" or "route-diagnostic")
        {
            string documentsGame = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Skyrim Special Edition");
            string localGame = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Skyrim Special Edition");
            var sourceRoots = new[] { selection.Mods, selection.Game, Path.GetDirectoryName(request.Mo2Executable)!, profiles,
                selection.Overwrite, documentsGame, localGame }.Concat(request.AdditionalProtectedRoots ?? [])
                .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var source in sourceRoots) SafePaths.NoLinks(source);
            string rootsPath = Path.Combine(output, "source-roots.txt");
            File.WriteAllLines(rootsPath, sourceRoots.Where(Directory.Exists).Select(p => "D\t" + p)
                .Concat(new[] { "F\t" + selection.InstanceIni }), new UTF8Encoding(false));
            config["sourceRoots"] = rootsPath; config["sourceRootsSha256"] = Hashing.File(rootsPath);
            File.WriteAllLines(Path.Combine(output, "probe-deny.txt"), new[] { request.Mo2Executable,
                Path.Combine(selection.Game, "SkyrimSE.exe"), selection.Save, selection.Cosave }, new UTF8Encoding(false));
            Directory.CreateDirectory(Path.Combine(output, "saves"));
            File.Copy(selection.Save, Path.Combine(output, "saves", "selected-copy.ess"));
            File.Copy(selection.Cosave, Path.Combine(output, "saves", "selected-copy.skse"));
            foreach (var selected in new[] { ("selectedSave", selection.Save), ("selectedCosave", selection.Cosave) })
            {
                config[selected.Item1] = selected.Item2; config[selected.Item1 + "Sha256"] = Hashing.File(selected.Item2);
            }
            config["copiedSave"] = Path.Combine(output, "saves", "selected-copy.ess");
            config["copiedCosave"] = Path.Combine(output, "saves", "selected-copy.skse");
            config["copiedSaveSha256"] = config["selectedSaveSha256"];
            config["copiedCosaveSha256"] = config["selectedCosaveSha256"];
            if (Hashing.File(config["copiedSave"]) != config["selectedSaveSha256"]
                || Hashing.File(config["copiedCosave"]) != config["selectedCosaveSha256"])
                throw new InvalidDataException("Selected save changed during copy");
            File.WriteAllText(Path.Combine(output, "probe-private.txt"), "TASK_BASELINE");
            Write("source-scope-policy.json", new { roots = sourceRoots, instanceIni = selection.InstanceIni,
                actualEnumeration = "Physical native controller after supported Connect removes this worker's injected HookManager; actual injected backing read and mapping retained first",
                defaultProviders = "All installed source mods including disabled manual followers, plus game/MO2/profile/overwrite/save/settings roots and explicit references",
                excludedUserMo2OperationalOutputs = "Active instance logs/cache/crash outputs remain user-owned and are snapshotted; server remains outside restriction/job",
                noOtherInstanceFallback = "Worker never starts another MO2 and has no alternate-instance resolution", payloadCopies = 2 });
        }
        foreach (var item in new[] { ("instanceIni", selection.InstanceIni), ("modlist", Path.Combine(profile, "modlist.txt")),
            ("plugins", Path.Combine(profile, "plugins.txt")), ("loadorder", Path.Combine(profile, "loadorder.txt")) })
        {
            SafePaths.NoLinks(item.Item2); config[item.Item1] = item.Item2; config[item.Item1 + "Sha256"] = Hashing.File(item.Item2);
            File.Copy(item.Item2, Path.Combine(output, "before-" + item.Item1 + ".txt"));
        }
        string configPath = Path.Combine(output, "workflow.tsv");
        File.WriteAllLines(configPath, config.Select(k => k.Key + "\t" + k.Value), new UTF8Encoding(false));
        Write("application-binding.json", new { request = new { path = requestPath, sha256 = Hashing.File(requestPath) },
            selection = new { path = request.Selection, sha256 = Hashing.File(request.Selection) },
            profile, serverPid = server.Id, serverCreationFileTime = serverCreation, userMo2Owned = false,
            appPid = Environment.ProcessId, worker = request.Worker, workerSha256 = request.WorkerSha256, mode = request.Mode });
        var info = new ProcessStartInfo(request.Worker) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { request.Mode, configPath, Hashing.File(configPath) }) info.ArgumentList.Add(arg);
        token.ThrowIfCancellationRequested();
        using var worker = Process.Start(info) ?? throw new IOException("Could not start owned workflow worker");
        long workerCreation = worker.StartTime.ToUniversalTime().ToFileTimeUtc();
        var stdout = Task.Run(async () => { while (await worker.StandardOutput.ReadLineAsync() is { } line) {
            File.AppendAllText(Path.Combine(output, "worker-stdout.log"), line + Environment.NewLine); Console.WriteLine(line); } });
        var stderr = Task.Run(async () => { while (await worker.StandardError.ReadLineAsync() is { } line) {
            File.AppendAllText(Path.Combine(output, "worker-stderr.log"), line + Environment.NewLine); Console.Error.WriteLine(line); } });
        var watch = Stopwatch.StartNew(); bool timedOut = false;
        while (!worker.WaitForExit(200))
        {
            if (token.IsCancellationRequested || watch.Elapsed.TotalSeconds > request.TimeoutSeconds)
            {
                timedOut = !token.IsCancellationRequested;
                if (request.Mode is "inspect" or "inventory") { worker.Kill(); worker.WaitForExit(); break; }
                File.WriteAllText(Path.Combine(output, "stop"), "Application cancel/deadline; task workers only");
                if (!worker.WaitForExit(30000)) { worker.Kill(); worker.WaitForExit(); }
                // A guardian may outlive a killed supervisor while draining its exact job.
                string release = Path.Combine(output, "source-guards-release.json");
                string error = Path.Combine(output, "guardian-error.txt");
                while (File.Exists(Path.Combine(output, "worker-ownership.json")) && !File.Exists(release) && !File.Exists(error)) Thread.Sleep(200);
                break;
            }
        }
        Task.WaitAll(stdout, stderr);
        bool serverSurvived = !server.HasExited && server.StartTime.ToUniversalTime().ToFileTimeUtc() == serverCreation;
        bool inputsUnchanged = config.Where(k => k.Key.EndsWith("Sha256") && config.ContainsKey(k.Key[..^6]))
            .All(k => Hashing.File(config[k.Key[..^6]]) == k.Value);
        int code = token.IsCancellationRequested ? 130 : timedOut || !serverSurvived || !inputsUnchanged ? 1 : worker.ExitCode;
        bool protectedZeroAndRelease = false;
        if (request.Mode is "probe" or "relay-diagnostic" or "route-probe" or "route-diagnostic")
        {
            try
            {
                UserMo2WorkflowEvidence.ValidateStopped(output, worker.Id, workerCreation, worker.ExitCode == 0);
                if (worker.ExitCode == 0 && request.Mode is "route-probe" or "route-diagnostic")
                    UserMo2WorkflowEvidence.ValidateRouting(output);
                protectedZeroAndRelease = true;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or JsonException or InvalidOperationException or FormatException or IndexOutOfRangeException or KeyNotFoundException)
            {
                Write("lifecycle-validation-error.json", new { error = e.Message });
            }
        }
        if (request.Mode is "probe" or "route-probe" or "relay-diagnostic" or "route-diagnostic" && !protectedZeroAndRelease) code = 1;
        Write("workflow-state.json", new { exitCode = code, phase = request.Mode, workerPid = worker.Id,
            workerCreationFileTime = workerCreation, workerExited = worker.HasExited, timedOut,
            ownedChildTimedOut = File.Exists(Path.Combine(output, "owned-child-deadline.json")), userMo2Survived = serverSurvived,
            inputsUnchanged, cancelled = code == 130, protectedZeroAndRelease, fullOriginalSourceProtection = request.Mode is "probe" or "route-probe" && protectedZeroAndRelease,
            userMo2Owned = false, freshCapturePerformed = false, generationCompleted = false });
        return code;
    }
}
