using System.Reflection;
using System.Text.RegularExpressions;

namespace Qalam.Core.Context;

public enum ContextFileKind
{
    BrandVoice,
    StyleGuide,
    AudienceProfile,
    ContentStrategy,
    EditorialStandards
}

public static class ContextFiles
{
    public static string FileName(ContextFileKind kind) => kind switch
    {
        ContextFileKind.BrandVoice => "brand-voice.md",
        ContextFileKind.StyleGuide => "style-guide.md",
        ContextFileKind.AudienceProfile => "audience-profile.md",
        ContextFileKind.ContentStrategy => "content-strategy.md",
        ContextFileKind.EditorialStandards => "editorial-standards.md",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static string DisplayName(ContextFileKind kind) => kind switch
    {
        ContextFileKind.BrandVoice => "Brand Voice and Identity",
        ContextFileKind.StyleGuide => "Editorial Style Guide",
        ContextFileKind.AudienceProfile => "Audience Profile",
        ContextFileKind.ContentStrategy => "Content Strategy",
        ContextFileKind.EditorialStandards => "Editorial Standards",
        _ => kind.ToString()
    };

    public static IReadOnlyList<ContextFileKind> All { get; } = Enum.GetValues<ContextFileKind>();

    public static string Template(ContextFileKind kind)
    {
        var name = "Qalam.Templates." + FileName(kind);
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Missing embedded template " + name);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}

/// <summary>The central knowledge base: five Markdown files every agent consults.</summary>
public sealed class ContextStore
{
    private readonly string _dir;
    private readonly string _historyDir;
    private readonly object _gate = new();

    public ContextStore(string dataFolder)
    {
        _dir = Path.Combine(dataFolder, "context");
        _historyDir = Path.Combine(_dir, "history");
        Directory.CreateDirectory(_historyDir);
        foreach (var kind in ContextFiles.All)
        {
            var path = PathOf(kind);
            if (!File.Exists(path)) File.WriteAllText(path, ContextFiles.Template(kind));
        }
    }

    public string Folder => _dir;
    public string PathOf(ContextFileKind kind) => Path.Combine(_dir, ContextFiles.FileName(kind));

    public string Read(ContextFileKind kind)
    {
        lock (_gate) return File.ReadAllText(PathOf(kind));
    }

    /// <summary>Saves the file and keeps the previous content in history.</summary>
    public void Write(ContextFileKind kind, string content)
    {
        lock (_gate)
        {
            var path = PathOf(kind);
            if (File.Exists(path))
            {
                var old = File.ReadAllText(path);
                if (old == content) return;
                File.WriteAllText(Path.Combine(_historyDir,
                    $"{Path.GetFileNameWithoutExtension(path)}-{DateTime.UtcNow:yyyyMMddHHmmssfff}.md"), old);
            }
            File.WriteAllText(path, content);
        }
    }

    public void ResetToTemplate(ContextFileKind kind) => Write(kind, ContextFiles.Template(kind));
}

public sealed record MissingContextItem(ContextFileKind File, string Item)
{
    public override string ToString() => $"{ContextFiles.DisplayName(File)}: {Item}";
}

public static class ContextValidator
{
    private static readonly Regex Placeholder = new(@"\[FILL IN:\s*([^\]]*)\]", RegexOptions.Compiled);

    public static IReadOnlyList<MissingContextItem> FindMissing(ContextStore store, IEnumerable<ContextFileKind> kinds)
    {
        var missing = new List<MissingContextItem>();
        foreach (var kind in kinds.Distinct())
            foreach (Match m in Placeholder.Matches(store.Read(kind)))
                missing.Add(new MissingContextItem(kind, m.Groups[1].Value.Trim()));
        return missing;
    }

    public static int CountPlaceholders(string content) => Placeholder.Matches(content).Count;
}
