using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;

// ═══════════════════════════════════════════════════════════════════════
// Spec Catalog — discovers OPC UA specs and their per-version metadata
// from the restructured reference.opcfoundation.org site.
//
//   Root page (/)                  → DiscoverAllSpecsAsync()
//   Per-spec landing (/specs/{id}) → GetLandingAsync(specId)
//
// Each landing page exposes:
//   • Title (h1)
//   • A "Download" dropdown carrying STS XML / Markdown URLs for the
//     CURRENT version. For older versions we parallel-fetch their own
//     /specs/{id}/v{ver} landing pages (concurrency 5).
//   • Version History table
//   • Namespaces table (first row marked "primary" wins)
//   • Supplementary Files list (GitHub /tree/{tag}/{path} URLs)
// ═══════════════════════════════════════════════════════════════════════

public sealed record SpecRef(string SpecId, string ReleaseVersion, string Title);

public sealed record VersionRef(
    string SpecId,
    string Version,
    DateOnly PublishDate,
    bool IsCurrent,
    string FullViewUrl,
    string? StsXmlUrl,
    string? MarkdownUrl);

public sealed record SupplementaryFileRef(string Repo, string Tag, string Path);

public sealed record SpecLanding(
    string SpecId,
    string Title,
    IReadOnlyList<VersionRef> Versions,
    IReadOnlyList<SupplementaryFileRef> SupplementaryFiles,
    string? PrimaryNamespaceUri);

sealed class SpecCatalog
{
    const string BaseUrl = "https://reference.opcfoundation.org";
    const int VersionFetchConcurrency = 5;
    static readonly Regex VersionPattern = new(@"\d+\.\d+(?:\.\d+)?", RegexOptions.Compiled);
    static readonly Regex VersionFromHrefPattern = new(@"/v(\d+\.\d+(?:\.\d+)?)", RegexOptions.Compiled);
    static readonly Regex SpecIdFromHrefPattern = new(@"/specs/([^/?#]+)", RegexOptions.Compiled);

    readonly HttpClient _http;
    readonly ILogger _log;

    public SpecCatalog(HttpClient http, ILogger log)
    {
        _http = http;
        _log = log;
    }

    public async Task<IReadOnlyList<SpecRef>> DiscoverAllSpecsAsync(CancellationToken ct = default)
    {
        var html = await FetchHtmlAsync(BaseUrl + "/", ct);
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(html);

        var specs = new List<SpecRef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var li in doc.QuerySelectorAll("ul.spec-list > li"))
        {
            var specId = ExtractSpecId(li);
            if (string.IsNullOrEmpty(specId) || !seen.Add(specId)) continue;

            var version = ExtractVersion(li);
            var title = ExtractTitle(li, version);

            specs.Add(new SpecRef(specId, version, title));
        }

        // Fallback: if the structured CSS classes are missing, scan every /specs/{id} link
        if (specs.Count == 0)
        {
            foreach (var a in doc.QuerySelectorAll("a[href^='/specs/']"))
            {
                var href = a.GetAttribute("href") ?? "";
                var m = SpecIdFromHrefPattern.Match(href);
                if (!m.Success) continue;

                var specId = m.Groups[1].Value;
                if (specId.Contains('/') || !seen.Add(specId)) continue;

                var parentText = a.ParentElement?.TextContent ?? a.TextContent;
                var vm = VersionPattern.Match(parentText);
                var version = vm.Success ? vm.Value : "";
                var afterVersion = vm.Success ? parentText[(vm.Index + vm.Length)..].Trim() : "";

                specs.Add(new SpecRef(specId, version, afterVersion));
            }
        }

