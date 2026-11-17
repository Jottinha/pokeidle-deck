using System.Globalization;
using System.Text.Json;

namespace PokeIdleMulti;

/// <summary>
/// Uma oportunidade no Mercado da Comunidade (estimativa: o deck não compra nada). <see cref="Gems"/>: termina com mais
/// gemas (o lucro está em gemas); senão, com mais gold. Os preços são por unidade.
/// </summary>
internal sealed record MarketChance(
    int ItemId,
    string Item,
    string? Icon,
    bool Gems,
    string Kind,
    double Buy,
    string BuyCurrency,
    double Sell,
    string SellCurrency,
    double Profit,
    double Percent,
    int Sales7d,
    string Note,
    string Explain);

/// <summary>
/// O analisador do Mercado da Comunidade. O mercado.js da página lê, pelo socket do jogo, o menor preço de cada item em
/// gold e em gemas e a média de venda dos últimos 7 dias; aqui viram oportunidades em duas listas:
/// <list type="bullet">
/// <item><b>Ganhar gemas:</b> revender em gemas (o menor anúncio em gemas abaixo da média em gemas) e gold vira gema
/// (comprar em gold e revender em gemas, com cada gema saindo mais barata que o mercado paga por ela).</item>
/// <item><b>Ganhar gold:</b> revender em gold e gema vira gold.</item>
/// </list>
/// Tudo já com a taxa de venda (15% em gold; 15% em gemas para itens). O valor da gema em gold é o que o próprio mercado
/// dá a ela: a mediana, entre os itens vendidos nas duas moedas, da média em gold ÷ média em gemas.
/// </summary>
internal static class Market
{
    public static string Script { get; } = ReadResource("mercado.js");

    /// <summary>A página da janela do mercado (cartões em HTML).</summary>
    public static string Page { get; } = ReadResource("mercado.html");

    public const string ScanScript = "window.__deckMercado ? window.__deckMercado.scan() : null";
    public const string SummaryScript = "window.__deckMercado ? window.__deckMercado.summary() : null";

    public static string OpenScript(int itemId, bool gems) =>
        $"window.__deckMercado ? window.__deckMercado.open({itemId}, '{(gems ? "orb" : "gold")}') : null";

    /// <summary>A taxa de venda em gemas para itens (no jogo, TAXA_ORB_PLANA).</summary>
    private const double OrbTax = 0.15;

    private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");

    public sealed class Snapshot
    {
        public bool Connected { get; init; }
        public DateTime? At { get; init; }
        public double GoldTax { get; init; } = 0.15;

        /// <summary>Quanto gold vale 1 gema no mercado agora (null sem itens vendidos nas duas moedas).</summary>
        public double? GemValue { get; set; }

        public int ItemsWithListings { get; init; }
        public List<MarketChance> Chances { get; init; } = new();

        /// <summary>O resultado do último "abrir no jogo" ("ok", "abrindo" ou o motivo); null se nunca abriu.</summary>
        public string? LastOpen { get; init; }

        public DateTime? LastOpenAt { get; init; }
    }

    private sealed record Row(int Id, string Name, string? Icon, double? MinGold, double? MinOrb, double? AvgGold, int SoldGold,
        double? AvgOrb, int SoldOrb);

