using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;

public sealed class WorkspaceService
{
    private const int MaxFileSize = 1_048_576;
    private readonly string _workspaceRoot;
    private readonly string _registryPath;
    private readonly object _registryLock = new();
    private readonly string[] _configuredIgnorePatterns;
    private readonly string[] _defaultIgnorePatterns =
    [
        ".git/",
        "node_modules/",
        "bin/",
        "obj/",
        ".vs/",
        ".angular/",
        "dist/",
        "coverage/",
        "TestResults/",
        "*.db",
        "*.db-shm",
        "*.db-wal",
        "*.sqlite",
        "*.sqlite3",
        "*.log"
    ];

    public WorkspaceService(IConfiguration configuration)
    {
        var applicationDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "LocalAIStudio");
        var configuredRoot = configuration["LocalAiStudio:WorkspaceRoot"];
        _workspaceRoot = Path.GetFullPath(
            string.IsNullOrWhiteSpace(configuredRoot)
                ? Path.Combine(applicationDataPath, "Workspaces")
                : Environment.ExpandEnvironmentVariables(configuredRoot));
        var configuredRegistryPath = configuration["LocalAiStudio:WorkspaceRegistryPath"];
        _registryPath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(configuredRegistryPath)
                ? Path.Combine(applicationDataPath, "workspaces.json")
                : Environment.ExpandEnvironmentVariables(configuredRegistryPath));
        _configuredIgnorePatterns =
            configuration.GetSection("LocalAiStudio:IgnorePatterns")
                .Get<string[]>() ?? [];

        Directory.CreateDirectory(_workspaceRoot);
        EnsureNoReparsePointsInAncestors(_workspaceRoot);
        var registryDirectory = Path.GetDirectoryName(_registryPath)
            ?? throw new InvalidOperationException("The workspace registry path must have a parent directory.");
        Directory.CreateDirectory(registryDirectory);
        EnsureNoReparsePointsInAncestors(registryDirectory);
    }

    public IReadOnlyList<WorkspaceInfo> ListWorkspaces()
    {
        lock (_registryLock)
        {
            var workspaces = ReadWorkspaceRegistry();
            var discovered = false;
            foreach (var path in Directory.EnumerateDirectories(_workspaceRoot))
            {
                if (IsReparsePoint(path) || IsSensitiveSegment(Path.GetFileName(path)))
                    continue;

                var fullPath = Path.GetFullPath(path);
                if (workspaces.Any(workspace => PathsEqual(workspace.Path, fullPath)))
                    continue;

                workspaces.Add(new WorkspaceInfo(
                    Guid.NewGuid().ToString("N"),
                    Path.GetFileName(path),
                    fullPath));
                discovered = true;
            }

            if (discovered)
                WriteWorkspaceRegistry(workspaces);

            return workspaces
                .Where(workspace => Directory.Exists(workspace.Path) &&
                    !IsReparsePoint(workspace.Path) &&
                    !IsSensitiveSegment(workspace.Name))
                .OrderBy(workspace => workspace.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public WorkspaceInfo CreateWorkspace(string? name, string? location)
    {
        var safeName = ValidateSegment(name, "Workspace name");
        if (IsSensitiveSegment(safeName))
            throw new WorkspaceException("This workspace name is reserved.", 400);

        var parentPath = string.IsNullOrWhiteSpace(location)
            ? _workspaceRoot
            : ResolveLocation(location);
        EnsureNoReparsePointsInAncestors(parentPath);
        var path = Path.GetFullPath(Path.Combine(parentPath, safeName));
        lock (_registryLock)
        {
            if (Directory.Exists(path) || File.Exists(path))
                throw new WorkspaceException("A folder with that name already exists at the selected location.", 409);

            try
            {
                Directory.CreateDirectory(path);
            }
            catch (UnauthorizedAccessException)
            {
                throw new WorkspaceException("The application does not have permission to create a workspace at that location.", 403);
            }
            catch (IOException exception)
            {
                throw new WorkspaceException($"Unable to create the workspace folder: {exception.Message}", 400);
            }

            var workspace = new WorkspaceInfo(
                Guid.NewGuid().ToString("N"),
                safeName,
                path);
            var workspaces = ReadWorkspaceRegistry();
            workspaces.Add(workspace);
            try
            {
                WriteWorkspaceRegistry(workspaces);
            }
            catch
            {
                Directory.Delete(path);
                throw;
            }

            return workspace;
        }
    }

    public WorkspaceDirectoryBrowser BrowseDirectories(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            var roots = OperatingSystem.IsWindows()
                ? DriveInfo.GetDrives()
                    .Where(drive => drive.IsReady)
                    .Select(drive => new WorkspaceDirectoryEntry(
                        drive.RootDirectory.FullName,
                        drive.Name))
                    .ToArray()
                : [new WorkspaceDirectoryEntry(
                    Path.GetPathRoot(Path.DirectorySeparatorChar.ToString())!,
                    Path.DirectorySeparatorChar.ToString())];

            return new WorkspaceDirectoryBrowser(null, null, roots);
        }

        if (!Path.IsPathFullyQualified(location))
            throw new WorkspaceException("Browse path must be absolute.", 400);

        var path = Path.GetFullPath(location);
        if (!Directory.Exists(path))
            throw new WorkspaceException("Folder not found.", 404);
        EnsureNoReparsePointsInAncestors(path);

        WorkspaceDirectoryEntry[] directories;
        try
        {
            directories = Directory.EnumerateDirectories(path)
                .Take(1001)
                .Where(directory => !IsReparsePoint(directory))
                .OrderBy(directory => Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase)
                .Take(1000)
                .Select(directory => new WorkspaceDirectoryEntry(
                    Path.GetFullPath(directory),
                    Path.GetFileName(directory)))
                .ToArray();
        }
        catch (UnauthorizedAccessException)
        {
            throw new WorkspaceException("The application does not have permission to browse this folder.", 403);
        }
        catch (IOException exception)
        {
            throw new WorkspaceException($"Unable to browse this folder: {exception.Message}", 400);
        }

        var parent = Directory.GetParent(path)?.FullName;
        if (parent is not null &&
            string.Equals(parent, path, PathComparison))
        {
            parent = null;
        }

        return new WorkspaceDirectoryBrowser(path, parent, directories);
    }

    public IReadOnlyList<WorkspaceEntry> ListEntries(
        string workspace,
        string? relativePath)
    {
        var workspacePath = ResolveWorkspace(workspace);
        var directoryPath = ResolvePath(workspacePath, relativePath, allowRoot: true);
        EnsureNotIgnored(workspacePath, relativePath, isDirectory: true);

        if (!Directory.Exists(directoryPath))
            throw new WorkspaceException("Directory not found.", 404);

        EnsureNoReparsePoints(workspacePath, relativePath, includeLeaf: true);

        return Directory.EnumerateFileSystemEntries(directoryPath)
            .Where(path => !IsReparsePoint(path))
            .Select(path => Directory.Exists(path)
                ? (FileSystemInfo)new DirectoryInfo(path)
                : new FileInfo(path))
            .Where(entry => !IsIgnored(
                workspacePath,
                ToRelativePath(workspacePath, entry.FullName),
                entry is DirectoryInfo))
            .OrderByDescending(entry => entry is DirectoryInfo)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Take(500)
            .Select(entry => new WorkspaceEntry(
                entry.Name,
                ToRelativePath(workspacePath, entry.FullName),
                entry is DirectoryInfo,
                entry is FileInfo file ? file.Length : null,
                entry.LastWriteTimeUtc))
            .ToArray();
    }

    public async Task<WorkspaceFile> ReadFileAsync(
        string workspace,
        string? relativePath,
        CancellationToken cancellationToken)
    {
        var workspacePath = ResolveWorkspace(workspace);
        var filePath = ResolvePath(workspacePath, relativePath, allowRoot: false);
        EnsureNotIgnored(workspacePath, relativePath, isDirectory: false);
        EnsureNoReparsePoints(workspacePath, relativePath, includeLeaf: true);

        if (!File.Exists(filePath))
            throw new WorkspaceException("File not found.", 404);

        var info = new FileInfo(filePath);
        if (info.Length > MaxFileSize)
            throw new WorkspaceException("Files larger than 1 MB cannot be opened.", 413);

        var content = await File.ReadAllTextAsync(filePath, cancellationToken);
        return new WorkspaceFile(
            ToRelativePath(workspacePath, filePath),
            content,
            info.Length,
            info.LastWriteTimeUtc);
    }

    public async Task<WorkspaceEntry> CreateFileAsync(
        string workspace,
        string? relativePath,
        string? content,
        CancellationToken cancellationToken)
    {
        if (content is null || Encoding.UTF8.GetByteCount(content) > MaxFileSize)
            throw new WorkspaceException("File content must be 1 MB or smaller.", 413);

        var workspacePath = ResolveWorkspace(workspace);
        var filePath = ResolvePath(workspacePath, relativePath, allowRoot: false);
        EnsureNotIgnored(workspacePath, relativePath, isDirectory: false);
        EnsureNoReparsePoints(
            workspacePath,
            Path.GetDirectoryName(relativePath!.Replace('\\', '/')),
            includeLeaf: true);

        var parent = Path.GetDirectoryName(filePath);
        if (parent is null || !Directory.Exists(parent))
            throw new WorkspaceException("The containing directory does not exist.", 404);

        try
        {
            await using var stream = new FileStream(
                filePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteAsync(content.AsMemory(), cancellationToken);
        }
        catch (IOException) when (File.Exists(filePath))
        {
            throw new WorkspaceException("A file with that path already exists.", 409);
        }

        var info = new FileInfo(filePath);
        return new WorkspaceEntry(
            info.Name,
            ToRelativePath(workspacePath, filePath),
            false,
            info.Length,
            info.LastWriteTimeUtc);
    }

    public WorkspaceEntry CreateDirectory(string workspace, string? relativePath)
    {
        var workspacePath = ResolveWorkspace(workspace);
        var directoryPath = ResolvePath(workspacePath, relativePath, allowRoot: false);
        EnsureNotIgnored(workspacePath, relativePath, isDirectory: true);
        EnsureNoReparsePoints(
            workspacePath,
            Path.GetDirectoryName(relativePath!.Replace('\\', '/')),
            includeLeaf: true);

        if (Directory.Exists(directoryPath) || File.Exists(directoryPath))
            throw new WorkspaceException("An item with that path already exists.", 409);

        Directory.CreateDirectory(directoryPath);
        var info = new DirectoryInfo(directoryPath);
        return new WorkspaceEntry(
            info.Name,
            ToRelativePath(workspacePath, directoryPath),
            true,
            null,
            info.LastWriteTimeUtc);
    }

    public WorkspaceEntry Rename(
        string workspace,
        string? relativePath,
        string? newName,
        bool isDirectory)
    {
        var workspacePath = ResolveWorkspace(workspace);
        var sourcePath = ResolvePath(workspacePath, relativePath, allowRoot: false);
        EnsureNotIgnored(workspacePath, relativePath, isDirectory);
        EnsureNoReparsePoints(workspacePath, relativePath, includeLeaf: true);

        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
            throw new WorkspaceException("Item not found.", 404);

        var safeName = ValidateSegment(newName, "New name");
        var parent = Path.GetDirectoryName(sourcePath);
        if (parent is null)
            throw new WorkspaceException("The workspace root cannot be renamed.", 400);

        var destinationPath = Path.Combine(parent, safeName);
        var destinationRelativePath = ToRelativePath(workspacePath, destinationPath);
        EnsureNotIgnored(workspacePath, destinationRelativePath, isDirectory);

        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
            throw new WorkspaceException("An item with that name already exists.", 409);

        if (isDirectory)
        {
            if (!Directory.Exists(sourcePath))
                throw new WorkspaceException("Directory not found.", 404);
            RejectReparsePointsBelow(sourcePath);
            Directory.Move(sourcePath, destinationPath);
            var info = new DirectoryInfo(destinationPath);
            return new WorkspaceEntry(
                info.Name,
                destinationRelativePath,
                true,
                null,
                info.LastWriteTimeUtc);
        }

        if (!File.Exists(sourcePath))
            throw new WorkspaceException("File not found.", 404);

        File.Move(sourcePath, destinationPath);
        var file = new FileInfo(destinationPath);
        return new WorkspaceEntry(
            file.Name,
            destinationRelativePath,
            false,
            file.Length,
            file.LastWriteTimeUtc);
    }

    public void Delete(
        string workspace,
        string? relativePath,
        bool isDirectory,
        bool confirmed)
    {
        if (!confirmed)
            throw new WorkspaceException("Explicit confirmation is required.", 400);

        var workspacePath = ResolveWorkspace(workspace);
        var path = ResolvePath(workspacePath, relativePath, allowRoot: false);
        EnsureNotIgnored(workspacePath, relativePath, isDirectory);
        EnsureNoReparsePoints(workspacePath, relativePath, includeLeaf: true);

        if (isDirectory)
        {
            if (!Directory.Exists(path))
                throw new WorkspaceException("Directory not found.", 404);
            RejectReparsePointsBelow(path);
            Directory.Delete(path, recursive: true);
            return;
        }

        if (!File.Exists(path))
            throw new WorkspaceException("File not found.", 404);
        File.Delete(path);
    }

    private string ResolveWorkspace(string? name)
    {
        if (!Guid.TryParseExact(name, "N", out _))
            throw new WorkspaceException("Workspace not found.", 404);
        var workspace = ReadWorkspaceRegistry()
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Id, name, StringComparison.OrdinalIgnoreCase));
        if (workspace is null || !Directory.Exists(workspace.Path))
            throw new WorkspaceException("Workspace not found.", 404);
        EnsureNoReparsePointsInAncestors(workspace.Path);
        RejectReparsePoint(workspace.Path);

        return workspace.Path;
    }

    private string ResolveLocation(string location)
    {
        if (!Path.IsPathFullyQualified(location))
            throw new WorkspaceException("Location must be an absolute path to an existing folder.", 400);

        var path = Path.GetFullPath(location);
        if (!Directory.Exists(path))
            throw new WorkspaceException("The selected location folder does not exist.", 404);

        return path;
    }

    private List<WorkspaceInfo> ReadWorkspaceRegistry()
    {
        lock (_registryLock)
        {
            if (!File.Exists(_registryPath))
                return [];
            RejectReparsePoint(_registryPath);
            return JsonSerializer.Deserialize<List<WorkspaceInfo>>(
                    File.ReadAllText(_registryPath)) ?? [];
        }
    }

    private void WriteWorkspaceRegistry(IReadOnlyCollection<WorkspaceInfo> workspaces)
    {
        lock (_registryLock)
        {
            if (File.Exists(_registryPath))
                RejectReparsePoint(_registryPath);

            var temporaryPath = _registryPath + ".tmp";
            if (File.Exists(temporaryPath))
                RejectReparsePoint(temporaryPath);
            var json = JsonSerializer.Serialize(workspaces);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _registryPath, overwrite: true);
        }
    }

    private static void EnsureNoReparsePointsInAncestors(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(current))
                RejectReparsePoint(current);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) ||
                string.Equals(parent, current, PathComparison))
            {
                break;
            }
            current = parent;
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), PathComparison);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static string ResolvePath(
        string workspacePath,
        string? relativePath,
        bool allowRoot)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            if (allowRoot)
                return workspacePath;
            throw new WorkspaceException("A relative file path is required.", 400);
        }

        if (Path.IsPathRooted(relativePath))
            throw new WorkspaceException("Absolute paths are not allowed.", 400);

        var segments = relativePath.Replace('\\', '/').Split('/');
        if (segments.Any(segment =>
                segment.Length == 0 || segment is "." or ".."))
            throw new WorkspaceException("The path contains an invalid segment.", 400);

        var safeSegments = segments
            .Select(segment => ValidateSegment(segment, "Path segment"))
            .ToArray();
        var path = Path.GetFullPath(Path.Combine([workspacePath, .. safeSegments]));
        EnsureContained(workspacePath, path);
        return path;
    }

    private static string ValidateSegment(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value is "." or ".." ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.Contains('/') ||
            value.Contains('\\') ||
            value.Contains(':') ||
            (OperatingSystem.IsWindows() &&
             (value.EndsWith(' ') || value.EndsWith('.'))))
        {
            throw new WorkspaceException($"{label} is invalid.", 400);
        }

        return value;
    }

    private static void EnsureContained(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new WorkspaceException("The requested path is outside the workspace.", 403);
        }
    }

    private static string ToRelativePath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static void EnsureNoReparsePoints(
        string workspacePath,
        string? relativePath,
        bool includeLeaf)
    {
        RejectReparsePoint(workspacePath);
        if (string.IsNullOrWhiteSpace(relativePath))
            return;

        var segments = relativePath.Replace('\\', '/').Split('/');
        var current = workspacePath;
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            if (!includeLeaf && index == segments.Length - 1)
                break;
            if (File.Exists(current) || Directory.Exists(current))
                RejectReparsePoint(current);
        }
    }

    private static void RejectReparsePointsBelow(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            RejectReparsePoint(entry);
            if (Directory.Exists(entry))
                RejectReparsePointsBelow(entry);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if (IsReparsePoint(path))
            throw new WorkspaceException("Symbolic links and reparse points are not allowed.", 403);
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private void EnsureNotIgnored(
        string workspacePath,
        string? relativePath,
        bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return;

        var path = relativePath.Replace('\\', '/');
        if (path.Split('/').Any(IsSensitiveSegment))
            throw new WorkspaceException("This sensitive or protected path is not accessible.", 403);

        if (IsIgnored(workspacePath, path, isDirectory))
            throw new WorkspaceException("This path is excluded by workspace ignore rules.", 404);
    }

    private bool IsIgnored(
        string workspacePath,
        string relativePath,
        bool isDirectory)
    {
        if (relativePath.Split('/').Any(IsSensitiveSegment))
            return true;

        var patterns = new List<string>(_defaultIgnorePatterns);
        patterns.AddRange(_configuredIgnorePatterns);
        var gitIgnore = Path.Combine(workspacePath, ".gitignore");
        if (File.Exists(gitIgnore) && !IsReparsePoint(gitIgnore))
        {
            patterns.AddRange(File.ReadLines(gitIgnore));
        }

        var ignored = false;
        foreach (var rawPattern in patterns)
        {
            var pattern = rawPattern.Trim();
            if (pattern.Length == 0 || pattern.StartsWith('#'))
                continue;

            var negated = pattern.StartsWith('!');
            if (negated)
                pattern = pattern[1..];
            if (pattern.Length == 0)
                continue;

            if (MatchesIgnorePattern(relativePath, pattern, isDirectory))
                ignored = !negated;
        }

        return ignored;
    }

    private static bool IsSensitiveSegment(string segment)
    {
        var name = segment.ToLowerInvariant();
        if (name == ".git" ||
            name == ".env" ||
            (name.StartsWith(".env.") && name != ".env.example") ||
            name is "id_rsa" or "id_ed25519" or "credentials" or
                "credentials.json" or "secrets.json" or "secrets.yaml" or
                "secrets.yml" or "secrets.toml")
        {
            return true;
        }

        return Path.GetExtension(name) is
            ".pem" or ".key" or ".pfx" or ".p12" or ".p7b" or ".keystore";
    }

    private static bool MatchesIgnorePattern(
        string relativePath,
        string rawPattern,
        bool isDirectory)
    {
        var directoryOnly = rawPattern.EndsWith('/');
        var pattern = rawPattern.TrimEnd('/');
        var anchored = pattern.StartsWith('/');
        pattern = pattern.TrimStart('/');
        var hasSlash = pattern.Contains('/');
        var regex = new Regex(
            "^" + Regex.Escape(pattern)
                .Replace(@"\*\*", ".*")
                .Replace(@"\*", "[^/]*")
                .Replace(@"\?", "[^/]") + "$",
            RegexOptions.CultureInvariant |
            (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None));

        var segments = relativePath.Split('/');
        for (var index = 0; index < segments.Length; index++)
        {
            var isDirectoryPrefix = index < segments.Length - 1 || isDirectory;
            if (directoryOnly && !isDirectoryPrefix)
                continue;

            var candidate = hasSlash || anchored
                ? string.Join('/', segments.Take(index + 1))
                : segments[index];

            if (regex.IsMatch(candidate))
                return true;
        }

        return false;
    }
}

public sealed record WorkspaceInfo(string Id, string Name, string Path);

public sealed record WorkspaceDirectoryEntry(string Path, string Name);

public sealed record WorkspaceDirectoryBrowser(
    string? CurrentPath,
    string? ParentPath,
    IReadOnlyList<WorkspaceDirectoryEntry> Directories);

public sealed record WorkspaceEntry(
    string Name,
    string Path,
    bool IsDirectory,
    long? Size,
    DateTime LastModifiedUtc);

public sealed record WorkspaceFile(
    string Path,
    string Content,
    long Size,
    DateTime LastModifiedUtc);

public sealed record CreateWorkspaceRequest(string? Name, string? Location);

public sealed record CreateWorkspaceFileRequest(string? Path, string? Content);

public sealed record CreateWorkspaceDirectoryRequest(string? Path);

public sealed record RenameWorkspaceEntryRequest(string? Path, string? NewName);

public sealed record DeleteWorkspaceEntryRequest(string? Path, bool Confirmed);

public sealed class WorkspaceException(string message, int statusCode)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
