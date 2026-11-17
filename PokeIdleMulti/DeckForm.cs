using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace PokeIdleMulti;

/// <summary>
/// A janela do deck: as 4 contas dentro dela, cada uma num perfil próprio do navegador embutido
/// (WebView2), sem barra de navegador. Dá para mover, redimensionar, maximizar e pôr em tela cheia;
/// as contas ficam em grade 2×2, em colunas ou uma ampliada.
/// </summary>
internal sealed class DeckForm : Form
{
    internal const string AppName = "DeckPokeIdle";
    private const string GameUrl = "https://pokeidle.io/app";
    private const int Accounts = 4;
    private const int Gap = 6;

    // Sem isso o navegador desacelera as contas escondidas, minimizadas ou atrás de outras janelas.
    // --process-per-site: as páginas do mesmo site num perfil dividem um processo. Cada conta é um perfil, e
    // perfis nunca dividem processo: o jogo de cada conta fica separado.
    internal const string BrowserArguments =
        "--disable-background-timer-throttling --disable-backgrounding-occluded-windows " +
        "--disable-renderer-backgrounding --process-per-site";

    private static readonly string DataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeckPokeIdle");

    private readonly string _root;
    private readonly string _configPath;
    private readonly DeckConfig _config;
    private readonly ExtensionStore _store;
    private readonly AccountPane[] _panes = new AccountPane[Accounts];
    private readonly Dictionary<(int Pane, string Extension), PopupWindow> _extensionWindows = new();
    private readonly ToolStrip _bar = new ClickThroughToolStrip();
    private readonly StatusStrip _status = new();
    private readonly Panel _grid = new() { Dock = DockStyle.Fill, BackColor = Theme.Back };
    // Layouts: a grade (2×2 ou colunas, na setinha), o Principal (uma grande e 3 miniaturas) e o foco. Na setinha do
    // Principal e do Foco, a conta.
    private readonly ToolStripSplitButton _gridButton = new("Grade");
    private readonly ToolStripMenuItem _gridItem = new("Grade 2×2");
    private readonly ToolStripMenuItem _columnsItem = new("Colunas (lado a lado)");
    private readonly ToolStripSplitButton _mainButton = new("Principal");
    private readonly ToolStripSplitButton _focusButton = new("Foco");
    private readonly ToolStripStatusLabel _summary = new();
    // ⇄ e ⚡, e não 🔁 e 🔋: na barra o Windows desenha esses dois emojis como quadrados.
    private readonly ToolStripButton _echoButton = new("⇄ Eco");
    private bool _echo;
    private readonly ToolStripSplitButton _ultraButton = new("⚡ Ultra");
    // O menu ⚙ junta o que não precisa estar sempre à vista.
    private readonly ToolStripDropDownButton _settingsButton = new("⚙") { ShowDropDownArrow = false };
    private readonly ToolStripMenuItem _zoomButton = new("Zoom 100%");
    private readonly ToolStripMenuItem _extensionsButton = new("🧩 Extensões…");
    private readonly ToolStripMenuItem _sniperButton = new("🎯 Rotom Sniper") { Visible = false };
    private readonly bool[] _hasSniper = new bool[Accounts];
    private readonly ToolStripMenuItem _fullscreenButton = new("Tela cheia") { ShortcutKeyDisplayString = "F11" };
    private readonly ToolStripMenuItem _diagStatus = new("🩺 Gerar diagnóstico…");
    private readonly ToolStripStatusLabel _ram = new();
    private readonly ToolStripStatusLabel _voteStatus = new();
    private readonly VoteState?[] _votes = new VoteState?[Accounts];
    private readonly DateTime[] _voteRefreshAt = new DateTime[Accounts];
    private readonly bool[] _voteBusy = new bool[Accounts];
    private readonly PopupWindow?[] _voteWindows = new PopupWindow?[Accounts];
    private readonly RestockWindow?[] _restockWindows = new RestockWindow?[Accounts];
    private readonly bool[] _restockBusy = new bool[Accounts];
    private readonly bool[] _pvpBusy = new bool[Accounts];
    private string? _topIdleGameId;
    private DateTime _topIdleRetryAt;
    private bool _topIdleLookup;
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 1000 };
    private CoreWebView2Environment? _env;
    // Para onde o foco volta (grade, colunas ou Principal) e qual grade o botão Grade abre.
    private DeckLayout _layoutBeforeFocus = DeckLayout.Grid;
    private DeckLayout _lastGrid = DeckLayout.Grid;
    private bool _fullscreen;
    private FormWindowState _restoreState;
    private Rectangle _restoreBounds;
    private int _seconds;
    private bool _memoryBusy;

    public DeckForm()
    {
        _root = DataRoot;
        Directory.CreateDirectory(_root);
        _configPath = Path.Combine(_root, "deck.json");
        _config = DeckConfig.Load(_configPath);
        // O consumo.log só começa 10 min depois de abrir, quando tudo já carregou.
        _usageLogAt = DateTime.Now.AddMinutes(10);
        // O diagnóstico da carga do jogo.
        Diag.Folder = Path.Combine(_root, "diagnostico");
        _store = new ExtensionStore(_root);
        if (_config.Layout != DeckLayout.Focus)
            _layoutBeforeFocus = _config.Layout;
        if (Wide(_config.Layout))
            _lastGrid = _config.Layout;
        // O zoom é um só para todas as contas. Do tempo em que cada uma tinha o seu, fica o da primeira ligada.
        var zoom = _config.Zoom[Enumerable.Range(0, Accounts).FirstOrDefault(i => !_config.Off[i])];
        Array.Fill(_config.Zoom, zoom);

        Text = AppName;
        Icon = Theme.AppIcon;
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
        KeyPreview = true;
        MinimumSize = new Size(900, 600);

        BuildBar();
        BuildStatus();
        for (int i = 0; i < Accounts; i++)
        {
            var pane = new AccountPane(i)
            {
                AccountName = _config.AccountNames[i], ReduceMotion = _config.ReduceMotion, Ultra = _config.Ultra, GameFps = _config.GameFps, SpriteMs = _config.SpriteMs,
                RestockConfig = _config.Restock[i], PvpOn = _config.Pvp[i],
                PvpScreen = _config.PvpScreen[i], PvpSpeed = _config.PvpSpeed[i],
                LoadTimeout = TimeSpan.FromSeconds(60),
            };
            pane.Diag = new PaneDiag(Diag.Folder, pane.ProfileName, () => pane.AccountName);
            pane.GameStuck += (p, text) => LogOpening(p, text);
            pane.ShowRestock(null);
            pane.RestockRequested += p => ShowRestockWindow(p.Index);
            pane.PvpRequested += p => _ = SetPvpAsync(p.Index, !_config.Pvp[p.Index]);
            pane.PvpMenuRequested += p => ShowPvpMenu(p.Index);
            pane.FocusRequested += p => ToggleFocus(p.Index);
            pane.ReloadRequested += p => _ = p.ReloadCleanAsync();
            pane.FullscreenRequested += _ => ToggleFullscreen();
            pane.SettingsChanged += OnPaneSettingsChanged;
            pane.Clicked += (p, click) => _ = EchoAsync(p, click);
            pane.PowerRequested += p => _ = SetPaneOffAsync(p.Index, !p.Off);
            pane.MenuRequested += (p, at) => ShowAccountMenu(p.Index, at);
            pane.HeaderClicked += OnHeaderClicked;
            pane.PopupRequested += (p, extension, anchor) => _ = OpenExtensionPopupAsync(p, extension, anchor);
            pane.VoteRequested += p => _ = OpenVoteAsync(p, null);
            pane.TopIdleLinkRequested += (p, uri) => _ = OpenVoteAsync(p, uri);
            _panes[i] = pane;
            _grid.Controls.Add(pane);
        }
        _grid.Resize += (_, _) => ArrangePanes();

        // Encaixe: o que preenche entra primeiro, as barras depois.
        Controls.Add(_grid);
        Controls.Add(_status);
        Controls.Add(_bar);

        RestoreWindowBounds();
        _clock.Tick += (_, _) => OnClock();
    }

    private void BuildBar()
    {
        Theme.Style(_bar);
        _bar.Padding = new Padding(8, 5, 8, 5);
        _bar.Font = Theme.BarFont;
        _bar.Items.Add(new ToolStripLabel(AppName) { Font = new Font(_bar.Font, FontStyle.Bold), ForeColor = Theme.Accent });
        _bar.Items.Add(new ToolStripSeparator());

        // Esquerda: os layouts.
        _gridButton.ToolTipText = "As 4 contas à vista (na setinha: em grade 2×2 ou lado a lado)";
        _gridButton.ButtonClick += (_, _) => SetLayout(_lastGrid);
        _gridItem.Click += (_, _) => SetLayout(DeckLayout.Grid);
        _columnsItem.Click += (_, _) => SetLayout(DeckLayout.Columns);
        _gridButton.DropDownItems.AddRange(new ToolStripItem[] { _gridItem, _columnsItem });
        _mainButton.ToolTipText = Theme.Wrap("Uma conta grande e as outras 3 em miniatura ao lado (clique no cabeçalho de uma " +
                                             "miniatura para trazer ela para o lugar da grande). Na setinha, qual é a principal");
        _mainButton.ButtonClick += (_, _) => SetMain(_config.MainAccount);
        _mainButton.DropDownOpening += (_, _) =>
            FillAccountChoice(_mainButton, _config.MainAccount, _config.Layout == DeckLayout.Main, SetMain);
        _mainButton.DropDownItems.Add("…");
        _focusButton.ToolTipText = "Uma conta ocupando o deck (as outras continuam rodando escondidas). Na setinha, qual conta";
        _focusButton.ButtonClick += (_, _) => FocusPane(_config.Focused);
        _focusButton.DropDownOpening += (_, _) =>
            FillAccountChoice(_focusButton, _config.Focused, _config.Layout == DeckLayout.Focus, FocusPane);
        _focusButton.DropDownItems.Add("…");
        _bar.Items.AddRange(new ToolStripItem[] { _gridButton, _mainButton, _focusButton });

        // Direita, sempre à vista: Eco, Ultra e o menu ⚙. Itens à direita entram da direita para a esquerda.
        _settingsButton.Alignment = _ultraButton.Alignment = _echoButton.Alignment = ToolStripItemAlignment.Right;
        _settingsButton.Font = new Font("Segoe UI Symbol", 12f);
        _settingsButton.ToolTipText = "Mais opções: zoom, extensões, sniper, tela cheia, diagnóstico";
        BuildSettingsMenu();

        _ultraButton.ToolTipText = Theme.Wrap("Ultra em todas as contas: o Modo ULTRA Economia do jogo. O jogo inteiro sai da tela e fica " +
                                              "só uma caixa com o XP, o Gold, os drops e os shinies da sessão; a caça continua. " +
                                              "Na setinha, o menos animações");
        _ultraButton.ButtonClick += async (_, _) => await SetUltraAsync(!_config.Ultra);
        _ultraButton.DropDownOpening += (_, _) => FillUltraMenu();
        _ultraButton.DropDownItems.Add("…");
        ShowUltra();

        _echoButton.ToolTipText = "Eco: um clique numa conta se repete nas outras (no mesmo botão)";
        _echoButton.Click += (_, _) => SetEcho(!_echo);

        _bar.Items.Add(_settingsButton);
        _bar.Items.Add(_ultraButton);
        _bar.Items.Add(_echoButton);
        // Os menus que descem da barra na mesma letra dela.
        foreach (var item in _bar.Items.OfType<ToolStripDropDownItem>())
            item.DropDown.Font = Theme.BarFont;
        foreach (var item in _settingsButton.DropDownItems.OfType<ToolStripDropDownItem>())
            item.DropDown.Font = Theme.BarFont;
    }

    /// <summary>O menu ⚙: zoom, extensões, sniper, tela cheia, diagnóstico e o sobre.</summary>
    private void BuildSettingsMenu()
    {
        _zoomButton.DropDownOpening += (_, _) => FillZoomMenu();
        _zoomButton.DropDownItems.Add("…");
        ShowZoom();
        _extensionsButton.ToolTipText = "Instalar e gerenciar as extensões de cada conta";
        _extensionsButton.Click += (_, _) => OpenExtensions();
        _sniperButton.ToolTipText = "Liga ou desliga o Rotom Sniper (compras automáticas no mercado) em todas as contas";
        _sniperButton.DropDownOpening += (_, _) => FillSniperMenu();
        _sniperButton.DropDownItems.Add("…");
        _fullscreenButton.Click += (_, _) => ToggleFullscreen();
        _diagStatus.ToolTipText = Theme.Wrap("O jogo não carrega? Gera na Área de trabalho um .zip com o log da carga de cada conta, prints e as " +
                                             "informações do PC e da rede (sem senha, token nem cookie).");
        _diagStatus.Click += async (_, _) => await CreateDiagnosticAsync();
        var about = new ToolStripMenuItem("Sobre o DeckPokeIdle…");
        about.Click += (_, _) => DeckBox.Show(this,
            $"{AppName}\n\n" +
            "Deck pessoal para jogar 4 contas do pokeidle.io numa janela só.\n\n" +
            "Baseado no PokeIdleMulti, by: artbruno (licença MIT):\nhttps://github.com/artbruno35/PokeIdleMulti", Text);
        _settingsButton.DropDownItems.AddRange(new ToolStripItem[]
        {
            _zoomButton, _fullscreenButton, new ToolStripSeparator(),
            _extensionsButton, _sniperButton, new ToolStripSeparator(),
            _diagStatus, about,
        });
    }

    private void BuildStatus()
    {
        Theme.Style(_status);
        _status.SizingGrip = false;
        _status.Font = Theme.BarFont;
        _status.Padding = new Padding(8, 2, 8, 2);
        _status.ShowItemToolTips = true;
        _ram.ForeColor = Theme.Muted;
        _summary.ForeColor = Theme.Muted;
        _status.Items.Add(_summary);
        _status.Items.Add(new ToolStripStatusLabel("·") { ForeColor = Theme.Muted });
        _status.Items.Add(_ram);
        _status.Items.Add(new ToolStripStatusLabel("·") { ForeColor = Theme.Muted });
        _voteStatus.ForeColor = Theme.Muted;
        _status.Items.Add(_voteStatus);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ArrangePanes();
        UpdateBar();
        try
        {
            await InitializeAsync();
        }
        catch (Exception ex)
        {
            Diag.Write("ERRO ao abrir o navegador embutido: " + ex);
            DeckBox.Show(this, "Não consegui abrir o navegador embutido.\n\n" + ex.Message, Text, DeckBoxIcon.Error);
        }
    }

    private async Task InitializeAsync()
    {
        // Configurações de extensão pedidas em "Trazer configurações…": só dá para gravar antes do navegador abrir.
        var imported = await Task.Run(() => StorageImport.ApplyPending(_root));

        var options = new CoreWebView2EnvironmentOptions(BrowserArguments) { AreBrowserExtensionsEnabled = true };
        _env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(_root, "WebView2"), options);
        Diag.Write(Diag.Summary(_env) + $" · ligadas: {string.Join(", ", _panes.Where(p => !_config.Off[p.Index]).Select(p => p.ProfileName))}");
        _env.BrowserProcessExited += (_, e) => Diag.Write($"o navegador interno FECHOU: {e.BrowserProcessExitKind} (processo {e.BrowserProcessId})");

        // Conta desligada nem cria o navegador: só abre quando for ligada.
        foreach (var pane in _panes.Where(p => _config.Off[p.Index]))
            pane.SetOff(true);
        await Task.WhenAll(_panes.Where(p => !p.Off).Select(p =>
            p.InitializeAsync(_env, GameUrl, _config.Zoom[p.Index], _config.Muted[p.Index], null, RepairExtensions)));
        await RefreshExtensionButtonsAsync();
        ApplyMiniatures();

        _clock.Start();
        OnClock();
        if (imported.Count > 0)
        {
            var lines = imported.Select(r =>
                $"{_panes.FirstOrDefault(p => p.ProfileName == r.Profile)?.AccountName ?? r.Profile}: {r.Result}");
            DeckBox.Show(this, "Configurações de extensão trazidas.\n\n" + string.Join("\n", lines), Text);
        }
    }

    /// <summary>Dados trazidos de outro PC: reinstala da pasta daqui as extensões cujo caminho antigo sumiu.</summary>
    private Task RepairExtensions(CoreWebView2 core) => _store.RepairAsync(core.Profile);


    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(this);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _clock.Stop();
        SaveWindowBounds();
        SaveConfig();
        foreach (var pane in _panes)
            pane.Diag?.Flush();
        base.OnFormClosing(e);
    }

    // ---------- Janela: posição, tamanho e tela cheia ----------

    private void RestoreWindowBounds()
    {
        var saved = _config.WindowBounds is { Length: 4 } b ? new Rectangle(b[0], b[1], b[2], b[3]) : Rectangle.Empty;
        if (saved.Width >= MinimumSize.Width && saved.Height >= MinimumSize.Height
            && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(saved)))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = saved;
        }
        else
        {
            StartPosition = FormStartPosition.CenterScreen;
            var work = Screen.PrimaryScreen!.WorkingArea;
            Size = new Size(Math.Min(1600, work.Width), Math.Min(950, work.Height));
        }
        if (_config.Maximized)
            WindowState = FormWindowState.Maximized;
    }

    private void SaveWindowBounds()
    {
        var state = _fullscreen ? _restoreState : WindowState;
        var bounds = _fullscreen ? _restoreBounds : WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _config.WindowBounds = new[] { bounds.X, bounds.Y, bounds.Width, bounds.Height };
        if (state != FormWindowState.Minimized)
            _config.Maximized = state == FormWindowState.Maximized;
    }

    private void ToggleFullscreen()
    {
        SuspendLayout();
        if (!_fullscreen)
        {
            _restoreState = WindowState == FormWindowState.Minimized ? FormWindowState.Normal : WindowState;
            _restoreBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            var screen = Screen.FromControl(this).Bounds;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = screen;
            _status.Visible = false;
            _fullscreen = true;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            Bounds = _restoreBounds;
            WindowState = _restoreState;
            _status.Visible = true;
            _fullscreen = false;
        }
        _fullscreenButton.Checked = _fullscreen;
        _fullscreenButton.Text = _fullscreen ? "Sair da tela cheia" : "Tela cheia";
        ResumeLayout();
    }

    // ---------- Layout das contas ----------

    private void ArrangePanes()
    {
        var area = _grid.ClientRectangle;
        if (area.Width <= 0 || area.Height <= 0)
            return;
        _grid.SuspendLayout();
        foreach (var pane in _panes)
        {
            var cell = CellFor(pane.Index, area);
            pane.Visible = cell is not null;
            if (cell is Rectangle bounds)
                pane.Bounds = bounds;
        }
        _grid.ResumeLayout();
    }

    private Rectangle? CellFor(int index, Rectangle area) => _config.Layout switch
    {
        DeckLayout.Columns => Cell(area, index, 0, Accounts, 1),
        DeckLayout.Focus => index == _config.Focused ? Cell(area, 0, 0, 1, 1) : null,
        DeckLayout.Main => MainCell(index, area),
        _ => Cell(area, index % 2, index / 2, 2, 2),
    };

    /// <summary>
    /// Layout Principal: a conta principal ocupa ~3/4 da largura; as outras 3, empilhadas na coluna da direita, na ordem
    /// das contas.
    /// </summary>
    private Rectangle MainCell(int index, Rectangle area)
    {
        const int MainCols = 3, Cols = MainCols + 1;
        if (index == _config.MainAccount)
            return Rectangle.FromLTRB(area.X + Gap, area.Y + Gap,
                area.X + MainCols * (area.Width - Gap) / Cols, area.Bottom);
        int row = Enumerable.Range(0, Accounts).Where(i => i != _config.MainAccount).ToList().IndexOf(index);
        return Cell(area, MainCols, row, Cols, Accounts - 1);
    }

    private static Rectangle Cell(Rectangle area, int col, int row, int cols, int rows) => Rectangle.FromLTRB(
        area.X + Gap + col * (area.Width - Gap) / cols,
        area.Y + Gap + row * (area.Height - Gap) / rows,
        area.X + (col + 1) * (area.Width - Gap) / cols,
        area.Y + (row + 1) * (area.Height - Gap) / rows);

    /// <summary>Grade e colunas: as 4 contas pequenas, no zoom que a pessoa escolheu. Foco e Principal: a conta grande em 100%.</summary>
    private static bool Wide(DeckLayout layout) => layout is DeckLayout.Grid or DeckLayout.Columns;

    private void SetLayout(DeckLayout layout)
    {
        bool entering = !Wide(layout) && Wide(_config.Layout);
        bool leaving = Wide(layout) && !Wide(_config.Layout);
        _config.Layout = layout;
        if (Wide(layout))
            _lastGrid = layout;
        if (layout != DeckLayout.Focus)
            _layoutBeforeFocus = layout;
        // A conta grande fica em 100%; voltar para a grade ou as colunas devolve o zoom de antes (o que deixa
        // as 4 contas cabendo na tela, como 50%).
        if (entering)
        {
            _config.ZoomOutsideFocus = _config.Zoom[0];
            SetZoomAll(1);
        }
        else if (leaving && _config.ZoomOutsideFocus is double zoom)
        {
            SetZoomAll(zoom);
        }
        ArrangePanes();
        ApplyMiniatures();
        UpdateBar();
        SaveConfig();
    }

    /// <summary>
    /// Cada conta no papel dela: no layout Principal as miniaturas ficam no zoom das miniaturas, com o ritmo da sessão no
    /// cabeçalho e (com a opção) no Ultra; a principal e as contas dos outros layouts voltam ao normal.
    /// </summary>
    private void ApplyMiniatures()
    {
        foreach (var pane in _panes)
        {
            bool mini = _config.Layout == DeckLayout.Main && pane.Index != _config.MainAccount;
            pane.Compact = mini;
            pane.Highlighted = _config.Layout == DeckLayout.Main && pane.Index == _config.MainAccount;
            double zoom = mini ? _config.MiniZoom : _config.Zoom[pane.Index];
            if (Math.Abs(pane.WebView.ZoomFactor - zoom) > 0.001)
                pane.WebView.ZoomFactor = zoom;
            bool ultra = _config.Ultra || (mini && _config.MiniUltra);
            if (pane.Ultra != ultra)
            {
                pane.Ultra = ultra;
                if (!pane.Off)
                    _ = pane.SetUltraAsync(ultra);
            }
        }
    }

    /// <summary>Layout Principal com <paramref name="index"/> como a conta grande.</summary>
    private void SetMain(int index)
    {
        _config.MainAccount = index;
        SetLayout(DeckLayout.Main);
        _panes[index].WebView.Focus();
    }

    private void FocusPane(int index)
    {
        _config.Focused = index;
        SetLayout(DeckLayout.Focus);
        _panes[index].WebView.Focus();
    }

    /// <summary>
    /// Ampliar (duplo clique no cabeçalho, menu ⋯): no Principal, a miniatura vira a principal; nos outros, entra no foco
    /// ou volta dele.
    /// </summary>
    private void ToggleFocus(int index)
    {
        if (_config.Layout == DeckLayout.Main && index != _config.MainAccount)
            SetMain(index);
        else if (_config.Layout == DeckLayout.Focus && _config.Focused == index)
            SetLayout(_layoutBeforeFocus);
        else
            FocusPane(index);
    }

    /// <summary>Um clique simples no cabeçalho: no Principal, a miniatura troca de lugar com a principal.</summary>
    private void OnHeaderClicked(AccountPane pane)
    {
        if (_config.Layout == DeckLayout.Main && pane.Index != _config.MainAccount)
            SetMain(pane.Index);
    }

    private void UpdateBar()
    {
        var layout = _config.Layout;
        _gridButton.Text = _lastGrid == DeckLayout.Columns ? "Colunas" : "Grade";
        Theme.SetPill(_gridButton, Wide(layout) ? Theme.Pill.Active : null);
        _gridItem.Checked = layout == DeckLayout.Grid;
        _columnsItem.Checked = layout == DeckLayout.Columns;
        Theme.SetPill(_mainButton, layout == DeckLayout.Main ? Theme.Pill.Active : null);
        _mainButton.Text = layout == DeckLayout.Main ? "Principal: " + _config.AccountNames[_config.MainAccount] : "Principal";
        Theme.SetPill(_focusButton, layout == DeckLayout.Focus ? Theme.Pill.Active : null);
        _focusButton.Text = layout == DeckLayout.Focus ? "Foco: " + _config.AccountNames[_config.Focused] : "Foco";
        UpdateSummary();
    }

    /// <summary>A setinha do Principal e do Foco: escolher a conta.</summary>
    private void FillAccountChoice(ToolStripDropDownItem button, int current, bool active, Action<int> choose)
    {
        var items = button.DropDownItems;
        items.Clear();
        for (int i = 0; i < Accounts; i++)
        {
            int index = i;
            var item = new ToolStripMenuItem(_config.AccountNames[i]) { Checked = active && i == current };
            item.Click += (_, _) => choose(index);
            items.Add(item);
        }
    }

    private static string HealthText(PaneHealth health) => health switch
    {
        PaneHealth.Ok => "no jogo",
        PaneHealth.Loading => "carregando",
        PaneHealth.Login => "na tela de login",
        PaneHealth.Error => "com erro",
        _ => "desligada",
    };

    /// <summary>O começo da barra de baixo: quantas contas estão no jogo, na tela de login, com erro ou desligadas.</summary>
    private void UpdateSummary()
    {
        var health = _panes.Select(p => p.Health).ToList();
        var parts = new List<string>();
        void Add(PaneHealth kind, string one, string many)
        {
            int n = health.Count(h => h == kind);
            if (n > 0)
                parts.Add($"{n} {(n == 1 ? one : many)}");
        }
        Add(PaneHealth.Ok, "no jogo", "no jogo");
        Add(PaneHealth.Loading, "carregando", "carregando");
        Add(PaneHealth.Login, "no login", "no login");
        Add(PaneHealth.Error, "com erro", "com erro");
        Add(PaneHealth.Off, "desligada", "desligadas");
        _summary.Text = $"{Accounts} contas: {string.Join(", ", parts)}";
        _summary.ForeColor = health.Any(h => h is PaneHealth.Login or PaneHealth.Error) ? Theme.Warn : Theme.Muted;
        _summary.ToolTipText = string.Join("\n", _panes.Select(p =>
            $"{p.AccountName}: {HealthText(p.Health)}" + (p.Stats is string s ? $" · {s}" : "")));
    }

    private void OnClock()
    {
        foreach (var pane in _panes)
            pane.RefreshHeader();
        // Medir lista todos os processos do navegador e abre cada um: a cada 10 s basta (e a CPU sai mais estável).
        if (_seconds++ % 10 == 0)
            _ = UpdateMemoryAsync();
        if (_seconds % 5 == 0)
            UpdateBar();
        if (_seconds % 5 == 1)
            foreach (var pane in _panes)
                _ = pane.ReadStatsAsync();
        if (_seconds % 30 == 0)
            foreach (var pane in _panes)
                _ = pane.CheckLoginScreenAsync();
        // O jogo que não abre (preso na tela de abertura, ou a página que não carregou): recarrega a conta.
        if (_seconds % 5 == 2)
            foreach (var pane in _panes)
                _ = pane.CheckGameOpenAsync();
        if (_seconds % 2 == 0)
            foreach (var pane in _panes.Where(p => p.RestockConfig.On || _restockWindows[p.Index] is not null))
                _ = RefreshRestockAsync(pane.Index);
        // A fila de PvP: o ⚔ a cada 5 s com ela ligada.
        if (_seconds % 5 == 3)
            foreach (var pane in _panes.Where(p => p.PvpOn))
                _ = RefreshPvpAsync(pane.Index);
        UpdateVotes();
    }

    /// <summary>Uma parte do consumo do deck: a memória privada e a CPU (em núcleos) dos processos dela.</summary>
    public sealed record UsagePart(string Name, long Bytes, double Cores, int Processes);

    /// <summary>As partes da última medição, na ordem da barra (para o autoteste).</summary>
    public IReadOnlyList<UsagePart> Usage { get; private set; } = Array.Empty<UsagePart>();

    /// <summary>A CPU já gasta por cada processo na medição anterior: a diferença dá a CPU do intervalo.</summary>
    private readonly Dictionary<int, TimeSpan> _cpuBefore = new();
    private readonly Stopwatch _cpuClock = new();
    /// <summary>A próxima linha do consumo.log: 10 min depois de abrir (o começo é pesado) e depois a cada 10 min.</summary>
    private DateTime _usageLogAt;

    private string UsageLog => Path.Combine(_root, "consumo.log");

    /// <summary>
    /// O consumo do deck, separado em partes: o jogo das contas, a placa de vídeo (que desenha o jogo), o resto do
    /// navegador (rede, áudio, extensões) e o próprio deck.
    /// A memória é a privada de cada processo, a que pesa no PC; a CPU é a do intervalo desde a medição anterior.
    /// </summary>
    private async Task UpdateMemoryAsync()
    {
        if (_env is null || _memoryBusy)
            return;
        _memoryBusy = true;
        try
        {
            var infos = await _env.GetProcessExtendedInfosAsync();
            double elapsed = _cpuClock.IsRunning ? _cpuClock.Elapsed.TotalMilliseconds : 0;
            _cpuClock.Restart();
            var parts = new[] { "jogo", "placa de vídeo", "navegador e resto", "deck" }
                .ToDictionary(n => n, _ => (Bytes: 0L, Cores: 0d, Processes: 0));
            var seen = new HashSet<int>();

            void Add(string part, Process process)
            {
                long bytes = process.PrivateMemorySize64;
                var cpu = process.TotalProcessorTime;
                // Processo novo (sem leitura anterior) entra só na próxima: senão conta a CPU dele desde que abriu.
                double cores = elapsed > 0 && _cpuBefore.TryGetValue(process.Id, out var was)
                    ? Math.Max(0, (cpu - was).TotalMilliseconds / elapsed) : 0;
                _cpuBefore[process.Id] = cpu;
                seen.Add(process.Id);
                var p = parts[part];
                parts[part] = (p.Bytes + bytes, p.Cores + cores, p.Processes + 1);
            }

            foreach (var info in infos)
            {
                try
                {
                    using var process = Process.GetProcessById(info.ProcessInfo.ProcessId);
                    Add(PartOf(info), process);
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // O processo acabou de fechar.
                }
            }
            using (var self = Process.GetCurrentProcess())
                Add("deck", self);
            foreach (int gone in _cpuBefore.Keys.Where(k => !seen.Contains(k)).ToList())
                _cpuBefore.Remove(gone);

            Usage = parts.Select(p => new UsagePart(p.Key, p.Value.Bytes, p.Value.Cores, p.Value.Processes)).ToList();
            long total = Usage.Sum(p => p.Bytes);
            double cores = Usage.Sum(p => p.Cores);
            // CPU em % do processador inteiro, como no Gerenciador de Tarefas.
            static string Pct(double used)
            {
                double pct = used * 100 / Environment.ProcessorCount;
                return (pct < 10 ? pct.ToString("0.#") : pct.ToString("0")) + "%";
            }
            string cpu = elapsed > 0 ? $"CPU {Pct(cores)}" : "CPU medindo…";
            // Na barra só o total; as partes (jogo, placa de vídeo…) ficam na dica.
            _ram.Text = $"RAM ~{Gb(total)} GB · {cpu}";
            _ram.ToolTipText =
                string.Join("\n", Usage.Select(p => $"{p.Name}: {p.Bytes / 1024 / 1024} MB, " +
                                                    (elapsed > 0 ? $"{Pct(p.Cores)} da CPU" : "CPU medindo…") +
                                                    $" ({p.Processes} {(p.Processes == 1 ? "processo" : "processos")})")) +
                $"\ntotal: {Gb(total)} GB, {(elapsed > 0 ? $"{Pct(cores)} da CPU ({Environment.ProcessorCount} núcleos)" : "CPU medindo…")}\n" +
                Theme.Wrap("Memória privada (a que pesa no PC); CPU dos últimos 10 s. A cada 10 min vai uma linha para o " +
                           "consumo.log, na pasta do deck. Para aliviar: ligue o ULTRA ou desligue as contas que não está usando.", 60, 6);

            if (elapsed > 0 && DateTime.Now >= _usageLogAt)
            {
                _usageLogAt = DateTime.Now.AddMinutes(10);
                LogUsage(total, cores);
                _ = LogGameAsync();
            }
        }
        catch
        {
            // O deck está fechando ou o navegador caiu: fica o último valor.
        }
        finally
        {
            _memoryBusy = false;
        }
    }

    /// <summary>
    /// A parte de um processo do navegador. Cada site roda no seu processo, e um iframe conta para a página que o
    /// abriu: o captcha do Cloudflare na tela de login é do jogo; o da Kick, das lives da Kick.
    /// </summary>
    private static string PartOf(CoreWebView2ProcessExtendedInfo info)
    {
        if (info.ProcessInfo.Kind == CoreWebView2ProcessKind.Gpu)
            return "placa de vídeo";
        var hosts = info.AssociatedFrameInfos.Select(PageHost).ToList();
        if (hosts.Any(h => h.EndsWith("pokeidle.io")))
            return "jogo";
        return "navegador e resto";
    }

    /// <summary>
    /// Uma linha no consumo.log (pasta do deck), para comparar horas inteiras antes e depois de uma mudança.
    /// Passando de 2 MB (uns 2 meses), o antigo vira consumo.old.log.
    /// </summary>
    private void LogUsage(long total, double cores)
    {
        var lines = new List<string>
        {
            $"{DateTime.Now:dd/MM/yy HH:mm}  RAM {Gb(total)} GB, CPU {cores:0.00} núcleos | " +
            string.Join(" | ", Usage.Select(p => $"{p.Name} {p.Bytes / 1024 / 1024} MB {p.Cores:0.00}")),
        };
        AppendUsage(lines);
    }

    private void AppendUsage(IReadOnlyCollection<string> lines)
    {
        if (lines.Count == 0)
            return;
        try
        {
            var file = new FileInfo(UsageLog);
            if (file.Exists && file.Length > 2 * 1024 * 1024)
                File.Move(file.FullName, Path.ChangeExtension(file.FullName, ".old.log"), true);
            File.AppendAllLines(UsageLog, lines);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Arquivo aberto em outro programa: vai na próxima.
        }
    }

    /// <summary>A leitura anterior das métricas do jogo de cada conta (o navegador dá o tempo acumulado; o intervalo é a diferença).</summary>
    private readonly Dictionary<int, (DateTime At, Dictionary<string, double> Metrics)> _gameBefore = new();

    // Quantos canvas a página do jogo tem, quantas animações (CSS ou de script) estão rodando agora e quantos quadros
    // (requestAnimationFrame) por segundo o jogo teve desde a leitura anterior, com o limite do deck.
    private const string GamePageScript =
        "(() => { const n = window.__deckFrames ?? 0, k = window.__deckSpriteTicks ?? 0, at = performance.now(), was = window.__deckFramesAt;" +
        " window.__deckFramesAt = [n, at, k];" +
        " const rate = (now, i) => was && at > was[1] && now >= (was[i] ?? 0) ? ((now - (was[i] ?? 0)) * 1000 / (at - was[1])).toFixed(1) + '/s' : '?';" +
        " return `canvas ${document.querySelectorAll('canvas').length}, animações rodando ${document.getAnimations ? " +
        "document.getAnimations().filter(a => a.playState === 'running').length : '?'}, quadros ${rate(n, 0)} (limite ${window.__deckFps ?? '-'})," +
        " sprites ${rate(k, 2)} (a cada ${window.__deckSpriteMs ?? '-'} ms)`; })()";

    /// <summary>
    /// Onde o jogo de cada conta gasta CPU, para saber se limitar os quadros ajuda (item 7 do plano de consumo): o tempo da
    /// linha principal da página em script, layout, estilo e o resto (pintura, eventos), e a CPU do processo inteiro, em
    /// núcleos, desde a leitura anterior. Também o tamanho da página (nós, memória do JavaScript, ouvintes), que mostra se
    /// ela cresce com as horas. Vai para o consumo.log junto com a linha do consumo; a primeira leitura só marca o começo.
    /// </summary>
    private async Task LogGameAsync()
    {
        if (_gameBusy)
            return;
        _gameBusy = true;
        var lines = new List<string>();
        for (int i = 0; i < _panes.Length; i++)
        {
            try
            {
                if (_panes[i]?.Core is not CoreWebView2 core)
                {
                    _gameBefore.Remove(i);
                    continue;
                }
                // Ligar de novo não zera os contadores; a página que recarregou volta a contar do zero.
                await core.CallDevToolsProtocolMethodAsync("Performance.enable", "{}");
                using var doc = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Performance.getMetrics", "{}"));
                var now = new Dictionary<string, double>();
                foreach (var metric in doc.RootElement.GetProperty("metrics").EnumerateArray())
                    now[metric.GetProperty("name").GetString() ?? ""] = metric.GetProperty("value").GetDouble();
                string page = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(GamePageScript)) ?? "";
                var at = DateTime.Now;
                if (_gameBefore.TryGetValue(i, out var before) && GameLine(before.Metrics, now, (at - before.At).TotalSeconds) is string cpu)
                    lines.Add($"{at:dd/MM/yy HH:mm}  jogo de {_config.AccountNames[i]}: {cpu} | {page}");
                _gameBefore[i] = (at, now);
            }
            catch
            {
                // A conta desligou ou a página está recarregando: começa de novo na próxima.
                _gameBefore.Remove(i);
            }
        }
        _gameBusy = false;
        AppendUsage(lines);
    }

    private bool _gameBusy;

    /// <summary>A CPU do jogo no intervalo, em núcleos, por tipo de trabalho (null: a página recarregou no meio).</summary>
    private static string? GameLine(Dictionary<string, double> was, Dictionary<string, double> now, double seconds)
    {
        double Delta(string name) => now.GetValueOrDefault(name) - was.GetValueOrDefault(name);
        double task = Delta("TaskDuration"), script = Delta("ScriptDuration"), layout = Delta("LayoutDuration"), style = Delta("RecalcStyleDuration");
        if (seconds < 1 || task < 0 || script < 0 || Delta("LayoutCount") < 0)
            return null;
        string Cores(double value) => (value / seconds).ToString("0.000");
        string process = now.ContainsKey("ProcessTime") ? $", processo inteiro {Cores(Delta("ProcessTime"))}" : "";
        return $"linha principal {Cores(task)} núcleo (script {Cores(script)}, layout {Cores(layout)} " +
               $"{Delta("LayoutCount") / seconds:0.0}/s, estilo {Cores(style)} {Delta("RecalcStyleCount") / seconds:0.0}/s, " +
               $"resto {Cores(Math.Max(0, task - script - layout - style))}){process} | nós {now.GetValueOrDefault("Nodes"):0}, " +
               $"JS {now.GetValueOrDefault("JSHeapUsedSize") / 1024 / 1024:0} MB, ouvintes {now.GetValueOrDefault("JSEventListeners"):0}";
    }

    private static string Gb(long bytes) => (bytes / 1024d / 1024 / 1024).ToString("0.0");

    /// <summary>O site da página onde o quadro está (sobe dos iframes até a página).</summary>
    private static string PageHost(CoreWebView2FrameInfo frame)
    {
        while (frame.ParentFrameInfo is CoreWebView2FrameInfo parent)
            frame = parent;
        return Uri.TryCreate(frame.Source, UriKind.Absolute, out var uri) ? uri.Host : "";
    }

    /// <summary>Liga ou desliga o "menos animações" em todas as contas, na hora (sem recarregar).</summary>
    private async Task SetReduceMotionAsync(bool reduce)
    {
        _config.ReduceMotion = reduce;
        SaveConfig();
        foreach (var pane in _panes)
        {
            pane.ReduceMotion = reduce;
            await pane.ApplyReduceMotionAsync();
        }
    }

    /// <summary>O ⚡ ULTRA na barra: laranja com "ligado", como o eco.</summary>
    private void ShowUltra()
    {
        _ultraButton.Text = _config.Ultra ? "⚡ Ultra ligado" : "⚡ Ultra";
        Theme.SetPill(_ultraButton, _config.Ultra ? Theme.Pill.On : null);
    }

    /// <summary>O ULTRA (o mesmo do botão) e, embaixo, o menos animações.</summary>
    private void FillUltraMenu()
    {
        var items = _ultraButton.DropDownItems;
        items.Clear();
        items.Add(_config.Ultra ? "Desligar o ULTRA" : "Ligar o ULTRA (em todas as contas)", null,
            async (_, _) => await SetUltraAsync(!_config.Ultra));
        var mini = new ToolStripMenuItem("Miniaturas sempre em Ultra (layout Principal)")
        {
            Checked = _config.MiniUltra,
            ToolTipText = "No layout Principal, as 3 miniaturas ligam o Ultra sozinhas: a caixa de números é o que dá para ler numa conta pequena, e gasta bem menos",
        };
        mini.Click += (_, _) => { _config.MiniUltra = !_config.MiniUltra; SaveConfig(); ApplyMiniatures(); };
        items.Add(mini);
        items.Add(new ToolStripSeparator());
        var motion = new ToolStripMenuItem("🐢 Menos animações (no jogo normal e na tela de login)")
        {
            Checked = _config.ReduceMotion,
            ToolTipText = "O próprio jogo desliga as animações que rodam sem parar (selos pulsando, halos, cartas). " +
                          "Gasta bem menos CPU e placa de vídeo. Desmarque para ver todas",
        };
        motion.Click += async (_, _) => await SetReduceMotionAsync(!_config.ReduceMotion);
        items.Add(motion);
        static string FpsText(int fps) => fps >= 60 ? "60 por segundo (sem limite)" : $"{fps} por segundo";
        var frames = new ToolStripMenuItem($"🎞 Quadros do mapa: {FpsText(_config.GameFps)}")
        {
            ToolTipText = "Quantas vezes por segundo o mapa da caça é desenhado. A caça roda no servidor: menos quadros só " +
                          "deixam o mapa menos fluido, e gastam bem menos CPU e placa de vídeo",
        };
        foreach (int fps in new[] { 60, 30, 15, 10 })
        {
            var choice = new ToolStripMenuItem(FpsText(fps)) { Checked = _config.GameFps == fps };
            choice.Click += async (_, _) => await SetGameFpsAsync(fps);
            frames.DropDownItems.Add(choice);
        }
        items.Add(frames);
        static string SpriteText(int ms) => ms switch
        {
            <= 220 => "normal (220 ms, como o jogo)",
            < 1000 => $"lento ({ms} ms)",
            < 3000 => $"bem lento ({ms / 1000.0:0.#} s)",
            _ => $"quase parados ({ms / 1000.0:0.#} s)",
        };
        var sprites = new ToolStripMenuItem($"🐾 Sprites animados: {SpriteText(_config.SpriteMs)}")
        {
            ToolTipText = "De quanto em quanto tempo os sprites da equipe, das listas e do mercado trocam de quadro. Cada conta " +
                          "tem 120–190 deles: mais devagar gasta bem menos CPU e placa de vídeo. Mais devagar vale na hora; mais " +
                          "rápido pega nos sprites novos ou ao recarregar a conta",
        };
        foreach (int ms in new[] { 220, 660, 1500, 5000 })
        {
            var choice = new ToolStripMenuItem(SpriteText(ms)) { Checked = _config.SpriteMs == ms };
            choice.Click += async (_, _) => await SetSpriteMsAsync(ms);
            sprites.DropDownItems.Add(choice);
        }
        items.Add(sprites);
    }

    /// <summary>Troca o ritmo dos sprites animados em todas as contas (mais devagar vale na hora).</summary>
    private async Task SetSpriteMsAsync(int ms)
    {
        _config.SpriteMs = ms;
        SaveConfig();
        foreach (var pane in _panes)
        {
            pane.SpriteMs = ms;
            await pane.ApplyGameFpsAsync();
        }
    }

    /// <summary>Troca o limite de quadros do jogo em todas as contas, na hora (sem recarregar).</summary>
    private async Task SetGameFpsAsync(int fps)
    {
        _config.GameFps = fps;
        SaveConfig();
        foreach (var pane in _panes)
        {
            pane.GameFps = fps;
            await pane.ApplyGameFpsAsync();
        }
    }

    /// <summary>
    /// Liga ou desliga o ULTRA em todas as contas, na hora. Ligado, vale também para a conta que recarrega ou
    /// é ligada depois (ela volta ao ULTRA sozinha).
    /// </summary>
    private async Task SetUltraAsync(bool on)
    {
        _config.Ultra = on;
        ShowUltra();
        SaveConfig();
        var failed = new List<string>();
        foreach (var pane in _panes)
        {
            bool want = on || (_config.Layout == DeckLayout.Main && pane.Index != _config.MainAccount && _config.MiniUltra);
            pane.Ultra = want;
            if (!pane.Off && !await pane.SetUltraAsync(want) && want == on)
                failed.Add(pane.AccountName);
        }
        if (failed.Count > 0)
            DeckBox.Show(this,
                $"Não consegui {(on ? "ligar" : "desligar")} o ULTRA em: {string.Join(", ", failed)}.\n\n" +
                "O deck usa o interruptor do próprio jogo (Configurações → Desempenho → Modo ULTRA Economia): " +
                "recarregue a conta (⟳ no cabeçalho) e tente de novo, ou ligue por lá.",
                Text, DeckBoxIcon.Warn);
    }

    // ---------- Ações das contas ----------

    /// <summary>
    /// O menu ⋯ da conta (e o botão direito no nome dela, na barra de cima): o jogo (ampliar, recarregar, som), as
    /// automações (recompra, PvP, voto, extensões) e a conta (renomear, apagar os dados, diagnóstico, desligar).
    /// </summary>
    private void ShowAccountMenu(int index, Point? at = null)
    {
        var pane = _panes[index];
        var menu = new ContextMenuStrip { ShowItemToolTips = true, Font = Theme.BarFont };
        if (pane.Off)
        {
            menu.Items.Add("Ligar esta conta", null, async (_, _) => await SetPaneOffAsync(index, false));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Renomear…", null, (_, _) => RenameAccount(index));
        }
        else
        {
            menu.Items.Add(_config.Layout == DeckLayout.Main && index != _config.MainAccount ? "Trazer para o lugar da principal"
                : _config.Layout == DeckLayout.Focus && _config.Focused == index ? "Voltar" : "Ampliar no deck",
                null, (_, _) => ToggleFocus(index));
            menu.Items.Add("Recarregar limpo (sem o cache do jogo)", null, (_, _) => _ = pane.ReloadCleanAsync());
            menu.Items.Add(pane.Muted ? "Ligar o som" : "Desligar o som", null, (_, _) => pane.SetMuted(!pane.Muted));

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("🛒 Recompra…", null, (_, _) => ShowRestockWindow(index))
            {
                Checked = _config.Restock[index].On,
            });
            var pvpMenu = new ToolStripMenuItem("⚔ Fila de PvP") { Checked = _config.Pvp[index] };
            FillPvpMenu(index, pvpMenu.DropDownItems);
            menu.Items.Add(pvpMenu);
            if (pane.HasVote)
                menu.Items.Add(new ToolStripMenuItem(pane.VoteText + " no TopIdle", null, (_, _) => _ = OpenVoteAsync(pane, null))
                {
                    ToolTipText = pane.VoteTip,
                });
            if (pane.Extensions.Count > 0)
            {
                var extensions = new ToolStripMenuItem("🧩 Extensões");
                foreach (var extension in pane.Extensions)
                    extensions.DropDownItems.Add(extension.Name, AccountPane.ExtensionIcon(extension), (_, _) => pane.OpenExtension(extension));
                menu.Items.Add(extensions);
            }

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Renomear…", null, (_, _) => RenameAccount(index));
            menu.Items.Add(new ToolStripMenuItem("Apagar os dados da conta (entrar de novo)…", null, async (_, _) => await WipeAccountAsync(index))
            {
                Enabled = pane.Core is not null,
            });
            menu.Items.Add("🩺 Gerar diagnóstico…", null, async (_, _) => await CreateDiagnosticAsync());
            menu.Items.Add("Desligar esta conta (fica no deck, sem rodar nada)", null, async (_, _) => await SetPaneOffAsync(index, true));
        }
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(at ?? Cursor.Position);
    }

    /// <summary>
    /// Desliga ou religa uma conta. Desligada, ela fica no deck, mas a página do jogo fecha (e com ela
    /// as extensões, que só rodam no jogo), e as janelas dela e a conferência do voto param.
    /// </summary>
    private async Task SetPaneOffAsync(int index, bool off)
    {
        var pane = _panes[index];
        _config.Off[index] = off;
        SaveConfig();
        if (off)
        {
            _voteWindows[index]?.Close();
            foreach (var (key, window) in _extensionWindows.Where(w => w.Key.Pane == index).ToList())
            {
                window.Close();
                _extensionWindows.Remove(key);
            }
            _votes[index] = null;
            _restockWindows[index]?.Close();
            pane.SetOff(true);
        }
        else
        {
            pane.SetOff(false);
            if (pane.Core is CoreWebView2 core)
                core.Navigate(GameUrl);
            else if (_env is not null)
            {
                // Estava desligada desde que o deck abriu: o navegador dela é criado agora.
                await pane.InitializeAsync(_env, GameUrl, _config.Zoom[index], _config.Muted[index], null, RepairExtensions);
                await RefreshExtensionButtonsAsync();
                ApplyMiniatures();
            }
            _voteRefreshAt[index] = DateTime.Now;
        }
        UpdateBar();
    }

    // ---------- Recompra ----------

    /// <summary>A janela da recompra da conta (o carrinho no cabeçalho); aberta de novo, só vem para a frente.</summary>
    private void ShowRestockWindow(int index)
    {
        if (_restockWindows[index] is RestockWindow open)
        {
            open.Activate();
            return;
        }
        var pane = _panes[index];
        var window = new RestockWindow(pane.AccountName, pane.RestockConfig);
        window.Changed += () =>
        {
            SaveConfig();
            _ = ApplyRestockAsync(index);
        };
        window.FormClosed += (_, _) => _restockWindows[index] = null;
        _restockWindows[index] = window;
        window.Show(this);
        _ = RefreshRestockAsync(index);
    }

    /// <summary>A configuração nova vale na hora: vai para a página e o carrinho e a janela mostram o resultado.</summary>
    private async Task ApplyRestockAsync(int index)
    {
        await _panes[index].PushRestockAsync();
        await RefreshRestockAsync(index);
    }

    /// <summary>Lê da página o estoque e o registro, para o carrinho do cabeçalho e a janela aberta.</summary>
    private async Task RefreshRestockAsync(int index)
    {
        if (_restockBusy[index])
            return;
        _restockBusy[index] = true;
        try
        {
            var status = await _panes[index].ReadRestockAsync();
            _panes[index].ShowRestock(status);
            _restockWindows[index]?.ShowStatus(status);
        }
        finally
        {
            _restockBusy[index] = false;
        }
    }

    // ---------- Fila de PvP ----------

    /// <summary>Liga ou desliga a fila de PvP da conta (o ⚔ do cabeçalho e o menu da conta); vale na hora.</summary>
    private async Task SetPvpAsync(int index, bool on)
    {
        _config.Pvp[index] = on;
        _panes[index].PvpOn = on;
        SaveConfig();
        await _panes[index].PushPvpAsync();
        await RefreshPvpAsync(index);
    }

    /// <summary>
    /// Como as partidas da fila aparecem na conta (completa, mini ou off) e a velocidade delas (0 = a do jogo); vale na
    /// próxima partida, com a fila ligada.
    /// </summary>
    private async Task SetPvpViewAsync(int index, string screen, double speed)
    {
        _config.PvpScreen[index] = _panes[index].PvpScreen = PvpScreens.Normalized(screen);
        _config.PvpSpeed[index] = _panes[index].PvpSpeed = PvpScreens.NormalizedSpeed(speed);
        SaveConfig();
        await _panes[index].PushPvpAsync();
        await RefreshPvpAsync(index);
    }

    /// <summary>Botão direito no ⚔ do cabeçalho: ligar a fila, a tela das partidas e a velocidade.</summary>
    private void ShowPvpMenu(int index)
    {
        var menu = new ContextMenuStrip();
        FillPvpMenu(index, menu.Items);
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(_panes[index].PvpMenuPoint, ToolStripDropDownDirection.BelowLeft);
    }

    /// <summary>O menu da fila de PvP (o botão direito no ⚔ e o "⚔ Fila de PvP" do menu da conta).</summary>
    private void FillPvpMenu(int index, ToolStripItemCollection items)
    {
        string screen = PvpScreens.Normalized(_config.PvpScreen[index]);
        double speed = PvpScreens.NormalizedSpeed(_config.PvpSpeed[index]);
        items.Add(new ToolStripMenuItem("Fila ligada (entra de novo depois de cada partida)", null,
            (_, _) => _ = SetPvpAsync(index, !_config.Pvp[index])) { Checked = _config.Pvp[index] });
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem("Partidas da fila:") { Enabled = false });
        // Economia ou ULTRA: o jogo esconde as partidas de qualquer jeito; o menu avisa antes da escolha.
        if (PvpScreens.NoSceneNote(_panes[index].PvpNoScene) is string noScene)
            items.Add(new ToolStripMenuItem("⚠ " + (_panes[index].PvpNoScene == "economia"
                ? "Battery Saver ligado: mostrar ou minimizar desliga ele com a fila ligada (o deck religa ao esconder)"
                : "Tela ULTRA ligada: o jogo esconde as partidas assim mesmo"))
            {
                Enabled = false, ToolTipText = noScene,
            });
        foreach (var (value, text) in new[]
                 {
                     (PvpScreens.Full, "Mostrar a luta (o replay na tela)"),
                     (PvpScreens.Mini, "Minimizada (a janelinha do canto)"),
                     (PvpScreens.Off, "Escondida (só o aviso do resultado)"),
                 })
            items.Add(new ToolStripMenuItem(text, null, (_, _) => _ = SetPvpViewAsync(index, value, _config.PvpSpeed[index]))
            {
                Checked = screen == value,
            });
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem("Velocidade da luta (mostrada ou minimizada):") { Enabled = false });
        items.Add(new ToolStripMenuItem("A do jogo (acelera só para caber até a próxima)", null,
            (_, _) => _ = SetPvpViewAsync(index, _config.PvpScreen[index], 0)) { Checked = speed == 0, Enabled = screen != PvpScreens.Off });
        foreach (double s in PvpScreens.Speeds)
            items.Add(new ToolStripMenuItem(PvpScreens.SpeedLabel(s), null, (_, _) => _ = SetPvpViewAsync(index, _config.PvpScreen[index], s))
            {
                Checked = speed == s, Enabled = screen != PvpScreens.Off,
            });
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem($"Usar nas outras contas (partidas {PvpScreens.Label(screen)}s" +
                                        (screen == PvpScreens.Off ? "" : $", {PvpScreens.SpeedLabel(speed)}") + ")", null, async (_, _) =>
        {
            for (int i = 0; i < Accounts; i++)
                if (i != index)
                    await SetPvpViewAsync(i, screen, speed);
        }));
    }

    /// <summary>Lê da página a fila de PvP para o ⚔ do cabeçalho.</summary>
    private async Task RefreshPvpAsync(int index)
    {
        if (_pvpBusy[index])
            return;
        _pvpBusy[index] = true;
        try
        {
            _panes[index].ShowPvp(await _panes[index].ReadPvpAsync());
        }
        finally
        {
            _pvpBusy[index] = false;
        }
    }

    // ---------- O jogo que não abre ----------

    /// <summary>Anota em aberturas.log (pasta do deck) cada vez que uma conta recarregou porque o jogo não abriu.</summary>
    private void LogOpening(AccountPane pane, string text)
    {
        try
        {
            var path = Path.Combine(_root, "aberturas.log");
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
                File.WriteAllLines(path, File.ReadAllLines(path).TakeLast(500));
            File.AppendAllText(path, $"{DateTime.Now:dd/MM/yy HH:mm:ss}  {pane.AccountName} · {text}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Fica para a próxima.
        }
    }

    private void RenameAccount(int index)
    {
        var name = Prompt.Ask(this, "Renomear conta", "Nome que aparece no deck (por exemplo, o nick do jogo):", _config.AccountNames[index], "Salvar");
        if (string.IsNullOrWhiteSpace(name))
            return;
        _config.AccountNames[index] = name;
        _panes[index].AccountName = name;
        _panes[index].RefreshHeader();
        SaveConfig();
        UpdateBar();
    }

    /// <summary>
    /// Apaga os dados da conta para ela entrar de novo (a conta que trava depois da carga mesmo recarregando limpo): só os do
    /// jogo, ou tudo (os cookies de todos os sites).
    /// </summary>
    private async Task WipeAccountAsync(int index)
    {
        var pane = _panes[index];
        int choice = DeckBox.Choose(this,
            $"Apagar os dados de {pane.AccountName}?\n\n" +
            "Apagar o jogo: sai da conta do jogo e apaga tudo o que o site guarda nesta conta do deck (login, id do aparelho, " +
            "cookies, cache e service workers). O jogo abre na tela de login: entre de novo com o e-mail e a senha.\n\n" +
            "Apagar tudo: o mesmo e os cookies de todos os sites que esta conta do deck abriu.\n\n" +
            "As extensões, as configurações delas e as do deck (nome, recompra, fila de PvP) ficam.",
            "Apagar os dados da conta", "Apagar o jogo", "Apagar tudo", danger: true);
        if (choice == 0)
            return;
        bool ok = await pane.WipeAsync(choice == 2);
        pane.Note(ok ? "dados apagados: entre de novo na conta" : "não consegui apagar tudo: recarregando assim mesmo");
    }

    // ---------- 🩺 Diagnóstico: o jogo que não carrega ----------

    private bool _diagBusy;

    /// <summary>O deck agora, para o relatório do diagnóstico: as contas, as opções que mexem no jogo, as lives e o consumo.</summary>
    private IEnumerable<string> DeckLines()
    {
        yield return "contas: " + string.Join("; ", _panes.Select(p => $"{p.ProfileName} {p.AccountName} {(_config.Off[p.Index] ? "desligada" : "ligada")}"));
        yield return $"zoom {_config.Zoom[0]:0.##}; menos animações {_config.ReduceMotion}; quadros {_config.GameFps}/s; sprites {_config.SpriteMs} ms; " +
                     $"ULTRA {_config.Ultra}; layout {_config.Layout}";
        yield return "automações ligadas: " + string.Join("; ", _panes.Select(p =>
            $"{p.ProfileName} recompra={_config.Restock[p.Index].On} pvp={_config.Pvp[p.Index]}"));
        yield return Diag.Cut($"consumo: {_ram.Text} {_ram.ToolTipText}", 600);
    }

    /// <summary>
    /// 🩺 Gerar diagnóstico (a barra de baixo e o menu da conta): o .zip na Área de trabalho, para a pessoa do PC onde o jogo
    /// não carrega mandar, e abre o Explorer nele.
    /// </summary>
    private async Task CreateDiagnosticAsync()
    {
        if (_diagBusy)
            return;
        _diagBusy = true;
        _diagStatus.Text = "🩺 gerando o diagnóstico…";
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string folder = Directory.Exists(desktop) ? desktop : _root;
            string zip = await Diag.CreateReportAsync(folder, _env, _panes, DeckLines().ToList(), _root, outside: true);
            DeckBox.Show(this,
                $"Pronto: {Path.GetFileName(zip)}, na {(folder == desktop ? "Área de trabalho" : folder)}.\n\n" +
                "Mande este arquivo para quem cuida do deck. Ele tem o log da carga do jogo de cada conta, prints das contas e as " +
                "informações do PC e da rede; não tem senha, token nem cookie.", Text);
            try
            {
                Process.Start("explorer.exe", $"/select,\"{zip}\"");
            }
            catch
            {
                // Sem o Explorer, o caminho está na caixa.
            }
        }
        catch (Exception ex)
        {
            Diag.Write("ERRO ao gerar o diagnóstico: " + ex);
            DeckBox.Show(this, "Não consegui gerar o diagnóstico.\n\n" + ex.Message, Text, DeckBoxIcon.Error);
        }
        finally
        {
            _diagStatus.Text = "🩺 Gerar diagnóstico";
            _diagBusy = false;
        }
    }

    // ---------- Extensões ----------

    private void OpenExtensions()
    {
        if (_env is null)
            return;
        using var dialog = new ExtensionsDialog(_panes, _store, _env, _root, () => _ = RefreshExtensionButtonsAsync());
        dialog.ShowDialog(this);
    }

    private async Task RefreshExtensionButtonsAsync()
    {
        foreach (var pane in _panes)
        {
            if (pane.Profile is not CoreWebView2Profile profile)
                continue;
            var extensions = await profile.GetBrowserExtensionsAsync();
            _hasSniper[pane.Index] = extensions.Any(e => e.Id == RotomSniper.Id && e.IsEnabled);
            _sniperButton.Visible = _hasSniper.Any(h => h);
            pane.SetExtensions(extensions
                .Where(e => e.IsEnabled)
                .Select(e => _store.Find(e.Id))
                .OfType<ExtensionInfo>()
                .Where(info => info.Popup is not null));
        }
    }

    private async Task<PopupWindow?> OpenExtensionPopupAsync(AccountPane pane, ExtensionInfo extension, Point anchor)
    {
        if (_env is null || extension.Popup is null)
            return null;

        var key = (pane.Index, extension.Id);
        if (extension.OpensWindow && _extensionWindows.TryGetValue(key, out var open) && !open.IsDisposed)
        {
            if (open.WindowState == FormWindowState.Minimized)
                open.WindowState = FormWindowState.Normal;
            open.Activate();
            return open;
        }

        var popup = await PopupWindow.CreateAsync(_env, pane.ProfileName,
            extension.OpensWindow ? new Size(1160, 760) : new Size(420, 580), closeOnDeactivate: !extension.OpensWindow);
        if (extension.OpensWindow)
        {
            var area = Screen.FromControl(this).WorkingArea;
            popup.Location = new Point(area.X + Math.Max(0, (area.Width - popup.Width) / 2), area.Y + Math.Max(0, (area.Height - popup.Height) / 2));
            _extensionWindows[key] = popup;
        }
        else
        {
            popup.PlaceBelow(anchor);
        }
        popup.WebView.CoreWebView2.Navigate($"chrome-extension://{extension.Id}/{extension.Popup}");
        popup.Show(this);
        return popup;
    }

    // ---------- Eco e zoom ----------

    private static readonly double[] ZoomLevels = { 0.5, 0.67, 0.75, 0.8, 0.9, 1, 1.1, 1.25, 1.5 };

    /// <summary>O zoom (o mesmo em todas as contas) no item do menu ⚙.</summary>
    private void ShowZoom() => _zoomButton.Text = $"Zoom de todas as contas: {_config.Zoom[0] * 100:0}%";

    /// <summary>O − e o +: o nível de zoom anterior ou o seguinte da lista.</summary>
    private void StepZoom(int direction)
    {
        double zoom = _config.Zoom[0];
        SetZoomAll(direction < 0
            ? ZoomLevels.LastOrDefault(level => level < zoom - 0.001, ZoomLevels[0])
            : ZoomLevels.FirstOrDefault(level => level > zoom + 0.001, ZoomLevels[^1]));
    }

    private void FillZoomMenu()
    {
        var items = _zoomButton.DropDownItems;
        items.Clear();
        double zoom = _config.Zoom[0];
        items.Add(new ToolStripMenuItem("− Diminuir", null, (_, _) => StepZoom(-1)) { Enabled = zoom > ZoomLevels[0] + 0.001 });
        items.Add(new ToolStripMenuItem("+ Aumentar", null, (_, _) => StepZoom(1)) { Enabled = zoom < ZoomLevels[^1] - 0.001 });
        items.Add(new ToolStripSeparator());
        foreach (var level in ZoomLevels)
        {
            var item = new ToolStripMenuItem($"{level * 100:0}%") { Checked = Math.Abs(_config.Zoom[0] - level) < 0.01 };
            item.Click += (_, _) => SetZoomAll(level);
            items.Add(item);
        }
    }

    /// <summary>O eco só repete cliques; o zoom vale para todas as contas com ele ligado ou não.</summary>
    private void SetEcho(bool on)
    {
        _echo = on;
        _echoButton.Text = on ? "⇄ Eco ligado" : "⇄ Eco";
        Theme.SetPill(_echoButton, on ? Theme.Pill.On : null);
    }

    /// <summary>
    /// Zoom (Ctrl + roda, Ctrl + 0) ou som de uma conta mudou. O zoom vale sempre para todas as contas. O WebView2
    /// só avisa zoom mudado pela pessoa, nunca o que o deck põe, então espalhar o zoom não volta aqui.
    /// </summary>
    private void OnPaneSettingsChanged(AccountPane pane)
    {
        _config.Muted[pane.Index] = pane.Muted;
        SetZoomAll(pane.WebView.ZoomFactor);
    }

    private void SetZoomAll(double zoom)
    {
        foreach (var pane in _panes)
        {
            // Só quem está diferente: assim a mudança não fica voltando de uma conta para outra.
            if (Math.Abs(pane.WebView.ZoomFactor - zoom) > 0.001)
                pane.WebView.ZoomFactor = zoom;
            _config.Zoom[pane.Index] = zoom;
        }
        ShowZoom();
        ApplyMiniatures();
    }

    /// <summary>Repete nas outras contas o clique feito numa; avisa no cabeçalho de quem não tem esse botão.</summary>
    private async Task EchoAsync(AccountPane source, Echo.Click click)
    {
        if (!_echo)
            return;
        var script = Echo.ReplayScript(click);
        await Task.WhenAll(_panes.Where(p => p != source && p.Core is not null && !p.Off).Select(async pane =>
        {
            string result;
            try
            {
                result = await pane.WebView.ExecuteScriptAsync(script);
            }
            catch
            {
                result = "false";
            }
            if (result != "true")
                pane.Note($"eco: \"{(click.Text.Length > 0 ? click.Text : "esse botão")}\" não está nesta tela");
        }));
    }

    // ---------- Rotom Sniper ----------

    /// <summary>Ligar/desligar em todas as contas, e uma linha por conta com o estado dela (clicar troca só ela).</summary>
    private async void FillSniperMenu()
    {
        var items = _sniperButton.DropDownItems;
        items.Clear();
        items.Add("Ligar o sniper em todas as contas", null, async (_, _) => await SetSniperAsync(null, true));
        items.Add("Desligar o sniper em todas as contas", null, async (_, _) => await SetSniperAsync(null, false));
        items.Add(new ToolStripSeparator());
        var lines = new List<(int Index, ToolStripMenuItem Item)>();
        for (int i = 0; i < Accounts; i++)
        {
            if (!_hasSniper[i])
                continue;
            if (_panes[i].Off)
            {
                items.Add(new ToolStripMenuItem($"{_config.AccountNames[i]}: conta desligada") { Enabled = false });
                continue;
            }
            var item = new ToolStripMenuItem($"{_config.AccountNames[i]}: conferindo…") { Enabled = false };
            items.Add(item);
            lines.Add((i, item));
        }
        foreach (var (index, item) in lines)
        {
            bool? on = _panes[index].Core is CoreWebView2 core ? await RotomSniper.GetEnabledAsync(core) : null;
            if (on is not bool enabled)
            {
                item.Text = $"{_config.AccountNames[index]}: o Rotom não está na página (recarregue a conta)";
                continue;
            }
            item.Text = $"{_config.AccountNames[index]}: {(enabled ? "ligado" : "desligado")}";
            item.Checked = enabled;
            item.Enabled = true;
            item.Click += async (_, _) => await SetSniperAsync(index, !enabled);
        }
    }

    /// <summary>Liga ou desliga o sniper numa conta (ou em todas, com <paramref name="only"/> nulo).</summary>
    private async Task SetSniperAsync(int? only, bool enabled)
    {
        var failed = new List<string>();
        for (int i = 0; i < Accounts; i++)
        {
            if (!_hasSniper[i] || _panes[i].Off || only is int one && one != i)
                continue;
            if (_panes[i].Core is not CoreWebView2 core || !await RotomSniper.SetEnabledAsync(core, enabled))
                failed.Add(_config.AccountNames[i]);
        }
        // Onde deu certo, o próprio Rotom avisa na página do jogo ("AUTO-SNIPE ATIVADO/PAUSADO").
        if (failed.Count > 0)
            DeckBox.Show(this,
                $"Não consegui {(enabled ? "ligar" : "desligar")} o sniper em: {string.Join(", ", failed)}.\n\n" +
                "O deck mexe no Rotom pela página do jogo: recarregue a conta (⟳ no cabeçalho) e tente de novo.",
                Text, DeckBoxIcon.Warn);
    }

    // ---------- Voto no TopIdle ----------

    /// <summary>A cada segundo: atualiza os botões de voto e confere no TopIdle as contas cuja hora chegou.</summary>
    private void UpdateVotes()
    {
        if (_env is null)
            return;
        if (_topIdleGameId is null)
        {
            if (!_topIdleLookup && DateTime.Now >= _topIdleRetryAt)
                _ = FindTopIdleGameAsync();
            return;
        }
        for (int i = 0; i < Accounts; i++)
        {
            if (!_voteBusy[i] && DateTime.Now >= _voteRefreshAt[i] && _panes[i].Core is not null && !_panes[i].Off)
                _ = RefreshVoteAsync(i);
            RenderVote(i);
        }
        RenderVoteStatus();
    }

    private async Task<string?> FindTopIdleGameAsync()
    {
        _topIdleLookup = true;
        _topIdleRetryAt = DateTime.Now.AddMinutes(10);
        try
        {
            _topIdleGameId = await TopIdle.FindGameIdAsync("pokeidle.io");
        }
        catch
        {
            // Sem internet ou TopIdle fora do ar: tenta de novo daqui a pouco.
        }
        finally
        {
            _topIdleLookup = false;
        }
        return _topIdleGameId;
    }

    private async Task RefreshVoteAsync(int index)
    {
        if (_topIdleGameId is not string gameId || _panes[index].Core is not CoreWebView2 core)
            return;
        _voteBusy[index] = true;
        try
        {
            var state = await TopIdle.GetStateAsync(core.CookieManager, gameId);
            _votes[index] = state;
            // Confere de novo a cada 30 min, ou logo depois que o próximo voto liberar
            // (já votou mas o TopIdle ainda não deu a hora do próximo: de novo em 2 min).
            var next = DateTime.Now.AddMinutes(30);
            if (state.LoggedIn && !state.CanVote)
                next = state.NextVoteAt is not DateTime at ? DateTime.Now.AddMinutes(2)
                    : at > DateTime.Now ? (at.AddSeconds(3) < next ? at.AddSeconds(3) : next) : DateTime.Now.AddMinutes(1);
            _voteRefreshAt[index] = next;
        }
        catch
        {
            _voteRefreshAt[index] = DateTime.Now.AddMinutes(5);
        }
        finally
        {
            _voteBusy[index] = false;
        }
        RenderVote(index);
        RenderVoteStatus();
    }

    private void RenderVote(int index)
    {
        var pane = _panes[index];
        if (_topIdleGameId is null)
        {
            pane.HideVote();
            return;
        }
        var state = _votes[index];
        if (state is null)
            pane.SetVote("🗳 TopIdle", Theme.Muted, false, "Conferindo o voto desta conta no TopIdle…");
        else if (!state.LoggedIn)
            pane.SetVote("🗳 Votar", Theme.Text, false,
                "Votar no pokeidle pelo TopIdle (o jogo dá recompensa por voto). Na primeira vez, entre no TopIdle " +
                "com o Discord ou o Google na janela que abre: o login fica salvo nesta conta.");
        else if (state.Ready)
            pane.SetVote("🗳 Votar", Theme.Vote, true,
                $"Voto liberado no TopIdle ({state.User}). Clique para votar: quem vota é você, na página do TopIdle.");
        else if (state.NextVoteAt is DateTime at)
            pane.SetVote("🗳 " + FormatLeft(at - DateTime.Now), Theme.Muted, false,
                $"Esta conta já votou. O próximo voto libera {FormatWhen(at)}.");
        else
            // Acabou de votar: o TopIdle ainda não disse quando libera o próximo.
            pane.SetVote("🗳 ✓ Votou", Theme.Muted, false, "Esta conta já votou. Conferindo no TopIdle quando libera o próximo voto…");
    }

    /// <summary>A janela de voto viu o voto entrar: o botão vira "votou" e o TopIdle é consultado logo em seguida.</summary>
    private void MarkVoted(int index)
    {
        _votes[index] = (_votes[index] ?? new VoteState(true, true, null, null)) with { LoggedIn = true, CanVote = false, NextVoteAt = null };
        _voteRefreshAt[index] = DateTime.Now.AddSeconds(2);
        RenderVote(index);
        RenderVoteStatus();
    }

    private void RenderVoteStatus()
    {
        if (_topIdleGameId is null)
        {
            _voteStatus.Text = "";
            return;
        }
        var on = Enumerable.Range(0, Accounts).Where(i => !_panes[i].Off).ToList();
        var ready = on.Where(i => _votes[i]?.Ready == true).Select(i => _config.AccountNames[i]).ToList();
        var next = on.Select(i => _votes[i]).Where(v => v is { LoggedIn: true, Ready: false, NextVoteAt: not null }).Select(v => v!.NextVoteAt!.Value)
            .DefaultIfEmpty().Min();
        _voteStatus.ForeColor = ready.Count > 0 ? Theme.Vote : Theme.Muted;
        _voteStatus.Text = ready.Count > 0 ? "🗳 Voto liberado: " + string.Join(", ", ready)
            : next > DateTime.Now ? "🗳 Próximo voto em " + FormatLeft(next - DateTime.Now)
            : "🗳 Vote no TopIdle pelo botão de cada conta";
    }

    private static string FormatLeft(TimeSpan left) =>
        left.TotalHours >= 1 ? $"{(int)left.TotalHours} h {left.Minutes:00} min" : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} min";

    private static string FormatWhen(DateTime at) =>
        at.Date == DateTime.Today ? $"hoje às {at:HH:mm}" : at.Date == DateTime.Today.AddDays(1) ? $"amanhã às {at:HH:mm}" : $"em {at:dd/MM} às {at:HH:mm}";

    /// <summary>
    /// Abre a página de voto do TopIdle no navegador da conta (onde fica o login do TopIdle dela), já com
    /// o nick do personagem. Quem vota é você; o deck percebe o voto registrado e atualiza o botão.
    /// </summary>
    private async Task OpenVoteAsync(AccountPane pane, string? url)
    {
        if (_env is null)
            return;
        int index = pane.Index;
        if (_voteWindows[index] is { IsDisposed: false } open)
        {
            if (url is not null)
                open.WebView.CoreWebView2?.Navigate(url);
            if (open.WindowState == FormWindowState.Minimized)
                open.WindowState = FormWindowState.Normal;
            open.Activate();
            return;
        }

        if ((_topIdleGameId ?? await FindTopIdleGameAsync()) is not string gameId)
        {
            DeckBox.Show(this, "Não consegui falar com o TopIdle agora.\n\nConfira a internet e tente de novo; as contas seguem rodando.",
                Text, DeckBoxIcon.Warn);
            return;
        }
        url ??= TopIdle.VoteUrl(gameId, await pane.ReadNickAsync());

        var window = await PopupWindow.CreateAsync(_env, pane.ProfileName, new Size(1100, 820), closeOnDeactivate: false);
        var area = Screen.FromControl(this).WorkingArea;
        window.Location = new Point(area.X + Math.Max(0, (area.Width - window.Width) / 2), area.Y + Math.Max(0, (area.Height - window.Height) / 2));
        string prefix = $"Votar com {pane.AccountName} · quem vota é você, nesta página do TopIdle";
        window.Text = prefix;
        var core = window.WebView.CoreWebView2;
        core.DocumentTitleChanged += (_, _) => window.Text = prefix;
        core.WebResourceResponseReceived += (_, e) =>
        {
            var uri = e.Request.Uri;
            if (TopIdle.IsVoteRegistered(e.Request.Method, uri, e.Response?.StatusCode ?? 0, gameId))
            {
                prefix = $"✓ Voto registrado com {pane.AccountName}! Pode fechar esta janela";
                window.Text = prefix;
                MarkVoted(index);
            }
            else if (TopIdle.IsLoginChange(uri))
            {
                _voteRefreshAt[index] = DateTime.Now.AddSeconds(2);
            }
        };
        window.FormClosed += (_, _) =>
        {
            _voteWindows[index] = null;
            _voteRefreshAt[index] = DateTime.Now.AddSeconds(1);
        };
        _voteWindows[index] = window;
        core.Navigate(url);
        window.Show(this);
    }

    private void SaveConfig() => _config.Save(_configPath);

}
