using System.Text;
using Microsoft.ML.Tokenizers;

namespace TranslateFlow.Processing.Translation;

/// <summary>Counts tokens the way the cost model counts them, not words or characters.</summary>
public interface ITokenCounter
{
    int Count(string text);
}

public sealed class TiktokenCounter : ITokenCounter
{
    private static readonly Lazy<Tokenizer> Tokenizer = new(() => TiktokenTokenizer.CreateForEncoding("o200k_base"));

    public int Count(string text) => text.Length == 0 ? 0 : Tokenizer.Value.CountTokens(text);
}

/// <summary>
/// Splits text into chunks below a token budget so each model call stays inside its context
/// window. Splits on paragraphs, then on sentences, and never inside a sentence unless it alone
/// is over the budget.
/// </summary>
public sealed class TextChunker(ITokenCounter tokens)
{
    public IReadOnlyList<string> Split(string text, int maxTokens)
    {
        if (maxTokens < 1) throw new ArgumentOutOfRangeException(nameof(maxTokens));
        var chunks = new List<string>();
        var current = new StringBuilder();
        var currentTokens = 0;

        void Flush()
        {
            if (current.Length == 0) return;
            chunks.Add(current.ToString());
            current.Clear();
            currentTokens = 0;
        }

        void Add(string piece, string separator)
        {
            var pieceTokens = tokens.Count(piece);
            if (currentTokens > 0 && currentTokens + pieceTokens > maxTokens) Flush();
            if (current.Length > 0) current.Append(separator);
            current.Append(piece);
            currentTokens += pieceTokens;
        }

        foreach (var paragraph in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (tokens.Count(paragraph) <= maxTokens)
            {
                Add(paragraph, "\n\n");
                continue;
            }
            Flush();
            foreach (var sentence in SplitSentences(paragraph)) Add(sentence, " ");
            Flush();
        }
        Flush();
        return chunks;
    }

    private static IEnumerable<string> SplitSentences(string paragraph)
    {
        var start = 0;
        for (var i = 0; i < paragraph.Length; i++)
        {
            var end = paragraph[i] is '.' or '!' or '?' or '\n' && (i + 1 == paragraph.Length || char.IsWhiteSpace(paragraph[i + 1]));
            if (!end) continue;
            var sentence = paragraph[start..(i + 1)].Trim();
            if (sentence.Length > 0) yield return sentence;
            start = i + 1;
        }
        var rest = paragraph[start..].Trim();
        if (rest.Length > 0) yield return rest;
    }
}
