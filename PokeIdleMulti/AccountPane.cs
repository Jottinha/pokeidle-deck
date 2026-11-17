using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PokeIdleMulti;

internal enum PaneStatus { Starting, Loading, Ok, Failed }

/// <summary>Como a conta está para quem olha: no jogo, carregando, na tela de login, com erro ou desligada.</summary>
internal enum PaneHealth { Ok, Loading, Login, Error, Off }

/// <summary>
/// Uma conta dentro do deck: um cabeçalho fino (status, tempo rodando, extensões, botões) e a página
/// do jogo ocupando o resto, sem barra de navegador. Cada conta tem um perfil próprio do WebView2,
/// com cookies, login e extensões separados.
/// </summary>
internal sealed class AccountPane : UserControl
{
    // F11 dentro da página não chega na janela do deck; o script avisa por mensagem.
    private const string KeyScript =
        "addEventListener('keydown', e => { if (e.key === 'F11') { e.preventDefault(); chrome.webview.postMessage('F11'); } }, true);";

    private readonly ToolStrip _header = new ClickThroughToolStrip();
    private readonly ToolStripLabel _dot = new("●");
    private readonly ToolStripLabel _name = new();
    private readonly ToolStripLabel _info = new();
    // O ⋯: tudo o que a conta faz (ampliar, desligar, recompra, PvP, voto, extensões, renomear…).
    private readonly ToolStripButton _more = new(Theme.MoreGlyph) { Font = Theme.Symbols };
    private readonly List<ExtensionInfo> _extensions = new();
    private readonly ToolStripButton _reload = new(Theme.ReloadGlyph) { Font = Theme.Symbols };
    private readonly ToolStripButton _mute = new(Theme.SoundGlyph) { Font = Theme.Symbols };
    private readonly ToolStripButton _vote = new("🗳 Votar") { Visible = false };
    private readonly ToolStripButton _power = new(Theme.PowerGlyph) { Font = Theme.Symbols };
    private readonly ToolStripButton _restock = new(Theme.CartGlyph) { Font = Theme.Symbols };
    // As espadas da fila de PvP: os ícones do Windows não têm, a fonte de símbolos tem.
    private readonly ToolStripButton _pvp = new("⚔") { Font = new Font("Segoe UI Symbol", 11f) };
    private readonly Label _offNote = new()
    {
        Dock = DockStyle.Fill,
        Visible = false,
        TextAlign = ContentAlignment.MiddleCenter,
        BackColor = Theme.Back,
        ForeColor = Theme.Muted,
    };
    private readonly Font _voteBold;

    private PaneStatus _status = PaneStatus.Starting;
    private string? _statusText = "abrindo…";
    private string? _note;
    private DateTime _noteUntil;
    private bool _loginScreen;
    private string _voteText = "🗳 Votar";
    private bool _voteShort;

    // A tela de login do jogo está à vista (a mesma conta que o jogo faz para desenhar o captcha).
    private const string LoginScript =
        "(() => { const p = document.querySelector('#portal'), l = document.querySelector('#login');" +
        " return !!p && !!l && !p.classList.contains('hidden') && !l.classList.contains('hidden'); })()";

    public AccountPane(int index)
    {
        Index = index;
        BackColor = Theme.Border;
        Padding = new Padding(1);

        Theme.Style(_header);
        // Um degrau mais claro que a barra do deck: o cabeçalho é da conta, não da janela.
        _header.BackColor = Theme.Header;
        _header.Dock = DockStyle.Top;
        _header.Padding = new Padding(8, 3, 4, 3);
        _header.CanOverflow = false;
        // Letra e ícones maiores que o padrão de 9 pt: com 4 contas na tela, os botões ficavam pequenos demais.
        _header.Font = new Font("Segoe UI", 10f);
        _header.ImageScalingSize = new Size(20, 20);
        _dot.ForeColor = Theme.Muted;
        _dot.Font = new Font("Segoe UI", 13f);
        _dot.Margin = new Padding(0, 0, 2, 2);
        _name.Font = new Font("Segoe UI Semibold", 10.5f);
        _info.ForeColor = Theme.Muted;

        // Itens à direita entram da direita para a esquerda.
        // Sempre à vista: ⟳, som e ⋯. O carrinho e o ⚔ só com a recompra e a fila ligadas, o voto só liberado, e o ⏻ só
        // com a conta desligada (para ligar); o resto fica no ⋯.
        _power.Alignment = _more.Alignment = _reload.Alignment = _mute.Alignment =
            _vote.Alignment = _restock.Alignment = _pvp.Alignment = ToolStripItemAlignment.Right;
        _power.Available = false;
        _more.ToolTipText = "Mais opções desta conta: ampliar, desligar, recompra, fila de PvP, voto, extensões, renomear…";
        _more.Click += (_, _) =>
        {
            var bounds = _more.Bounds;
            MenuRequested?.Invoke(this, _header.PointToScreen(new Point(bounds.Left, bounds.Bottom)));
        };
        _restock.Click += (_, _) => RestockRequested?.Invoke(this);
        ShowRestock(null);
        _pvp.Click += (_, _) => PvpRequested?.Invoke(this);
        // Botão direito: a tela e a velocidade das partidas (o clique de botão direito num ToolStripButton não dispara o Click).
        _pvp.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
                PvpMenuRequested?.Invoke(this);
        };
        ShowPvp(null);
        _power.Click += (_, _) => PowerRequested?.Invoke(this);
        _reload.ToolTipText = "Recarregar limpo: apaga o cache do jogo (e os service workers do site) e recarrega. O login e as extensões ficam";
        _mute.ToolTipText = "Ligar / desligar o som desta conta";
        _voteBold = new Font(_header.Font, FontStyle.Bold);
        _reload.Click += (_, _) => ReloadRequested?.Invoke(this);
        _mute.Click += (_, _) => SetMuted(!Muted);
        _vote.Click += (_, _) => VoteRequested?.Invoke(this);
        _header.DoubleClick += (_, _) => FocusRequested?.Invoke(this);
        _header.Click += (_, _) => HeaderClicked?.Invoke(this);
        foreach (var label in new ToolStripItem[] { _dot, _name, _info })
            label.Click += (_, _) => HeaderClicked?.Invoke(this);
        _header.SizeChanged += (_, _) => FitHeader();
        _header.Items.AddRange(new ToolStripItem[] { _dot, _name, _info, _more, _power, _mute, _reload, _pvp, _restock, _vote });
        foreach (var item in new ToolStripItem[] { _more, _power, _mute, _reload, _pvp, _restock, _vote })
            Roomy(item);

