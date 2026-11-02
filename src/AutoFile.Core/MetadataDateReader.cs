using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.QuickTime;

namespace AutoFile.Core;

/// <summary>Reads the "date taken" or "date created" stored inside a file, where the format has one.</summary>
public static partial class MetadataDateReader
{
    static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".tif", ".tiff", ".heic", ".heif", ".png", ".webp",
        ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2",
        ".mp4", ".mov", ".m4v",
    };

    static readonly HashSet<string> OfficeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".docm", ".dotx", ".xlsx", ".xlsm", ".xltx", ".pptx", ".pptm", ".potx", ".vsdx",
    };

    const int PdfHeadBytes = 1024 * 1024;
    const int PdfTailBytes = 256 * 1024;

    public static DateTime? TryRead(string path)
    {
        var extension = Path.GetExtension(path);
        try
        {
            if (MediaExtensions.Contains(extension))
            {
                return ReadMedia(path);
            }

            if (OfficeExtensions.Contains(extension))
            {
                return ReadOffice(path);
            }

            if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                return ReadPdf(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ImageProcessingException
                                       or InvalidDataException or System.Xml.XmlException or FormatException)
        {
            // Unreadable or malformed metadata just means we fall back to file timestamps.
        }

        return null;
    }

    static DateTime? ReadMedia(string path)
    {
        var directories = ImageMetadataReader.ReadMetadata(path);

        var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        if (subIfd != null
            && (subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var taken)
                || subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out taken)))
        {
            return taken;
        }

        var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        if (ifd0 != null && ifd0.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var modified))
        {
            return modified;
        }

        var movie = directories.OfType<QuickTimeMovieHeaderDirectory>().FirstOrDefault();
        if (movie != null && movie.TryGetDateTime(QuickTimeMovieHeaderDirectory.TagCreated, out var created))
        {
            // QuickTime stores UTC; an unset value comes back as 1904-01-01.
            return DateTime.SpecifyKind(created, DateTimeKind.Utc).ToLocalTime();
        }

        return null;
    }

    static DateTime? ReadOffice(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("docProps/core.xml");
        if (entry == null)
        {
            return null;
        }

        using var stream = entry.Open();
        XNamespace dcterms = "http://purl.org/dc/terms/";
        var created = XDocument.Load(stream).Descendants(dcterms + "created").FirstOrDefault()?.Value;
        return DateTimeOffset.TryParse(created, out var value) ? value.LocalDateTime : null;
    }

    static DateTime? ReadPdf(string path)
    {
        using var stream = File.OpenRead(path);
        var text = ReadLatin1(stream, 0, PdfHeadBytes);
        if (stream.Length > PdfHeadBytes)
        {
            text += ReadLatin1(stream, Math.Max(PdfHeadBytes, stream.Length - PdfTailBytes), PdfTailBytes);
        }

        var info = PdfCreationDatePattern().Match(text);
        if (!info.Success)
        {
            info = XmpCreateDatePattern().Match(text);
        }

        if (!info.Success)
        {
            return null;
        }

        var year = int.Parse(info.Groups["y"].Value);
        var month = int.Parse(info.Groups["m"].Value);
        var day = int.Parse(info.Groups["d"].Value);
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return null;
        }

        return new DateTime(year, month, day);
    }

    static string ReadLatin1(Stream stream, long offset, int count)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[count];
        var read = stream.ReadAtLeast(buffer, count, throwOnEndOfStream: false);
        return Encoding.Latin1.GetString(buffer, 0, read);
    }

    [GeneratedRegex(@"/CreationDate\s*\(\s*D:(?<y>\d{4})(?<m>\d{2})(?<d>\d{2})")]
    private static partial Regex PdfCreationDatePattern();

    [GeneratedRegex(@"<xmp:CreateDate>\s*(?<y>\d{4})-(?<m>\d{2})-(?<d>\d{2})")]
    private static partial Regex XmpCreateDatePattern();
}
