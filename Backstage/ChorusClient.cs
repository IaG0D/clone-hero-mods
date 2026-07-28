using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Backstage;

/// <summary>Um chart no Chorus Encore. Campos do POST /search do api.enchor.us,
/// contrato lido do codigo aberto do Bridge (Geomitron/Bridge).</summary>
public sealed class Chart
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("artist")] public string? Artist { get; set; }
    [JsonPropertyName("album")] public string? Album { get; set; }
    [JsonPropertyName("genre")] public string? Genre { get; set; }
    [JsonPropertyName("year")] public string? Year { get; set; }
    [JsonPropertyName("charter")] public string? Charter { get; set; }
    [JsonPropertyName("chartId")] public int ChartId { get; set; }
    [JsonPropertyName("md5")] public string Md5 { get; set; } = "";
    [JsonPropertyName("song_length")] public long? SongLengthMs { get; set; }
    [JsonPropertyName("hasVideoBackground")] public bool HasVideoBackground { get; set; }
}

public sealed class SearchResult
{
    [JsonPropertyName("found")] public int Found { get; set; }
    [JsonPropertyName("out_of")] public int OutOf { get; set; }
    [JsonPropertyName("page")] public int Page { get; set; }
    [JsonPropertyName("data")] public List<Chart> Data { get; set; } = new();
}

/// <summary>
/// Cliente do Chorus Encore. Etiqueta obrigatoria (o servico vive de doacao):
/// User-Agent identificando o mod, cache local de resultados, uma requisicao por busca.
/// Falar com o Geo no Discord do Chorus ANTES de qualquer release publico.
/// </summary>
public sealed class ChorusClient : IDisposable
{
    const string Api = "https://api.enchor.us";
    const string Files = "https://files.enchor.us";
    public const string UserAgent = "Backstage/0.1 (Clone Hero mod; by IaG0D)";

    readonly HttpClient _http;
    readonly Dictionary<string, SearchResult> _cache = new(); // ponytail: cache em memoria; disco so se a sessao real pedir

    public ChorusClient()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    public async Task<SearchResult> SearchAsync(string query, int page = 1, CancellationToken ct = default)
    {
        var key = $"{page}|{query}";
        if (_cache.TryGetValue(key, out var cached)) return cached;

        // Corpo identico ao do Bridge; "source" identifica a origem pro servidor.
        // JSON manual (sem System.Net.Http.Json): o runtime do BepInEx nao traz esse pacote.
        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["search"] = query,
            ["per_page"] = 25,
            ["page"] = page,
            ["instrument"] = null,
            ["difficulty"] = null,
            ["drumType"] = null,
            ["drumsReviewed"] = true,
            ["sort"] = null,
            ["source"] = "bridge",
        });

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync($"{Api}/search", content, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<SearchResult>(json)
                     ?? throw new InvalidOperationException("resposta vazia do /search");

        _cache[key] = result;
        return result;
    }

    /// <summary>Baixa o .sng (arquivo unico, o CH v1.1 le nativo — sem extracao).
    /// Escreve em .tmp e renomeia no fim: o scanner do CH nunca ve arquivo pela metade.</summary>
    public async Task<string> DownloadSngAsync(Chart chart, string destFolder,
                                               IProgress<(long done, long total)>? progress = null,
                                               CancellationToken ct = default)
    {
        Directory.CreateDirectory(destFolder);
        var name = Sanitize($"{chart.Artist} - {chart.Name} ({chart.Charter})");
        var finalPath = Path.Combine(destFolder, name + ".sng");
        var tmpPath = finalPath + ".tmp";

        var url = $"{Files}/{chart.Md5}{(chart.HasVideoBackground ? "_novideo" : "")}.sng";
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? -1;

        await using (var src = await response.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(tmpPath))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                progress?.Report((done, total));
            }
        }

        File.Move(tmpPath, finalPath, overwrite: true);
        return finalPath;
    }

    static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim();
    }

    public void Dispose() => _http.Dispose();
}
