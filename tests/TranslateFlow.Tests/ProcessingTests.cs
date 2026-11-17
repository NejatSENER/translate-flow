using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using TranslateFlow.Core.Domain;
using TranslateFlow.Processing.Extraction;
using TranslateFlow.Processing.Translation;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace TranslateFlow.Tests;

public static class Samples
{
    public static byte[] Docx(params string[] paragraphs)
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body(paragraphs.Select(p => new Paragraph(new Run(new Text(p))))));
        }
        return stream.ToArray();
    }

    public static byte[] Pdf(params string[] lines)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var y = 750;
        foreach (var line in lines)
        {
            page.AddText(line, 12, new UglyToad.PdfPig.Core.PdfPoint(50, y), font);
            y -= 20;
        }
        return builder.Build();
    }
}

public class ExtractionTests
{
    private readonly TextExtractor _extractor = new();

    private Task<string> Extract(byte[] content, string name) => _extractor.ExtractAsync(new MemoryStream(content), name, CancellationToken.None);

    [Fact]
    public async Task Text_files_are_normalized()
    {
        var text = await Extract(Encoding.UTF8.GetBytes("  Birinci satır  \r\nİkinci satır\r\n\r\n\r\n\r\nYeni paragraf"), "a.txt");
        Assert.Equal("Birinci satır\nİkinci satır\n\nYeni paragraf", text);
    }

    [Fact]
    public async Task Docx_paragraphs_become_lines() =>
        Assert.Equal("Kargo yarın teslim edilecek.\nŞubeye bırakılır.",
            await Extract(Samples.Docx("Kargo yarın teslim edilecek.", "Şubeye bırakılır."), "kargo.DOCX"));

    [Fact]
    public async Task Pdf_text_is_read() =>
        Assert.Contains("Delivery tomorrow", await Extract(Samples.Pdf("Delivery tomorrow morning"), "note.pdf"));

    [Fact]
    public async Task Unknown_or_broken_files_are_rejected()
    {
        await Assert.ThrowsAsync<UnsupportedFileException>(() => Extract([1, 2, 3], "image.png"));
        await Assert.ThrowsAsync<UnsupportedFileException>(() => Extract(Encoding.UTF8.GetBytes("not a zip"), "fake.docx"));
        await Assert.ThrowsAsync<UnsupportedFileException>(() => Extract(Encoding.UTF8.GetBytes("not a pdf"), "fake.pdf"));
    }
}

public class TranslationTests
{
    private readonly TiktokenCounter _tokens = new();

    [Fact]
    public void Tokens_are_counted_with_the_o200k_encoding()
    {
        Assert.Equal(0, _tokens.Count(""));
        Assert.InRange(_tokens.Count("Hello world"), 2, 3);
        Assert.True(_tokens.Count("Merhaba dünya, bugün hava çok güzel.") > 5);
    }

    [Fact]
    public void Chunks_stay_under_the_budget_and_keep_all_text()
    {
        var paragraphs = Enumerable.Range(1, 30).Select(i => $"Paragraf {i}. Bu cümle bir deneme cümlesidir ve biraz uzundur.");
        var text = string.Join("\n\n", paragraphs);

        var chunks = new TextChunker(_tokens).Split(text, maxTokens: 60);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(_tokens.Count(c) <= 60, $"{_tokens.Count(c)} tokens: {c}"));
        Assert.Equal(text, string.Join("\n\n", chunks));
    }

    [Fact]
    public void A_paragraph_over_the_budget_is_split_on_sentences()
    {
        var paragraph = string.Join(" ", Enumerable.Range(1, 20).Select(i => $"Sentence number {i} is here."));

        var chunks = new TextChunker(_tokens).Split(paragraph, maxTokens: 30);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.EndsWith(".", c));
        Assert.Equal(paragraph, string.Join(" ", chunks));
    }

    [Fact]
    public void Cost_uses_prices_per_million_tokens()
    {
        var route = new ModelRoute { SourceLanguage = "tr", TargetLanguage = "en", Model = "m", InputPricePerMillion = 0.30m, OutputPricePerMillion = 1.20m };
        Assert.Equal(0.0009m, route.CostOf(1_000, 500));
        Assert.Equal(0m, route.CostOf(0, 0));
    }

    [Fact]
    public async Task Pseudo_translator_tags_every_paragraph() =>
        Assert.Equal("[EN] Bir\n\n[EN] İki",
            await new PseudoTranslator().TranslateAsync("Bir\n\nİki", "tr", "en", "pseudo", CancellationToken.None));
}
