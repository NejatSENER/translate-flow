using System.Text;

namespace TranslateFlow.Core.Storage;

/// <summary>Where uploaded files and results live. Local disk here; blob storage in production.</summary>
public interface IFileStore
{
    Task SaveAsync(string key, Stream content, CancellationToken ct);
    Task<Stream> OpenReadAsync(string key, CancellationToken ct);
}

public static class FileStoreExtensions
{
    public static Task SaveTextAsync(this IFileStore store, string key, string text, CancellationToken ct) =>
        store.SaveAsync(key, new MemoryStream(Encoding.UTF8.GetBytes(text)), ct);

    public static async Task<string> ReadTextAsync(this IFileStore store, string key, CancellationToken ct)
    {
        await using var stream = await store.OpenReadAsync(key, ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct);
    }
}

public sealed class LocalFileStore(string root) : IFileStore
{
    private string PathOf(string key)
    {
        var full = Path.GetFullPath(Path.Combine(root, key));
        if (!full.StartsWith(Path.GetFullPath(root), StringComparison.Ordinal))
            throw new ArgumentException("Key escapes the storage root.", nameof(key));
        return full;
    }

    public async Task SaveAsync(string key, Stream content, CancellationToken ct)
    {
        var path = PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        await content.CopyToAsync(file, ct);
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct) =>
        Task.FromResult<Stream>(File.OpenRead(PathOf(key)));
}
