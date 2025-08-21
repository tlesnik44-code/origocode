using System.Text.RegularExpressions;

public static class PathUtils
{
    private static readonly Regex ProjectNameRx = new("^[A-Za-z0-9_\\-]{1,64}$", RegexOptions.Compiled);
    private static readonly Regex CleanSegRx = new("^[^/]+$", RegexOptions.Compiled);

    public static void ValidateProjectName(string projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName) || !ProjectNameRx.IsMatch(projectName))
            throw new ArgumentException("invalid projectName");
    }

    public static (string FolderRel, string LeafName, bool IsFolder) SplitFileName(string fileName)
    {
        // normalizacja
        var p = (fileName ?? string.Empty).Replace('\\', '/').Trim();
        if (p.StartsWith("/")) p = p[1..];

        // folder?
        var isFolder = p.EndsWith("/");
        if (isFolder) p = p.TrimEnd('/');

        if (string.IsNullOrEmpty(p))
            return ("", "", true); // root projektu

        var parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var seg in parts)
        {
            if (seg == "." || seg == "..") throw new ArgumentException("invalid path");
            if (!CleanSegRx.IsMatch(seg)) throw new ArgumentException("invalid path segment");
        }

        var leaf = parts.LastOrDefault() ?? "";
        var folder = parts.Count > 1 ? string.Join('/', parts.Take(parts.Count - 1)) : "";

        if (isFolder) { folder = parts.Count == 0 ? "" : string.Join('/', parts); leaf = ""; }
        return (folder, leaf, isFolder);
    }
}