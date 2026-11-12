using System.Text.Json;
using System.Text.Json.Serialization;
using Qalam.Core.Models;

namespace Qalam.Core.Storage;

public interface IProjectRepository
{
    IReadOnlyList<Project> ListProjects();
    Project? GetProject(string id);
    void SaveProject(Project project);
    void DeleteProject(string id);

    /// <summary>Saves a new immutable version. Throws if a version with the same id already exists.</summary>
    ArtifactVersion AddVersion(ArtifactVersion version);
    ArtifactVersion? GetVersion(string projectId, string versionId);
    IReadOnlyList<ArtifactVersion> ListVersions(string projectId);
    IReadOnlyList<ArtifactVersion> ListAllVersions(ArtifactKind? kind = null);
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>
/// Stores each project as a folder: project.json plus one file per artifact version.
/// Version files are write-once; nothing is ever overwritten.
/// </summary>
public sealed class JsonProjectRepository : IProjectRepository
{
    private readonly string _root;
    private readonly object _gate = new();

    public JsonProjectRepository(string dataFolder)
    {
        _root = Path.Combine(dataFolder, "projects");
        Directory.CreateDirectory(_root);
    }

    private string ProjectDir(string id) => Path.Combine(_root, Sanitize(id));
    private string VersionDir(string id) => Path.Combine(ProjectDir(id), "versions");

    private static string Sanitize(string id)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) id = id.Replace(c, '_');
        return id.Replace("..", "_");
    }

    public IReadOnlyList<Project> ListProjects()
    {
        lock (_gate)
        {
            var list = new List<Project>();
            foreach (var dir in Directory.EnumerateDirectories(_root))
            {
                var file = Path.Combine(dir, "project.json");
                if (!File.Exists(file)) continue;
                try
                {
                    var p = JsonSerializer.Deserialize<Project>(File.ReadAllText(file), Json.Options);
                    if (p != null) list.Add(p);
                }
                catch (JsonException) { /* skip corrupt file; keep app usable */ }
            }
            return list.OrderByDescending(p => p.UpdatedAt).ToList();
        }
    }

    public Project? GetProject(string id)
    {
        lock (_gate)
        {
            var file = Path.Combine(ProjectDir(id), "project.json");
            return File.Exists(file)
                ? JsonSerializer.Deserialize<Project>(File.ReadAllText(file), Json.Options)
                : null;
        }
    }

    public void SaveProject(Project project)
    {
        lock (_gate)
        {
            project.UpdatedAt = DateTime.UtcNow;
            var dir = ProjectDir(project.Id);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "project.json");
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(project, Json.Options));
            File.Move(tmp, file, overwrite: true); // atomic replace of the project record (not of versions)
        }
    }

    public void DeleteProject(string id)
    {
        lock (_gate)
        {
            var dir = ProjectDir(id);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    public ArtifactVersion AddVersion(ArtifactVersion version)
    {
        lock (_gate)
        {
            var dir = VersionDir(version.ProjectId);
            Directory.CreateDirectory(dir);
            var existing = LoadVersions(version.ProjectId);
            if (existing.Any(v => v.Id == version.Id))
                throw new InvalidOperationException($"Version {version.Id} already exists and cannot be overwritten.");
            version.VersionNumber = existing.Count(v => v.Kind == version.Kind) + 1;
            var file = Path.Combine(dir, $"{version.CreatedAt:yyyyMMddHHmmssfff}-{version.Kind}-{version.Id}.json");
            using (var fs = new FileStream(file, FileMode.CreateNew, FileAccess.Write))
            using (var w = new StreamWriter(fs))
                w.Write(JsonSerializer.Serialize(version, Json.Options));
            return version;
        }
    }

    public ArtifactVersion? GetVersion(string projectId, string versionId)
    {
        lock (_gate) return LoadVersions(projectId).FirstOrDefault(v => v.Id == versionId);
    }

    public IReadOnlyList<ArtifactVersion> ListVersions(string projectId)
    {
        lock (_gate) return LoadVersions(projectId);
    }

    public IReadOnlyList<ArtifactVersion> ListAllVersions(ArtifactKind? kind = null)
    {
        lock (_gate)
        {
            var all = new List<ArtifactVersion>();
            foreach (var dir in Directory.EnumerateDirectories(_root))
                all.AddRange(LoadVersions(Path.GetFileName(dir)));
            return all.Where(v => kind == null || v.Kind == kind)
                      .OrderByDescending(v => v.CreatedAt).ToList();
        }
    }

    private List<ArtifactVersion> LoadVersions(string projectId)
    {
        var dir = VersionDir(projectId);
        if (!Directory.Exists(dir)) return new();
        var list = new List<ArtifactVersion>();
        foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var v = JsonSerializer.Deserialize<ArtifactVersion>(File.ReadAllText(f), Json.Options);
                if (v != null) list.Add(v);
            }
            catch (JsonException) { }
        }
        return list.OrderBy(v => v.CreatedAt).ToList();
    }
}

/// <summary>Simple JSON-file stores for settings and the content calendar.</summary>
public sealed class JsonFileStore<T> where T : class, new()
{
    private readonly string _file;
    private readonly object _gate = new();

    public JsonFileStore(string file)
    {
        _file = file;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    }

    public T Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_file)) return new T();
            try { return JsonSerializer.Deserialize<T>(File.ReadAllText(_file), Json.Options) ?? new T(); }
            catch (JsonException) { return new T(); }
        }
    }

    public void Save(T value)
    {
        lock (_gate)
        {
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json.Options));
            File.Move(tmp, _file, overwrite: true);
        }
    }
}
