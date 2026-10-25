using System.Net.Http;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;

namespace VideoConcat;

/// <summary>Lists vision-capable models from the local Ollama and llama.cpp servers for the Auto Caption model selector.</summary>
public static class CaptionModels
{
    public const string OllamaUrl = "http://127.0.0.1:11434";
    public const string LlamaCppUrl = "http://127.0.0.1:8080";

    /// <summary>Dropdown values look like 'ollama/name' or 'llamacpp/name' (backend is split on the first '/').</summary>
    public record Selection(string Backend, string Model, string ServerUrl);

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);
    private static readonly object CacheLock = new();
    private static List<string> CachedValues = [];
    private static DateTimeOffset CacheTime = DateTimeOffset.MinValue;

    public static Selection Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "None")
        {
            return null;
        }
        int slash = value.IndexOf('/');
        if (slash <= 0)
        {
            return null;
        }
        string backend = value[..slash];
        string model = value[(slash + 1)..];
        return backend switch
        {
            "ollama" => new Selection("ollama", model, OllamaUrl),
            "llamacpp" => new Selection("llamacpp", model, LlamaCppUrl),
            _ => null
        };
    }

    public static List<string> List()
    {
        lock (CacheLock)
        {
            if (DateTimeOffset.Now - CacheTime < CacheLifetime)
            {
                return CachedValues;
            }
        }
        List<string> values = ["None"];
        values.AddRange(Query(ListOllama));
        values.AddRange(Query(ListLlamaCpp));
        lock (CacheLock)
        {
            CachedValues = values;
            CacheTime = DateTimeOffset.Now;
        }
        return values;
    }

    /// <summary>Runs a listing with a short timeout so an offline server never stalls the UI.</summary>
    private static List<string> Query(Func<CancellationToken, Task<List<string>>> lister)
    {
        try
        {
            using CancellationTokenSource cancel = new(TimeSpan.FromSeconds(2));
            return lister(cancel.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logs.Debug($"[VideoConcat] Caption model listing failed: {ex.Message}");
            return [];
        }
    }

    private static async Task<JObject> GetJson(string url, CancellationToken cancel)
    {
        using HttpResponseMessage response = await Utilities.UtilWebClient.GetAsync(url, cancel);
        response.EnsureSuccessStatusCode();
        return JObject.Parse(await response.Content.ReadAsStringAsync(cancel));
    }

    private static async Task<List<string>> ListOllama(CancellationToken cancel)
    {
        JObject json = await GetJson($"{OllamaUrl}/api/tags", cancel);
        return [.. (json["models"] as JArray ?? [])
            .Where(m => m["capabilities"] is not JArray caps || caps.Any(c => $"{c}" == "vision"))
            .Select(m => $"{m["name"]}")
            .Order()
            .Select(name => $"ollama/{name}///Ollama: {name}")];
    }

    private static async Task<List<string>> ListLlamaCpp(CancellationToken cancel)
    {
        JObject json = await GetJson($"{LlamaCppUrl}/v1/models", cancel);
        // Router mode exposes each model's launch args: only those with an mmproj can see images.
        return [.. (json["data"] as JArray ?? [])
            .Where(m => m["status"]?["args"] is not JArray args || args.Any(a => $"{a}" == "--mmproj"))
            .Select(m => $"{m["id"]}")
            .Order()
            .Select(id => $"llamacpp/{id}///llama.cpp: {id}")];
    }
}
