using System.Net;
using System.Text;
using System.Text.Json;

namespace PokeIdleMulti;

/// <summary>O Telegram do deck (salvo no deck.json).</summary>
internal sealed class TelegramConfig
{
    /// <summary>O token do bot (do @BotFather). Fica só no deck.json deste PC.</summary>
    public string? Token { get; set; }

    /// <summary>A conversa com você (vem do /start mandado ao bot ao ligar). Só ela recebe e só ela manda comandos.</summary>
    public long? ChatId { get; set; }

    public bool On { get; set; } = true;

    /// <summary>Avisar na hora quando uma conta sobe de nível.</summary>
    public bool LevelUp { get; set; } = true;

    /// <summary>Avisar na hora cada shiny capturado.</summary>
    public bool Shiny { get; set; } = true;

    /// <summary>Avisar cada captura (juntadas numa mensagem por minuto).</summary>
    public bool AllCaptures { get; set; }

    /// <summary>O resumo (nível, gold, gemas e capturas de cada conta) a cada tantas horas; 0 = nunca.</summary>
    public int SummaryHours { get; set; } = 2;

    public bool Ready => On && !string.IsNullOrWhiteSpace(Token) && ChatId is not null;

    public TelegramConfig Normalized()
    {
        SummaryHours = Math.Clamp(SummaryHours, 0, 24);
        Token = string.IsNullOrWhiteSpace(Token) ? null : Token.Trim();
        return this;
    }
}

/// <summary>
/// Manda para o celular, pelo bot do Telegram, o que o relatorio.js de cada conta conta: subir de nível, shiny, as
/// capturas e um resumo de tempos em tempos. E responde /status e /resumo. Tudo roda na linha da janela (os eventos das
/// contas e o timer chegam nela, e os awaits voltam para ela), então o estado não precisa de trava.
/// </summary>
internal sealed class TelegramNotifier : IDisposable
{
    public const string MessagePrefix = "deckrel:";

