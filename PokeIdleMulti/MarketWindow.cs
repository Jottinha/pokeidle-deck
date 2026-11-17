using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PokeIdleMulti;

/// <summary>
/// A janela do analisador do Mercado da Comunidade (botão $ Mercado na barra de cima): uma página em cartões
/// (mercado.html) com as oportunidades separadas em "Ganhar gemas" e "Ganhar gold". Lê o mercado pela conta principal
/// (ou pela primeira no jogo, se a principal não estiver) e, em "Abrir no jogo", leva a conta escolhida até o item no
/// mercado do jogo. SÓ LÊ: o deck nunca compra nem anuncia.
/// </summary>
internal sealed class MarketWindow : Form
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly Func<IReadOnlyList<AccountPane>> _panes;
    private readonly Func<int> _preferred;
    private readonly Action<int> _bringToFront;
    private readonly CoreWebView2Environment _env;
    private readonly WebView2 _view = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Theme.Back };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 60_000 };
    private int _openAccount;
    private bool _busy;

    /// <param name="preferred">A conta principal agora (a de leitura e a primeira da lista de "abrir no jogo").</param>
    /// <param name="bringToFront">Põe a conta à vista no deck (a principal do layout Principal, ou o foco).</param>
    public MarketWindow(CoreWebView2Environment env, Func<IReadOnlyList<AccountPane>> panes, Func<int> preferred, Action<int> bringToFront)
    {
        _env = env;
        _panes = panes;
        _preferred = preferred;
        _bringToFront = bringToFront;
        _openAccount = preferred();
        Text = "Mercado da Comunidade · oportunidades";
        Icon = Theme.AppIcon;
        BackColor = Theme.Back;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(760, 480);
        Size = new Size(1240, 800);
        Controls.Add(_view);
        _timer.Tick += async (_, _) => await RefreshAsync();
        Shown += async (_, _) => await StartAsync();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(this);
    }

    private async Task StartAsync()
    {
        try
        {
            await _view.EnsureCoreWebView2Async(_env);
        }
        catch (Exception ex)
        {
            DeckBox.Show(this, "Não consegui abrir a janela do mercado.\n\n" + ex.Message, Text, DeckBoxIcon.Error);
            Close();
            return;
        }
        var core = _view.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.WebMessageReceived += (_, e) => _ = OnMessageAsync(e.WebMessageAsJson);
        // Links da página (nenhum hoje) nunca navegam a janela para fora.
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && e.Uri != "about:blank")
                e.Cancel = true;
        };
        core.NavigateToString(Market.Page);
    }

    private void Post(object message)
    {
        if (_view.CoreWebView2 is { } core && !IsDisposed)
            core.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json));
    }

    private void Toast(string text) => Post(new { type = "toast", text });

    private async Task OnMessageAsync(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var m = doc.RootElement;
        switch (m.TryGetProperty("cmd", out var c) ? c.GetString() : null)
        {
            case "ready":
                SendAccounts();
                await RefreshAsync();
                break;
            case "refresh":
                await RefreshAsync();
                break;
            case "auto":
                _timer.Enabled = m.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.True;
                break;
            case "account":
                if (m.TryGetProperty("index", out var i) && i.TryGetInt32(out int index))
                    _openAccount = index;
                break;
            case "open":
                if (m.TryGetProperty("itemId", out var item) && item.TryGetInt32(out int itemId))
                {
                    bool gems = m.TryGetProperty("gems", out var g) && g.ValueKind == JsonValueKind.True;
                    int account = m.TryGetProperty("account", out var a) && a.TryGetInt32(out int ac) ? ac : _openAccount;
                    await OpenInGameAsync(account, itemId, gems);
                }
                break;
        }
    }

    /// <summary>A lista do "Abrir no jogo em": as contas ligadas, começando pela escolhida (a principal).</summary>
    private void SendAccounts()
    {
        var accounts = _panes().Where(p => !p.Off).Select(p => new { index = p.Index, name = p.AccountName }).ToList();
        if (accounts.Count > 0 && accounts.All(a => a.index != _openAccount))
            _openAccount = accounts[0].index;
        Post(new { type = "accounts", accounts, selected = _openAccount });
    }

    /// <summary>A conta de leitura: a principal, ou a primeira no jogo se a principal não estiver.</summary>
    private AccountPane? ReadingPane()
    {
        var panes = _panes();
        int preferred = _preferred();
        return panes.Where(p => p.Index == preferred && p.Health == PaneHealth.Ok)
            .Concat(panes.Where(p => p.Health == PaneHealth.Ok))
            .FirstOrDefault();
    }

    /// <summary>Pede uma leitura nova e espera a resposta (até 8 s).</summary>
    private async Task RefreshAsync()
    {
        if (_busy || IsDisposed || _view.CoreWebView2 is null)
            return;
        var pane = ReadingPane();
        if (pane is null)
        {
            Status("<span class=\"warn\">Nenhuma conta está no jogo agora: entre numa conta e clique em Atualizar.</span>");
            return;
        }
        _busy = true;
        Post(new { type = "busy", on = true });
        try
        {
            var before = Market.Parse(await pane.RunGameScriptAsync(Market.SummaryScript));
            if (before is null)
            {
                Status($"<span class=\"warn\">{Esc(pane.AccountName)} ainda não tem o leitor do mercado: recarregue a conta (⟳ no cabeçalho) e clique em Atualizar.</span>");
                return;
            }
            await pane.RunGameScriptAsync(Market.ScanScript);
            var snapshot = before;
            for (int i = 0; i < 16 && !IsDisposed; i++)
            {
                await Task.Delay(500);
                snapshot = Market.Parse(await pane.RunGameScriptAsync(Market.SummaryScript)) ?? snapshot;
                if (snapshot.At is DateTime at && at != before.At)
                    break;
            }
            if (IsDisposed)
                return;
            if (snapshot.At is null)
            {
                Status("<span class=\"warn\">O jogo não respondeu à leitura do mercado. Tente de novo em alguns segundos.</span>");
                return;
            }
            string gem = snapshot.GemValue is double v ? $"1 gema ≈ <b>{Market.GoldShort(v)} gold</b>" : "valor da gema: sem vendas nas duas moedas";
            Status($"Lido às <b>{snapshot.At:HH:mm:ss}</b> pela <b>{Esc(pane.AccountName)}</b> · {gem} · " +
                   $"{snapshot.ItemsWithListings} itens à venda · taxa de venda {snapshot.GoldTax:P0} em gold e 15% em gemas");
            Post(new { type = "data", chances = snapshot.Chances });
        }
        catch (Exception ex)
        {
            Status($"<span class=\"warn\">Não consegui ler o mercado: {Esc(ex.Message)}</span>");
        }
        finally
        {
            _busy = false;
            Post(new { type = "busy", on = false });
        }
    }

    /// <summary>
    /// Leva a conta até o item no mercado do jogo: põe a conta à vista no deck, sai do Ultra (que esconde o jogo) e faz
    /// na página os cliques de abrir o mercado, a aba Itens, o item e a moeda. Não compra.
    /// </summary>
    private async Task OpenInGameAsync(int account, int itemId, bool gems)
    {
        var pane = _panes().FirstOrDefault(p => p.Index == account);
        if (pane is null || pane.Off)
        {
            Toast("Essa conta está desligada.");
            return;
        }
        if (pane.Health != PaneHealth.Ok)
        {
            Toast($"{pane.AccountName} não está no jogo agora.");
            return;
        }
        _bringToFront(account);
        if (pane.Ultra)
            await pane.SetUltraAsync(false);
        await Task.Delay(400);
        if (await pane.RunGameScriptAsync(Market.OpenScript(itemId, gems)) is not "true")
        {
            Toast($"{pane.AccountName} ainda não tem o leitor do mercado: recarregue a conta (⟳).");
            return;
        }
        Toast($"Abrindo em {pane.AccountName}…");
        for (int i = 0; i < 30 && !IsDisposed; i++)
        {
            await Task.Delay(300);
            var s = Market.Parse(await pane.RunGameScriptAsync(Market.SummaryScript));
            if (s?.LastOpen is string result && result != "abrindo")
            {
                Toast(result == "ok" ? $"Pronto: o item está aberto no mercado da {pane.AccountName}." : $"Não deu para abrir: {result}.");
                return;
            }
        }
    }

    private void Status(string html) => Post(new { type = "status", html });

    private static string Esc(string text) => System.Net.WebUtility.HtmlEncode(text);

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Dispose();
        _view.Dispose();
        base.OnFormClosed(e);
    }
}
