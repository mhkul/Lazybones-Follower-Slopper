using AutoFollower;

namespace AutoFollower.Gui;

internal sealed record Companion(string Key, string Label, string[] Candidates)
{ public string? Resolved => Candidates.Length == 1 ? Candidates[0] : null; }

internal static class InputCompanions
{
    // Exact filename/texture associations in bounded conventional locations.
    // No directory inventory, timestamp ranking or assumed session identity.
    public static Companion[] Resolve(string save, string head, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); SafePaths.NoLinks(head);
        using var stream = new FileStream(head, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > BoundedBinary.Limit) throw new InvalidDataException("Selected head exceeds the supported byte bound.");
        byte[] raw = new byte[checked((int)stream.Length)]; stream.ReadExactly(raw);
        string tint = LeanExportInputs.RaceMenuTintReference(LeanExportInputs.ReadNif(raw));
        string directory = Path.GetDirectoryName(Path.GetFullPath(head))!;
        string stem = Path.GetFileNameWithoutExtension(head);
        string tintName = Path.GetFileName(tint.Replace('/', Path.DirectorySeparatorChar));
        List<string> tintPaths = [Path.Combine(directory, tintName)];
        List<string> slotPaths = [Path.ChangeExtension(head, ".jslot")];
        if (Path.GetFileName(directory).Equals("CharGen", StringComparison.OrdinalIgnoreCase)) slotPaths.Add(Path.Combine(directory, "Presets", stem + ".jslot"));
        Companion Choice(string key, string label, IEnumerable<string> paths) => new(key, label,
            paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToArray());
        token.ThrowIfCancellationRequested();
        return [Choice("Cosave", "matching co-save", [Path.ChangeExtension(save, ".skse")]),
            Choice("TintDds", "composed tint", tintPaths), Choice("Jslot", "JSlot appearance", slotPaths)];
    }
}