    /// <summary>O relatorio.js com o socket.js na frente.</summary>
    public static string Script { get; } = SocketHub.With("relatorio.js");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(70) };

    private sealed class Account
    {
        public string? Nick;
        public int Level;
        public long Gold, Diamonds;
        public bool Seen;
        public int LevelAtSummary = -1;
        public long GoldAtSummary, DiamondsAtSummary;
        public readonly Dictionary<string, int> Caught = new(); // nome -> quantas desde o último resumo
        public int CaughtCount, ShinyCount;
        public readonly List<string> Shinies = new();
    }

    private readonly TelegramConfig _config;
    private readonly Func<int, string> _accountName;
    private readonly Func<int, bool> _accountOff;
    private readonly Account[] _accounts;
    private readonly List<string> _pendingCaptures = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 60_000 };
    private DateTime _summaryAt;
    private DateTime _summaryFrom = DateTime.Now;
    private CancellationTokenSource? _poll;
    private long _offset;

    /// <summary>O último erro do envio (para o menu), ou null.</summary>
    public string? LastError { get; private set; }

    public TelegramNotifier(TelegramConfig config, int accounts, Func<int, string> accountName, Func<int, bool> accountOff)
    {
        _config = config;
        _accountName = accountName;
        _accountOff = accountOff;
        _accounts = Enumerable.Range(0, accounts).Select(_ => new Account()).ToArray();
        _timer.Tick += (_, _) => _ = OnMinuteAsync();
        ScheduleSummary();
    }

    public TelegramConfig Config => _config;

    public void Start()
    {
        _timer.Start();
        Restart();
    }

    /// <summary>Recomeça a escuta dos comandos (depois de trocar o token ou ligar/desligar).</summary>
    public void Restart()
    {
        _poll?.Cancel();
        _poll = null;
        if (_config.On && _config.Token is not null)
        {
            _poll = new CancellationTokenSource();
            _ = PollAsync(_config.Token, _poll.Token);
        }
    }

    public void ScheduleSummary() =>
        _summaryAt = _config.SummaryHours > 0 ? DateTime.Now.AddHours(_config.SummaryHours) : DateTime.MaxValue;

    // ---------- O que chega das contas ----------

    public void OnPageMessage(int index, string message)
    {
        if (index < 0 || index >= _accounts.Length)
            return;
        JsonElement m;
        try
        {
            m = JsonDocument.Parse(message.AsMemory(MessagePrefix.Length)).RootElement;
        }
        catch (JsonException)
        {
            return;
        }
        var a = _accounts[index];
        switch (Str(m, "k"))
        {
            case "conta":
                int level = Int(m, "level");
                int before = a.Level;
                bool first = !a.Seen;
                a.Nick = Str(m, "nick") ?? a.Nick;
                a.Level = level;
                a.Gold = Long(m, "gold");
                a.Diamonds = Long(m, "diamonds");
                a.Seen = true;
                if (a.LevelAtSummary < 0)
                {
                    a.LevelAtSummary = level;
                    a.GoldAtSummary = a.Gold;
                    a.DiamondsAtSummary = a.Diamonds;
                }
                // O welcome de uma recarga traz o mesmo nível: só avisa quando sobe de verdade.
                if (!first && level > before && _config.LevelUp)
                    _ = SendAsync($"⬆️ <b>{H(Label(index))}</b> subiu para o nível <b>{level}</b>");
                break;

            case "captura":
                string name = Str(m, "nome") ?? "?";
                bool shiny = m.TryGetProperty("shiny", out var s) && s.ValueKind == JsonValueKind.True;
                bool depot = m.TryGetProperty("depot", out var d) && d.ValueKind == JsonValueKind.True;
                int pkLevel = Int(m, "level");
                string quality = m.TryGetProperty("quality", out var q) && q.ValueKind is JsonValueKind.String or JsonValueKind.Number
                    ? q.ToString() : "";
                string detail = $"nv {pkLevel}" + (quality.Length > 0 ? $", qualidade {quality}" : "") + (depot ? ", foi para o Depot" : "");
                a.CaughtCount++;
                a.Caught[name] = a.Caught.GetValueOrDefault(name) + 1;
                if (shiny)
                {
                    a.ShinyCount++;
                    a.Shinies.Add(name);
                    if (_config.Shiny)
                        _ = SendAsync($"✨ <b>{H(Label(index))}</b> pegou um <b>SHINY {H(name)}</b> ({H(detail)})");
                }
                else if (_config.AllCaptures)
                {
                    _pendingCaptures.Add($"• {H(Label(index))}: {H(name)} ({H(detail)})");
                }
                break;
        }
    }

    // ---------- Timer: as capturas juntadas e o resumo ----------

    private async Task OnMinuteAsync()
    {
        if (_pendingCaptures.Count > 0)
        {
            var lines = _pendingCaptures.Take(40).ToList();
            int more = _pendingCaptures.Count - lines.Count;
            _pendingCaptures.Clear();
            await SendAsync("🎯 <b>Capturas</b>\n" + string.Join("\n", lines) + (more > 0 ? $"\n… e mais {more}" : ""));
        }
        if (DateTime.Now >= _summaryAt)
        {
            ScheduleSummary();
            await SendAsync(Summary(reset: true));
        }
    }

    /// <summary>Nível, gold e gemas de cada conta agora.</summary>
    public string Status()
    {
        var sb = new StringBuilder("📊 <b>Status</b>\n");
        for (int i = 0; i < _accounts.Length; i++)
        {
            var a = _accounts[i];
            sb.Append($"\n<b>{H(Label(i))}</b>: ");
            if (_accountOff(i)) sb.Append("desligada");
            else if (!a.Seen) sb.Append("ainda sem dados (carregando ou deslogada)");
            else sb.Append($"nv {a.Level} · {Num(a.Gold)} gold · {Num(a.Diamonds)} gemas");
        }
        return sb.ToString();
    }

    /// <summary>O que cada conta fez desde o último resumo. <paramref name="reset"/>: começa a contar de novo.</summary>
    public string Summary(bool reset)
    {
        var span = DateTime.Now - _summaryFrom;
        var sb = new StringBuilder($"🗒 <b>Resumo</b> (últimas {(int)span.TotalHours}h{span.Minutes:00})\n");
        for (int i = 0; i < _accounts.Length; i++)
        {
            var a = _accounts[i];
            sb.Append($"\n<b>{H(Label(i))}</b>");
            if (_accountOff(i)) { sb.Append(": desligada\n"); continue; }
            if (!a.Seen) { sb.Append(": ainda sem dados\n"); continue; }
            sb.Append($"\n  Nível {a.Level}");
            if (a.LevelAtSummary >= 0 && a.Level > a.LevelAtSummary) sb.Append($" (+{a.Level - a.LevelAtSummary})");
            sb.Append($" · {Num(a.Gold)} gold ({Signed(a.Gold - a.GoldAtSummary)}) · {Num(a.Diamonds)} gemas ({Signed(a.Diamonds - a.DiamondsAtSummary)})");
            sb.Append($"\n  Capturas: {a.CaughtCount}");
            if (a.ShinyCount > 0) sb.Append($" · ✨ {a.ShinyCount} shiny: {H(string.Join(", ", a.Shinies))}");
            if (a.Caught.Count > 0)
            {
                var top = a.Caught.OrderByDescending(p => p.Value).Take(8).Select(p => p.Value > 1 ? $"{p.Key} ×{p.Value}" : p.Key);
                sb.Append($"\n  {H(string.Join(", ", top))}{(a.Caught.Count > 8 ? ", …" : "")}");
            }
            sb.Append('\n');
            if (reset)
            {
                a.LevelAtSummary = a.Level;
                a.GoldAtSummary = a.Gold;
                a.DiamondsAtSummary = a.Diamonds;
                a.Caught.Clear();
                a.CaughtCount = a.ShinyCount = 0;
                a.Shinies.Clear();
            }
        }
        if (reset) _summaryFrom = DateTime.Now;
        return sb.ToString();
    }

    // ---------- Ligar: pegar a conversa pelo /start ----------

    /// <summary>
    /// Confere o token e espera o /start mandado ao bot (até <paramref name="wait"/>). Devolve o nome do bot e a conversa,
    /// ou o erro em <c>Error</c>.
    /// </summary>
    public async Task<(string? Bot, long? Chat, string? Error)> LinkAsync(string token, TimeSpan wait, CancellationToken cancel)
    {
        _poll?.Cancel();
        _poll = null;
        try
        {
            var me = await CallAsync(token, "getMe", null, cancel);
            string bot = Str(me, "username") ?? "bot";
            // Joga fora as mensagens antigas: só vale o /start mandado agora.
            var old = await CallAsync(token, "getUpdates", new Dictionary<string, string> { ["timeout"] = "0", ["offset"] = "-1" }, cancel);
            long offset = old.ValueKind == JsonValueKind.Array && old.GetArrayLength() > 0 ? Long(old[old.GetArrayLength() - 1], "update_id") + 1 : 0;
            var until = DateTime.Now + wait;
            while (DateTime.Now < until)
            {
                var updates = await CallAsync(token, "getUpdates",
                    new Dictionary<string, string> { ["timeout"] = "20", ["offset"] = offset.ToString() }, cancel);
                foreach (var u in updates.EnumerateArray())
                {
                    offset = Long(u, "update_id") + 1;
                    if (u.TryGetProperty("message", out var msg) && msg.TryGetProperty("chat", out var chat)
                        && (Str(msg, "text") ?? "").StartsWith("/start", StringComparison.OrdinalIgnoreCase))
                    {
                        // Confirma a leitura deste /start para ele não voltar na escuta dos comandos.
                        await CallAsync(token, "getUpdates", new Dictionary<string, string> { ["timeout"] = "0", ["offset"] = offset.ToString() }, cancel);
                        _offset = offset;
                        return (bot, Long(chat, "id"), null);
                    }
                }
            }
            return (bot, null, "Não chegou nenhum /start a tempo.");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return (null, null, "Cancelado.");
        }
        catch (Exception ex)
        {
            return (null, null, ex.Message);
        }
    }

    // ---------- Comandos: /status e /resumo ----------

    private async Task PollAsync(string token, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                var updates = await CallAsync(token, "getUpdates",
                    new Dictionary<string, string> { ["timeout"] = "50", ["offset"] = _offset.ToString(), ["allowed_updates"] = "[\"message\"]" }, cancel);
                foreach (var u in updates.EnumerateArray())
                {
                    _offset = Long(u, "update_id") + 1;
                    if (!u.TryGetProperty("message", out var msg) || !msg.TryGetProperty("chat", out var chat))
                        continue;
                    // Só a sua conversa manda no bot.
                    if (_config.ChatId is not long mine || Long(chat, "id") != mine)
                        continue;
                    string text = (Str(msg, "text") ?? "").Trim().ToLowerInvariant();
                    int at = text.IndexOf('@');
                    if (at > 0) text = text[..at];
                    if (text is "/status") await SendAsync(Status());
                    else if (text is "/resumo") await SendAsync(Summary(reset: false));
                    else if (text is "/start" or "/ajuda" or "/help")
                        await SendAsync("Comandos:\n/status — nível, gold e gemas de cada conta\n/resumo — o que cada conta fez desde o último resumo");
                }
                LastError = null;
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                try { await Task.Delay(TimeSpan.FromSeconds(30), cancel); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    // ---------- Envio ----------

    public async Task<bool> SendAsync(string html)
    {
        if (!_config.Ready)
            return false;
        try
        {
            await CallAsync(_config.Token!, "sendMessage", new Dictionary<string, string>
            {
                ["chat_id"] = _config.ChatId!.Value.ToString(),
                ["text"] = html.Length > 4000 ? html[..4000] + "…" : html,
                ["parse_mode"] = "HTML",
                ["disable_web_page_preview"] = "true",
            }, CancellationToken.None);
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Diag.Write("Telegram: não consegui mandar a mensagem: " + ex.Message);
            return false;
        }
    }

    private static async Task<JsonElement> CallAsync(string token, string method, Dictionary<string, string>? args, CancellationToken cancel)
    {
        using var content = new FormUrlEncodedContent(args ?? new Dictionary<string, string>());
        using var response = await Http.PostAsync($"https://api.telegram.org/bot{token}/{method}", content, cancel);
        string body = await response.Content.ReadAsStringAsync(cancel);
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(body).RootElement;
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"o Telegram respondeu {(int)response.StatusCode}");
        }
        if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
        {
            string why = Str(root, "description") ?? $"erro {(int)response.StatusCode}";
            if (response.StatusCode == HttpStatusCode.Unauthorized) why = "token inválido";
            throw new InvalidOperationException("Telegram: " + why);
        }
        return root.GetProperty("result").Clone();
    }

    // ---------- Ajudantes ----------

    private string Label(int index)
    {
        string name = _accountName(index);
        string? nick = _accounts[index].Nick;
        return string.IsNullOrWhiteSpace(nick) || nick == name ? name : $"{name} ({nick})";
    }

    private static string H(string text) => WebUtility.HtmlEncode(text);
    private static string Num(long n) => n.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("pt-BR"));
    private static string Signed(long n) => (n >= 0 ? "+" : "−") + Num(Math.Abs(n));

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? (long)d : 0;

    private static int Int(JsonElement e, string name) => (int)Math.Clamp(Long(e, name), int.MinValue, int.MaxValue);

    public void Dispose()
    {
        _timer.Dispose();
        _poll?.Cancel();
    }
}
