using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace GarenaOrchestrator;

public interface IStateStore
{
    string Description { get; }
    Task<OrchestratorState> LoadAsync(CancellationToken ct);
    Task SaveAsync(OrchestratorState state, CancellationToken ct);
}

public sealed class JsonFileStateStore(string path) : IStateStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path = Path.GetFullPath(path);
    public string Description => "JSON: " + _path;

    public async Task<OrchestratorState> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return new OrchestratorState();
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<OrchestratorState>(stream, Options, ct)
            ?? new OrchestratorState();
    }

    public async Task SaveAsync(OrchestratorState state, CancellationToken ct)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporary = _path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(stream, state, Options, ct);
        File.Move(temporary, _path, true);
    }
}

public sealed class TursoStateStore : IStateStore, IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _token;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public string Description => "Turso: " + _endpoint.Host;

    public TursoStateStore(string url, string token)
    {
        _endpoint = NormalizeEndpoint(url);
        _token = token.Trim();
        if (_token.Length < 10 || _token.Contains('\r') || _token.Contains('\n'))
            throw new InvalidOperationException("TURSO_TOKEN is invalid.");
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<OrchestratorState> LoadAsync(CancellationToken ct)
    {
        await ExecuteAsync("CREATE TABLE IF NOT EXISTS orchestrator_state (id INTEGER PRIMARY KEY, json TEXT NOT NULL, updated_at TEXT NOT NULL)", [], false, ct);
        using JsonDocument result = await ExecuteAsync("SELECT json FROM orchestrator_state WHERE id = 1", [], true, ct);
        JsonElement rows = result.RootElement.GetProperty("rows");
        if (rows.GetArrayLength() == 0) return new OrchestratorState();
        string? json = rows[0][0].TryGetProperty("value", out var value) ? value.GetString() : null;
        return string.IsNullOrWhiteSpace(json)
            ? new OrchestratorState()
            : JsonSerializer.Deserialize<OrchestratorState>(json, Options) ?? new OrchestratorState();
    }

    public async Task SaveAsync(OrchestratorState state, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(state, Options);
        object[] args = [TextArg(json), TextArg(DateTime.UtcNow.ToString("O"))];
        const string sql = "INSERT INTO orchestrator_state (id, json, updated_at) VALUES (1, ?, ?) " +
                           "ON CONFLICT(id) DO UPDATE SET json = excluded.json, updated_at = excluded.updated_at";
        using JsonDocument _ = await ExecuteAsync(sql, args, false, ct);
    }

    private async Task<JsonDocument> ExecuteAsync(string sql, object[] args, bool wantRows, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Content = JsonContent.Create(new
        {
            requests = new object[]
            {
                new { type = "execute", stmt = new { sql, args, want_rows = wantRows } },
                new { type = "close" }
            }
        });
        using HttpResponseMessage response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Turso returned HTTP {(int)response.StatusCode}.");
        JsonDocument envelope = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        try
        {
            JsonElement first = envelope.RootElement.GetProperty("results")[0];
            if (first.GetProperty("type").GetString() != "ok")
                throw new InvalidOperationException("Turso rejected the statement.");
            JsonElement result = first.GetProperty("response").GetProperty("result");
            return JsonDocument.Parse(result.GetRawText());
        }
        finally { envelope.Dispose(); }
    }

    private static object TextArg(string value) => new { type = "text", value };

    private static Uri NormalizeEndpoint(string url)
    {
        string normalized = url.Trim();
        if (normalized.StartsWith("libsql://", StringComparison.OrdinalIgnoreCase)) normalized = "https://" + normalized[9..];
        if (normalized.StartsWith("turso://", StringComparison.OrdinalIgnoreCase)) normalized = "https://" + normalized[8..];
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri) || uri.Scheme != "https" || string.IsNullOrEmpty(uri.Host))
            throw new InvalidOperationException("TURSO_URL must be a valid libsql://, turso:// or https:// URL.");
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/v2/pipeline");
    }

    public void Dispose() => _http.Dispose();
}
