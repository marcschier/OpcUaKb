// ═══════════════════════════════════════════════════════════════════════
// UrlHelper — small utility for resolving anchor hrefs against a base URL.
//
// Why this lives in its own file: the obvious one-liner
//
//     if (Uri.TryCreate(href, UriKind.Absolute, out _)) return href;
//
// is broken on Linux. There, `Uri.TryCreate("/specs/foo", Absolute, out u)`
// returns true with `u.Scheme == "file"` and `u.AbsolutePath == "/specs/foo"`.
// On Windows the same call returns false. So a SpecCatalog instance running
// inside the Container Apps Job (Linux) would treat root-relative hrefs as
// already-absolute and pass them straight through to HttpClient, which then
// throws "An invalid request URI was provided".
//
// We therefore distinguish absolute vs. relative URLs by checking explicitly
// for an http(s):// scheme prefix instead of relying on Uri parsing.
// ═══════════════════════════════════════════════════════════════════════

internal static class UrlHelper
{
    public static string Absolutize(string baseUrl, string href)
    {
        if (string.IsNullOrEmpty(href)) return href;

        // Already absolute (any scheme). Anything starting with "scheme:" is
        // returned as-is. We also explicitly accept protocol-relative URLs.
        if (HasScheme(href)) return href;
        if (href.StartsWith("//", StringComparison.Ordinal))
        {
            // Protocol-relative — inherit the base URL's scheme.
            var schemeEnd = baseUrl.IndexOf("://", StringComparison.Ordinal);
            var scheme = schemeEnd > 0 ? baseUrl[..schemeEnd] : "https";
            return $"{scheme}:{href}";
        }

        var trimmedBase = baseUrl.TrimEnd('/');
        if (href.StartsWith('/')) return trimmedBase + href;
        return $"{trimmedBase}/{href}";
    }

    static bool HasScheme(string href)
    {
        // A URI scheme per RFC 3986: ALPHA *( ALPHA / DIGIT / "+" / "-" / "." )
        // followed by ":". We only need to distinguish "http(s)://..." and
        // similar from root-relative paths like "/foo", so a small loop is
        // sufficient and avoids platform-specific Uri parsing surprises.
        for (int i = 0; i < href.Length; i++)
        {
            var c = href[i];
            if (i == 0)
            {
                if (!char.IsAsciiLetter(c)) return false;
                continue;
            }
            if (c == ':') return i > 0;
            if (!char.IsAsciiLetterOrDigit(c) && c != '+' && c != '-' && c != '.')
                return false;
        }
        return false;
    }
}
