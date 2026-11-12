using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Qalam.App;

/// <summary>Small Markdown → HTML converter for exporting drafts (headings, lists, bold, italics, links, paragraphs).</summary>
public static class MarkdownLite
{
    public static string ToHtml(string title, string markdown)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset=\"utf-8\">");
        sb.AppendLine($"<title>{WebUtility.HtmlEncode(title)}</title>");
        sb.AppendLine("<style>body{font-family:Georgia,serif;max-width:720px;margin:40px auto;line-height:1.6;color:#222;padding:0 16px}h1,h2,h3{font-family:'Segoe UI',sans-serif}a{color:#2b59c3}</style>");
        sb.AppendLine("</head><body>");

        string? list = null;
        var para = new List<string>();
        void FlushPara()
        {
            if (para.Count == 0) return;
            sb.AppendLine("<p>" + Inline(string.Join(" ", para)) + "</p>");
            para.Clear();
        }
        void CloseList()
        {
            if (list == null) return;
            sb.AppendLine($"</{list}>");
            list = null;
        }

        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            var h = Regex.Match(line, @"^(#{1,6})\s+(.*)$");
            var ul = Regex.Match(line, @"^\s*[-*+]\s+(.*)$");
            var ol = Regex.Match(line, @"^\s*\d+[.)]\s+(.*)$");
            if (line.Length == 0) { FlushPara(); CloseList(); }
            else if (h.Success)
            {
                FlushPara(); CloseList();
                var n = h.Groups[1].Value.Length;
                sb.AppendLine($"<h{n}>{Inline(h.Groups[2].Value)}</h{n}>");
            }
            else if (ul.Success || ol.Success)
            {
                FlushPara();
                var tag = ul.Success ? "ul" : "ol";
                if (list != tag) { CloseList(); sb.AppendLine($"<{tag}>"); list = tag; }
                sb.AppendLine("<li>" + Inline((ul.Success ? ul : ol).Groups[1].Value) + "</li>");
            }
            else if (line.StartsWith(">"))
            {
                FlushPara(); CloseList();
                sb.AppendLine("<blockquote>" + Inline(line.TrimStart('>', ' ')) + "</blockquote>");
            }
            else { CloseList(); para.Add(line.Trim()); }
        }
        FlushPara(); CloseList();
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static string Inline(string text)
    {
        var s = WebUtility.HtmlEncode(text);
        s = Regex.Replace(s, @"\[([^\]]+)\]\((https?://[^)\s]+)\)", m =>
            $"<a href=\"{m.Groups[2].Value}\">{m.Groups[1].Value}</a>");
        s = Regex.Replace(s, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
        s = Regex.Replace(s, @"(?<![*\w])\*(?!\s)(.+?)(?<!\s)\*(?![*\w])", "<em>$1</em>");
        return s;
    }
}
