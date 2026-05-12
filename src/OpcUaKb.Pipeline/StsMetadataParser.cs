using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

// ═══════════════════════════════════════════════════════════════════════
// NISO STS XML metadata parser — extracts per-spec metadata and the
// section-number → section-slug map from the IEC standard publication
// format used by reference.opcfoundation.org STS-XML downloads.
// Pure metadata extraction; no chunking.
// ═══════════════════════════════════════════════════════════════════════

public sealed record StsMetadata(
    string SpecId,
    string SpecTitle,
    string SpecVersion,
    DateOnly? PublicationDate,
    string? NamespaceUri,
    string? NamespaceVersion,
    bool? IsPrimaryNamespace,
    string? GitHubTag,
    IReadOnlyDictionary<string, string> SectionSlugByNumber);

sealed class StsMetadataParser
{
    readonly ILogger _log;

    public StsMetadataParser(ILogger log) { _log = log; }

    public StsMetadata Parse(string xml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (XmlException)
        {
            // STS XML declares an external DTD; retry with DTD processing disabled.
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
            };
            using var stringReader = new StringReader(xml);
            using var reader = XmlReader.Create(stringReader, settings);
            doc = XDocument.Load(reader);
        }

        var standard = doc.Root
            ?? throw new InvalidOperationException("STS XML has no root element");

        var isoMeta = standard.Element("front")?.Element("iso-meta")
            ?? throw new InvalidOperationException("STS XML missing <front><iso-meta>");

        var specIdRaw = isoMeta.Element("std-ident")?.Value?.Trim() ?? "";
        var specId = specIdRaw.Replace(' ', '-');

        var specTitle = isoMeta.Element("title-wrap")?.Element("full")?.Value?.Trim() ?? "";
        var specVersion = isoMeta.Element("release-version")?.Value?.Trim() ?? "";

        DateOnly? pubDate = null;
        var pubDateEl = isoMeta.Element("pub-date");
        if (pubDateEl?.Attribute("iso-8601-date")?.Value is string iso
            && DateOnly.TryParse(iso, out var d))
        {
            pubDate = d;
        }

        var customMetas = isoMeta
            .Elements("custom-meta-group")
            .Elements("custom-meta")
            .Select(e => (
                Name: e.Element("meta-name")?.Value?.Trim() ?? "",
                Value: e.Element("meta-value")?.Value?.Trim() ?? ""))
            .Where(t => !string.IsNullOrEmpty(t.Name))
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);

        var namespaceUri = customMetas.GetValueOrDefault("opc:nodeSet.namespace");
        var namespaceVersion = customMetas.GetValueOrDefault("opc:nodeSet.version");
        bool? isPrimary = bool.TryParse(customMetas.GetValueOrDefault("opc:nodeSet.isPrimary"), out var b)
            ? b
            : null;
        var gitHubTag = customMetas.GetValueOrDefault("opc:gitHubTag");

        var slugMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var body = standard.Element("body");
        if (body != null)
        {
            foreach (var sec in body.DescendantsAndSelf("sec"))
            {
                var id = sec.Attribute("id")?.Value;
                var label = sec.Element("label")?.Value?.Trim();
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(label))
                {
                    slugMap.TryAdd(label, id);
                }
            }
        }

        _log.LogInformation(
            "[STS] Parsed SpecId={SpecId} Version={Version} PubDate={PubDate} Namespace={Namespace} Sections={Count}",
            specId, specVersion, pubDate, namespaceUri, slugMap.Count);

        return new StsMetadata(
            specId,
            specTitle,
            specVersion,
            pubDate,
            namespaceUri,
            namespaceVersion,
            isPrimary,
            gitHubTag,
            slugMap);
    }
}
