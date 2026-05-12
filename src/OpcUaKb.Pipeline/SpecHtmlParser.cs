using System.Text;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;

// ═══════════════════════════════════════════════════════════════════════
// SpecHtmlParser — parses the reference.opcfoundation.org "Single Page"
// HTML view (/specs/{id}/v{ver}/full) into per-section SectionChunk
// records. Walks the H2–H6 heading hierarchy, accumulates body text
// (paragraphs, lists, notes, figures, tables) for each section, and
// emits a chunk per heading with breadcrumb context.
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
/// Spec-level metadata. Provided externally by the catalog/STS parser
/// because it isn't fully present in the Single Page HTML view.
/// </summary>
public sealed record SpecMetadata(
    string SpecId,
    string SpecTitle,
    string SpecVersion,
    DateOnly? PublicationDate,
    string? NamespaceUri,
    string? GitHubTag);

/// <summary>
/// One emitted chunk corresponding to a single heading in the spec.
/// </summary>
public sealed record SectionChunk(
    string SectionId,
    string SectionNumber,
    string SectionTitle,
    IReadOnlyList<string> Breadcrumb,
    string SectionPath,
    string SourceUrl,
    string PageChunk,
    IReadOnlyList<string> Figures);

public sealed class SpecHtmlParser
{
    const string ReferenceBaseUrl = "https://reference.opcfoundation.org";
    const int MaxChunkChars = 16000; // ~4000 tokens; warn if exceeded

