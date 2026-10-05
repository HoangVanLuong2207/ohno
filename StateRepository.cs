namespace GarenaOrchestrator;

public sealed class StateRepository
{
    private readonly IStateStore _store;
    private readonly SecretProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OrchestratorState _state = new();

    public StateRepository(IStateStore store, SecretProtector protector)
    {
        _store = store;
        _protector = protector;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        _state = await _store.LoadAsync(ct);
        _state.Agents ??= [];
        _state.Proxies ??= [];
    }

    public async Task<DashboardView> GetDashboardAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            DateTime now = DateTime.UtcNow;
            var agents = _state.Agents.OrderBy(a => a.Name).Select(a => new AgentView(
                a.Id, a.Name, a.MaxSlots, a.ActiveJobs, a.AssignedProxyId, a.ObservedIp,
                a.CreatedUtc, a.LastSeenUtc, now - a.LastSeenUtc < TimeSpan.FromSeconds(55),
                a.Version, a.ProxyStatus, a.ProxyEgressIp, a.ProxyLatencyMs)).ToList();
            var proxies = _state.Proxies.OrderBy(p => p.Name).Select(p => new ProxyView(
                p.Id, p.Name, p.Url, p.Username, !string.IsNullOrEmpty(p.EncryptedPassword),
                _state.Agents.Count(a => a.AssignedProxyId == p.Id), p.UpdatedUtc)).ToList();
            return new DashboardView(now, _store.Description, agents, proxies);
        }
        finally { _gate.Release(); }
    }

    public async Task<AgentTokenResponse> CreateAgentAsync(CreateAgentRequest request, string masterUrl, CancellationToken ct)
    {
        string name = CleanName(request.Name, "Agent name");
        int slots = Math.Clamp(request.MaxSlots, 1, 50);
        string id = "agt_" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
        string token = id + "." + SecretProtector.NewSecret();
        var record = new AgentRecord { Id = id, Name = name, TokenHash = _protector.HashToken(token), MaxSlots = slots };
        await _gate.WaitAsync(ct);
        try
        {
            _state.Agents.Add(record);
            await _store.SaveAsync(_state, ct);
        }
        finally { _gate.Release(); }
        string command = $"dotnet GarenaOrchestrator.dll satellite --master-url {masterUrl.TrimEnd('/')} --agent-token {token} --name \"{name.Replace("\"", "") }\" --slots {slots}";
        return new AgentTokenResponse(id, name, token, command);
    }

    public async Task<bool> DeleteAgentAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            int removed = _state.Agents.RemoveAll(a => a.Id == id);
            if (removed > 0) await _store.SaveAsync(_state, ct);
            return removed > 0;
        }
        finally { _gate.Release(); }
    }

    public async Task<ProxyView> CreateProxyAsync(UpsertProxyRequest request, CancellationToken ct)
    {
        ValidateProxy(request, out string name, out string normalizedUrl);
        var proxy = new ProxyRecord
        {
            Id = "pxy_" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)),
            Name = name,
            Url = normalizedUrl,
            Username = NullIfWhiteSpace(request.Username),
            EncryptedPassword = string.IsNullOrEmpty(request.Password) ? null : _protector.Encrypt(request.Password),
            UpdatedUtc = DateTime.UtcNow
        };
        await _gate.WaitAsync(ct);
        try
        {
            _state.Proxies.Add(proxy);
            await _store.SaveAsync(_state, ct);
            return new ProxyView(proxy.Id, proxy.Name, proxy.Url, proxy.Username, proxy.EncryptedPassword != null, 0, proxy.UpdatedUtc);
        }
        finally { _gate.Release(); }
    }

    public async Task<ProxyView?> UpdateProxyAsync(string id, UpsertProxyRequest request, CancellationToken ct)
    {
        ValidateProxy(request, out string name, out string normalizedUrl);
        await _gate.WaitAsync(ct);
        try
        {
            ProxyRecord? proxy = _state.Proxies.FirstOrDefault(p => p.Id == id);
            if (proxy is null) return null;
            proxy.Name = name;
            proxy.Url = normalizedUrl;
            proxy.Username = NullIfWhiteSpace(request.Username);
            if (request.Password is not null)
                proxy.EncryptedPassword = request.Password.Length == 0 ? null : _protector.Encrypt(request.Password);
            proxy.UpdatedUtc = DateTime.UtcNow;
            await _store.SaveAsync(_state, ct);
            return new ProxyView(proxy.Id, proxy.Name, proxy.Url, proxy.Username, proxy.EncryptedPassword != null,
                _state.Agents.Count(a => a.AssignedProxyId == proxy.Id), proxy.UpdatedUtc);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteProxyAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            int removed = _state.Proxies.RemoveAll(p => p.Id == id);
            if (removed == 0) return false;
            foreach (AgentRecord agent in _state.Agents.Where(a => a.AssignedProxyId == id))
                agent.AssignedProxyId = null;
            await _store.SaveAsync(_state, ct);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> AssignProxyAsync(string agentId, string? proxyId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            AgentRecord? agent = _state.Agents.FirstOrDefault(a => a.Id == agentId);
            if (agent is null) return false;
            if (proxyId is not null && !_state.Proxies.Any(p => p.Id == proxyId)) return false;
            agent.AssignedProxyId = proxyId;
            agent.ProxyStatus = proxyId is null ? "Chưa được gán proxy" : "Đang chờ vệ tinh kiểm tra";
            agent.ProxyEgressIp = null;
            agent.ProxyLatencyMs = null;
            await _store.SaveAsync(_state, ct);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<AgentHeartbeatResponse?> HeartbeatAsync(string token, AgentHeartbeatRequest request, string observedIp, CancellationToken ct)
    {
        string id = token.Split('.', 2)[0];
        await _gate.WaitAsync(ct);
        try
        {
            AgentRecord? agent = _state.Agents.FirstOrDefault(a => a.Id == id);
            if (agent is null || !_protector.VerifyToken(token, agent.TokenHash)) return null;
            bool persist = agent.ObservedIp != observedIp;
            agent.ObservedIp = observedIp;
            agent.LastSeenUtc = DateTime.UtcNow;
            agent.ActiveJobs = Math.Clamp(request.ActiveJobs, 0, 1000);
            agent.MaxSlots = Math.Clamp(request.MaxSlots, 1, 50);
            agent.Version = (request.Version ?? "").Trim()[..Math.Min((request.Version ?? "").Trim().Length, 40)];
            if (!string.IsNullOrWhiteSpace(request.Name)) agent.Name = CleanName(request.Name, "Agent name");
            if (!string.IsNullOrWhiteSpace(request.ProxyStatus)) agent.ProxyStatus = request.ProxyStatus.Trim()[..Math.Min(request.ProxyStatus.Trim().Length, 180)];
            agent.ProxyEgressIp = NullIfWhiteSpace(request.ProxyEgressIp);
            agent.ProxyLatencyMs = request.ProxyLatencyMs;
            if (persist) await _store.SaveAsync(_state, ct);

            ProxyLease? lease = null;
            ProxyRecord? proxy = _state.Proxies.FirstOrDefault(p => p.Id == agent.AssignedProxyId);
            if (proxy is not null)
                lease = new ProxyLease(proxy.Id, proxy.Name, proxy.Url, proxy.Username,
                    proxy.EncryptedPassword is null ? null : _protector.Decrypt(proxy.EncryptedPassword));
            return new AgentHeartbeatResponse(DateTime.UtcNow, observedIp, 20, lease);
        }
        finally { _gate.Release(); }
    }

    private static void ValidateProxy(UpsertProxyRequest request, out string name, out string normalizedUrl)
    {
        name = CleanName(request.Name, "Proxy name");
        if (!Uri.TryCreate(request.Url?.Trim(), UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("http" or "https" or "socks4" or "socks5") ||
            string.IsNullOrWhiteSpace(uri.Host) || uri.Port is < 1 or > 65535 || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Proxy URL must use http, https, socks4 or socks5 and must not embed credentials.");
        normalizedUrl = uri.GetLeftPart(UriPartial.Authority);
        if ((request.Username?.Length ?? 0) > 200 || (request.Password?.Length ?? 0) > 500)
            throw new ArgumentException("Proxy credentials are too long.");
    }

    private static string CleanName(string? value, string label)
    {
        string result = (value ?? "").Trim();
        if (result.Length is < 1 or > 80 || result.Any(char.IsControl))
            throw new ArgumentException(label + " must contain 1-80 printable characters.");
        return result;
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
