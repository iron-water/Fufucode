using System.Xml;
using System.Xml.Linq;
using Fufucode.App.Abstractions.Graph;

namespace Fufucode.Plugins.AutoDev.Graph;

internal sealed record GraphSource(string EntityId, string Path, string Kind, string Hash, string Detail);
internal sealed record GraphScanResult(IReadOnlyList<GraphModule> Modules, IReadOnlyList<GraphRelation> Relations,
    IReadOnlyList<GraphSource> Sources, IReadOnlyList<string> Notices);

/// <summary>Reads directories and literal MSBuild references without evaluating or executing project code.</summary>
internal static class GraphProjectScanner
{
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".vs", ".idea", "bin", "obj", "node_modules", "build", "out", "output",
        "dist", "artifacts", "packages", "vendor", "coverage", "TestResults", ".next", ".nuxt", ".cache",
        ".venv", "venv", "__pycache__", "target", ".dart_tool", ".gradle", ".symlinks", "Pods",
        ".claude", ".fufucode", ".playwright-mcp"
    };

    internal static GraphScanResult Scan(string root, int depth, CancellationToken token)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var modules = new Dictionary<string, GraphModule>(StringComparer.Ordinal);
        var relations = new Dictionary<string, GraphRelation>(StringComparer.Ordinal);
        var sources = new List<GraphSource>();
        var notices = new List<string>();
        var builds = new List<string>();
        var dartFiles = new List<string>();
        var packageRoots = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var now = GraphStorage.Now();
        string Relative(string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
        string Key(string path) => OperatingSystem.IsWindows() ? Relative(path).ToUpperInvariant() : Relative(path);
        string ModuleId(string path, string kind) => "module-" + GraphStorage.Hash(kind + ":" + Key(path))[..24];
        bool Inside(string path) => Path.GetRelativePath(root, path) is var relative && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
        bool Allowed(string path)
        {
            if (!Inside(path) || Relative(path).Split('/').Any(Excluded.Contains)) return false;
            for (var parent = Path.GetDirectoryName(path); parent is not null && Inside(parent); parent = Path.GetDirectoryName(parent))
            {
                if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) return false;
                if (GraphStorage.PathKey(parent) == GraphStorage.PathKey(root)) break;
            }
            return true;
        }

        void Walk(string directory, string? parent, int level)
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            { notices.Add("Skipped directory link: " + Relative(directory)); return; }
            var id = ModuleId(directory, "directory");
            modules[id] = new(id, parent, Path.GetFileName(directory), "Directory: " + Relative(directory), [Relative(directory)], now);
            sources.Add(new(id, Relative(directory), "directory", "", "Observed real directory; containment only."));
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory).Order(StringComparer.OrdinalIgnoreCase))
                {
                    token.ThrowIfCancellationRequested();
                    if (IsBuildFile(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                    {
                        builds.Add(file);
                        AddFile(file, id, "build-file");
                    }
                    else if (Path.GetExtension(file).Equals(".dart", StringComparison.OrdinalIgnoreCase)
                        && !file.EndsWith(".g.dart", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".freezed.dart", StringComparison.OrdinalIgnoreCase)
                        && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                    {
                        dartFiles.Add(file);
                        AddFile(file, id, "dart-source");
                    }
                    else if (Path.GetFileName(file).Equals("pubspec.yaml", StringComparison.OrdinalIgnoreCase))
                        AddFile(file, id, "package-manifest");
                }
                if (level >= depth) return;
                foreach (var child in Directory.EnumerateDirectories(directory).Order(StringComparer.OrdinalIgnoreCase))
                    if (!Excluded.Contains(Path.GetFileName(child))) Walk(child, id, level + 1);
                    else sources.Add(new(id, Relative(child), "excluded-directory", "", "Generated or tool runtime metadata directory excluded from architecture scan."));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { notices.Add("Cannot enumerate " + Relative(directory) + ": " + e.Message); }
        }

        string AddFile(string path, string? parent, string kind)
        {
            var id = ModuleId(path, "file");
            if (!modules.ContainsKey(id))
            {
                modules[id] = new(id, parent, Path.GetFileNameWithoutExtension(path), "File: " + Relative(path), [Relative(path)], now);
                sources.Add(new(id, Relative(path), kind, FileHash(path), "Observed real file."));
            }
            return id;
        }

        string FileHash(string file)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (new FileInfo(file).Length > 10 * 1024 * 1024)
                { notices.Add("Skipped oversized source: " + Relative(file)); return "unread:size-limit"; }
                using var stream = File.OpenRead(file);
                return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { notices.Add("Cannot read " + Relative(file) + ": " + e.Message); return "unread"; }
        }

        Walk(root, null, 0);
        foreach (var build in builds)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (new FileInfo(build).Length > 10 * 1024 * 1024) continue;
                using var reader = XmlReader.Create(build, new XmlReaderSettings
                    { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 10 * 1024 * 1024 });
                var document = XDocument.Load(reader, LoadOptions.SetLineInfo);
                var sourceId = ModuleId(build, "file");
                foreach (var node in document.Descendants().Where(n => n.Name.LocalName is "ProjectReference" or "Import" or "Compile"))
                {
                    var kind = node.Name.LocalName;
                    var raw = (string?)node.Attribute(kind == "Import" ? "Project" : "Include");
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    var line = (node as IXmlLineInfo)?.LineNumber ?? 0;
                    var condition = string.Join("; ", node.AncestorsAndSelf().Select(n => (string?)n.Attribute("Condition")).Where(x => !string.IsNullOrWhiteSpace(x)));
                    foreach (var item in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var detail = $"{kind} {item}; line {line}" + (condition.Length > 0 ? "; condition (not evaluated): " + condition : "");
                        var literal = item.Replace("$(MSBuildThisFileDirectory)", Path.GetDirectoryName(build) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                            .Replace("$(MSBuildProjectDirectory)", Path.GetDirectoryName(build), StringComparison.Ordinal);
                        if (literal.IndexOfAny(['$', '*', '?', '@', '%']) >= 0)
                        { sources.Add(new(sourceId, Relative(build) + "#" + line + ":" + item, "unresolved-reference", GraphStorage.Hash(item), detail)); continue; }
                        var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(build)!, literal.Replace('\\', Path.DirectorySeparatorChar)));
                        if (!Allowed(target) || !File.Exists(target) || (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                        { sources.Add(new(sourceId, Relative(build) + "#" + line + ":" + item, "unresolved-reference", GraphStorage.Hash(item), detail)); continue; }
                        // A literal existing file is the fact. A conditional edge remains explicitly labelled;
                        // no assembly/namespace, package, or unresolved property is guessed into a module edge.
                        var relative = Relative(target);
                        if (relative.Split('/').Length - 1 > depth)
                        { sources.Add(new(sourceId, Relative(build) + "#" + line + ":" + item, "depth-limited-reference", GraphStorage.Hash(item), detail)); continue; }
                        var parent = ModuleId(Path.GetDirectoryName(target)!, "directory");
                        var targetId = AddFile(target, modules.ContainsKey(parent) ? parent : null, kind == "Compile" ? "shared-source" : "build-file");
                        var relationId = "relation-" + GraphStorage.Hash(sourceId + ":" + targetId + ":" + kind)[..24];
                        relations[relationId] = new(relationId, sourceId, targetId, kind, condition.Length == 0 ? kind : kind + " (conditional: " + condition + ")");
                        sources.Add(new(relationId, Relative(build) + "#" + line, kind, FileHash(build), detail));
                    }
                }
            }
            catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException or ArgumentException)
            { notices.Add("Cannot inspect " + Relative(build) + ": " + e.Message); }
        }
        foreach (var file in dartFiles)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (new FileInfo(file).Length > 2 * 1024 * 1024)
                { notices.Add("Skipped oversized Dart source: " + Relative(file)); continue; }
                var sourceId = ModuleId(file, "file");
                foreach (var import in GraphLiteralImports.Read(File.ReadAllText(file)))
                {
                    var raw = import.Target;
                    var detail = $"{import.Kind} '{raw}'; line {import.Line}; literal directive, conditions not evaluated";
                    string? target = null;
                    if (raw.StartsWith("package:", StringComparison.Ordinal))
                    {
                        // Resolve only this package's declared name, never pub-cache or guessed package roots.
                        var package = FindPackage(Path.GetDirectoryName(file)!);
                        if (package is not null && raw.StartsWith("package:" + package.Value.Name + "/", StringComparison.Ordinal))
                            target = Path.GetFullPath(Path.Combine(package.Value.Root, "lib", raw[("package:" + package.Value.Name + "/").Length..]));
                    }
                    else if (!raw.Contains(':') && raw.IndexOfAny(['$', '\\', '*', '?']) < 0)
                        target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, raw));
                    var originPath = Relative(file) + "#" + import.Line + ":" + raw;
                    if (target is null || !Allowed(target) || !File.Exists(target) || (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                    { sources.Add(new(sourceId, originPath, "unresolved-reference", GraphStorage.Hash(raw), detail)); continue; }
                    if (Relative(target).Split('/').Length - 1 > depth)
                    { sources.Add(new(sourceId, originPath, "depth-limited-reference", GraphStorage.Hash(raw), detail)); continue; }
                    var parent = ModuleId(Path.GetDirectoryName(target)!, "directory");
                    var targetId = AddFile(target, modules.ContainsKey(parent) ? parent : null, "dart-source");
                    if (sourceId == targetId) { sources.Add(new(sourceId, originPath, "self-reference", GraphStorage.Hash(raw), detail)); continue; }
                    var relationId = "relation-" + GraphStorage.Hash(sourceId + ":" + targetId + ":" + import.Kind)[..24];
                    relations[relationId] = new(relationId, sourceId, targetId, import.Kind, raw);
                    sources.Add(new(relationId, originPath, import.Kind, FileHash(file), detail));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            { notices.Add("Cannot inspect " + Relative(file) + ": " + e.Message); }
        }
        return new(modules.Values.ToArray(), relations.Values.ToArray(), sources, notices);

        (string Root, string Name)? FindPackage(string directory)
        {
            for (var current = directory; Inside(current); current = Path.GetDirectoryName(current)!)
            {
                token.ThrowIfCancellationRequested();
                var manifest = Path.Combine(current, "pubspec.yaml");
                if (!packageRoots.TryGetValue(manifest, out var name))
                {
                    name = null;
                    if (File.Exists(manifest) && new FileInfo(manifest).Length < 1024 * 1024)
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(manifest), @"(?m)^name:\s*([A-Za-z_][A-Za-z0-9_]*)\s*(?:#.*)?$");
                        if (match.Success) name = match.Groups[1].Value;
                    }
                    packageRoots[manifest] = name;
                }
                if (name is not null) return (current, name);
                if (GraphStorage.PathKey(current) == GraphStorage.PathKey(root)) break;
            }
            return null;
        }
    }

    internal static bool IsExcludedSourcePath(string path) => path.Replace('\\', '/').Split('/').Any(Excluded.Contains);

    private static bool IsBuildFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".csproj" or ".fsproj" or ".vbproj" or ".props" or ".targets";
}