    static readonly Regex ImgSha256Re = new(
        @"/img/([a-f0-9]{64})\.png",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static readonly Regex WhitespaceRe = new(@"\s+", RegexOptions.Compiled);

    readonly ILogger _log;

    public SpecHtmlParser(ILogger log) => _log = log;

    /// <summary>
    /// Parse the Single Page HTML view and emit one <see cref="SectionChunk"/>
    /// per heading found inside <c>&lt;main id="content"&gt;</c>.
    /// </summary>
    /// <param name="html">Raw HTML string.</param>
    /// <param name="metadata">Spec-level metadata used to build URLs.</param>
    /// <param name="sectionSlugByNumber">
    /// Optional map from section number (e.g. <c>"5.6.2"</c>) to STS slug
    /// (e.g. <c>"sec_5-6-2_variable-nodeclass"</c>). When a section number
    /// is missing from the map, a fallback id <c>n_5-6-2</c> is used.
    /// </param>
    public IEnumerable<SectionChunk> ParseSections(
        string html,
        SpecMetadata metadata,
        IReadOnlyDictionary<string, string>? sectionSlugByNumber = null)
    {
        var ctx = BrowsingContext.New(Configuration.Default);
        var htmlParser = ctx.GetService<IHtmlParser>()!;
        using var doc = htmlParser.ParseDocument(html);

        var main = doc.GetElementById("content");
        if (main == null)
        {
            _log.LogWarning(
                "[PARSER] No <main id=\"content\"> found in spec {SpecId}",
                metadata.SpecId);
            yield break;
        }

        // reference.opcfoundation.org wraps the body in <article class="full-document">.
        // Walk its children when present; otherwise fall back to <main>'s children.
        var container = main.QuerySelector("article.full-document") ?? main;

        var stack = new Stack<HeadingFrame>();
        SectionChunkBuilder? current = null;
        var unnumberedCounter = 0;

        foreach (var element in container.Children)
        {
            if (TryGetHeadingLevel(element.TagName, out var level))
            {
                // Skip the spec's H1 (the page title); we don't emit a chunk for it.
                if (level == 1) continue;

                var secNumberSpan = element.Children.FirstOrDefault(
                    c => c.TagName.Equals("SPAN", StringComparison.OrdinalIgnoreCase)
                        && c.ClassList.Contains("sec-number"));

                var number = secNumberSpan != null
                    ? CollapseWhitespace(secNumberSpan.TextContent).Trim()
                    : "";

                var title = ExtractHeadingTitle(element);

                if (string.IsNullOrEmpty(number))
                    number = $"unnumbered_{++unnumberedCounter}";

                if (current != null)
                    yield return current.Build(metadata, sectionSlugByNumber, _log);

                while (stack.Count > 0 && stack.Peek().Level >= level)
                    stack.Pop();

                var breadcrumb = stack.Reverse()
                    .Select(f => $"{f.Number} {f.Title}".Trim())
                    .ToList();

                current = new SectionChunkBuilder(number, title, breadcrumb);
                stack.Push(new HeadingFrame(level, number, title));
                continue;
            }

            // Skip content before the first heading (front-matter wrapper, TOCs, etc.).
            if (current == null) continue;

            AppendElement(element, current);
        }

        if (current != null)
            yield return current.Build(metadata, sectionSlugByNumber, _log);
    }

    static bool TryGetHeadingLevel(string tagName, out int level)
    {
        if (tagName.Length == 2
            && (tagName[0] == 'H' || tagName[0] == 'h')
            && tagName[1] >= '1' && tagName[1] <= '6')
        {
            level = tagName[1] - '0';
            return true;
        }
        level = 0;
        return false;
    }

    static string ExtractHeadingTitle(IElement heading)
    {
        // Concatenate text content of all direct children except the sec-number span.
        var sb = new StringBuilder();
        foreach (var node in heading.ChildNodes)
        {
            if (node is IElement el
                && el.TagName.Equals("SPAN", StringComparison.OrdinalIgnoreCase)
                && el.ClassList.Contains("sec-number"))
            {
                continue;
            }
            sb.Append(node.TextContent);
        }
        return CollapseWhitespace(sb.ToString()).Trim();
    }

    static string CollapseWhitespace(string s) =>
        WhitespaceRe.Replace(s, " ");

    static void AppendElement(IElement element, SectionChunkBuilder current)
    {
        var tag = element.TagName.ToLowerInvariant();
        switch (tag)
        {
            case "figure":
                AppendFigure(element, current);
                break;
            case "aside":
                AppendAside(element, current);
                break;
            case "table":
                // Bare table (uncommon — usually wrapped in figure.table-wrap).
                AppendTableMarkdown(element, current);
                break;
            default:
                AppendFlattenedText(element, current);
                break;
        }
    }

    static void AppendFigure(IElement figure, SectionChunkBuilder current)
    {
        var captionEl = figure.Children.FirstOrDefault(
            c => c.TagName.Equals("FIGCAPTION", StringComparison.OrdinalIgnoreCase));
        var caption = captionEl != null
            ? CollapseWhitespace(captionEl.TextContent).Trim()
            : "";

        if (figure.ClassList.Contains("table-wrap"))
        {
            current.AppendNewline();
            if (!string.IsNullOrEmpty(caption))
            {
                current.AppendLine($"[Table: {caption}]");
            }
            var table = figure.QuerySelector("table");
            if (table != null) AppendTableMarkdown(table, current);
            current.AppendNewline();
            return;
        }

        // Image figure: emit "[Figure: caption] (image: {sha}.png)".
        var img = figure.QuerySelector("img");
        var src = img?.GetAttribute("src") ?? "";
        var match = ImgSha256Re.Match(src);
        var sha256 = match.Success ? match.Groups[1].Value.ToLowerInvariant() : "";

        current.AppendNewline();
        var line = new StringBuilder();
        line.Append("[Figure: ");
        line.Append(caption);
        line.Append(']');
        if (!string.IsNullOrEmpty(sha256))
        {
            line.Append(" (image: ");
            line.Append(sha256);
            line.Append(".png)");
            current.Figures.Add(sha256);
        }
        current.AppendLine(line.ToString());
        current.AppendNewline();
    }

    static void AppendAside(IElement aside, SectionChunkBuilder current)
    {
        if (!aside.ClassList.Contains("note"))
        {
            AppendFlattenedText(aside, current);
            return;
        }

        // <aside class="note"><span class="label">NOTE</span><p>...</p></aside>
        var sb = new StringBuilder();
        foreach (var child in aside.ChildNodes)
        {
            if (child is IElement el
                && el.TagName.Equals("SPAN", StringComparison.OrdinalIgnoreCase)
                && el.ClassList.Contains("label"))
            {
                continue;
            }
            sb.Append(FlattenInline(child));
        }
        var body = CollapseWhitespace(sb.ToString()).Trim();
        if (body.Length == 0) return;

        current.AppendNewline();
        current.AppendLine($"NOTE: {body}");
        current.AppendNewline();
    }

    static void AppendTableMarkdown(IElement table, SectionChunkBuilder current)
    {
        var rows = table.QuerySelectorAll("tr").ToList();
        if (rows.Count == 0) return;

        var separatorEmitted = false;
        var emittedRows = 0;
        foreach (var row in rows)
        {
            var cells = row.QuerySelectorAll("th, td").ToList();
            if (cells.Count == 0) continue;

            var cellTexts = cells.Select(c =>
                CollapseWhitespace(FlattenInline(c)).Trim().Replace("|", "\\|"));

            current.AppendLine("| " + string.Join(" | ", cellTexts) + " |");

            if (!separatorEmitted)
            {
                current.AppendLine("|" + string.Concat(Enumerable.Repeat("---|", cells.Count)));
                separatorEmitted = true;
            }
            emittedRows++;
        }

        if (emittedRows == 0) return;
    }

    static void AppendFlattenedText(IElement element, SectionChunkBuilder current)
    {
        var text = CollapseWhitespace(FlattenInline(element)).Trim();
        if (text.Length == 0) return;
        current.AppendLine(text);
    }

    /// <summary>
    /// Recursively flatten an element to a string, replacing <c>&lt;a class="xref"&gt;</c>
    /// links with <c>"{label} (see {data-rid or href})"</c>.
    /// </summary>
    static string FlattenInline(INode node)
    {
        var sb = new StringBuilder();
        FlattenInlineInto(node, sb);
        return sb.ToString();
    }

    static void FlattenInlineInto(INode node, StringBuilder sb)
    {
        foreach (var child in node.ChildNodes)
        {
            switch (child)
            {
                case IText t:
                    sb.Append(t.Text);
                    break;
                case IElement el:
                    if (el.TagName.Equals("A", StringComparison.OrdinalIgnoreCase)
                        && el.ClassList.Contains("xref"))
                    {
                        var label = CollapseWhitespace(el.TextContent).Trim();
                        sb.Append(label);
                        var rid = el.GetAttribute("data-rid");
                        var href = el.GetAttribute("href");
                        var target = !string.IsNullOrEmpty(rid)
                            ? rid
                            : ShortenHref(href);
                        if (!string.IsNullOrEmpty(target))
                        {
                            sb.Append(" (see ");
                            sb.Append(target);
                            sb.Append(')');
                        }
                    }
                    else if (el.TagName.Equals("BR", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.Append(' ');
                    }
                    else
                    {
                        FlattenInlineInto(el, sb);
                    }
                    break;
            }
        }
    }

    static string ShortenHref(string? href)
    {
        if (string.IsNullOrEmpty(href)) return "";
        // Strip the host but keep relative paths intact.
        if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(href, UriKind.Absolute, out var u))
                return u.PathAndQuery;
        }
        return href;
    }

