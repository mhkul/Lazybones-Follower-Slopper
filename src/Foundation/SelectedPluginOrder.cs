namespace AutoFollower;

// Validates the selected MO2 lists without inventing or repairing their order.
public static class SelectedPluginOrder
{
    public static string[] Resolve(string[] plugins, string[] order, string[] ccc,
        Func<string, bool> present, string context)
    {
        string[] Validate(string[] entries, string list, bool starred)
        {
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            var result = new string[entries.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                string name = starred && entries[i].StartsWith('*') ? entries[i][1..] : entries[i];
                RequireName(name, $"{context}: {list} entry {i + 1}");
                if (!names.Add(name) && list == "loadorder.txt") throw new InvalidDataException($"{context}: duplicate plugin '{name}' in {list} (entry {i + 1}).");
                result[i] = name;
            }
            return result;
        }
        var listed = Validate(plugins, "plugins.txt", true);
        Validate(order, "loadorder.txt", false);
        var cc = Validate(ccc, "Skyrim.ccc", false).Distinct(StringComparer.OrdinalIgnoreCase);
        var active = listed.Where((_, i) => plugins[i].StartsWith('*')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        active.UnionWith(new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" }.Where(present));
        // CCC describes possible primary plugins, not installed files. Query the
        // selected Data view, including MO2 mappings, never physical candidates.
        active.UnionWith(cc.Where(present));
        var missingOrder = active.Except(order, StringComparer.OrdinalIgnoreCase).ToArray();
        if (missingOrder.Length > 0) throw new InvalidDataException($"{context}: active plugins missing from loadorder.txt: {string.Join(", ", missingOrder)}.");
        var missingFiles = active.Where(x => !present(x)).ToArray();
        if (missingFiles.Length > 0) throw new InvalidDataException($"{context}: active plugins missing from selected Data: {string.Join(", ", missingFiles)}.");
        return order.Where(active.Contains).ToArray();
    }

    public static void RequireName(string? name, string context)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException($"{context}: empty plugin name.");
        if (name != name.Trim() || name.IndexOfAny(['/', '\\', ':', '*']) >= 0 ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !new[] { ".esm", ".esp", ".esl" }.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"{context}: invalid plugin name '{name}'. Expected a plugin filename without a path.");
    }
}
