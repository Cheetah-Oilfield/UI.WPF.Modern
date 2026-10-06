using System.Diagnostics;
using System.Xml.Linq;

namespace iNKORE.UI.WPF.Modern.Mcp;

public enum VersionStatus
{
    /// <summary>No project under the folder references the library.</summary>
    NotReferenced,
    Match,
    Mismatch,

    /// <summary>Referenced, but with a range, wildcard or source reference that can't be compared.</summary>
    Unknown,
}

public sealed record LibraryReference(string Project, string? Version, string Source);

public sealed record VersionCheckResult(string Root, VersionStatus Status, IReadOnlyList<LibraryReference> References);

/// <summary>
/// Finds which iNKORE.UI.WPF.Modern version the user's projects reference, so the server can warn
/// when its index describes a different version.
/// </summary>
public static class ProjectVersionCheck
{
    private const string PackageId = "iNKORE.UI.WPF.Modern";
    private const int MaxDepth = 5;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "packages", ".git", ".vs", ".idea", "artifacts",
    };

    public static VersionCheckResult Check(string root, string indexVersion)
    {
        var references = new List<LibraryReference>();
        var centralVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var projects = new List<string>();
        var stopwatch = Stopwatch.StartNew();

        foreach (var file in EnumerateFiles(root, 0, stopwatch))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase))
                ReadCentralVersions(file, centralVersions);
            else if (name.Equals("packages.config", StringComparison.OrdinalIgnoreCase))
                references.AddRange(ReadPackagesConfig(root, file));
            else
                projects.Add(file);
        }

        foreach (var project in projects)
            references.AddRange(ReadProject(root, project, centralVersions));

        var status = references.Count == 0
            ? VersionStatus.NotReferenced
            : references.Any(r => Compare(r.Version, indexVersion) == VersionStatus.Mismatch)
                ? VersionStatus.Mismatch
                : references.All(r => Compare(r.Version, indexVersion) == VersionStatus.Match)
                    ? VersionStatus.Match
                    : VersionStatus.Unknown;

        return new VersionCheckResult(root, status, references);
    }

    private static VersionStatus Compare(string? referenced, string indexVersion)
    {
        if (referenced is null || referenced.IndexOfAny(['*', '[', '(', ',', '$']) >= 0)
            return VersionStatus.Unknown;

        var core = referenced.Split('-', '+')[0];
        if (!Version.TryParse(core, out var version) || !Version.TryParse(indexVersion, out var index))
            return VersionStatus.Unknown;

        return version.Major == index.Major && version.Minor == index.Minor && Math.Max(version.Build, 0) == Math.Max(index.Build, 0)
            ? VersionStatus.Match
            : VersionStatus.Mismatch;
    }

    private static IEnumerable<string> EnumerateFiles(string directory, int depth, Stopwatch stopwatch)
    {
        if (depth > MaxDepth || stopwatch.Elapsed > Budget)
            yield break;

        string[] files, directories;
        try
        {
            files = Directory.GetFiles(directory);
            directories = Directory.GetDirectories(directory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase)
                || name.Equals("packages.config", StringComparison.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }

        foreach (var child in directories)
        {
            var name = Path.GetFileName(child);
            if (SkippedDirectories.Contains(name) || name.StartsWith('.'))
                continue;
            foreach (var file in EnumerateFiles(child, depth + 1, stopwatch))
                yield return file;
        }
    }

    private static IEnumerable<LibraryReference> ReadProject(string root, string path, Dictionary<string, string> centralVersions)
    {
        var doc = TryLoad(path);
        if (doc is null)
            yield break;

        var relative = Path.GetRelativePath(root, path);
        foreach (var element in doc.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
        {
            var include = (string?)element.Attribute("Include") ?? (string?)element.Attribute("Update");
            if (!string.Equals(include, PackageId, StringComparison.OrdinalIgnoreCase))
                continue;

            var version = (string?)element.Attribute("VersionOverride")
                ?? (string?)element.Attribute("Version")
                ?? element.Elements().FirstOrDefault(e => e.Name.LocalName is "Version" or "VersionOverride")?.Value.Trim();
            if (version is not null)
                yield return new LibraryReference(relative, version, "PackageReference");
            else
                yield return new LibraryReference(relative, centralVersions.GetValueOrDefault(PackageId), "Directory.Packages.props");
        }

        foreach (var element in doc.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
        {
            var include = ((string?)element.Attribute("Include") ?? "").Replace('\\', '/');
            if (include.EndsWith("/" + PackageId + ".csproj", StringComparison.OrdinalIgnoreCase))
                yield return new LibraryReference(relative, null, "ProjectReference (source)");
        }
    }

    private static IEnumerable<LibraryReference> ReadPackagesConfig(string root, string path)
    {
        var doc = TryLoad(path);
        if (doc is null)
            yield break;

        foreach (var package in doc.Descendants("package"))
        {
            if (string.Equals((string?)package.Attribute("id"), PackageId, StringComparison.OrdinalIgnoreCase))
                yield return new LibraryReference(Path.GetRelativePath(root, path), (string?)package.Attribute("version"), "packages.config");
        }
    }

    private static void ReadCentralVersions(string path, Dictionary<string, string> versions)
    {
        var doc = TryLoad(path);
        if (doc is null)
            return;

        foreach (var element in doc.Descendants().Where(e => e.Name.LocalName == "PackageVersion"))
        {
            if ((string?)element.Attribute("Include") is { } id && (string?)element.Attribute("Version") is { } version)
                versions.TryAdd(id, version);
        }
    }

    private static XDocument? TryLoad(string path)
    {
        try
        {
            return XDocument.Load(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }
}