    readonly record struct HeadingFrame(int Level, string Number, string Title);

    sealed class SectionChunkBuilder
    {
        public string Number { get; }
        public string Title { get; }
        public IReadOnlyList<string> Breadcrumb { get; }
        public List<string> Figures { get; } = new();

        readonly StringBuilder _body = new();
        bool _atLineStart = true;

        public SectionChunkBuilder(string number, string title, IReadOnlyList<string> breadcrumb)
        {
            Number = number;
            Title = title;
            Breadcrumb = breadcrumb;
        }

        public void AppendLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (!_atLineStart) _body.Append('\n');
            _body.Append(text);
            _body.Append('\n');
            _atLineStart = true;
        }

        public void AppendNewline()
        {
            if (!_atLineStart)
            {
                _body.Append('\n');
                _atLineStart = true;
            }
        }

        public SectionChunk Build(
            SpecMetadata metadata,
            IReadOnlyDictionary<string, string>? slugMap,
            ILogger log)
        {
            var sectionId = slugMap != null
                && slugMap.TryGetValue(Number, out var slug)
                && !string.IsNullOrEmpty(slug)
                    ? slug
                    : "n_" + Number.Replace('.', '-');

            var sectionPath = $"/specs/{metadata.SpecId}/v{metadata.SpecVersion}/{Number}";
            var sourceUrl = $"{ReferenceBaseUrl}{sectionPath}";

            var bodyText = _body.ToString().TrimEnd();

            // Compose final chunk text: breadcrumb header line, then body.
            var header = new StringBuilder();
            if (Breadcrumb.Count > 0)
            {
                header.Append(string.Join(" > ", Breadcrumb));
                header.Append(" > ");
            }
            header.Append(Number).Append(' ').Append(Title);

            var pageChunk = bodyText.Length > 0
                ? header.ToString() + "\n\n" + bodyText
                : header.ToString();

            if (pageChunk.Length > MaxChunkChars)
            {
                log.LogWarning(
                    "[PARSER] Section {SpecId}/{Number} exceeds {Max} chars (actual={Actual}); consider splitting at indexing time",
                    metadata.SpecId, Number, MaxChunkChars, pageChunk.Length);
            }

            return new SectionChunk(
                SectionId: sectionId,
                SectionNumber: Number,
                SectionTitle: Title,
                Breadcrumb: Breadcrumb,
                SectionPath: sectionPath,
                SourceUrl: sourceUrl,
                PageChunk: pageChunk,
                Figures: Figures);
        }
    }
}
