using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace TranslateFlow.Processing.Translation;

public interface ITranslator
{
    Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, string model, CancellationToken ct);
}

/// <summary>Deterministic stand-in used by tests, CI and anyone without a local model: tags each paragraph.</summary>
public sealed class PseudoTranslator : ITranslator
{
    public Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, string model, CancellationToken ct)
    {
        var tag = $"[{targetLanguage.ToUpperInvariant()}] ";
        var paragraphs = text.Split("\n\n").Select(p => tag + p);
        return Task.FromResult(string.Join("\n\n", paragraphs));
    }
}

/// <summary>Translates with a local model through Ollama's chat API.</summary>
public sealed class OllamaTranslator(HttpClient http) : ITranslator
{
    private static readonly Dictionary<string, string> Names = new()
    {
        ["tr"] = "Turkish", ["en"] = "English", ["de"] = "German",
    };

    public async Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, string model, CancellationToken ct)
    {
        var request = new ChatRequest(model, false, new ChatOptions(0),
        [
            new ChatMessage("system",
                $"You are a professional translator. Translate the user's text from {Name(sourceLanguage)} to {Name(targetLanguage)}. " +
                "Reply with the translation only. Keep paragraph breaks. Do not add notes or explanations."),
            new ChatMessage("user", text),
        ]);
        using var response = await http.PostAsJsonAsync("/api/chat", request, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ChatResponse>(ct)
            ?? throw new InvalidOperationException("Empty response from the model.");
        return body.Message.Content.Trim();
    }

    private static string Name(string code) => Names.GetValueOrDefault(code, code);

    private record ChatRequest(string Model, bool Stream, ChatOptions Options, ChatMessage[] Messages);
    private record ChatOptions([property: JsonPropertyName("temperature")] double Temperature);
    private record ChatMessage(string Role, string Content);
    private record ChatResponse(ChatMessage Message);
}