    /// <summary>Lê o JSON do summary() do mercado.js; null sem o script na página.</summary>
    public static Snapshot? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "null")
            return null;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        bool connected = root.TryGetProperty("connected", out var c) && c.ValueKind == JsonValueKind.True;
        double goldTax = root.TryGetProperty("taxGoldPct", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetDouble() / 100 : 0.15;
        string? lastOpen = null;
        DateTime? lastOpenAt = null;
        if (root.TryGetProperty("lastOpen", out var lo) && lo.ValueKind == JsonValueKind.Object)
        {
            lastOpen = Str(lo, "result");
            lastOpenAt = Num(lo, "at") is double at0 ? DateTimeOffset.FromUnixTimeMilliseconds((long)at0).LocalDateTime : null;
        }
        var names = new Dictionary<int, string>();
        if (root.TryGetProperty("catalog", out var catalog) && catalog.ValueKind == JsonValueKind.Array)
            foreach (var item in catalog.EnumerateArray())
                if (Int(item, "id") is int id)
                    names[id] = Str(item, "nome") ?? $"item {id}";
        var icons = new Dictionary<int, string>();
        if (root.TryGetProperty("icons", out var ic) && ic.ValueKind == JsonValueKind.Object)
            foreach (var p in ic.EnumerateObject())
                if (int.TryParse(p.Name, out int id) && p.Value.ValueKind == JsonValueKind.String)
                    icons[id] = IconUrl(p.Value.GetString()!);

        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object)
            return new Snapshot { Connected = connected, GoldTax = goldTax, LastOpen = lastOpen, LastOpenAt = lastOpenAt };

        var at = Num(items, "at") is double ms ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms).LocalDateTime : (DateTime?)null;
        var rows = new List<Row>();
        var resumo = items.TryGetProperty("resumo", out var r) && r.ValueKind == JsonValueKind.Object ? r : default;
        var medias = items.TryGetProperty("medias", out var m) && m.ValueKind == JsonValueKind.Object ? m : default;
        if (resumo.ValueKind == JsonValueKind.Object)
            foreach (var p in resumo.EnumerateObject())
            {
                if (!int.TryParse(p.Name, out int id) || (Int(p.Value, "anuncios") ?? 0) <= 0)
                    continue;
                double? avgGold = null, avgOrb = null;
                int soldGold = 0, soldOrb = 0;
                if (medias.ValueKind == JsonValueKind.Object && medias.TryGetProperty(p.Name, out var avg) && avg.ValueKind == JsonValueKind.Object)
                {
                    if (avg.TryGetProperty("gold", out var g) && g.ValueKind == JsonValueKind.Object)
                    {
                        avgGold = Num(g, "media");
                        soldGold = Int(g, "unidades") ?? 0;
                    }
                    if (avg.TryGetProperty("orb", out var o) && o.ValueKind == JsonValueKind.Object)
                    {
                        avgOrb = Num(o, "media");
                        soldOrb = Int(o, "unidades") ?? 0;
                    }
                }
                rows.Add(new Row(id, names.GetValueOrDefault(id, $"item {id}"), icons.GetValueOrDefault(id),
                    Positive(Num(p.Value, "minGold")), Positive(Num(p.Value, "minOrb")),
                    Positive(avgGold), soldGold, Positive(avgOrb), soldOrb));
            }

        var snapshot = new Snapshot
        {
            Connected = connected, At = at, GoldTax = goldTax, ItemsWithListings = rows.Count, LastOpen = lastOpen, LastOpenAt = lastOpenAt,
        };
        snapshot.GemValue = GemValue(rows);
        snapshot.Chances.AddRange(Find(rows, goldTax, snapshot.GemValue));
        snapshot.Chances.Sort((a, b) => b.Profit.CompareTo(a.Profit));
        return snapshot;
    }

    /// <summary>O caminho do ícone no jogo ("site/assets/items/x.png") como endereço completo.</summary>
    private static string IconUrl(string rel) =>
        "https://pokeidle.io" + (rel.StartsWith('/') ? rel : "/assets/" + rel);

    /// <summary>
    /// O valor da gema em gold: a mediana, entre os itens vendidos nas duas moedas nos últimos 7 dias, da média em gold
    /// dividida pela média em gemas. Sem nenhum, a mediana dos menores preços de hoje.
    /// </summary>
    private static double? GemValue(List<Row> rows)
    {
        var ratios = rows.Where(r => r is { AvgGold: > 0, AvgOrb: > 0, SoldGold: > 0, SoldOrb: > 0 })
            .Select(r => r.AvgGold!.Value / r.AvgOrb!.Value).ToList();
        if (ratios.Count == 0)
            ratios = rows.Where(r => r is { MinGold: > 0, MinOrb: > 0 }).Select(r => r.MinGold!.Value / r.MinOrb!.Value).ToList();
        if (ratios.Count == 0)
            return null;
        ratios.Sort();
        int mid = ratios.Count / 2;
        return ratios.Count % 2 == 1 ? ratios[mid] : (ratios[mid - 1] + ratios[mid]) / 2;
    }

    private static IEnumerable<MarketChance> Find(List<Row> rows, double goldTax, double? gem)
    {
        foreach (var r in rows)
        {
            // ---- Ganhar gemas ----

            // Revenda em gemas: compra o menor anúncio em gemas e revende pela média de 7 dias.
            if (r is { MinOrb: double o, AvgOrb: double ao, SoldOrb: > 0 })
            {
                double net = ao * (1 - OrbTax);
                double profit = net - o;
                if (profit > 0)
                    yield return new MarketChance(r.Id, r.Name, r.Icon, true, "Revenda em gemas", o, "gemas", ao, "gemas", profit, profit / o,
                        r.SoldOrb, $"vende em média por {Gems(ao)}",
                        $"Compre pelo menor anúncio em gemas ({Gems(o)}) e anuncie de novo pela média dos últimos 7 dias ({Gems(ao)}; " +
                        $"{r.SoldOrb} vendidos). Depois da taxa de {OrbTax:P0} você recebe {Gems(net)}: lucro de {Gems(profit)} por unidade.");
            }

            // Gold vira gema: compra em gold e revende em gemas pelo menor de hoje (ou pela média, sem anúncio em gemas).
            if (gem is double value && r.MinGold is double g && (r.MinOrb ?? r.AvgOrb) is double sellOrb && r.SoldOrb > 0)
            {
                double net = sellOrb * (1 - OrbTax);
                double perGem = g / net;
                double profit = net - g / value;
                if (profit > 0)
                    yield return new MarketChance(r.Id, r.Name, r.Icon, true, "Gold vira gema", g, "gold", sellOrb, "gemas", profit, profit / (g / value),
                        r.SoldOrb, $"cada gema sai por {GoldShort(perGem)} (o mercado paga {GoldShort(value)})",
                        $"Compre em gold pelo menor anúncio ({Gold(g)}) e anuncie em gemas por {Gems(sellOrb)}. Depois da taxa de {OrbTax:P0} " +
                        $"você recebe {Gems(net)}: cada gema sai por {Gold(perGem)}, e o mercado paga {Gold(value)} por gema. " +
                        $"Em gemas, o ganho é de {Gems(profit)} por unidade.");
            }

            // ---- Ganhar gold ----

            // Revenda em gold: compra o menor anúncio em gold e revende pela média de 7 dias.
            if (r is { MinGold: double g2, AvgGold: double ag, SoldGold: > 0 })
            {
                double net = ag * (1 - goldTax);
                double profit = net - g2;
                if (profit > 0)
                    yield return new MarketChance(r.Id, r.Name, r.Icon, false, "Revenda em gold", g2, "gold", ag, "gold", profit, profit / g2,
                        r.SoldGold, $"vende em média por {GoldShort(ag)}",
                        $"Compre pelo menor anúncio em gold ({Gold(g2)}) e anuncie de novo pela média dos últimos 7 dias ({Gold(ag)}; " +
                        $"{r.SoldGold} vendidos). Depois da taxa de {goldTax:P0} você recebe {Gold(net)}: lucro de {Gold(profit)} por unidade.");
            }

            // Gema vira gold: compra em gemas e revende em gold pelo menor de hoje (ou pela média).
            if (gem is double value2 && r.MinOrb is double o2 && (r.MinGold ?? r.AvgGold) is double sellGold && r.SoldGold > 0)
            {
                double cost = o2 * value2;
                double net = sellGold * (1 - goldTax);
                double profit = net - cost;
                if (profit > 0)
                    yield return new MarketChance(r.Id, r.Name, r.Icon, false, "Gema vira gold", o2, "gemas", sellGold, "gold", profit, profit / cost,
                        r.SoldGold, $"cada gema rende {GoldShort(net / o2)} (o mercado paga {GoldShort(value2)})",
                        $"Compre em gemas pelo menor anúncio ({Gems(o2)}, que valem {Gold(cost)}) e anuncie em gold por {Gold(sellGold)}. " +
                        $"Depois da taxa de {goldTax:P0} você recebe {Gold(net)}: lucro de {Gold(profit)} por unidade.");
            }
        }
    }

    public static string Gold(double value) => Math.Round(value).ToString("N0", Br) + " gold";

    public static string Gems(double value) => (value < 100 ? value.ToString("0.##", Br) : Math.Round(value).ToString("N0", Br)) + " gemas";

    /// <summary>Gold curto: 6,7M, 950k.</summary>
    public static string GoldShort(double value) =>
        value >= 1e9 ? (value / 1e9).ToString("0.#", Br) + "B"
        : value >= 1e6 ? (value / 1e6).ToString("0.#", Br) + "M"
        : value >= 1e3 ? (value / 1e3).ToString("0.#", Br) + "k"
        : Math.Round(value).ToString(Br);

    private static double? Positive(double? value) => value is > 0 ? value : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static int? Int(JsonElement e, string name) => Num(e, name) is double d ? (int)Math.Round(d) : null;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string ReadResource(string name)
    {
        using var stream = typeof(Market).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
