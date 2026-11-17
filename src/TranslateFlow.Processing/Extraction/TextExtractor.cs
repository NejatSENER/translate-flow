using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace TranslateFlow.Processing.Extraction;

public class UnsupportedFileException(string message) : Exception(message);

/// <summary>Turns an uploaded file into plain text, one paragraph per line.</summary>
public interface ITextExtractor
{
    Task<string> ExtractAsync(Stream content, string fileName, CancellationToken ct);
}

public sealed class TextExtractor : ITextExtractor
{
    public static readonly IReadOnlySet<string> SupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt", ".docx", ".pdf" };

    public async Task<string> ExtractAsync(Stream content, string fileName, CancellationToken ct)
    {
        // OpenXml and PdfPig need a seekable stream.
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        var text = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".txt" => FromText(buffer),
            ".docx" => FromDocx(buffer),
            ".pdf" => FromPdf(buffer),
            var ext => throw new UnsupportedFileException($"Files of type '{ext}' are not supported."),
        };
        return Normalize(text);
    }

    private static string FromText(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string FromDocx(Stream stream)
    {
        try
        {
            using var doc = WordprocessingDocument.Open(stream, false);
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body is null) return "";
            return string.Join("\n", body.Descendants<Paragraph>().Select(p => p.InnerText));
        }
        catch (Exception ex) when (ex is DocumentFormat.OpenXml.Packaging.OpenXmlPackageException or InvalidDataException or System.IO.FileFormatException)
        {
            throw new UnsupportedFileException("The file is not a valid .docx document.");
        }
    }

    private static string FromPdf(Stream stream)
    {
        try
        {
            using var pdf = PdfDocument.Open(stream);
            return string.Join("\n\n", pdf.GetPages().Select(p => ContentOrderTextExtractor.GetText(p)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not UnsupportedFileException)
        {
            throw new UnsupportedFileException("The file is not a readable PDF document.");
        }
    }

    /// <summary>Unifies line endings, trims lines and collapses runs of blank lines.</summary>
    private static string Normalize(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.Trim());
        var sb = new StringBuilder();
        var blank = 0;
        foreach (var line in lines)
        {
            if (line.Length == 0) { blank++; continue; }
            if (sb.Length > 0) sb.Append(blank > 0 ? "\n\n" : "\n");
            sb.Append(line);
            blank = 0;
        }
        return sb.ToString();
    }
}
