using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace PaddleOcrNet.Office.Internal;

internal sealed record OfficeRelationship(string Id, string Type, string? Target);

/// <summary>Read-only, bounded OPC package; no ZIP entries are extracted to disk.</summary>
internal sealed class OfficePackage : IDisposable
{
    private readonly MemoryStream _buffer;
    private readonly ZipArchive _archive;
    private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<OfficeRelationship>> _relationships = new(StringComparer.Ordinal);
    private readonly CancellationToken _ct;
    private readonly HashSet<string> _warnings = new(StringComparer.Ordinal);
    private int _blocks, _sections;
    private long _readBytes, _textCharacters;
    internal OfficeDocumentOptions Options { get; }
    internal IReadOnlyList<string> Warnings => _warnings.Order(StringComparer.Ordinal).ToArray();

    internal OfficePackage(Stream input, OfficeDocumentOptions options, CancellationToken ct)
        : this(Buffer(input, options, ct), options, ct, true) { }

    private OfficePackage(MemoryStream buffer, OfficeDocumentOptions options, CancellationToken ct, bool owned)
    {
        _buffer = buffer;
        Options = options;
        _ct = ct;
        ZipArchive? archive = null;
        try
        {
            archive = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > options.MaxEntries) throw new InvalidDataException("Office package entry limit exceeded.");
            long expanded = 0;
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.Length > options.MaxPartBytes || entry.Length > options.MaxExpandedBytes - expanded)
                    throw new InvalidDataException("Office package uncompressed size limit exceeded.");
                expanded += entry.Length;
                if (entry.FullName.EndsWith('/')) continue;
                var path = Normalize(entry.FullName, "");
                if (path != entry.FullName || !_entries.TryAdd(path, entry))
                    throw new InvalidDataException("Noncanonical or duplicate Office package part.");
            }
            _archive = archive;
        }
        catch { archive?.Dispose(); buffer.Dispose(); throw; }
    }

    internal static async Task<OfficePackage> OpenAsync(Stream input, OfficeDocumentOptions options, CancellationToken ct)
    {
        ValidateInput(input, options, ct);
        var buffer = new MemoryStream();
        try
        {
            var chunk = new byte[81920];
            int count;
            while ((count = await input.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false)) != 0)
            {
                if (count > options.MaxInputBytes - buffer.Length) throw new InvalidDataException("Office input byte limit exceeded.");
                buffer.Write(chunk, 0, count);
            }
            buffer.Position = 0;
            return new OfficePackage(buffer, options, ct, true);
        }
        catch { buffer.Dispose(); throw; }
    }

    private static void ValidateInput(Stream input, OfficeDocumentOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        ct.ThrowIfCancellationRequested();
        if (!input.CanRead) throw new ArgumentException("Office input stream must be readable.", nameof(input));
        if (input.CanSeek && input.Length - input.Position > options.MaxInputBytes)
            throw new InvalidDataException("Office input byte limit exceeded.");
    }

    private static MemoryStream Buffer(Stream input, OfficeDocumentOptions options, CancellationToken ct)
    {
        ValidateInput(input, options, ct);
        var buffer = new MemoryStream();
        try
        {
            var chunk = new byte[81920];
            int count;
            while ((count = input.Read(chunk, 0, chunk.Length)) != 0)
            {
                ct.ThrowIfCancellationRequested();
                if (count > options.MaxInputBytes - buffer.Length) throw new InvalidDataException("Office input byte limit exceeded.");
                buffer.Write(chunk, 0, count);
            }
            buffer.Position = 0;
            return buffer;
        }
        catch { buffer.Dispose(); throw; }
    }

    internal byte[] ReadBytes(string part)
    {
        _ct.ThrowIfCancellationRequested();
        if (!_entries.TryGetValue(part, out var entry)) throw new InvalidDataException($"Missing Office part: {part}");
        if (entry.Length > Options.MaxExpandedBytes - _readBytes) throw new InvalidDataException("Office cumulative part-read limit exceeded.");
        _readBytes += entry.Length;
        using var stream = entry.Open();
        using var output = new MemoryStream();
        var chunk = new byte[81920];
        int count;
        while ((count = stream.Read(chunk, 0, chunk.Length)) != 0)
        {
            _ct.ThrowIfCancellationRequested();
            if (count > Options.MaxPartBytes - output.Length || count > entry.Length - output.Length)
                throw new InvalidDataException("Office part exceeds its declared or configured size.");
            output.Write(chunk, 0, count);
        }
        if (output.Length != entry.Length) throw new InvalidDataException("Truncated Office part.");
        return output.ToArray();
    }

    internal XDocument ReadXml(string part)
    {
        using var input = new MemoryStream(ReadBytes(part), writable: false);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = Options.MaxXmlCharacters,
            IgnoreComments = true,
        };
        try
        {
            // Validate depth and observe cancellation while scanning, before format readers recurse.
            using (var scan = XmlReader.Create(input, settings))
                while (scan.Read())
                {
                    _ct.ThrowIfCancellationRequested();
                    if (scan.Depth > 128) throw new InvalidDataException("Office XML nesting limit exceeded.");
                }
            input.Position = 0;
            using var reader = XmlReader.Create(input, settings);
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            _ct.ThrowIfCancellationRequested();
            return document;
        }
        catch (XmlException ex) { throw new InvalidDataException($"Invalid XML in Office part: {part}", ex); }
    }

    internal IReadOnlyList<OfficeRelationship> Relationships(string sourcePart)
    {
        if (_relationships.TryGetValue(sourcePart, out var cached)) return cached;
        int slash = sourcePart.LastIndexOf('/');
        var directory = slash < 0 ? "" : sourcePart[..(slash + 1)];
        var name = slash < 0 ? sourcePart : sourcePart[(slash + 1)..];
        var relPart = sourcePart.Length == 0 ? "_rels/.rels" : directory + "_rels/" + name + ".rels";
        if (!_entries.ContainsKey(relPart)) return Array.Empty<OfficeRelationship>();
        var xml = ReadXml(relPart);
        XNamespace ns = "http://schemas.openxmlformats.org/package/2006/relationships";
        if (xml.Root?.Name != ns + "Relationships") throw new InvalidDataException("Invalid Office relationship document.");
        var result = new List<OfficeRelationship>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rel in xml.Root.Elements(ns + "Relationship"))
        {
            _ct.ThrowIfCancellationRequested();
            string id = (string?)rel.Attribute("Id") ?? "";
            string type = (string?)rel.Attribute("Type") ?? "";
            string target = (string?)rel.Attribute("Target") ?? "";
            if (id.Length == 0 || type.Length == 0 || target.Length == 0 || !ids.Add(id))
                throw new InvalidDataException("Missing or duplicate Office relationship attributes.");
            string? mode = (string?)rel.Attribute("TargetMode");
            if (mode is not null and not "Internal" and not "External") throw new InvalidDataException("Invalid Office relationship target mode.");
            if (mode == "External")
            {
                Warn("External relationships were not fetched.");
                result.Add(new(id, type, null));
            }
            else result.Add(new(id, type, Normalize(target, directory)));
        }
        _relationships.Add(sourcePart, result);
        return result;
    }

    internal string? ResolveRelationship(string sourcePart, string relationshipId)
    {
        var matches = Relationships(sourcePart).Where(r => r.Id == relationshipId).ToArray();
        if (matches.Length != 1) throw new InvalidDataException($"Missing Office relationship: {relationshipId}");
        if (matches[0].Target is { } target && !_entries.ContainsKey(target)) throw new InvalidDataException($"Missing Office relationship target: {target}");
        return matches[0].Target;
    }

    internal (OfficeDocumentFormat Format, string MainPart) Identify()
    {
        var roots = Relationships("").Where(r => r.Type is
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" or
            "http://purl.oclc.org/ooxml/officeDocument/relationships/officeDocument").ToArray();
        if (roots.Length != 1 || roots[0].Target is not { } main)
            throw new InvalidDataException("A single internal Office document relationship is required.");
        var types = ReadXml("[Content_Types].xml");
        XNamespace ns = "http://schemas.openxmlformats.org/package/2006/content-types";
        if (types.Root?.Name != ns + "Types") throw new InvalidDataException("Invalid Office content types.");
        var declarations = types.Root.Elements(ns + "Override")
            .Where(e => (string?)e.Attribute("PartName") == "/" + main).ToArray();
        if (declarations.Length != 1) throw new InvalidDataException("Missing or ambiguous main Office content type.");
        var format = (string?)declarations[0].Attribute("ContentType") switch
        {
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml" => OfficeDocumentFormat.Docx,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml" => OfficeDocumentFormat.Xlsx,
            "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml" => OfficeDocumentFormat.Pptx,
            _ => throw new NotSupportedException("Only non-macro DOCX, XLSX and PPTX packages are supported; encrypted, legacy and template formats are not supported."),
        };
        return (format, main);
    }

    private static string Normalize(string target, string directory)
    {
        target = Uri.UnescapeDataString(target);
        if (target.IndexOfAny(['\\', ':', '?', '#', '\0']) >= 0 || target.Any(char.IsControl) || target.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid Office package part path.");
        var parts = new List<string>();
        foreach (var part in (target.StartsWith('/') ? target[1..] : directory + target).Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) throw new InvalidDataException("Office relationship escapes its package.");
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }
        if (parts.Count == 0) throw new InvalidDataException("Empty Office package part path.");
        return string.Join('/', parts);
    }

    internal void AddBlock()
    {
        _ct.ThrowIfCancellationRequested();
        if (++_blocks > Options.MaxBlocks) throw new InvalidDataException("Office block/cell limit exceeded.");
    }
    internal void AddSection()
    {
        _ct.ThrowIfCancellationRequested();
        if (++_sections > Options.MaxSections) throw new InvalidDataException("Office section limit exceeded.");
    }
    internal void AddText(string text)
    {
        _ct.ThrowIfCancellationRequested();
        if (text.Length > Options.MaxTextCharacters - _textCharacters) throw new InvalidDataException("Office text character limit exceeded.");
        _textCharacters += text.Length;
    }
    internal void Warn(string warning)
    {
        if (_warnings.Count < 256) _warnings.Add(warning);
    }
    public void Dispose() { _archive.Dispose(); _buffer.Dispose(); }
}