        _offNote.Font = new Font(Font.FontFamily, 11f);
        _offNote.DoubleClick += (_, _) => FocusRequested?.Invoke(this);
        Controls.Add(WebView);
        Controls.Add(_offNote);
        Controls.Add(_header);
    }

    /// <summary>Botão do cabeçalho com folga: área de clique maior e espaço entre um e outro (apertado, sem a folga).</summary>
    private static void Roomy(ToolStripItem item, bool tight = false)
    {
        item.Margin = tight ? new Padding(0, 1, 0, 1) : new Padding(2, 1, 2, 1);
        item.Padding = tight ? new Padding(1) : new Padding(5, 1, 5, 1);
    }

    public event Action<AccountPane>? FocusRequested;
    public event Action<AccountPane>? ReloadRequested;
    public event Action<AccountPane>? FullscreenRequested;
    public event Action<AccountPane>? SettingsChanged;
    public event Action<AccountPane, ExtensionInfo, Point>? PopupRequested;
    public event Action<AccountPane>? VoteRequested;

    /// <summary>Um clique de verdade na página (para o eco repetir nas outras contas).</summary>
    public event Action<AccountPane, Echo.Click>? Clicked;

    /// <summary>O botão ⏻: ligar ou desligar a conta.</summary>
    public event Action<AccountPane>? PowerRequested;

    /// <summary>O ⋯ do cabeçalho: o deck abre o menu da conta neste ponto da tela.</summary>
    public event Action<AccountPane, Point>? MenuRequested;

    /// <summary>Um clique no nome ou no espaço livre do cabeçalho (no layout Principal, a miniatura vira a principal).</summary>
    public event Action<AccountPane>? HeaderClicked;

    /// <summary>
    /// Miniatura (layout Principal): o cabeçalho mostra o ritmo da sessão (XP/h e gold/h) no lugar do tempo rodando, e um
    /// clique nele troca a conta com a principal.
    /// </summary>
    public bool Compact
    {
        get => _compact;
        set
        {
            if (_compact == value)
                return;
            _compact = value;
            _header.Cursor = value ? Cursors.Hand : Cursors.Default;
            _name.ToolTipText = value ? "Clique para trazer esta conta para o lugar da principal" : null;
            RefreshHeader();
        }
    }

    private bool _compact;

    /// <summary>A conta principal do layout Principal: borda roxa mais grossa, para ver de longe qual é a grande.</summary>
    public bool Highlighted
    {
        get => _highlighted;
        set
        {
            if (_highlighted == value)
                return;
            _highlighted = value;
            BackColor = value ? Theme.Accent : Theme.Border;
            Padding = new Padding(value ? 2 : 1);
        }
    }

    private bool _highlighted;

    /// <summary>O ritmo da sessão do jogo ("202M XP/h · 516k gold/h"), lido da página; null sem o jogo ou sem a sessão.</summary>
    public string? Stats { get; private set; }

    /// <summary>
    /// Lê o XP/h e o gold/h da sessão: da caixa do Ultra (quando a conta está nele) ou do painel "Sessão" do jogo.
    /// </summary>
    private const string StatsScript =
        "(() => { const t = s => { const e = document.querySelector(s); return e ? e.textContent : null; };" +
        " const xp = t('.ultra-linha[data-u=\"xpTreinador\"] small') ?? t('#sessao-xp-treinador-h');" +
        " const gold = t('.ultra-linha[data-u=\"gold\"] small') ?? t('#sessao-gold-h');" +
        " return JSON.stringify({ xp, gold }); })()";

    public async Task ReadStatsAsync()
    {
        string? stats = null;
        if (!Off && OnGame && _status == PaneStatus.Ok && !_loginScreen)
        {
            try
            {
                var json = JsonSerializer.Deserialize<string>(await WebView.ExecuteScriptAsync(StatsScript));
                using var doc = JsonDocument.Parse(json ?? "{}");
                string? Part(string key, string unit) =>
                    doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && Short(v.GetString()) is string n
                        ? $"{n} {unit}/h" : null;
                var parts = new[] { Part("xp", "XP"), Part("gold", "gold") }.OfType<string>().ToList();
                stats = parts.Count > 0 ? string.Join(" · ", parts) : null;
            }
            catch
            {
                // Página recarregando: fica sem os números até a próxima leitura.
            }
        }
        if (stats != Stats)
        {
            Stats = stats;
            RefreshHeader();
        }
    }

    /// <summary>Um número do jogo ("202.633.115" ou "202633115/h") em curto: 202M, 516k, 950.</summary>
    private static string? Short(string? text)
    {
        var digits = new string((text ?? "").TakeWhile(c => c != '/').Where(char.IsDigit).ToArray());
        if (digits.Length == 0 || !long.TryParse(digits, out long n))
            return null;
        var pt = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
        return n >= 1_000_000_000 ? (n / 1e9).ToString(n >= 10_000_000_000 ? "0" : "0.#", pt) + "B"
            : n >= 1_000_000 ? (n / 1e6).ToString(n >= 10_000_000 ? "0" : "0.#", pt) + "M"
            : n >= 1_000 ? (n / 1e3).ToString(n >= 10_000 ? "0" : "0.#", pt) + "k"
            : n.ToString(pt);
    }

    /// <summary>Como a conta está, na ordem do resumo da barra de baixo (a mesma conta da cor da bolinha).</summary>
    public PaneHealth Health =>
        Off ? PaneHealth.Off
        : _status == PaneStatus.Failed ? PaneHealth.Error
        : _loginScreen ? PaneHealth.Login
        : _status == PaneStatus.Ok && _statusText is null ? PaneHealth.Ok
        : PaneHealth.Loading;

    /// <summary>As extensões desta conta que têm popup (o deck põe no menu ⋯).</summary>
    public IReadOnlyList<ExtensionInfo> Extensions => _extensions;

    /// <summary>Abre o popup de uma extensão encostado no ⋯ do cabeçalho.</summary>
    public void OpenExtension(ExtensionInfo extension)
    {
        var bounds = _more.Bounds;
        PopupRequested?.Invoke(this, extension, _header.PointToScreen(new Point(bounds.Right, bounds.Bottom)));
    }

    /// <summary>O texto do voto (liberado ou quanto falta) e a dica, para o menu ⋯.</summary>
    public string VoteText => _voteText;

    public string VoteTip => _vote.ToolTipText ?? "";

    /// <summary>O voto já foi conferido nesta conta (o menu ⋯ só mostra o item com o que dizer).</summary>
    public bool HasVote { get; private set; }

    /// <summary>O carrinho do cabeçalho: abrir a recompra desta conta.</summary>
    public event Action<AccountPane>? RestockRequested;

    /// <summary>O ⚔ do cabeçalho: ligar ou desligar a fila de PvP da conta.</summary>
    public event Action<AccountPane>? PvpRequested;

    /// <summary>Botão direito no ⚔: o menu da tela e da velocidade das partidas.</summary>
    public event Action<AccountPane>? PvpMenuRequested;


    /// <summary>Desligada: fica no deck, mas sem o jogo (nem as extensões, que só rodam nele), lives ou voto.</summary>
    public bool Off { get; private set; }

    /// <summary>A página do jogo abriu um link do TopIdle (o "votar" de dentro do jogo).</summary>
    public event Action<AccountPane, string>? TopIdleLinkRequested;

    public int Index { get; }
    public string ProfileName => $"conta{Index + 1}";
    public string AccountName { get; set; } = "";
    public WebView2 WebView { get; } = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Theme.Back };
    public CoreWebView2? Core => WebView.CoreWebView2;
    public CoreWebView2Profile? Profile => WebView.CoreWebView2?.Profile;
    public PaneStatus Status => _status;
    public DateTime? StartedAt { get; private set; }
    public bool Muted => Core?.IsMuted ?? false;
    public int ExtensionButtonCount => _extensions.Count;

    /// <summary>O diagnóstico da carga desta conta (diagnostico\contaN.log; o deck cria, null: sem log).</summary>
    public PaneDiag? Diag { get; set; }

    /// <summary>Cria o navegador da conta e abre o jogo; <paramref name="prepare"/> roda antes de abrir (o conserto das extensões).</summary>
    public async Task InitializeAsync(CoreWebView2Environment env, string url, double zoom, bool muted, Action<CoreWebView2>? setup,
        Func<CoreWebView2, Task>? prepare = null)
    {
        var options = env.CreateCoreWebView2ControllerOptions();
        options.ProfileName = ProfileName;
        _ = WebView.Handle;
        await WebView.EnsureCoreWebView2Async(env, options);

        var core = WebView.CoreWebView2;
        if (Diag is not null)
        {
            // Antes dos outros scripts do deck, para pegar os erros deles também.
            await Diag.AttachAsync(core);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(PaneDiag.PageScript);
        }
        core.Settings.IsStatusBarEnabled = false;
        core.IsMuted = muted;
        UpdateMuteButton();
        WebView.ZoomFactor = zoom;
        WebView.ZoomFactorChanged += (_, _) => SettingsChanged?.Invoke(this);

        core.NavigationStarting += (_, e) =>
        {
            Diag?.Navigation(e.Uri, e.IsRedirected);
            if (e.IsRedirected || Off || _wiping)
                return;
            _openSince = _failedSince = null;
            SetStatus(PaneStatus.Loading, "carregando…");
        };
        core.NavigationCompleted += (_, e) =>
        {
            Diag?.Completed(e.IsSuccess, e.WebErrorStatus, e.HttpStatusCode);
            if (Off || _wiping)
                return;
            if (e.IsSuccess)
            {
                Loads++;
                _openSince = DateTime.Now;
                StartedAt ??= DateTime.Now;
                SetStatus(PaneStatus.Ok, null);
                // O ULTRA do deck vale para todas: a conta que recarrega volta a ele, mesmo se tinha saído no
                // "Voltar ao jogo" da caixa.
                if (Ultra)
                    _ = SetUltraAsync(true);
                // A página nova (inclusive a que o próprio jogo recarrega numa versão nova) começa sem configuração.
                _ = PushRestockAsync();
            }
            else if (e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
            {
                _failedSince = DateTime.Now;
                SetStatus(PaneStatus.Failed, "não carregou: sem internet ou o site caiu (tento de novo sozinho)");
            }
        };
        core.ProcessFailed += (_, e) =>
        {
            Diag?.ProcessFailed(e);
            OnProcessFailed(e);
        };
        core.NewWindowRequested += (_, e) =>
        {
            if (TopIdleLinkRequested is not null && TopIdle.IsTopIdleLink(e.Uri))
            {
                // O voto de dentro do jogo abre na janela de voto do deck, que percebe quando o voto entra.
                e.Handled = true;
                TopIdleLinkRequested(this, e.Uri);
                return;
            }
            PopupWindow.HandleNewWindow(env, ProfileName, FindForm(), e);
        };
        core.WebMessageReceived += (_, e) =>
        {
            string? message;
            try
            {
                message = e.TryGetWebMessageAsString();
            }
            catch (ArgumentException)
            {
                return;
            }
            if (message is not null && message.StartsWith(PaneDiag.MessagePrefix))
                Diag?.Page(message);
            else if (message == "F11")
                FullscreenRequested?.Invoke(this);
            else if (message is not null && Echo.Parse(message) is Echo.Click click)
                Clicked?.Invoke(this, click);
        };
        await core.AddScriptToExecuteOnDocumentCreatedAsync(KeyScript);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(Echo.CaptureScript);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(Restock.Script);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(Pvp.Script);
        // O leitor do Mercado da Comunidade (só lê; quem usa é a janela Mercado do deck).
        await core.AddScriptToExecuteOnDocumentCreatedAsync(Market.Script);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(FrameLimitScript);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(SpriteLimitScript);
        _fpsScript = null;
        await ApplyGameFpsAsync();
        await ApplyReduceMotionAsync();

        setup?.Invoke(core);
        if (prepare is not null)
            await prepare(core);
        _url = url;
        core.Navigate(url);
    }

    public void Reload()
    {
        Diag?.Write("recarregar");
        StartedAt = null;
        Core?.Reload();
    }

    /// <summary>Quantas vezes a conta recarregou limpo, para o autoteste.</summary>
    public int CleanReloads { get; private set; }

    /// <summary>
    /// Recarrega limpo (o ⟳ e o menu da conta; o vigia do jogo que não abre, da 2ª tentativa em diante): apaga o cache de
    /// disco do perfil e, só do site do jogo, o Cache Storage e os service workers, e recarrega sem o cache. O login (que o
    /// jogo guarda no localStorage), os cookies da Twitch e da Kick e as extensões ficam.
    /// </summary>
    public async Task ReloadCleanAsync()
    {
        StartedAt = null;
        if (Core is not CoreWebView2 core)
            return;
        Diag?.Write("recarregar limpo (sem o cache, o Cache Storage e os service workers do jogo)");
        string origin = GameOrigin(core);
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Network.clearBrowserCache", "{}");
            await core.CallDevToolsProtocolMethodAsync("Storage.clearDataForOrigin",
                JsonSerializer.Serialize(new { origin, storageTypes = "cache_storage,service_workers,shader_cache" }));
        }
        catch (Exception ex)
        {
            // Sem limpar, recarrega assim mesmo.
            Diag?.Write("não consegui limpar o cache: " + ex.Message);
        }
        CleanReloads++;
        // A página que nem carregou (sem internet, site fora do ar) volta para o endereço do jogo.
        if (_status == PaneStatus.Failed && _url is not null)
        {
            core.Navigate(_url);
            return;
        }
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Page.reload", "{\"ignoreCache\":true}");
        }
        catch
        {
            core.Reload();
        }
    }

    /// <summary>O site do jogo aberto na conta (https://pokeidle.io; no autoteste, https://deck.test).</summary>
    private string GameOrigin(CoreWebView2 core) =>
        Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) && uri.Host is "pokeidle.io" or "deck.test"
            ? uri.GetLeftPart(UriPartial.Authority)
            : _url is not null && Uri.TryCreate(_url, UriKind.Absolute, out var game) ? game.GetLeftPart(UriPartial.Authority) : "https://pokeidle.io";

    /// <summary>Quantas vezes os dados da conta foram apagados, para o autoteste.</summary>
    public int Wipes { get; private set; }

    private bool _wiping; // saindo do jogo para apagar: a página em branco não conta como carregada

    /// <summary>
    /// Apaga os dados da conta e abre o jogo do zero, na tela de login (o "Recarregar limpo" deixa o login e o id do
    /// aparelho, que o jogo guarda no localStorage, e os cookies). Do site do jogo, tudo: localStorage, IndexedDB, cookies
    /// (os do Cloudflare também), Cache Storage e service workers; e o cache de disco do perfil. Com
    /// <paramref name="everything"/>, os cookies de todos os sites também (sai da Twitch e da Kick). As extensões e as
    /// configurações delas ficam. Sai do jogo antes (página em branco), para ele não gravar a sessão de volta ao fechar.
    /// </summary>
    public async Task<bool> WipeAsync(bool everything)
    {
        if (Core is not CoreWebView2 core || _url is null)
            return false;
        string origin = GameOrigin(core);
        Diag?.Write(everything ? "apagar os dados da conta: tudo (o jogo e os cookies de todos os sites)" : "apagar os dados da conta: o jogo");
        StartedAt = null;
        StuckReloads = 0;
        _openSince = _failedSince = null;
        _wiping = true;
        bool ok;
        try
        {
            SetStatus(PaneStatus.Loading, "apagando os dados da conta…");
            var blank = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Left(object? sender, CoreWebView2NavigationCompletedEventArgs e) => blank.TrySetResult();
            core.NavigationCompleted += Left;
            core.Navigate("about:blank");
            await Task.WhenAny(blank.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            core.NavigationCompleted -= Left;

            await core.CallDevToolsProtocolMethodAsync("Network.clearBrowserCache", "{}");
            await core.CallDevToolsProtocolMethodAsync("Storage.clearDataForOrigin",
                JsonSerializer.Serialize(new { origin, storageTypes = "all" }));
            if (everything)
            {
                await core.CallDevToolsProtocolMethodAsync("Network.clearBrowserCookies", "{}");
            }
            else
            {
                // Os cookies do domínio (.pokeidle.io, os do Cloudflare) nem sempre saem pela origem.
                foreach (var cookie in await core.CookieManager.GetCookiesAsync(origin))
                    core.CookieManager.DeleteCookie(cookie);
            }
            ok = true;
        }
        catch (Exception ex)
        {
            ok = false;
            Diag?.Write("não consegui apagar tudo: " + ex.Message);
        }
        finally
        {
            _wiping = false;
        }
        Wipes++;
        SetStatus(PaneStatus.Loading, null);
        core.Navigate(_url);
        return ok;
    }

    // ---------- O jogo que não abre ----------

    /// <summary>
    /// Quanto esperar o jogo abrir depois que a página carregou (null: não vigia; o autoteste). Já aconteceu, ao abrir o
    /// deck, as 4 contas ficarem na tela de abertura do jogo (o portal com a barra de carga) sem conectar nem mostrar o
    /// login; recarregar resolve. Cada nova tentativa espera mais (2×, 3×… até 5× isto), para não martelar o site fora do ar.
    /// </summary>
    public TimeSpan? LoadTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Quantas vezes seguidas o deck recarregou a conta porque o jogo não abriu (volta a 0 quando abre).</summary>
    public int StuckReloads { get; private set; }

    /// <summary>Quantas vezes a página terminou de carregar, para o autoteste.</summary>
    public int Loads { get; private set; }

    /// <summary>A conta recarregou sozinha porque o jogo não abriu; o texto diz onde ele parou.</summary>
    public event Action<AccountPane, string>? GameStuck;

    private DateTime? _openSince; // a página carregou e o jogo ainda não abriu
    private DateTime? _failedSince; // a página não carregou (sem internet, site fora do ar)
    private bool _checkingOpen;
    private string? _url;

    /// <summary>
    /// Onde o jogo está: "jogo" (o socket conectou), um aviso do jogo por cima da tela ("aberta em outro lugar", quando
    /// outro aparelho pegou a sessão; "aviso do jogo: …", o aviso da conta: limite de contas no mesmo IP, sessão inválida,
    /// banimento), a tela que espera a pessoa (login, confirmar o e-mail, escolher o nick, o jogo aberto), "cloudflare" (o
    /// desafio de lá), ou "carga N% etapa" (ainda na tela de abertura). Os avisos vêm antes da carga: eles aparecem por
    /// cima dela, e recarregar ali tomaria a sessão do outro aparelho (ou não adiantaria).
    /// </summary>
    private const string OpenScript =
        "(() => {" +
        " try { const f = window.__deckPvp && window.__deckPvp.status(); if (f && f.connected && f.state) return 'jogo'; } catch { }" +
        " const shown = (id) => { const e = document.getElementById(id); return !!e && !e.classList.contains('hidden'); };" +
        " if (shown('sessao-tomada')) return 'aberta em outro lugar';" +
        " if (shown('alerta-conta')) { const t = document.getElementById('alerta-conta-titulo');" +
        "  return ('aviso do jogo: ' + (t ? t.textContent : '')).trim(); }" +
        " if (/just a moment|um momento/i.test(document.title)) return 'cloudflare';" +
        " for (const id of ['login', 'confirmar-email', 'escolher-nick', 'app']) {" +
        "  const e = document.getElementById(id);" +
        "  if (e && !e.classList.contains('hidden') && e.offsetParent !== null) return id; }" +
        " const p = document.getElementById('carga-pct'), t = document.getElementById('carga-etapa');" +
        " return ('carga ' + (p ? p.textContent : '?') + '% ' + (t ? t.textContent : '')).trim(); })()";

    /// <summary>
    /// Confere se o jogo abriu; preso na abertura além do tempo (ou com a página que não carregou), recarrega. O deck chama
    /// a cada 5 s; depois que o jogo abre, só volta a vigiar na próxima vez que a página carregar.
    /// </summary>
    public async Task CheckGameOpenAsync()
    {
        // Só no site do jogo: numa página de fora (um login do Google aberto na conta, por exemplo) não mexe.
        if (LoadTimeout is not TimeSpan timeout || Core is null || Off || _checkingOpen || !OnGame)
            return;
        var wait = timeout * Math.Min(StuckReloads + 1, 5);
        if (_status == PaneStatus.Failed && _failedSince is DateTime failed && _url is not null)
        {
            // A página nem carregou: tenta de novo (pela metade do tempo; o site pode ter voltado).
            if (DateTime.Now - failed < wait / 2)
                return;
            _failedSince = null;
            if (Diag is not null)
            {
                // O print mostra a página de erro do navegador (com o código do erro).
                _checkingOpen = true;
                try
                {
                    await Diag.SnapshotAsync(Core, "a página não carregou");
                }
                finally
                {
                    _checkingOpen = false;
                }
                if (Core is null || Off || _status != PaneStatus.Failed)
                    return; // desligou, ou já está carregando de novo
            }
            Stuck($"a página não carregou em {(wait / 2).TotalSeconds:0} s", () => Core.Navigate(_url));
            return;
        }
        if (_status != PaneStatus.Ok || _openSince is not DateTime since)
            return;
        _checkingOpen = true;
        try
        {
            string state;
            try
            {
                state = JsonSerializer.Deserialize<string>(await WebView.ExecuteScriptAsync(OpenScript)) ?? "";
            }
            catch
            {
                return;
            }
            if (_openSince != since)
                return; // carregou de novo enquanto conferia
            Diag?.State(state);
            if (!state.StartsWith("carga"))
            {
                _openSince = null;
                StuckReloads = 0;
                // Aberta em outro lugar, ou um aviso da conta: o deck não recarrega (tomaria a sessão de volta, ou não
                // adiantaria); fica o aviso no cabeçalho.
                if (state == "aberta em outro lugar" || state.StartsWith("aviso do jogo"))
                    Note($"{state}: o deck não recarrega");
                return;
            }
            // Da 2ª tentativa em diante, recarrega limpo (o cache e os service workers do site podem ser o que prende).
            if (DateTime.Now - since >= wait)
            {
                // O retrato e o print antes de recarregar: onde a tela de abertura parou.
                if (Diag is not null && Core is CoreWebView2 stuck)
                {
                    await Diag.SnapshotAsync(stuck, "o vigia vai recarregar");
                    if (_openSince != since || Off)
                        return;
                }
                Stuck($"o jogo não abriu em {wait.TotalSeconds:0} s ({state})" + (StuckReloads >= 1 ? ", limpando o cache" : ""),
                    StuckReloads >= 1 ? () => _ = ReloadCleanAsync() : Reload);
            }
        }
        finally
        {
            _checkingOpen = false;
        }
    }

    private void Stuck(string why, Action retry)
    {
        // Até a página nova carregar, não confere de novo (senão recarregaria duas vezes).
        _openSince = _failedSince = null;
        StuckReloads++;
        string text = $"{why}: recarregando ({StuckReloads}ª vez)";
        Diag?.Write("vigia: " + text);
        GameStuck?.Invoke(this, text);
        Note(text);
        retry();
    }

    /// <summary>
    /// O limite de quadros do jogo: troca o requestAnimationFrame da página (só a de cima, não os iframes) por um que
    /// espera o próximo quadro permitido com um relógio, sem acordar a cada quadro da tela. O limite é lido a cada
    /// pedido em window.__deckFps (0 ou 60+: sem limite), então muda na hora. window.__deckFrames conta os quadros
    /// entregues (o consumo.log e o autoteste leem).
    /// </summary>
    private const string FrameLimitScript =
        "(() => {" +
        " if (window !== window.top || window.__deckFrameLimit) return;" +
        " window.__deckFrameLimit = true; window.__deckFrames = 0;" +
        " const raf = window.requestAnimationFrame.bind(window), caf = window.cancelAnimationFrame.bind(window);" +
        " const pending = new Map(); let next = 1, due = 0, last = -1;" +
        " const limit = () => { const fps = +window.__deckFps || 0; return fps > 0 && fps < 60 ? fps : 0; };" +
        // Os pedidos do mesmo quadro recebem o mesmo t e passam juntos; o próximo quadro só vem 1/fps depois da entrega
        // (8 ms a menos, para cair no quadro da tela certo e não no seguinte). Conta do relógio da entrega, não do t: o
        // Chrome às vezes entrega com o t de um quadro que começou antes do relógio, e aí passaria um quadro a mais.
        " const frame = (id, cb) => raf(t => {" +
        "  if (!pending.delete(id)) return;" +
        "  const fps = limit(); if (fps && t !== last) { last = t; due = performance.now() + 1000 / fps - 8; }" +
        "  window.__deckFrames++; cb(t);" +
        " });" +
        " window.requestAnimationFrame = cb => {" +
        "  const id = next++, entry = {}; pending.set(id, entry);" +
        "  const wait = limit() ? due - performance.now() : 0;" +
        "  if (wait > 1) entry.timer = setTimeout(() => { if (pending.has(id)) entry.frame = frame(id, cb); }, wait);" +
        "  else entry.frame = frame(id, cb);" +
        "  return id;" +
        " };" +
        " window.cancelAnimationFrame = id => {" +
        "  const entry = pending.get(id); if (!entry) return;" +
        "  pending.delete(id); clearTimeout(entry.timer); if (entry.frame) caf(entry.frame);" +
        " };" +
        "})();";

    /// <summary>
    /// O ritmo dos sprites animados do jogo: o jogo anima cada um com um setInterval de 220 ms exatos (o único
    /// setInterval de 220 ms do jogo). O gancho cria esses com o ritmo de window.__deckSpriteMs e, se ele subir
    /// depois, pula as chamadas até dar o tempo novo (vale na hora). Se descer, só os sprites criados depois (ou a
    /// página recarregada) pegam o ritmo novo. Nunca para de vez: a chamada é que tira do ar o sprite que saiu da tela.
    /// window.__deckSpriteTicks conta as trocas de quadro (o consumo.log e o autoteste leem).
    /// </summary>
    private const string SpriteLimitScript =
        "(() => {" +
        " if (window !== window.top || window.__deckSpriteLimit) return;" +
        " window.__deckSpriteLimit = true; window.__deckSpriteTicks = 0;" +
        " const si = window.setInterval.bind(window);" +
        " const pick = () => Math.max(220, +window.__deckSpriteMs || 220);" +
        " window.setInterval = function (fn, ms, ...rest) {" +
        "  if (ms !== 220 || typeof fn !== 'function') return si(fn, ms, ...rest);" +
        "  const every = pick(); let last = 0;" +
        "  return si(function () {" +
        "   const want = pick();" +
        "   if (want > every + 5) { const now = performance.now(); if (now - last < want - 5) return; last = now; }" +
        "   window.__deckSpriteTicks++;" +
        "   return fn.apply(this, arguments);" +
        "  }, every, ...rest);" +
        " };" +
        "})();";

    /// <summary>Quadros por segundo do jogo (60: sem limite). Mudou depois, chame <see cref="ApplyGameFpsAsync"/>.</summary>
    public int GameFps { get; set; } = 60;

    /// <summary>O ritmo dos sprites animados, em ms (220: o do jogo). Mudou depois, chame <see cref="ApplyGameFpsAsync"/>.</summary>
    public int SpriteMs { get; set; } = 220;

    // O script que põe o limite nas páginas novas (trocado a cada mudança).
    private string? _fpsScript;

    /// <summary>Aplica o <see cref="GameFps"/> e o <see cref="SpriteMs"/> na página aberta, na hora, e nas que abrirem depois (recarregar).</summary>
    public async Task ApplyGameFpsAsync()
    {
        if (Core is not CoreWebView2 core)
            return;
        string set = $"window.__deckFps = {GameFps}; window.__deckSpriteMs = {SpriteMs};";
        try
        {
            if (_fpsScript is not null)
                core.RemoveScriptToExecuteOnDocumentCreated(_fpsScript);
            _fpsScript = await core.AddScriptToExecuteOnDocumentCreatedAsync(set);
            await core.ExecuteScriptAsync(set);
        }
        catch
        {
            // A página está fechando: na próxima abertura ela volta a ler a escolha.
        }
    }

    /// <summary>Menos animações (lido ao criar o navegador da conta; mudou depois, chame <see cref="ApplyReduceMotionAsync"/>).</summary>
    public bool ReduceMotion { get; set; }

    /// <summary>
    /// A página passa a ver o "reduzir movimento" do sistema ligado (ou desligado), e o próprio jogo
    /// desliga ~100 grupos de animação que rodam sem parar: selos pulsando, halos, cartas, o portal do
    /// login. Medido na tela de login: metade da CPU da página e da placa de vídeo. Vale até fechar o deck,
    /// inclusive depois de recarregar.
    /// </summary>
    public async Task ApplyReduceMotionAsync()
    {
        if (Core is null)
            return;
        var features = ReduceMotion ? "[{\"name\":\"prefers-reduced-motion\",\"value\":\"reduce\"}]" : "[]";
        try
        {
            await Core.CallDevToolsProtocolMethodAsync("Emulation.setEmulatedMedia", $"{{\"features\":{features}}}");
        }
        catch
        {
            // A página está fechando: na próxima abertura ela volta a ler a escolha.
        }
    }

    // Modo ULTRA Economia do jogo ("ULTRA Battery Saver"): a escolha fica no localStorage ("cfg-ultra"),
    // e o jogo acende a classe "modo-ultra" no <html> quando entra com ela ligada.
    // ligado: na tela; escolhido: liga quando entrar no jogo (ou quando a conta tiver equipe); normal; fora.
    private const string UltraStateScript =
        "(() => document.documentElement.classList.contains('modo-ultra') ? 'ligado'" +
        " : localStorage.getItem('cfg-ultra') === '1' ? 'escolhido'" +
        " : document.querySelector('#app:not(.hidden)') ? 'normal' : 'fora')()";

    // Liga pelo mesmo caminho do jogador (Configurações → interruptor do ULTRA, que fecha a janela sozinho)
    // e desliga pelo "Voltar ao jogo" da caixa. Fora do jogo, ou com a conta ainda sem equipe, só fica a
    // escolha: o próprio jogo liga ao entrar.
    private const string SetUltraScript =
        "(on => { const ultra = () => document.documentElement.classList.contains('modo-ultra');" +
        " if (!on) { document.querySelector('#ultra-sair')?.click(); localStorage.setItem('cfg-ultra', '0'); return !ultra(); }" +
        " if (ultra()) return true;" +
        " const app = document.querySelector('#app');" +
        " if (!app || app.classList.contains('hidden') || localStorage.getItem('cfg-ultra') === '1') { localStorage.setItem('cfg-ultra', '1'); return true; }" +
        " document.querySelector('#btn-config')?.click(); document.querySelector('#cfg-ultra')?.click(); return ultra(); })";

    /// <summary>O ULTRA do deck ligado: a cada carregamento do jogo a conta volta a ele.</summary>
    public bool Ultra { get; set; }

    /// <summary>A página é o jogo (deck.test: o jogo do autoteste).</summary>
    private bool OnGame => Core is not null && Uri.TryCreate(Core.Source, UriKind.Absolute, out var uri)
        && uri.Host is "pokeidle.io" or "deck.test";

    /// <summary>"ligado", "escolhido", "normal" ou "fora"; null se a página não é o jogo ou não respondeu.</summary>
    public async Task<string?> GetUltraAsync()
    {
        if (Off || !OnGame)
            return null;
        try
        {
            return JsonSerializer.Deserialize<string>(await WebView.ExecuteScriptAsync(UltraStateScript));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Liga ou desliga o Modo ULTRA Economia do jogo nesta conta, na hora (sem recarregar).</summary>
    public async Task<bool> SetUltraAsync(bool on)
    {
        if (Off || !OnGame)
            return false;
        try
        {
            return await WebView.ExecuteScriptAsync($"{SetUltraScript}({(on ? "true" : "false")})") == "true";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>A recompra desta conta (a mesma instância do deck.json: a janela da recompra muda ela).</summary>
    public RestockConfig RestockConfig { get; set; } = new();

    /// <summary>Manda a configuração da recompra para a página (o script dela já está lá desde o carregamento).</summary>
    public async Task PushRestockAsync()
    {
        if (Off || !OnGame)
            return;
        try
        {
            await WebView.ExecuteScriptAsync(Restock.ConfigureScript(RestockConfig));
        }
        catch
        {
            // A página está fechando: a próxima chega configurada no carregamento.
        }
    }

    /// <summary>O estoque, o ouro e o registro da recompra; null se a página não é o jogo ou não respondeu.</summary>
    public async Task<RestockStatus?> ReadRestockAsync()
    {
        if (Off || !OnGame)
            return null;
        RestockStatus? status;
        try
        {
            status = Restock.Parse(await WebView.ExecuteScriptAsync(Restock.SummaryScript));
        }
        catch
        {
            return null;
        }
        // Página que perdeu a configuração (carregou sem o aviso de pronta): manda de novo.
        if (status is { Configured: false })
            await PushRestockAsync();
        return status;
    }

    /// <summary>
    /// O carrinho do cabeçalho: verde com a recompra ligada e o jogo conectado, amarelo ligada esperando o jogo
    /// (ou em pausa), apagado desligada. A dica tem o estoque e a última linha do registro.
    /// </summary>
    public void ShowRestock(RestockStatus? status)
    {
        bool on = RestockConfig.On;
        if (_fitted.Restock != on)
            FitHeader();
        bool working = on && status is { Connected: true, Paused: false };
        _restock.ForeColor = !on ? Theme.Muted : working ? Theme.Ok : Theme.Warn;
        RestockState = !on ? "desligada" : working ? "ligada" : "esperando";
        var lines = new List<string>
        {
            !on ? "Recompra desligada: clique para configurar e ligar (pokebolas, revives e poções no Market do NPC)"
            : status is null or { Connected: false } ? "Recompra ligada: esperando o jogo conectar"
            : status.Paused ? "Recompra ligada, em pausa (veja o registro)"
            : "Recompra ligada",
        };
        if (status is { Connected: true })
        {
            var ball = status.Balls?.FirstOrDefault(b => b.Id == RestockConfig.Balls.Id);
            lines.Add($"Ouro {Restock.N(status.Gold)} · {ball?.Name ?? "pokebola"} {Restock.N(ball?.Have ?? 0)} · " +
                      $"revives {Restock.N(status.ReviveHave)} · poções {Restock.N(status.PotionHave)}");
        }
        if (status?.Log is { Count: > 0 } log)
            lines.Add(log[^1]);
        _restock.ToolTipText = string.Join("\n", lines);
    }

    /// <summary>
    /// Roda um script na página do jogo da conta e devolve o resultado (o JSON do ExecuteScript); null com a conta
    /// desligada, fora do jogo ou sem resposta.
    /// </summary>
    public async Task<string?> RunGameScriptAsync(string script)
    {
        if (Off || !OnGame)
            return null;
        try
        {
            return await WebView.ExecuteScriptAsync(script);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A fila de PvP desta conta ligada (deck.json <c>Pvp</c>).</summary>
    public bool PvpOn { get; set; }

    /// <summary>Como as partidas da fila aparecem (deck.json <c>PvpScreen</c>: completa, mini ou off).</summary>
    public string PvpScreen { get; set; } = PvpScreens.Off;

    /// <summary>A velocidade das partidas mostradas (deck.json <c>PvpSpeed</c>; 0 = a do jogo).</summary>
    public double PvpSpeed { get; set; }

    /// <summary>"economia" ou "ultra" quando o jogo esconde as partidas de qualquer jeito (a última leitura da página).</summary>
    public string? PvpNoScene { get; private set; }

    /// <summary>Onde abrir o menu do botão direito no ⚔: logo abaixo dele, sem cobrir o cabeçalho (o menu cresce para a esquerda).</summary>
    public Point PvpMenuPoint => _header.PointToScreen(new Point(_pvp.Bounds.Right, _header.Height + 2));

    /// <summary>Liga ou desliga a fila de PvP na página, com a tela e a velocidade (o script dela já está lá desde o carregamento).</summary>
    public async Task PushPvpAsync()
    {
        if (Off || !OnGame)
            return;
        try
        {
            await WebView.ExecuteScriptAsync(Pvp.SetScript(PvpOn, PvpScreen, PvpSpeed));
        }
        catch
        {
            // A página está fechando: a próxima recebe na primeira leitura.
        }
    }

    /// <summary>A fila de PvP da página; página que recarregou (e voltou desligada) recebe o liga de novo.</summary>
    public async Task<PvpStatus?> ReadPvpAsync()
    {
        if (Off || !OnGame)
            return null;
        PvpStatus? status;
        string raw;
        try
        {
            raw = await WebView.ExecuteScriptAsync(Pvp.StatusScript);
            status = Pvp.Parse(raw);
        }
        catch
        {
            return null;
        }
        // O script não está na página (ela carregou sem ele): põe agora; ele acha o socket do jogo pelo do Rotom.
        if (raw == "null" && PvpOn)
        {
            try
            {
                await WebView.ExecuteScriptAsync(Pvp.Script);
            }
            catch
            {
                return null;
            }
        }
        if (status is null || status.On != PvpOn || status.WantScreen != PvpScreens.Normalized(PvpScreen)
            || status.WantSpeed != PvpScreens.NormalizedSpeed(PvpSpeed))
            await PushPvpAsync();
        return status;
    }

    /// <summary>
    /// O ⚔ do cabeçalho: verde com a fila ligada e a conta na fila (ou numa partida), amarelo ligada esperando (o jogo, a
    /// pausa entre partidas, uma recusa), apagado desligada. A dica tem há quanto tempo está na fila, as partidas desde
    /// que ligou e a última linha do registro.
    /// </summary>
    public void ShowPvp(PvpStatus? status)
    {
        bool on = PvpOn;
        if (_fitted.Pvp != on)
            FitHeader();
        bool connected = status is { Connected: true, On: true };
        PvpWaiting = connected || !on ? null : Pvp.Waiting(status);
        // VIP: a auto-fila do jogo ligada já é a fila funcionando (o jogo põe a conta na fila depois de cada partida).
        bool working = on && connected && (status!.Na || status.Vip && status.Auto);
        _pvp.ForeColor = !on ? Theme.Muted : working ? Theme.Ok : Theme.Warn;
        PvpState = !on ? "desligada" : working ? "na fila" : "esperando";
        var lines = new List<string>
        {
            !on ? "Fila de PvP desligada: clique para ligar (a conta entra na fila do PvP ranqueado e volta depois de cada partida)"
            : !connected ? "Fila de PvP ligada: " + PvpWaiting
            : status!.Vip && status.Auto ? "Auto-fila do jogo ligada (VIP): o jogo põe a conta na fila depois de cada partida" +
                                           (status.Na ? $"; na fila há {Pvp.Duration(status.NaSegundos)}" : "")
            : status.Na ? $"Na fila de PvP há {Pvp.Duration(status.NaSegundos)}" + (status.Tamanho > 1 ? $" ({status.Tamanho} na fila)" : "")
            : status.EsperaSegundos > 0 ? $"Fila de PvP ligada: entra de novo em {Pvp.Duration(status.EsperaSegundos)}"
            : "Fila de PvP ligada: entrando na fila",
        };
        if (connected && status!.Vip && !status.Auto)
            lines.Add("Conta VIP: ligando a auto-fila do jogo");
        string screen = PvpScreens.Normalized(PvpScreen);
        if (status is not null)
            PvpNoScene = status.NoScene;
        lines.Add(screen == PvpScreens.Off
            ? "Partidas escondidas: não abrem na tela, o jogo só avisa o resultado"
            : $"Partidas {PvpScreens.Label(screen)}s ({(screen == PvpScreens.Mini ? "a janelinha do canto" : "o replay na tela")})," +
              $" velocidade {PvpScreens.SpeedLabel(PvpSpeed)}" +
              (PvpScreens.NormalizedSpeed(PvpSpeed) > 0 && on && status is { SpeedReady: false, SpeedError: string why } ? $" (não aplicada: {why})" : ""));
        if (screen != PvpScreens.Off && PvpScreens.NoSceneNote(PvpNoScene) is string noScene)
            lines.Add("⚠ " + noScene);
        if (status is { EcoByDeck: true })
            lines.Add("Battery Saver desligado pelo deck para mostrar as partidas (religa ao voltar para Escondida ou desligar a fila)");
        lines.Add("Botão direito: mostrar, minimizar ou esconder as partidas e a velocidade");
        if (status is { Connected: false, Seen: > 0 })
            lines.Add($"(o script viu {status.Seen} mensagens do jogo{(status.Welcomed ? ", com o welcome" : "")})");
        if (connected && status!.Wins + status.Losses > 0)
            lines.Add($"{status.Wins + status.Losses} partidas desde que ligou: {status.Wins} vitórias, {status.Losses} derrotas" +
                      (status.Points is double points ? $" · {points:0} pontos" : ""));
        if (status?.Log is { Count: > 0 } log)
            lines.Add(log[^1]);
        _pvp.ToolTipText = string.Join("\n", lines);
    }

    /// <summary>Por que a fila ligada não está conectada (null conectada ou desligada), para o autoteste.</summary>
    public string? PvpWaiting { get; private set; }

    /// <summary>"desligada", "na fila" ou "esperando" (a cor do ⚔), para o autoteste.</summary>
    public string PvpState { get; private set; } = "desligada";

    /// <summary>"desligada", "ligada" ou "esperando" (a cor do carrinho), para o autoteste.</summary>
    public string RestockState { get; private set; } = "desligada";

    /// <summary>
    /// Confere se a conta está parada na tela de login. Ali o jogo mantém o captcha do Cloudflare, que
    /// refaz o desafio sozinho e vai juntando memória (medido: +60 MB a cada 30 s no começo).
    /// </summary>
    public async Task CheckLoginScreenAsync()
    {
        bool login = false;
        if (Core is not null && !Off && _status == PaneStatus.Ok)
        {
            try
            {
                login = await WebView.ExecuteScriptAsync(LoginScript) == "true";
            }
            catch
            {
                return;
            }
        }
        if (login == _loginScreen)
            return;
        _loginScreen = login;
        Diag?.Write(login ? "parada na tela de login" : "saiu da tela de login");
        _info.ToolTipText = login
            ? "Na tela de login o jogo mantém o captcha do Cloudflare, que refaz o desafio sozinho e vai juntando " +
              "memória (centenas de MB em poucas horas). Entre na conta ou desligue-a no botão de desligar, no canto direito do cabeçalho."
            : null;
        RefreshHeader();
    }

    /// <summary>O texto ao lado do nome no cabeçalho, para o autoteste.</summary>
    public string HeaderInfo => _info.Text ?? "";

    public void SetMuted(bool muted)
    {
        if (Core is null)
            return;
        Core.IsMuted = muted;
        UpdateMuteButton();
        SettingsChanged?.Invoke(this);
    }

    /// <summary>O nick salvo pelo jogo nesta conta, para a recompensa do voto ir para o personagem certo.</summary>
    public async Task<string?> ReadNickAsync()
    {
        if (Core is null || !Core.Source.StartsWith("https://pokeidle.io", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            return TopIdle.ParseNick(await WebView.ExecuteScriptAsync("localStorage.getItem('nick')"));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Botão de voto do cabeçalho: laranja quando o voto está liberado, com o tempo que falta quando não.</summary>
    public void SetVote(string text, Color color, bool highlight, string tooltip)
    {
        if (Off)
        {
            HideVote();
            return;
        }
        _voteText = text;
        HasVote = true;
        _vote.Text = _voteShort ? VoteIcon(text) : text;
        _vote.ForeColor = color;
        _vote.Font = highlight ? _voteBold : _header.Font;
        _vote.ToolTipText = tooltip;
        // No cabeçalho só o voto liberado (é um aviso); o tempo que falta fica no menu ⋯.
        _vote.Visible = highlight;
        // Depois de mostrar (e do negrito): a conta do espaço tem de ver o voto do tamanho que ele vai ter.
        FitHeader();
    }

    public void HideVote()
    {
        HasVote = false;
        _vote.Visible = false;
        FitHeader();
    }

    /// <summary>Só o 🗳 do texto do voto (ele vem antes de tudo e ocupa dois chars).</summary>
    private static string VoteIcon(string text) => text.Length > 2 ? text[..2] : text;

    public string? NoteText => DateTime.Now < _noteUntil ? _note : null;

    /// <summary>
    /// Desliga (a página do jogo vira em branco e o aviso toma o lugar dela) ou religa a conta. Religar só
    /// arruma o painel; quem abre o jogo de novo é o deck.
    /// </summary>
    public void SetOff(bool off)
    {
        if (off != Off)
            Diag?.Write(off ? "conta desligada" : "conta ligada");
        Off = off;
        _offNote.Text = $"{AccountName} desligada\n\nNada roda nela: jogo, extensões e voto ficam parados.\n" +
                        "Clique no botão de ligar, no canto direito do cabeçalho, para ligar.";
        _offNote.Visible = off;
        WebView.Visible = !off;
        _reload.Available = _mute.Available = !off;
        // Desligada, o ⏻ aparece para ligar; ligada, desligar fica no menu ⋯.
        _power.Available = off;
        _power.ToolTipText = "Ligar esta conta: o jogo abre de novo, com as extensões dela";
        _power.ForeColor = Theme.Accent;
        if (off)
        {
            HideVote();
            StartedAt = null;
            SetStatus(PaneStatus.Starting, "desligada");
            Core?.Navigate("about:blank");
        }
        else
        {
            SetStatus(PaneStatus.Starting, "ligando…");
        }
        FitHeader();
    }

    /// <summary>O texto de status do cabeçalho ("desligada", "carregando…"), para o autoteste.</summary>
    public string? StatusText => _statusText;

    /// <summary>Um aviso rápido no cabeçalho (some sozinho em alguns segundos).</summary>
    public void Note(string text)
    {
        _note = text;
        _noteUntil = DateTime.Now.AddSeconds(4);
        RefreshHeader();
    }

    /// <summary>As extensões desta conta que têm popup: ficam no menu ⋯ (com o ícone de cada uma).</summary>
    public void SetExtensions(IEnumerable<ExtensionInfo> extensions)
    {
        _extensions.Clear();
        _extensions.AddRange(extensions);
    }

    /// <summary>O ícone de uma extensão para o menu (null: sem ícone).</summary>
    public static Image? ExtensionIcon(ExtensionInfo extension) => LoadIcon(extension);

    /// <summary>
    /// Atualiza nome, bolinha de status e o tempo rodando (chamado a cada segundo). A bolinha: verde caçando, amarela
    /// carregando ou na tela de login, vermelha desligada ou com erro; a dica dela diz o porquê.
    /// </summary>
    public void RefreshHeader()
    {
        _name.Text = AccountName;
        _dot.ForeColor = Off || _status == PaneStatus.Failed ? Theme.Bad
            : _loginScreen || _status != PaneStatus.Ok || _statusText is not null ? Theme.Warn
            : Theme.Ok;
        _dot.ToolTipText = Off ? "Desligada"
            : _status == PaneStatus.Failed ? "Com erro: " + _statusText
            : _loginScreen ? "Na tela de login: entre na conta ou desligue-a (parada ali ela vai juntando memória)"
            : _statusText is not null ? Capitalize(_statusText)
            : _status == PaneStatus.Ok ? "No jogo" : "Abrindo…";
        string uptime = StartedAt is DateTime started ? FormatUptime(DateTime.Now - started) : "";
        var info = DateTime.Now < _noteUntil ? "· " + _note
            : _statusText is not null ? "· " + _statusText
            : _loginScreen ? "· na tela de login"
            // Na miniatura o ritmo da sessão vale mais que o tempo rodando (que fica na dica da bolinha).
            : _compact && Stats is not null ? "· " + Stats
            : uptime.Length > 0 ? "· " + uptime + (Stats is not null ? " · " + Stats : "")
            : "";
        if (uptime.Length > 0 && Health == PaneHealth.Ok)
            _dot.ToolTipText = $"No jogo há {uptime}";
        if (_info.Text != info)
        {
            _info.Text = info;
            FitHeader();
        }
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpper(text[0]) + text[1..];

    private void SetStatus(PaneStatus status, string? text)
    {
        _status = status;
        _statusText = text;
        RefreshHeader();
    }

    private void OnProcessFailed(CoreWebView2ProcessFailedEventArgs e)
    {
        if (Off)
            return;
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
                // A aba morreu de qualquer jeito; recarregar já põe a conta de volta no jogo.
                SetStatus(PaneStatus.Failed, "a página caiu, recarregando…");
                Reload();
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                SetStatus(PaneStatus.Loading, "a página travou, esperando ela voltar…");
                break;
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                SetStatus(PaneStatus.Failed, "o navegador interno fechou: feche e abra o deck");
                break;
        }
    }

    private void UpdateMuteButton() => _mute.Text = Muted ? Theme.MutedGlyph : Theme.SoundGlyph;

    /// <summary>
    /// Conta estreita (em colunas): primeiro o voto fica só com o 🗳, depois sai o tempo rodando e por último os botões
    /// perdem a folga, para nenhum sumir por falta de espaço. Com espaço de novo, tudo volta.
    /// </summary>
    private void FitHeader()
    {
        int room = _header.DisplayRectangle.Width - 8;
        int Needed() => _header.Items.Cast<ToolStripItem>()
            .Where(i => i.Available)
            .Sum(i => i.GetPreferredSize(Size.Empty).Width + i.Margin.Horizontal);
        // A conta pelo tamanho preferido sai menor que o desenho (as setinhas dos menus, o ícone da extensão): além dela,
        // confere no próprio cabeçalho se algum botão ficou sem lugar.
        bool Over()
        {
            if (Needed() > room)
                return true;
            _header.PerformLayout();
            return _header.Items.Cast<ToolStripItem>().Any(i => i.Available && i.Placement == ToolStripItemPlacement.None);
        }
        var buttons = _header.Items.Cast<ToolStripItem>().Where(i => i.Alignment == ToolStripItemAlignment.Right).ToList();
        buttons.ForEach(b => Roomy(b));
        SetShortVote(false);
        _info.Available = true;
        _fitted = (RestockConfig.On, PvpOn);
        // O carrinho e o ⚔ só aparecem ligados (como aviso, clicáveis); desligados, ficam no menu ⋯.
        _restock.Available = !Off && RestockConfig.On;
        _pvp.Available = !Off && PvpOn;
        if (Over())
            SetShortVote(true);
        if (Over())
            _info.Available = false;
        if (Over())
            buttons.ForEach(b => Roomy(b, tight: true));
        _header.PerformLayout();
    }

    // Com que recompra e fila (ligadas ou não) o cabeçalho foi acertado: ligar uma delas pode trazer o botão de volta.
    private (bool Restock, bool Pvp) _fitted;

    private void SetShortVote(bool shortVote)
    {
        _voteShort = shortVote;
        var text = shortVote ? VoteIcon(_voteText) : _voteText;
        if (_vote.Text != text)
            _vote.Text = text;
    }

    private static string FormatUptime(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{(int)time.TotalHours} h {time.Minutes:00} min" : $"{time.Minutes} min";

    // Ícone em SVG ou faltando: o botão fica com a peça de quebra-cabeça.
    private static Image? LoadIcon(ExtensionInfo extension) => ExtensionStore.IconOf(extension, 20);
}
