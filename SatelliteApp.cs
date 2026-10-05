using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace GarenaOrchestrator;

public static class SatelliteApp
{
    public static async Task RunAsync(string[] args)
    {
        var options = SatelliteOptions.Parse(args);
        var health = new SatelliteHealth();
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };

        WebApplication? healthApp = await StartHealthServerAsync(health, shutdown.Token);
        using var directHandler = new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(15) };
        using var directClient = new HttpClient(directHandler)
        {
            BaseAddress = new Uri(options.MasterUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(25)
        };
        directClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.AgentToken);
        directClient.DefaultRequestHeaders.UserAgent.ParseAdd("GarenaOrchestrator-Satellite/1.0");

        ProxyLease? assignedProxy = null;
        DateTime nextProxyCheck = DateTime.MinValue;
        int failures = 0;
        Console.WriteLine($"Satellite '{options.Name}' connecting to {options.MasterUrl}; slots={options.MaxSlots}");

        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                if (assignedProxy is not null && DateTime.UtcNow >= nextProxyCheck)
                {
                    await TestProxyAsync(options.MasterUrl, assignedProxy, health, shutdown.Token);
                    nextProxyCheck = DateTime.UtcNow.AddSeconds(60);
                }

                var payload = new AgentHeartbeatRequest(options.Name, "1.0.0", options.MaxSlots, 0,
                    health.ProxyStatus, health.ProxyEgressIp, health.ProxyLatencyMs);
                using HttpResponseMessage response = await directClient.PostAsJsonAsync("api/agent/heartbeat", payload, shutdown.Token);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    throw new InvalidOperationException("Agent token was rejected. Create a new agent token in the master admin page.");
                response.EnsureSuccessStatusCode();
                AgentHeartbeatResponse heartbeat = await response.Content.ReadFromJsonAsync<AgentHeartbeatResponse>(cancellationToken: shutdown.Token)
                    ?? throw new InvalidOperationException("Master returned an empty heartbeat response.");

                health.Connected = true;
                health.LastError = null;
                health.LastHeartbeatUtc = DateTime.UtcNow;
                if (health.ObservedIp != heartbeat.ObservedIp)
                    Console.WriteLine($"Direct VPS egress IP observed by master: {heartbeat.ObservedIp}");
                health.ObservedIp = heartbeat.ObservedIp;
                failures = 0;

                if (ProxyChanged(assignedProxy, heartbeat.AssignedProxy))
                {
                    assignedProxy = heartbeat.AssignedProxy;
                    nextProxyCheck = DateTime.MinValue;
                    health.ProxyEgressIp = null;
                    health.ProxyLatencyMs = null;
                    health.ProxyStatus = assignedProxy is null ? "Chưa được gán proxy" : "Đang chờ kiểm tra";
                    Console.WriteLine(assignedProxy is null
                        ? "No proxy assigned."
                        : $"Proxy assignment changed: {assignedProxy.Name} ({assignedProxy.Url})");
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(heartbeat.HeartbeatSeconds, 10, 60)), shutdown.Token);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                failures++;
                health.Connected = false;
                health.LastError = SafeError(ex);
                int delay = Math.Min(60, 5 * (int)Math.Pow(2, Math.Min(failures - 1, 4)));
                Console.Error.WriteLine($"Heartbeat failed: {health.LastError}. Retry in {delay}s.");
                try { await Task.Delay(TimeSpan.FromSeconds(delay), shutdown.Token); }
                catch (OperationCanceledException) { break; }
            }
        }

        if (healthApp is not null) await healthApp.StopAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task TestProxyAsync(string masterUrl, ProxyLease proxy, SatelliteHealth health, CancellationToken ct)
    {
        var webProxy = new WebProxy(new Uri(proxy.Url));
        if (!string.IsNullOrWhiteSpace(proxy.Username))
            webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password ?? "");
        using var handler = new SocketsHttpHandler { Proxy = webProxy, UseProxy = true, ConnectTimeout = TimeSpan.FromSeconds(12) };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(18) };
        var watch = Stopwatch.StartNew();
        try
        {
            using HttpResponseMessage response = await client.GetAsync(masterUrl.TrimEnd('/') + "/api/ip", ct);
            response.EnsureSuccessStatusCode();
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            string ip = json.RootElement.GetProperty("ip").GetString() ?? "unknown";
            watch.Stop();
            health.ProxyEgressIp = ip;
            health.ProxyLatencyMs = watch.ElapsedMilliseconds;
            health.ProxyStatus = $"Hoạt động ({watch.ElapsedMilliseconds} ms)";
            Console.WriteLine($"Proxy '{proxy.Name}' OK; egress IP={ip}; latency={watch.ElapsedMilliseconds}ms");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            health.ProxyEgressIp = null;
            health.ProxyLatencyMs = null;
            health.ProxyStatus = "Lỗi: " + SafeError(ex);
            Console.Error.WriteLine($"Proxy '{proxy.Name}' failed: {SafeError(ex)}");
        }
    }

    private static async Task<WebApplication?> StartHealthServerAsync(SatelliteHealth health, CancellationToken ct)
    {
        string? port = Environment.GetEnvironmentVariable("PORT");
        if (string.IsNullOrWhiteSpace(port)) return null;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://0.0.0.0:" + port);
        var app = builder.Build();
        app.MapGet("/", () => Results.Ok(new { service = "garena-satellite", health.Connected, health.ObservedIp, health.ProxyStatus }));
        app.MapGet("/health", () => Results.Ok(new { status = "ok", mode = "satellite", health.Connected, health.LastHeartbeatUtc, health.LastError }));
        await app.StartAsync(ct);
        return app;
    }

    private static bool ProxyChanged(ProxyLease? oldValue, ProxyLease? newValue) =>
        oldValue?.Id != newValue?.Id || oldValue?.Url != newValue?.Url || oldValue?.Username != newValue?.Username || oldValue?.Password != newValue?.Password;

    private static string SafeError(Exception ex)
    {
        string message = ex is TaskCanceledException ? "timeout" : ex.Message;
        message = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return message[..Math.Min(message.Length, 160)];
    }
}

public sealed record SatelliteOptions(string MasterUrl, string AgentToken, string Name, int MaxSlots)
{
    public static SatelliteOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--") || i + 1 >= args.Length) throw new ArgumentException($"Invalid option: {args[i]}");
            values[args[i][2..]] = args[++i];
        }
        string master = Get(values, "master-url", "MASTER_URL");
        string token = Get(values, "agent-token", "AGENT_TOKEN");
        string name = values.GetValueOrDefault("name") ?? Environment.GetEnvironmentVariable("SATELLITE_NAME") ?? Environment.MachineName;
        string slotsText = values.GetValueOrDefault("slots") ?? Environment.GetEnvironmentVariable("SATELLITE_SLOTS") ?? "1";
        if (!Uri.TryCreate(master, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("MASTER_URL must be an absolute http/https URL.");
        if (token.Length < 20 || !token.StartsWith("agt_")) throw new ArgumentException("AGENT_TOKEN is invalid.");
        if (!int.TryParse(slotsText, out int slots)) throw new ArgumentException("SATELLITE_SLOTS must be a number.");
        return new SatelliteOptions(master.TrimEnd('/'), token, name.Trim()[..Math.Min(name.Trim().Length, 80)], Math.Clamp(slots, 1, 50));
    }

    private static string Get(Dictionary<string, string> values, string option, string environment) =>
        values.GetValueOrDefault(option) ?? Environment.GetEnvironmentVariable(environment)
        ?? throw new ArgumentException($"--{option} or {environment} is required.");
}
