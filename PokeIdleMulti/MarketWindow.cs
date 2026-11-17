namespace PokeIdleMulti;

/// <summary>
/// A janela do analisador do Mercado da Comunidade (botão Mercado na barra de cima). Lê o mercado pela conta escolhida
/// (o mercado.js da página dela pede ao servidor as mesmas leituras da tela do mercado) e lista as oportunidades de
/// revenda, com o lucro estimado em gold depois da taxa. SÓ LÊ: o deck nunca compra nem anuncia; quem compra é você, no
/// jogo. Duplo clique numa linha explica a conta.
/// </summary>
internal sealed class MarketWindow : Form
{
    private readonly Func<IReadOnlyList<AccountPane>> _panes;
    private readonly UiSelect _account = new() { Width = 180 };
    private readonly UiButton _refresh = new("Atualizar", UiLook.Primary);
    private readonly UiSwitch _auto = new("Atualizar sozinho a cada 1 min");
    private readonly UiStepper _minPercent = new() { Minimum = 0, Maximum = 1000, Increment = 5, Value = 10, Width = 110 };
    private readonly UiStepper _minSales = new() { Minimum = 0, Maximum = 100000, Increment = 1, Value = 3, Width = 110 };
    private readonly Label _status = Ui.Label("Clique em Atualizar para ler o mercado.", muted: true);
    private readonly UiList _list = new(26) { FillLast = true };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 60_000 };
    private Market.Snapshot? _last;
    private bool _busy;

    private sealed record AccountChoice(AccountPane Pane)
    {
        public override string ToString() => Pane.AccountName;
    }

    public MarketWindow(Func<IReadOnlyList<AccountPane>> panes, int preferred)
    {
        _panes = panes;
        Text = "Mercado da Comunidade · oportunidades";
        Ui.Dialog(this);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimumSize = new Size(820, 420);
        ClientSize = new Size(1000, 600);
        ShowInTaskbar = true;

        // Linha 1: de qual conta ler, atualizar e o automático; o estado da última leitura.
        var row1 = Row();
        row1.Controls.Add(Centered(Ui.Label("Ler pela conta")));
        row1.Controls.Add(_account);
        row1.Controls.Add(_refresh);
        row1.Controls.Add(Centered(_auto));
        var top1 = new Panel { Dock = DockStyle.Top, Height = 40 };
        top1.Controls.Add(row1);

        // Linha 2: os filtros.
        var row2 = Row();
        row2.Controls.Add(Centered(Ui.Label("Lucro mínimo (%)")));
        row2.Controls.Add(_minPercent);
        row2.Controls.Add(Centered(Ui.Label("Vendidos em 7 dias, no mínimo")));
        row2.Controls.Add(_minSales);
        var top2 = new Panel { Dock = DockStyle.Top, Height = 40 };
        top2.Controls.Add(row2);

        _status.AutoSize = false;
        _status.Dock = DockStyle.Top;
        _status.Height = 26;
        _status.TextAlign = ContentAlignment.MiddleLeft;

        _list.Columns.Add("Item", 200);
        _list.Columns.Add("Oportunidade", 160);
        _list.Columns.Add("Comprar por", 130, HorizontalAlignment.Right);
        _list.Columns.Add("Revender por", 130, HorizontalAlignment.Right);
        _list.Columns.Add("Lucro (gold)", 130, HorizontalAlignment.Right);
        _list.Columns.Add("%", 70, HorizontalAlignment.Right);
        _list.Columns.Add("Vendidos 7d", 90, HorizontalAlignment.Right);
        _list.DoubleClick += (_, _) => ExplainSelected();

        var close = new UiButton("Fechar", UiLook.Outline);
        close.Click += (_, _) => Close();
        CancelButton = close;
        var footer = new UiFooter("Só lê o mercado: o deck nunca compra nem anuncia. Duplo clique numa linha explica a conta.", close);

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 14, 10) };
        body.Controls.Add(new UiFrame(_list) { Dock = DockStyle.Fill });
        body.Controls.Add(Ui.Gap(6, DockStyle.Top));
        body.Controls.Add(_status);
        body.Controls.Add(top2);
        body.Controls.Add(top1);
        Controls.Add(body);
        Controls.Add(footer);

        FillAccounts(preferred);
        _refresh.Click += async (_, _) => await RefreshAsync();
        _auto.CheckedChanged += (_, _) => _timer.Enabled = _auto.Checked;
        _timer.Tick += async (_, _) => await RefreshAsync();
        _minPercent.ValueChanged += (_, _) => Fill();
        _minSales.ValueChanged += (_, _) => Fill();
        Shown += async (_, _) => await RefreshAsync();
    }

    private static FlowLayoutPanel Row() => new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = false,
        Padding = new Padding(0, 4, 0, 0),
    };

    private static Control Centered(Control control)
    {
        control.Margin = new Padding(0, 6, 10, 0);
        return control;
    }

    /// <summary>As contas ligadas; começa pela <paramref name="preferred"/> (a principal) se ela estiver ligada.</summary>
    private void FillAccounts(int preferred)
    {
        _account.Items.Clear();
        foreach (var pane in _panes().Where(p => !p.Off))
            _account.Items.Add(new AccountChoice(pane));
        int index = _account.Items.Cast<AccountChoice>().ToList().FindIndex(c => c.Pane.Index == preferred);
        _account.SelectedIndex = _account.Items.Count == 0 ? -1 : Math.Max(0, index);
    }

    /// <summary>Pede uma leitura nova pela conta escolhida e espera a resposta (até 8 s).</summary>
    private async Task RefreshAsync()
    {
        if (_busy || IsDisposed)
            return;
        if (_account.SelectedItem is not AccountChoice { Pane: var pane })
        {
            _status.Text = "Nenhuma conta ligada para ler o mercado.";
            return;
        }
        _busy = true;
        _refresh.Enabled = false;
        _status.Text = $"Lendo o mercado por {pane.AccountName}…";
        try
        {
            var before = Market.Parse(await pane.RunGameScriptAsync(Market.SummaryScript));
            if (before is null)
            {
                _status.Text = $"{pane.AccountName} ainda não tem o leitor do mercado: recarregue a conta (⟳ no cabeçalho) e tente de novo.";
                return;
            }
            if (!before.Connected)
            {
                _status.Text = $"{pane.AccountName} não está conectada ao jogo (tela de login ou carregando). Escolha outra conta.";
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
                _status.Text = "O jogo não respondeu à leitura do mercado. Tente de novo em alguns segundos.";
                return;
            }
            _last = snapshot;
            Fill();
        }
        catch (Exception ex)
        {
            _status.Text = "Não consegui ler o mercado: " + ex.Message;
        }
        finally
        {
            _busy = false;
            if (!IsDisposed)
                _refresh.Enabled = true;
        }
    }

    /// <summary>A lista com os filtros de agora (sem ler o mercado de novo).</summary>
    private void Fill()
    {
        if (_last is not { } snapshot)
            return;
        double minPercent = (double)_minPercent.Value / 100;
        int minSales = (int)_minSales.Value;
        var shown = snapshot.Chances.Where(c => c.Percent >= minPercent && c.Sales7d >= minSales).ToList();

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var c in shown)
        {
            var item = new ListViewItem(c.Item) { Tag = c, UseItemStyleForSubItems = false };
            item.SubItems.Add(c.Kind, c.Kind.StartsWith("Abaixo") ? Ui.Text : Ui.Accent, Color.Empty, Ui.Font);
            item.SubItems.Add(c.Buy);
            item.SubItems.Add(c.Sell);
            item.SubItems.Add(c.ProfitGold > 0 ? Market.N(c.ProfitGold) : "?", Ui.Ok, Color.Empty, Ui.Bold);
            item.SubItems.Add($"{c.Percent * 100:0}%");
            item.SubItems.Add(c.Sales7d.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("pt-BR")), Ui.Muted, Color.Empty, Ui.Font);
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        _list.FitLastColumn();

        string gem = snapshot.GemValue is double v ? $"1 gema ≈ {Market.Gold(v)}" : "valor da gema: sem vendas nas duas moedas";
        _status.Text = $"Lido às {snapshot.At:HH:mm:ss} · {snapshot.ItemsWithListings} itens à venda · {gem} · " +
                       $"{shown.Count} de {snapshot.Chances.Count} oportunidades com os filtros · taxa de venda: {snapshot.GoldTax:P0} em gold, 15% em gemas";
    }

    private void ExplainSelected()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not MarketChance c)
            return;
        DeckBox.Show(this,
            $"{c.Item}: {c.Kind}\n\n{c.Explain}\n\n" +
            "É uma estimativa: o preço pode mudar até você comprar, e revender depende de alguém comprar. " +
            $"Para conferir e comprar, abra no jogo o Mercado da Comunidade (botão RMT) → Itens e procure \"{c.Item}\".",
            Text);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Dispose();
        base.OnFormClosed(e);
    }
}
