using System.Text;
using System.IO;
using SRdeckPlugin.OneSeg.Models;

namespace SRdeckPlugin.OneSeg.Transport;

internal static class BmlResourceParser
{
    public static IReadOnlyList<OneSegBmlResource> Parse(byte[] content, ushort moduleId,
        string declaredContentType, string? declaredName = null)
    {
        if (content.Length == 0) return [];
        int outerHeaderEnd = FindHeaderEnd(content, 0, out int outerSeparatorLength);
        string outerHeaders = outerHeaderEnd >= 0
            ? Encoding.ASCII.GetString(content, 0, outerHeaderEnd) : "";
        string contentType = HeaderValue(outerHeaders, "Content-Type") ?? declaredContentType;
        string? boundary = ParameterValue(contentType, "boundary");
        if (!string.IsNullOrWhiteSpace(boundary))
        {
            int bodyStart = outerHeaderEnd >= 0 ? outerHeaderEnd + outerSeparatorLength : 0;
            IReadOnlyList<OneSegBmlResource> multipart = ParseMultipart(content, bodyStart,
                boundary, moduleId);
            if (multipart.Count > 0) return multipart;
        }

        string path = NormalizePath(HeaderValue(outerHeaders, "Content-Location") ??
            declaredName ?? $"module-{moduleId:X4}{ExtensionFor(contentType)}");
        int offset = outerHeaderEnd >= 0 && outerHeaders.Contains("Content-Type:",
            StringComparison.OrdinalIgnoreCase) ? outerHeaderEnd + outerSeparatorLength : 0;
        byte[] body = content.AsSpan(offset).ToArray();
        return [new(path, NormalizeContentType(contentType, path), body)];
    }

    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "index.bml";
        string value = Uri.UnescapeDataString(path.Trim().Replace('\\', '/'));
        int scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)) value = uri.AbsolutePath;
            else value = value[(scheme + 3)..];
        }
        value = value.TrimStart('/');
        var parts = new List<string>();
        foreach (string part in value.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(part);
        }
        return parts.Count == 0 ? "index.bml" : string.Join('/', parts);
    }

    private static IReadOnlyList<OneSegBmlResource> ParseMultipart(byte[] content, int start,
        string boundary, ushort moduleId)
    {
        byte[] marker = Encoding.ASCII.GetBytes("--" + boundary);
        var result = new List<OneSegBmlResource>();
        int cursor = IndexOf(content, marker, start);
        int partNumber = 0;
        while (cursor >= 0)
        {
            cursor += marker.Length;
            if (cursor + 1 < content.Length && content[cursor] == '-' && content[cursor + 1] == '-') break;
            cursor = SkipLineBreak(content, cursor);
            int headerEnd = FindHeaderEnd(content, cursor, out int separatorLength);
            if (headerEnd < 0) break;
            string headers = Encoding.ASCII.GetString(content, cursor, headerEnd - cursor);
            int bodyStart = headerEnd + separatorLength;
            int next = IndexOf(content, marker, bodyStart);
            if (next < 0) break;
            int bodyEnd = next;
            while (bodyEnd > bodyStart && content[bodyEnd - 1] is (byte)'\r' or (byte)'\n') bodyEnd--;
            string type = HeaderValue(headers, "Content-Type") ?? "application/octet-stream";
            string location = HeaderValue(headers, "Content-Location") ??
                HeaderValue(headers, "Content-ID")?.Trim('<','>') ??
                $"module-{moduleId:X4}/part-{partNumber}{ExtensionFor(type)}";
            string path = NormalizePath(location);
            result.Add(new(path, NormalizeContentType(type, path),
                content.AsSpan(bodyStart, Math.Max(0, bodyEnd - bodyStart)).ToArray()));
            partNumber++;
            cursor = next;
        }
        return result;
    }

    private static int FindHeaderEnd(byte[] content, int start, out int separatorLength)
    {
        int crlf = IndexOf(content, "\r\n\r\n"u8, start);
        int lf = IndexOf(content, "\n\n"u8, start);
        if (crlf >= 0 && (lf < 0 || crlf <= lf)) { separatorLength = 4; return crlf; }
        if (lf >= 0) { separatorLength = 2; return lf; }
        separatorLength = 0;
        return -1;
    }

    private static int SkipLineBreak(byte[] content, int offset)
    {
        if (offset < content.Length && content[offset] == '\r') offset++;
        if (offset < content.Length && content[offset] == '\n') offset++;
        return offset;
    }

    private static int IndexOf(byte[] source, ReadOnlySpan<byte> value, int start)
    {
        if (value.IsEmpty) return start;
        start = Math.Max(0, start);
        if (start > source.Length) return -1;
        int offset = source.AsSpan(start).IndexOf(value);
        return offset < 0 ? -1 : start + offset;
    }

    private static string? HeaderValue(string headers, string name)
    {
        foreach (string line in headers.Replace("\r", "").Split('\n'))
        {
            int separator = line.IndexOf(':');
            if (separator <= 0 || !line[..separator].Trim().Equals(name,
                StringComparison.OrdinalIgnoreCase)) continue;
            return line[(separator + 1)..].Trim();
        }
        return null;
    }

    private static string? ParameterValue(string value, string name)
    {
        foreach (string part in value.Split(';').Skip(1))
        {
            int separator = part.IndexOf('=');
            if (separator <= 0 || !part[..separator].Trim().Equals(name,
                StringComparison.OrdinalIgnoreCase)) continue;
            return part[(separator + 1)..].Trim().Trim('"', '\'');
        }
        return null;
    }

    private static string NormalizeContentType(string type, string path)
    {
        string value = type.Split(';')[0].Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(value) && value != "application/octet-stream") return value;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".bml" => "application/bml+xml", ".xml" => "application/xml",
            ".html" or ".htm" => "text/html", ".css" => "text/css",
            ".js" => "text/javascript", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif", ".mng" => "video/x-mng", ".wav" => "audio/wav",
            _ => "application/octet-stream"
        };
    }

    private static string ExtensionFor(string contentType) => contentType.Split(';')[0].Trim().ToLowerInvariant() switch
    {
        "application/bml+xml" => ".bml", "application/xhtml+xml" => ".xhtml",
        "text/html" => ".html", "text/css" => ".css", "text/javascript" => ".js",
        "image/png" => ".png", "image/jpeg" => ".jpg", "image/gif" => ".gif",
        _ => ".bin"
    };
}
