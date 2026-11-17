using System.Text.Json;
using System.Text.Json.Serialization;

namespace PokeIdleMulti;

internal enum DeckLayout { Grid, Columns, Focus, Main }

/// <summary>Preferências do deck salvas entre uma execução e outra.</summary>
internal sealed class DeckConfig
{
    private const int Accounts = 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string[] AccountNames { get; set; } = { "Conta 1", "Conta 2", "Conta 3", "Conta 4" };

    public DeckLayout Layout { get; set; } = DeckLayout.Grid;

    /// <summary>A conta ampliada no layout de foco.</summary>
    public int Focused { get; set; }

    /// <summary>A conta grande no layout Principal (as outras 3 ficam em miniatura ao lado).</summary>
    public int MainAccount { get; set; }

    /// <summary>
    /// No layout Principal, as miniaturas ligam o Ultra do jogo sozinhas (a caixa de números é o que dá para ler numa
    /// conta pequena, e gasta bem menos). Ao virar a principal, a conta volta ao jogo normal (se o Ultra geral estiver desligado).
    /// </summary>
    public bool MiniUltra { get; set; } = true;

    /// <summary>O zoom das miniaturas no layout Principal.</summary>
    public double MiniZoom { get; set; } = 0.5;

    /// <summary>Zoom da página de cada conta (Ctrl + roda do mouse).</summary>
    public double[] Zoom { get; set; } = { 1, 1, 1, 1 };

    /// <summary>
    /// O zoom da grade e das colunas, guardado ao ampliar uma conta (o foco põe tudo em 100%); voltar do foco
    /// devolve este. Nulo enquanto não entrou no foco.
    /// </summary>
    public double? ZoomOutsideFocus { get; set; }

    public bool[] Muted { get; set; } = new bool[Accounts];

    /// <summary>Contas desligadas no botão ⏻: ficam no deck, mas não abrem o jogo (nem extensões, lives e voto).</summary>
    public bool[] Off { get; set; } = new bool[Accounts];

    /// <summary>
    /// Menos animações no jogo (o "reduzir movimento" do sistema, que o próprio jogo respeita). Gasta bem
    /// menos CPU e placa de vídeo com várias contas à vista.
    /// </summary>
    public bool ReduceMotion { get; set; } = true;

    /// <summary>
    /// Quadros por segundo do mapa da caça (o requestAnimationFrame do jogo). O mapa é só tela: a caça roda no servidor.
    /// 60 = sem limite. 15 corta a maior parte da pintura do jogo e da placa de vídeo.
    /// </summary>
    public int GameFps { get; set; } = 15;

    /// <summary>
    /// De quanto em quanto tempo (ms) os sprites animados do jogo (equipe, listas, mercado) trocam de quadro. O jogo usa
    /// 220; cada conta tem 120–190 deles, e cada troca é uma pintura. 660 corta ~2/3 delas.
    /// </summary>
    public int SpriteMs { get; set; } = 660;

    /// <summary>
    /// O Modo ULTRA Economia do jogo em todas as contas (o jogo inteiro fora da tela, só uma caixa de números).
    /// Ligado, a conta que recarrega ou é ligada volta a ele sozinha.
    /// </summary>
    public bool Ultra { get; set; }

    /// <summary>Posição e tamanho da janela quando não está maximizada: x, y, largura, altura.</summary>
    public int[]? WindowBounds { get; set; }

    public bool Maximized { get; set; } = true;

    /// <summary>A recompra de pokebolas, revives e poções de cada conta (o carrinho no cabeçalho dela).</summary>
    public RestockConfig[] Restock { get; set; } = Enumerable.Range(0, Accounts).Select(_ => new RestockConfig()).ToArray();

    /// <summary>A fila de PvP de cada conta (⚔ no cabeçalho): ligada, a conta entra na fila do ranqueado e volta depois de cada partida.</summary>
    public bool[] Pvp { get; set; } = new bool[Accounts];

    /// <summary>
    /// Como as partidas da fila de PvP aparecem em cada conta (botão direito no ⚔): "completa" (o replay do jogo na tela),
    /// "mini" (a janelinha do canto) ou "off" (só o aviso do resultado). De fábrica, "off".
    /// </summary>
    public string[] PvpScreen { get; set; } = Enumerable.Range(0, Accounts).Select(_ => PvpScreens.Off).ToArray();

    /// <summary>A velocidade das partidas mostradas (completa ou mini) em cada conta: 0 = a do jogo (acelera para caber até a próxima).</summary>
    public double[] PvpSpeed { get; set; } = new double[Accounts];

    public static DeckConfig Load(string path)
    {
        DeckConfig config;
        try
        {
            config = JsonSerializer.Deserialize<DeckConfig>(File.ReadAllText(path), JsonOptions) ?? new DeckConfig();
        }
        catch
        {
            config = new DeckConfig();
        }

        // Um arquivo editado à mão não pode deixar listas mais curtas que as 4 contas.
        config.AccountNames = Fill(config.AccountNames, i => $"Conta {i + 1}");
        config.Zoom = Fill(config.Zoom, _ => 1.0);
        config.Muted = Fill(config.Muted, _ => false);
        config.Off = Fill(config.Off, _ => false);
        config.Restock = Fill(config.Restock, _ => new RestockConfig()).Select(r => (r ?? new RestockConfig()).Normalized()).ToArray();
        config.Pvp = Fill(config.Pvp, _ => false);
        config.PvpScreen = Fill(config.PvpScreen, _ => PvpScreens.Off).Select(PvpScreens.Normalized).ToArray();
        config.PvpSpeed = Fill(config.PvpSpeed, _ => 0d).Select(PvpScreens.NormalizedSpeed).ToArray();
        config.Focused = Math.Clamp(config.Focused, 0, Accounts - 1);
        config.MainAccount = Math.Clamp(config.MainAccount, 0, Accounts - 1);
        config.MiniZoom = Math.Clamp(config.MiniZoom, 0.25, 1);
        config.GameFps = Math.Clamp(config.GameFps, 5, 60);
        config.SpriteMs = Math.Clamp(config.SpriteMs, 220, 10000);
        return config;
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // Não conseguir salvar a preferência não pode derrubar o deck.
        }
    }


    private static T[] Fill<T>(T[]? values, Func<int, T> fallback) =>
        Enumerable.Range(0, Accounts).Select(i => values is not null && i < values.Length ? values[i] : fallback(i)).ToArray();
}
