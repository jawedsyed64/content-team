using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Qalam.Core.Documents;

public sealed record BrandDocument(string FileName, string StoredPath, long Size, DateTime AddedAt, int TextLength);

/// <summary>Uploaded brand documents (.txt, .md, .docx). Text is extracted once and given to brand-aware agents.</summary>
public sealed class BrandDocumentStore
{
    public static readonly string[] SupportedExtensions = { ".txt", ".md", ".markdown", ".docx" };

    private readonly string _dir;

    public BrandDocumentStore(string dataFolder)
    {
        _dir = Path.Combine(dataFolder, "brand-docs");
        Directory.CreateDirectory(_dir);
    }

    public BrandDocument Import(string sourcePath)
    {
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!SupportedExtensions.Contains(ext))
            throw new NotSupportedException($"Unsupported file type {ext}. Use .txt, .md or .docx.");

        var name = Path.GetFileName(sourcePath);
        var target = Path.Combine(_dir, name);
        int n = 1;
        while (File.Exists(target))
            target = Path.Combine(_dir, $"{Path.GetFileNameWithoutExtension(name)} ({n++}){ext}");
        File.Copy(sourcePath, target);

        var text = ExtractText(target);
        File.WriteAllText(target + ".txt-extract", text);
        var info = new FileInfo(target);
        return new BrandDocument(info.Name, target, info.Length, info.CreationTimeUtc, text.Length);
    }

    public IReadOnlyList<BrandDocument> List()
    {
        return Directory.EnumerateFiles(_dir)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Select(f =>
            {
                var info = new FileInfo(f);
                var extract = f + ".txt-extract";
                var len = File.Exists(extract) ? (int)new FileInfo(extract).Length : 0;
                return new BrandDocument(info.Name, f, info.Length, info.CreationTimeUtc, len);
            })
            .OrderBy(d => d.FileName).ToList();
    }

    public void Remove(BrandDocument doc)
    {
        if (File.Exists(doc.StoredPath)) File.Delete(doc.StoredPath);
        var extract = doc.StoredPath + ".txt-extract";
        if (File.Exists(extract)) File.Delete(extract);
    }

    /// <summary>All document text, trimmed to a budget so prompts stay within limits.</summary>
    public string CombinedText(int maxChars)
    {
        var sb = new StringBuilder();
        foreach (var doc in List())
        {
            var extract = doc.StoredPath + ".txt-extract";
            var text = File.Exists(extract) ? File.ReadAllText(extract) : ExtractText(doc.StoredPath);
            var remaining = maxChars - sb.Length;
            if (remaining <= 200) { sb.AppendLine("[Further documents omitted: size limit reached]"); break; }
            sb.AppendLine($"<brand_document name=\"{doc.FileName}\">");
            sb.AppendLine(text.Length > remaining - 100 ? text[..(remaining - 100)] + "\n[…truncated]" : text);
            sb.AppendLine("</brand_document>");
        }
        return sb.ToString();
    }

    public static string ExtractText(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext == ".docx" ? ExtractDocx(path) : File.ReadAllText(path);
    }

    internal static string ExtractDocx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("word/document.xml")
            ?? throw new InvalidDataException("Not a valid Word document.");
        using var s = entry.Open();
        var doc = XDocument.Load(s);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var sb = new StringBuilder();
        foreach (var p in doc.Descendants(w + "p"))
        {
            foreach (var node in p.Descendants())
            {
                if (node.Name == w + "t") sb.Append(node.Value);
                else if (node.Name == w + "tab") sb.Append('\t');
                else if (node.Name == w + "br") sb.Append('\n');
            }
            sb.Append('\n');
        }
        return sb.ToString().Trim();
    }
}
