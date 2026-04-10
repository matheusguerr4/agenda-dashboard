using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using System.Net.Http.Json;
using System.Text.Json;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

public class Function
{
    private static readonly HttpClient Http = new();

    // Busca token do Graph via client_credentials
    private async Task<string> GetTokenAsync()
    {
        var tenantId     = Environment.GetEnvironmentVariable("TENANT_ID");
        var clientId     = Environment.GetEnvironmentVariable("CLIENT_ID");
        var clientSecret = Environment.GetEnvironmentVariable("CLIENT_SECRET");

        var res = await Http.PostAsync(
            $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"]    = "client_credentials",
                ["client_id"]     = clientId!,
                ["client_secret"] = clientSecret!,
                ["scope"]         = "https://graph.microsoft.com/.default",
            })
        );

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("access_token").GetString()!;
    }

    // Handler principal — roteia pelo path
    public async Task<APIGatewayHttpApiV2ProxyResponse> FunctionHandler(
        APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
    {
        var token = await GetTokenAsync();
        var conta = Environment.GetEnvironmentVariable("CONTA_EMAIL");
        var path  = request.RawPath;

        object resultado = path switch
        {
            "/aniversariantes" => await GetAniversariantes(token, conta!),
            "/eventos"         => await GetEventos(token, conta!),
            _                  => new { erro = "rota não encontrada" }
        };

        return new APIGatewayHttpApiV2ProxyResponse
        {
            StatusCode = 200,
            Headers    = new Dictionary<string, string>
            {
                ["Content-Type"]                = "application/json",
                ["Access-Control-Allow-Origin"] = "*", // CORS para o HTML
            },
            Body = JsonSerializer.Serialize(resultado),
        };
    }

    private async Task<object> GetAniversariantes(string token, string conta)
    {
        // Busca o calendário "Birthdays" (nome pode variar por idioma)
        var calRes = await Http.SendAsync(new HttpRequestMessage(
            HttpMethod.Get,
            $"https://graph.microsoft.com/v1.0/users/{conta}/calendars?$select=id,name"
        )
        { Headers = { { "Authorization", $"Bearer {token}" } } });

        var calRaw = await calRes.Content.ReadAsStringAsync();
        Console.WriteLine($"[calendarios] status={calRes.StatusCode} body={calRaw}");

        var calJson = JsonSerializer.Deserialize<JsonElement>(calRaw);
        if (!calJson.TryGetProperty("value", out var cals))
            return new { erro = $"Erro ao listar calendários: {calRaw}" };

        var birthdayCalId = cals.EnumerateArray()
            .Where(c =>
            {
                var name = c.GetProperty("name").GetString() ?? "";
                return name.Contains("irth", StringComparison.OrdinalIgnoreCase) ||
                       name.Contains("niversár", StringComparison.OrdinalIgnoreCase) ||
                       name.Contains("niversar", StringComparison.OrdinalIgnoreCase);
            })
            .Select(c => c.GetProperty("id").GetString())
            .FirstOrDefault();

        if (birthdayCalId == null)
            return new { erro = "Calendário de aniversários não encontrado. Calendários disponíveis: " + calRaw };

        // Busca eventos de hoje no calendário de aniversários
        var inicio = Uri.EscapeDataString(DateTime.UtcNow.Date.ToString("o"));
        var fim    = Uri.EscapeDataString(DateTime.UtcNow.Date.AddDays(1).AddSeconds(-1).ToString("o"));

        var res = await Http.SendAsync(new HttpRequestMessage(
            HttpMethod.Get,
            $"https://graph.microsoft.com/v1.0/users/{conta}/calendars/{birthdayCalId}/calendarView" +
            $"?startDateTime={inicio}&endDateTime={fim}&$select=subject,categories"
        )
        { Headers = { { "Authorization", $"Bearer {token}" } } });

        var raw = await res.Content.ReadAsStringAsync();
        Console.WriteLine($"[aniversariantes] status={res.StatusCode} body={raw}");

        var json = JsonSerializer.Deserialize<JsonElement>(raw);
        if (!json.TryGetProperty("value", out var valueEl))
            return new { erro = $"Graph API erro: {raw}" };

        var eventos = valueEl.EnumerateArray().ToList();
        if (eventos.Count == 0) return new Dictionary<string, List<object>>();

        // Extrai nomes para busca (remove prefixo "Aniversário de ")
        var nomesParaBusca = eventos
            .Select(e => System.Text.RegularExpressions.Regex.Replace(
                e.GetProperty("subject").GetString() ?? "",
                @"^Anivers[aá]rio\s+de\s+",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim())
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct()
            .ToList();

        // Busca pastas de contatos
        var foldersRes = await Http.SendAsync(new HttpRequestMessage(
            HttpMethod.Get,
            $"https://graph.microsoft.com/v1.0/users/{conta}/contactFolders?$select=id,displayName"
        ) { Headers = { { "Authorization", $"Bearer {token}" } } });

        var foldersJson = JsonSerializer.Deserialize<JsonElement>(await foldersRes.Content.ReadAsStringAsync());
        var pastas = foldersJson.TryGetProperty("value", out var foldersEl)
            ? foldersEl.EnumerateArray().Select(f => (id: f.GetProperty("id").GetString()!, nome: f.GetProperty("displayName").GetString()!)).ToList()
            : new List<(string id, string nome)>();

        // Filtro combinado com todos os nomes
        var filtro = Uri.EscapeDataString(string.Join(" or ", nomesParaBusca.Select(n => $"displayName eq '{n.Replace("'", "''")}'")));
        var campos = "$select=displayName,jobTitle,companyName,mobilePhone,businessPhones";

        // 4 chamadas paralelas, uma por pasta
        var lookupMap = new System.Collections.Concurrent.ConcurrentDictionary<string, (string? cargo, string? empresa, List<string> telefones, string categoria)>();

        await Task.WhenAll(pastas.Select(async pasta =>
        {
            var url = $"https://graph.microsoft.com/v1.0/users/{conta}/contactFolders/{pasta.id}/contacts?$filter={filtro}&{campos}";
            var r = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Get, url)
            { Headers = { { "Authorization", $"Bearer {token}" } } });

            var rJson = JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());
            if (!rJson.TryGetProperty("value", out var contacts)) return;

            foreach (var c in contacts.EnumerateArray())
            {
                var dn = c.GetProperty("displayName").GetString() ?? "";
                var cargo   = c.TryGetProperty("jobTitle",    out var jt) ? jt.GetString() : null;
                var empresa = c.TryGetProperty("companyName", out var cn) ? cn.GetString() : null;
                var telefones = new List<string>();
                if (c.TryGetProperty("mobilePhone", out var mob) && mob.ValueKind == JsonValueKind.String)
                { var v = mob.GetString(); if (!string.IsNullOrWhiteSpace(v)) telefones.Add(v); }
                if (c.TryGetProperty("businessPhones", out var biz) && biz.ValueKind == JsonValueKind.Array)
                    telefones.AddRange(biz.EnumerateArray().Select(p => p.GetString()).Where(p => !string.IsNullOrWhiteSpace(p))!);

                lookupMap[dn] = (cargo, empresa, telefones, pasta.nome);
            }
        }));

        // Agrupa aniversariantes por categoria (pasta do contato)
        var grupos = new Dictionary<string, List<object>>();
        foreach (var e in eventos)
        {
            var nome = e.GetProperty("subject").GetString();
            var nomeParaBusca = System.Text.RegularExpressions.Regex.Replace(
                nome ?? "", @"^Anivers[aá]rio\s+de\s+", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

            lookupMap.TryGetValue(nomeParaBusca, out var info);
            var categoria = info.categoria ?? "Sem categoria";
            var pessoa = (object)new { nome, cargo = info.cargo, empresa = info.empresa, telefones = info.telefones ?? new List<string>() };

            if (!grupos.ContainsKey(categoria)) grupos[categoria] = new List<object>();
            grupos[categoria].Add(pessoa);
        }

        return grupos;
    }

    private async Task<object> GetEventos(string token, string conta)
    {
        var inicio = Uri.EscapeDataString(DateTime.UtcNow.Date.ToString("o"));
        var fim    = Uri.EscapeDataString(DateTime.UtcNow.Date.AddDays(1).AddSeconds(-1).ToString("o"));

        var res = await Http.SendAsync(new HttpRequestMessage(
            HttpMethod.Get,
            $"https://graph.microsoft.com/v1.0/users/{conta}/calendarView" +
            $"?startDateTime={inicio}&endDateTime={fim}" +
            $"&$select=subject,start,end,location"
        )
        { Headers = { { "Authorization", $"Bearer {token}" } } });

        var raw = await res.Content.ReadAsStringAsync();
        Console.WriteLine($"[eventos] status={res.StatusCode} body={raw}");

        var json = JsonSerializer.Deserialize<JsonElement>(raw);
        if (!json.TryGetProperty("value", out var valueEl))
            return new { erro = $"Graph API erro: {raw}" };

        var eventos = valueEl.EnumerateArray()
            .Select(e => new
            {
                titulo    = e.GetProperty("subject").GetString(),
                inicio    = e.GetProperty("start").GetProperty("dateTime").GetString(),
                fim       = e.GetProperty("end").GetProperty("dateTime").GetString(),
                local     = e.TryGetProperty("location", out var loc)
                            ? loc.GetProperty("displayName").GetString()
                            : null,
            });

        return eventos.ToList();
    }
}