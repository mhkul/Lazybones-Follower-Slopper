using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutoFollower.Gui;

// A report is a safe projection, never a redacted copy of arbitrary exception prose.
// Only this factory can construct the payload consumed by the report dialog.
internal sealed class DiagnosticReport
{
    public string Text { get; }
    private DiagnosticReport(string text) => Text = text;
    private const int Limit = 1024 * 1024;
    private const int MaximumNodes = 2048;
    private const string Notice = "Identifying details removed; unknown text withheld. Numeric diagnostics remain. Review before sharing. No automatic upload.";
    internal static DiagnosticReport Create(string operation, Exception error, JsonElement? stored = null)
    {
        string safeOperation = operation switch { "Choose items" => "Choose items", "Build follower" => "Build follower", _ => "Application operation" };
        string header = "Lazybones Follower Slopper - anonymized failure report\r\nReport format: 1\r\nOperation: " + safeOperation + "\r\nProduct version: " + typeof(DiagnosticReport).Assembly.GetName().Version + "\r\nRuntime version: " + Environment.Version + "\r\n" + Notice + "\r\n";
        try
        {
            var builder = new Projection();
            builder.Exception(error, 0);
            // Project the complete stored result with the SAME report-local alias table.
            // Old raw exception strings and future/unknown metadata are never appended.
            if (stored is { } value) builder.Json(value, "Stored result", 0);
            return new(header + builder.Finish());
        }
        catch (Exception)
        {
            // No exception message or original payload may enter this fallback.
            return new(header + "\r\nDiagnostic projection unavailable. Error category: application failure.\r\n");
        }
    }

