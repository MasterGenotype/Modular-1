using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modular.Core.Steam;

/// <summary>A game as SteamGridDB knows it.</summary>
public sealed record SteamGridDbGame(int Id, string Name, int? ReleaseYear);

/// <summary>One image on SteamGridDB.</summary>
public sealed record SteamGridDbImage(int Id, string Url, string Thumb, int Width, int Height, string? Style, string Mime, string? Author);

/// <summary>
/// SteamGridDB API v2 (community artwork: grids, heroes, logos, icons). Needs a free API key from
/// steamgriddb.com/profile/preferences/api, kept in the secret store under <see cref="SecretKey"/>.
/// </summary>
public sealed class SteamGridDbClient
{
    public const string SecretKey = "steamgriddb-api-key";
    private const string BaseUrl = "https://www.steamgriddb.com/api/v2/";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly string _apiKey;

    public SteamGridDbClient(HttpClient http, string apiKey)
    {
        _http = http;
        _apiKey = apiKey;
    }

    public async Task<IReadOnlyList<SteamGridDbGame>> SearchAsync(string term, CancellationToken ct = default)
    {
        var data = await GetAsync<List<GameDto>>($"search/autocomplete/{Uri.EscapeDataString(term)}", ct);
        return (data ?? []).Select(ToGame).ToList();
    }

    /// <summary>The SteamGridDB game for a Steam app id, or null if it has none.</summary>
    public async Task<SteamGridDbGame?> GetBySteamAppIdAsync(uint appId, CancellationToken ct = default)
    {
        try
        {
            var data = await GetAsync<GameDto>($"games/steam/{appId}", ct);
            return data is null ? null : ToGame(data);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>Images for one slot, PNG/JPEG only (Steam doesn't show WebP or animations in every view).</summary>
    public async Task<IReadOnlyList<SteamGridDbImage>> GetImagesAsync(int gameId, SteamArtKind kind, CancellationToken ct = default)
    {
        var query = kind switch
        {
            SteamArtKind.Grid => $"grids/game/{gameId}?dimensions=600x900,342x482,660x930&types=static&mimes=image/png,image/jpeg",
            SteamArtKind.Wide => $"grids/game/{gameId}?dimensions=920x430,460x215&types=static&mimes=image/png,image/jpeg",
            SteamArtKind.Hero => $"heroes/game/{gameId}?types=static&mimes=image/png,image/jpeg",
            SteamArtKind.Logo => $"logos/game/{gameId}?types=static&mimes=image/png",
            SteamArtKind.Icon => $"icons/game/{gameId}?types=static&mimes=image/png",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var data = await GetAsync<List<ImageDto>>(query, ct);
        return (data ?? [])
            .Where(i => !string.IsNullOrEmpty(i.Url))
            .Select(i => new SteamGridDbImage(i.Id, i.Url!, i.Thumb ?? i.Url!, i.Width, i.Height, i.Style, i.Mime ?? "image/png", i.Author?.Name))
            .ToList();
    }

    public Task<byte[]> DownloadAsync(string url, CancellationToken ct = default) => _http.GetByteArrayAsync(url, ct);

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("SteamGridDB rejected the API key. Check it in Settings.");
        response.EnsureSuccessStatusCode();
        var envelope = await JsonSerializer.DeserializeAsync<Envelope<T>>(await response.Content.ReadAsStreamAsync(ct), Json, ct);
        if (envelope is null || !envelope.Success)
            throw new InvalidOperationException($"SteamGridDB: {string.Join("; ", envelope?.Errors ?? ["no data"])}");
        return envelope.Data;
    }

    private static SteamGridDbGame ToGame(GameDto g) => new(g.Id, g.Name ?? $"#{g.Id}",
        g.ReleaseDate is { } t and > 0 ? DateTimeOffset.FromUnixTimeSeconds(t).Year : null);

    private sealed class Envelope<T>
    {
        public bool Success { get; set; }
        public T? Data { get; set; }
        public List<string>? Errors { get; set; }
    }

    private sealed class GameDto
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        [JsonPropertyName("release_date")]
        public long? ReleaseDate { get; set; }
    }

    private sealed class ImageDto
    {
        public int Id { get; set; }
        public string? Url { get; set; }
        public string? Thumb { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string? Style { get; set; }
        public string? Mime { get; set; }
        public AuthorDto? Author { get; set; }
    }

    private sealed class AuthorDto
    {
        public string? Name { get; set; }
    }
}