        _log.LogInformation("[SPEC_CATALOG] root_discovered={Count}", specs.Count);
        return specs;
    }

    public async Task<SpecLanding> GetLandingAsync(string specId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/specs/{specId}";
        var html = await FetchHtmlAsync(url, ct);
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(html);

        var title = doc.QuerySelector("header.intro-header h1")?.TextContent.Trim()
            ?? doc.QuerySelector("h1")?.TextContent.Trim()
            ?? specId;

        var (currentSts, currentMd, currentVersionFromDownloads) = ExtractDownloadUrls(doc);
        var releaseVersion = ExtractMetaField(doc, "Release Version");
        var publicationDate = ExtractMetaDate(doc, "Publication Date");
        var currentVersion = currentVersionFromDownloads ?? releaseVersion;

        var versionRows = ParseVersionHistory(doc, specId, currentVersion, publicationDate);

        // Build the VersionRef list — current version uses the URLs already
        // parsed from the page; older versions parallel-fetch their own landing.
        var versionRefs = new VersionRef?[versionRows.Count];
        using var sem = new SemaphoreSlim(VersionFetchConcurrency);
        var fetches = new List<Task>();

        for (int i = 0; i < versionRows.Count; i++)
        {
            var idx = i;
            var row = versionRows[i];
            var fullView = $"{BaseUrl}/specs/{specId}/v{row.Version}/full";

            if (row.IsCurrent)
            {
                versionRefs[idx] = new VersionRef(
                    specId, row.Version, row.PublishDate, true, fullView, currentSts, currentMd);
                continue;
            }

            fetches.Add(FetchOlderVersionAsync(idx, row.Version, row.PublishDate, fullView));
        }

        await Task.WhenAll(fetches);

        var primaryNs = ExtractPrimaryNamespace(doc);
        var supplementary = ExtractSupplementaryFiles(doc);

        _log.LogInformation(
            "[SPEC_CATALOG] spec={Spec} versions={Versions} supplementary_files={Files}",
            specId, versionRefs.Length, supplementary.Count);

        return new SpecLanding(
            specId,
            title,
            versionRefs.Select(v => v!).ToArray(),
            supplementary,
            primaryNs);

        async Task FetchOlderVersionAsync(int idx, string version, DateOnly date, string fullView)
        {
            await sem.WaitAsync(ct);
            try
            {
                var (sts, md) = await FetchVersionDownloadUrlsAsync(specId, version, ct);
                versionRefs[idx] = new VersionRef(specId, version, date, false, fullView, sts, md);
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    "[SPEC_CATALOG] spec={Spec} version={Version} error={Error}",
                    specId, version, ex.Message);
                versionRefs[idx] = new VersionRef(specId, version, date, false, fullView, null, null);
            }
            finally
            {
                sem.Release();
            }
        }
    }

    // ───────────────────────────────── helpers ──────────────────────────────

    static string ExtractSpecId(IElement li)
    {
        var anchor = li.QuerySelector("a.spec-id") ?? li.QuerySelector("a[href^='/specs/']");
        if (anchor == null) return "";

        // Prefer href — text may contain whitespace artifacts
        var href = anchor.GetAttribute("href") ?? "";
        var m = SpecIdFromHrefPattern.Match(href);
        if (m.Success && !m.Groups[1].Value.Contains('/'))
            return m.Groups[1].Value;

        var text = anchor.TextContent.Trim();
        return text.Contains(' ') ? "" : text;
    }

    static string ExtractVersion(IElement li)
    {
        var tag = li.QuerySelector(".version-tag")?.TextContent.Trim();
        if (!string.IsNullOrEmpty(tag) && VersionPattern.IsMatch(tag))
            return VersionPattern.Match(tag).Value;

        var m = VersionPattern.Match(li.TextContent);
        return m.Success ? m.Value : "";
    }

    static string ExtractTitle(IElement li, string version)
    {
        var explicitTitle = li.QuerySelector(".spec-title")?.TextContent.Trim();
        if (!string.IsNullOrEmpty(explicitTitle)) return explicitTitle;

        // Fallback: everything after the version number in the li text
        var text = Regex.Replace(li.TextContent, @"\s+", " ").Trim();
        if (string.IsNullOrEmpty(version)) return text;

        var idx = text.IndexOf(version, StringComparison.Ordinal);
        if (idx < 0) return text;

        return text[(idx + version.Length)..].Trim();
    }

    static (string? sts, string? markdown, string? version) ExtractDownloadUrls(IDocument doc)
    {
        var menu = doc.QuerySelector(".intro-download-menu") ?? doc.QuerySelector(".intro-actions");
        if (menu == null) return (null, null, null);

        string? sts = null, md = null, version = null;
        foreach (var a in menu.QuerySelectorAll("a[href]"))
        {
            var href = a.GetAttribute("href") ?? "";
            if (string.IsNullOrEmpty(href)) continue;

            var absolute = UrlHelper.Absolutize(BaseUrl, href);
            if (href.EndsWith("/sts-xml", StringComparison.OrdinalIgnoreCase))
                sts = absolute;
            else if (href.EndsWith("/markdown", StringComparison.OrdinalIgnoreCase))
                md = absolute;

            if (version == null)
            {
                var vm = VersionFromHrefPattern.Match(href);
                if (vm.Success) version = vm.Groups[1].Value;
            }
        }
        return (sts, md, version);
    }

    static string? ExtractMetaField(IDocument doc, string headerLabel)
    {
        foreach (var row in doc.QuerySelectorAll("table.meta-table tr"))
        {
            var th = row.QuerySelector("th")?.TextContent.Trim() ?? "";
            if (!th.Equals(headerLabel, StringComparison.OrdinalIgnoreCase)) continue;
            var value = row.QuerySelector("td")?.TextContent.Trim();
            if (!string.IsNullOrEmpty(value)) return value;
        }
        return null;
    }

    static DateOnly ExtractMetaDate(IDocument doc, string headerLabel)
    {
        var raw = ExtractMetaField(doc, headerLabel);
        return DateOnly.TryParse(raw, CultureInfo.InvariantCulture, out var d) ? d : default;
    }

    static List<(string Version, DateOnly PublishDate, bool IsCurrent)> ParseVersionHistory(
        IDocument doc, string specId, string? fallbackVersion, DateOnly fallbackDate)
    {
        var rows = new List<(string Version, DateOnly PublishDate, bool IsCurrent)>();
        var table = FindSectionElement(doc, "Version History", "table");

        if (table != null)
        {
            foreach (var tr in table.QuerySelectorAll("tbody > tr"))
            {
                var link = tr.QuerySelector("td a");
                if (link == null) continue;

                var version = link.TextContent.Trim();
                if (!VersionPattern.IsMatch(version))
                {
                    var href = link.GetAttribute("href") ?? "";
                    var m = VersionFromHrefPattern.Match(href);
                    if (!m.Success) continue;
                    version = m.Groups[1].Value;
                }
                else
                {
                    version = VersionPattern.Match(version).Value;
                }

                DateOnly date = default;
                var cells = tr.QuerySelectorAll("td").ToList();
                if (cells.Count >= 2)
                    DateOnly.TryParse(cells[1].TextContent.Trim(), CultureInfo.InvariantCulture, out date);

                var isCurrent = tr.ClassList.Contains("is-current")
                    || tr.QuerySelector(".current-tag") != null;

                rows.Add((version, date, isCurrent));
            }
        }

        // If we have no version history but the meta-table told us the release
        // version, emit a synthetic single entry so downstream code still works.
        if (rows.Count == 0 && !string.IsNullOrEmpty(fallbackVersion))
            rows.Add((fallbackVersion, fallbackDate, true));

        // Guarantee at most one current
        if (!rows.Any(r => r.IsCurrent) && rows.Count > 0)
            rows[0] = (rows[0].Version, rows[0].PublishDate, true);

        return rows;
    }

    static string? ExtractPrimaryNamespace(IDocument doc)
    {
        var table = FindSectionElement(doc, "Namespaces", "table");
        if (table == null) return null;

        string? firstNs = null;
        foreach (var tr in table.QuerySelectorAll("tbody > tr"))
        {
            var firstCell = tr.QuerySelector("td");
            if (firstCell == null) continue;

            var code = firstCell.QuerySelector("code");
            var nsText = (code?.TextContent ?? firstCell.TextContent).Trim();
            nsText = Regex.Replace(nsText, @"\s*primary\s*$", "", RegexOptions.IgnoreCase).Trim();
            if (string.IsNullOrEmpty(nsText)) continue;

            firstNs ??= nsText;

            var isPrimary = firstCell.QuerySelector(".primary-tag") != null
                || firstCell.TextContent.Contains("primary", StringComparison.OrdinalIgnoreCase);
            if (isPrimary) return nsText;
        }
        return firstNs;
    }

    static IReadOnlyList<SupplementaryFileRef> ExtractSupplementaryFiles(IDocument doc)
    {
        var list = FindSectionElement(doc, "Supplementary Files", "ul, ol");
        if (list == null) return Array.Empty<SupplementaryFileRef>();

        var refs = new List<SupplementaryFileRef>();
        foreach (var a in list.QuerySelectorAll("a[href]"))
        {
            var sup = ParseGitHubTreeUrl(a.GetAttribute("href") ?? "");
            if (sup != null) refs.Add(sup);
        }
        return refs;
    }

    static IElement? FindSectionElement(IDocument doc, string headingText, string childSelector)
    {
        foreach (var sec in doc.QuerySelectorAll("section"))
        {
            var h2 = sec.QuerySelector("h2");
            if (h2 == null) continue;
            if (!h2.TextContent.Trim().Equals(headingText, StringComparison.OrdinalIgnoreCase))
                continue;
            return sec.QuerySelector(childSelector);
        }
        return null;
    }

    static SupplementaryFileRef? ParseGitHubTreeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return null;

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 4) return null;

        var kind = segments[2];
        if (!kind.Equals("tree", StringComparison.OrdinalIgnoreCase) &&
            !kind.Equals("blob", StringComparison.OrdinalIgnoreCase))
            return null;

        var owner = segments[0];
        var repo = segments[1];
        var tag = Uri.UnescapeDataString(segments[3]);
        var path = segments.Length > 4
            ? string.Join('/', segments[4..].Select(Uri.UnescapeDataString))
            : "";

        return new SupplementaryFileRef($"{owner}/{repo}", tag, path);
    }

    async Task<(string? sts, string? markdown)> FetchVersionDownloadUrlsAsync(
        string specId, string version, CancellationToken ct)
    {
        var url = $"{BaseUrl}/specs/{specId}/v{version}";
        var html = await FetchHtmlAsync(url, ct);
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(html);
        var (sts, md, _) = ExtractDownloadUrls(doc);
        return (sts, md);
    }

    async Task<string> FetchHtmlAsync(string url, CancellationToken ct)
    {
        using var response = await RetryHelper.RetryAsync(() => _http.GetAsync(url, ct), _log);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }
}