    private sealed class Projection
    {
        private readonly List<(string Label, string Raw)> sections = [];
        private readonly Dictionary<string, (string Kind, int Number)> aliases = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> counts = new(StringComparer.Ordinal);
        private readonly HashSet<Exception> visited = new(ReferenceEqualityComparer.Instance);
        private int nodes, bytes;
        private bool limited;
        private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);
        private const string Extensions = "bsa|ba2|esm|esp|esl|nif|tri|dds|ess|skse|jslot|xml|osp|json|ini|txt|wav|xwm|hkx|pex|dll|exe|zip|py|cs|log";
        // These recognize references, not safe prose. Everything unmatched is withheld.
        private static readonly Regex Files = new("""["“]([^"”\r\n]*?\.(?:[A-Za-z0-9]{2,5}))["”]|'([^'\r\n]*?\.(?:[A-Za-z0-9]{2,5}))'|(?:[A-Za-z]:[/\\]|\\\\[^\r\n"<>|:;]+[/\\]|(?:meshes|textures|sound|CalienteTools)[/\\])[^\r\n"<>|:;]*?\.(?:[A-Za-z0-9]{2,5})\b|[^\s"'“”<>|:;=(){}\[\],/\\]+\.(?:[A-Za-z0-9]{2,5})\b""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, Timeout);
        private static readonly Regex Names = new("\\b(profile|preset|character|username|machine|mod|editorId|follower)\\s*(?:name)?\\s*[:=]?\\s*[\\\"'“]([^\\r\\n\\\"'”]+)[\\\"'”]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
        private static Regex Numbers => new("(?<![\\w])(" + string.Join("|", NumericKeys.OrderByDescending(x => x.Length).Select(Regex.Escape)) + ")\\s*(?:[:=]\\s*|\\s+)(-?(?:0x[0-9a-f]{1,16}|[0-9]{1,20}(?:\\.[0-9]{1,6})?|[0-9a-f]{2,8}|true|false))(?![\\w.])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
        private static Regex Types => new("\\b(?:System\\.(?:IO\\.|Text\\.Json\\.|Xml\\.)?)?(" + string.Join("|", ErrorTypes) + ")\\b", RegexOptions.CultureInvariant, Timeout);
        private static readonly Regex Calls = new("\\bat\\s+([A-Za-z_][A-Za-z0-9_.+`<>]*)\\s*\\(", RegexOptions.CultureInvariant, Timeout);
        private static readonly Regex PythonFrames = new("""File "([^"\r\n]+)", line ([0-9]{1,7})""", RegexOptions.CultureInvariant, Timeout);
        private static readonly HashSet<string> AllowedCalls = BuildCallSites();
        private static readonly HashSet<string> PythonModules = new(StringComparer.Ordinal) { "lean_build_stage.py", "lean_head_stage.py", "lean_package_stage.py", "body_mesh_stage.py", "validate_package.py", "nif_blocks.py", "nif_paths.py", "nif_transform.py", "dds.py" };
        private static readonly string[] ErrorTypes = ["InvalidDataException", "IOException", "FileNotFoundException", "DirectoryNotFoundException", "UnauthorizedAccessException", "InvalidOperationException", "ArgumentException", "ArgumentOutOfRangeException", "OperationCanceledException", "AggregateException", "EndOfStreamException", "JsonException", "XmlException", "NotSupportedException", "OverflowException", "FormatException", "TimeoutException", "ValueError", "FileNotFoundError", "PermissionError", "RuntimeError", "Exception"];
        private static readonly string[] NumericKeys = ["Archive bytes", "magic", "version", "folder offset", "flags", "folders", "files", "folder name bytes", "file name bytes", "file flags", "calculated filename offset", "calculated metadata end", "declared files", "declared name bytes", "folders read", "files counted", "folder name bytes counted", "observed filename table offset", "stored offset", "observed block offset", "consumed", "remaining", "expected buffer size", "archive offset", "stored bytes", "Archive flags", "member compressed", "expected decoded bytes", "frame bytes", "frame magic", "FLG", "BD", "count", "offset", "length", "bytes", "exit code", "exitCode", "packageExitCode", "truncated", "milliseconds", "totalMilliseconds", "filesRead", "bytesRead", "filesCopied", "bytesCopied", "sourceFilesRead", "sourceBytesRead", "line", "bytePositionInLine", "lineNumber", "outputPreserved", "gameLaunched", "originalInputsWritten", "runtimeVerified"];
        private static readonly string[] Categories = ["Unexpected binary tail", "Truncated/oversized binary field", "Count exceeds supported bound", "Binary offset out of bounds", "Supported archive format is BSA105", "BSA names required", "Xbox layout unsupported", "Unsupported BSA header offset", "BSA name exceeds bound", "Invalid BSA name bytes", "BSA folder ordering/offset mismatch", "Empty BSA folder name", "BSA folder name lacks terminator", "BSA folder records overlap filename table", "Unsupported BSA size flags/count", "BSA member bounds/duplicate path", "BSA header/count/name lengths disagree", "Partially overlapping BSA members", "Missing BSA member", "Non-ASCII BSA lookup", "Unsafe virtual path", "LZ4 length mismatch", "Invalid LZ4 match", "Zlib output length mismatch", "Unsupported worn equipment", "Unsupported equipment record type", "Affected installed model has no supported prebuilt project", "Missing prebuilt projects", "Required prebuilt project", "Required project", "No compatible prebuilt project", "Active plugin order incomplete/ambiguous", "Required body/ESPFE/package stage failed", "Installed preset XML is malformed", "No authored XML", "multiple eligible projects", "equipment", "ammunition", "prebuilt", "project", "preset", "filename table", "folder directory", "member directory", "directory counts", "member overlap", "read header", "missing", "ambiguous", "unsupported", "bounds", "terminator", "mismatch", "nonzero", "unused", "cancelled", "Required stage failed", "evidence unavailable", "Failed to load", "Failed to build", "Failed to write TRI file", "Failed to create TRI file", "Unable to get slider set from file", "Unable to open slider set file", "Unable to load input nif", "Unable to create destination directory", "Unable to save nif file", "Aborted batch build with unconfigured data path", "Unexpected exception has occurred", "Unhandled exception has occurred", "Fatal exception has occurred", "BodySlide initialized", "All group build sets processed successfully", "BodySlide group success receipt absent"];
        private static readonly string[] Signatures = ["AMMO", "PROJ", "ARMO", "ARMA", "NPC_", "BSA105", "NIF", "DDS", "LZ4", "XML", "OSP"];

        private static HashSet<string> BuildCallSites()
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var assembly in new[] { typeof(DiagnosticReport).Assembly, typeof(AutoFollower.BsaReader).Assembly })
                foreach (var type in assembly.GetTypes())
                    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                        result.Add(type.FullName + "." + method.Name);
            return result;
        }
        private bool Allow(int depth)
        {
            if (depth > 32 || ++nodes > MaximumNodes) { limited = true; return false; }
            return true;
        }
        private void Add(string label, string raw)
        {
            if (sections.Count >= MaximumNodes || raw.Length > Limit - bytes) { limited = true; return; }
            bytes += raw.Length; sections.Add((label, raw));
        }
        private void AddContent(string kind, string raw)
        {
            if (raw.Length > Limit - bytes) { limited = true; return; }
            bytes += raw.Length;
            Add("Aliased content", Alias(raw, kind));
        }
        public void Exception(Exception error, int depth)
        {
            if (!Allow(depth) || !visited.Add(error)) return;
            string type = ErrorTypes.Contains(error.GetType().Name, StringComparer.Ordinal) ? error.GetType().Name : "Exception";
            Add("Exception " + visited.Count, type);
            Add("Error context", error.Message);
            Add("Call sites", error.StackTrace ?? "");
            if (error is FileNotFoundException file && file.FileName is { } name) Add("Input reference", name);
            foreach (DictionaryEntry entry in error.Data)
            {
                string key = entry.Key is string s ? s : "";
                if (IsFingerprint(key)) continue;
                if (entry.Value is string text)
                {
                    if (SensitiveKind(key) is { } kind) AddContent(kind, text);
                    else Add(SafeLabel(key), text);
                }
                else if (entry.Value is JsonElement json) Json(json, SafeLabel(key), depth + 1);
                else if (entry.Value is Exception nested) Exception(nested, depth + 1);
                else if (NumericKeys.FirstOrDefault(x => x.Equals(key, StringComparison.OrdinalIgnoreCase)) is { } numeric && entry.Value is int or long or uint or ulong or bool)
                    Add("Parser metadata", numeric + "=" + Convert.ToString(entry.Value, System.Globalization.CultureInfo.InvariantCulture));
                // Arbitrary objects must never invoke user-defined ToString().
            }
            if (error is AggregateException aggregate) foreach (var nested in aggregate.InnerExceptions) Exception(nested, depth + 1);
            else if (error.InnerException != null) Exception(error.InnerException, depth + 1);
        }
        private static string SafeLabel(string key) => key.ToLowerInvariant() switch
        {
            "inputfile" => "Input reference", "inputparsedetails" or "parserdetails" => "Parser metadata", "exception" => "Recorded exception chain and call sites", "error" => "Stored error", "phases" => "Build stages", _ => "Additional metadata"
        };
        public void Json(JsonElement value, string label, int depth)
        {
            if (!Allow(depth)) return;
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var item in value.EnumerateObject())
                    {
                        if (IsFingerprint(item.Name)) continue;
                        if (item.Name.Equals("validationFailure", StringComparison.OrdinalIgnoreCase))
                        {
                            // This protocol is projected as typed facts, never parsed as prose.
                            if (AutoFollower.PluginValidationFailure.TryRead(item.Value, out var failure)) Validation(failure!);
                            continue;
                        }
                        if (item.Value.ValueKind == JsonValueKind.String && FixedField(item.Name, item.Value.GetString() ?? "") is { } fixedField)
                        { Add(fixedField, item.Value.GetString()!); continue; }
                        if (NumericKeys.FirstOrDefault(x => x.Equals(item.Name, StringComparison.OrdinalIgnoreCase)) is { } numeric && item.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                            Add(label, numeric + "=" + item.Value.GetRawText());
                        else if (SensitiveKind(item.Name) is { } kind && item.Value.ValueKind == JsonValueKind.String)
                            AddContent(kind, item.Value.GetString() ?? "");
                        else Json(item.Value, SafeLabel(item.Name), depth + 1);
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in value.EnumerateArray()) Json(item, label, depth + 1);
                    break;
                case JsonValueKind.String: Add(label, value.GetString() ?? ""); break;
                // Unknown scalar fields cannot be diagnostic facts by default.
            }
        }
        private void Validation(AutoFollower.PluginValidationFailure failure)
        {
            Add("Validation category", "Unresolved emitted references");
            Add("Validation summary", "count=" + failure.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "; detailsTruncated=" + (failure.DetailsTruncated ? "true" : "false"));
            foreach (var detail in failure.References)
                Add("Validation reference", Alias(detail.SourceRecord,"Record") + " (" + detail.SourceRecordType + ") -> " + Alias(detail.TargetRecord,"Record") + " (" + detail.TargetRecordType + "); " + detail.TargetScope + "; " + Alias(detail.TargetPlugin,"Plugin"));
        }
        private static bool IsFingerprint(string name) => name.Contains("hash", StringComparison.OrdinalIgnoreCase) || name.Contains("digest", StringComparison.OrdinalIgnoreCase) || name.Contains("sha", StringComparison.OrdinalIgnoreCase) || name.Contains("fingerprint", StringComparison.OrdinalIgnoreCase) || name.Equals("inputPins", StringComparison.OrdinalIgnoreCase) || name.Equals("mvid", StringComparison.OrdinalIgnoreCase);
        private static string? FixedField(string key, string value)
        {
            return key.ToLowerInvariant() switch
            {
                "stage" when Regex.IsMatch(value, "^prebuilt-[0-9]{1,4}$|^plugin-(?:generation|validation)-(?:prebuilt|obody|as-is)$|^(?:package|private-head)$", RegexOptions.CultureInvariant, Timeout) => "Stage",
                "state" when value is "empty" or "missing" or "unreadable" or "oversize" or "present" => "Evidence state",
                "kind" when value is "Child output" or "BodySlide tool log (may include earlier stages)" or "Package output" or "Private head output" => "Evidence kind",
                "crash" when value is "access violation" or "fast-fail termination (reason unavailable)" or "stack overflow" or "DLL not found" or "entry point not found" or "not classified" or "child exit unavailable" => "Crash classification",
                _ => null
            };
        }
        private static string? SensitiveKind(string key) => key.ToLowerInvariant() switch
        {
            "profile" or "profilename" => "Profile", "preset" or "presetname" => "Preset", "character" or "charactername" => "Character", "mod" or "modname" => "Mod", "editorid" or "follower" or "followername" => "Follower", "username" => "Username", "machine" or "machinename" => "Machine", _ => null
        };
        private static string Normalize(string value) => value.Trim().Replace('\\', '/');
        private string Alias(string value, string? kind = null)
        {
            string key = Normalize(value);
            kind ??= Path.GetExtension(key).ToLowerInvariant() switch { ".bsa" => "BSA", ".ba2" => "BA2", ".esp" or ".esm" or ".esl" => "Plugin", ".ess" => "Save", ".skse" => "Co-save", ".jslot" => "Preset", _ => "Asset" };
            key = kind + ":" + key;
            if (!aliases.TryGetValue(key, out var alias))
            {
                int number = counts.GetValueOrDefault(kind) + 1; counts[kind] = number;
                aliases.Add(key, alias = (kind, number));
            }
            return alias.Kind + " " + alias.Number;
        }
        private static string FileValue(Match match) => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value : match.Value;
        private static string SafeExtension(string file)
        {
            string extension = Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
            return Extensions.Split('|').Contains(extension, StringComparer.Ordinal) ? extension : "other";
        }
        private static bool IsFile(Match match) => SafeExtension(FileValue(match)) != "other";
        private string FileAlias(string value)
        {
            string key = Normalize(value);
            // Resolve a bare name only when this report proves a UNIQUE full reference.
            // Distinct same-basename paths stay distinct; ambiguous bare names stay separate.
            if (!key.Contains('/'))
            {
                var full = aliases.Keys.Where(x => x.Contains('/') && x.EndsWith('/' + key, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
                if (full.Length == 1) key = full[0][(full[0].IndexOf(':') + 1)..];
            }
            return Alias(key);
        }
        public string Finish()
        {
            // Decode embedded/escaped JSON as data, with bounds. No raw JSON is rendered.
            for (int i = 0; i < sections.Count && i < MaximumNodes; i++)
            {
                string raw = sections[i].Raw.Trim();
                if (raw.StartsWith('{') || raw.StartsWith('[') || raw.StartsWith('"'))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 32 });
                        Json(doc.RootElement, sections[i].Label, 1);
                        sections[i] = (sections[i].Label, "");
                    }
                    catch (JsonException) { /* Unknown/malformed text is still projected below. */ }
                }
            }
            foreach (var section in sections)
            {
                if (section.Label == "Input reference" && section.Raw.Length > 0) Alias(section.Raw);
                foreach (Match match in Files.Matches(section.Raw)) if (IsFile(match) && Normalize(FileValue(match)).Contains('/')) Alias(FileValue(match));
            }
            var output = new StringBuilder();
            foreach (var section in sections)
            {
                if (output.Length > Limit) { limited = true; break; }
                var facts = new List<string>();
                string raw = section.Raw;
                if (section.Label is "Stage" or "Evidence state" or "Evidence kind" or "Crash classification" or "Aliased content" or "Validation category" or "Validation summary" or "Validation reference")
                {
                    output.AppendLine(section.Label + ": " + raw);
                    continue;
                }
                if (section.Label == "Input reference" && raw.Length > 0)
                {
                    facts.Add("Reference: " + Alias(raw));
                    raw = "";
                }
                foreach (Match match in Files.Matches(raw)) if (IsFile(match)) facts.Add("Reference: " + FileAlias(FileValue(match)) + " (" + SafeExtension(FileValue(match)) + ")");
                // Mask whole references before parsing numbers/categories, so content names
                // containing diagnostic words/numbers cannot become apparent metadata.
                raw = Files.Replace(raw, match => IsFile(match) ? " " : match.Value);
                foreach (Match match in Names.Matches(raw)) facts.Add("Reference: " + Alias(match.Groups[2].Value, SensitiveKind(match.Groups[1].Value) ?? "Content"));
                raw = Names.Replace(raw, " ");
                foreach (Match match in Types.Matches(raw)) facts.Add("Exception type: " + match.Groups[1].Value);
                foreach (Match match in Regex.Matches(raw, "\\b(?:prebuilt-[0-9]{1,4}|plugin-(?:generation|validation)-(?:prebuilt|obody|as-is))\\b", RegexOptions.CultureInvariant, Timeout)) facts.Add("Stage: " + match.Value);
                foreach (string category in Categories) if (raw.Contains(category, StringComparison.OrdinalIgnoreCase)) facts.Add("Context: " + category);
                foreach (string signature in Signatures) if (Regex.IsMatch(raw, "(?<![\\w])" + Regex.Escape(signature) + "(?![\\w])", RegexOptions.CultureInvariant, Timeout)) facts.Add("Format/record: " + signature);
                foreach (Match match in Numbers.Matches(raw))
                {
                    string key = NumericKeys.First(x => x.Equals(match.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
                    // Hex belongs only to signatures/flags; arbitrary fingerprints are omitted.
                    string number = match.Groups[2].Value;
                    bool hexadecimal = key.Contains("flags", StringComparison.OrdinalIgnoreCase) || key.Contains("magic", StringComparison.OrdinalIgnoreCase) || key is "FLG" or "BD";
                    if (hexadecimal || Regex.IsMatch(number, "^-?[0-9]{1,18}(?:\\.[0-9]{1,6})?$|^(?:true|false)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout)) facts.Add(key + "=" + number);
                }
                foreach (Match match in Regex.Matches(raw, "(?:Failed to load[^\\r\\n]{0,256}?|from slider set list\\s*)\\(([0-9]{1,7})\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout)) facts.Add("Tool error code=" + match.Groups[1].Value);
                foreach (Match match in Calls.Matches(section.Raw)) if (AllowedCalls.Contains(match.Groups[1].Value)) facts.Add("Call site: " + match.Groups[1].Value);
                foreach (Match match in PythonFrames.Matches(section.Raw))
                {
                    string module = Path.GetFileName(Normalize(match.Groups[1].Value));
                    if (PythonModules.Contains(module)) facts.Add("Call site: " + module + ":" + match.Groups[2].Value);
                }
                if (facts.Count > 0) { output.AppendLine("\r\n" + section.Label + ":"); foreach (string fact in facts.Distinct(StringComparer.Ordinal)) output.AppendLine(fact); }
            }
            output.AppendLine("\r\nUnrecognized free text and identifying fingerprints withheld.");
            if (limited) output.AppendLine("Diagnostic detail limit reached; additional data withheld.");
            return output.ToString();
        }
    }
}
