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
    string Explain)
{
    /// <summary>Já conferida com as vendas reais e os anúncios de agora (senão, só pela média de 7 dias do jogo).</summary>
    public bool Checked { get; init; }

    /// <summary>Na fila da conferência (o cartão mostra "conferindo…").</summary>
    public bool Checking { get; init; }

    /// <summary>"alta", "média" ou "baixa": quanto dá para confiar que vende pelo preço de revenda.</summary>
    public string? Confidence { get; init; }

    /// <summary>A mediana do preço por unidade das vendas reais recentes na moeda da revenda, e quantas foram (7 dias).</summary>
    public double? RecentMedian { get; init; }

    public int RecentCount { get; init; }

    /// <summary>O anúncio mais barato de outro vendedor na moeda da revenda (null: ninguém mais vendendo).</summary>
    public double? Competitor { get; init; }

    /// <summary>O preço de revenda pela média do jogo, antes da conferência.</summary>
    public double AverageSell { get; init; }

    public string SellKey => SellCurrency == "gemas" ? "orb" : "gold";
}

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
    public static string Script { get; } = SocketHub.With("mercado.js");

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

        /// <summary>Os anúncios de agora de cada item conferido ("itemId:gold|orb" → preços por unidade, do menor ao maior).</summary>
        public Dictionary<string, List<double>> Listings { get; } = new();

        /// <summary>As vendas reais de cada item conferido ("itemId:gold|orb" → quando e o preço por unidade).</summary>
        public Dictionary<string, List<(DateTime At, double Unit)>> History { get; } = new();

        /// <summary>Pedidos de conferência ainda na fila da página.</summary>
        public int Pending { get; init; }
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
            Pending = Int(root, "pending") ?? 0,
        };
        if (root.TryGetProperty("listings", out var ls) && ls.ValueKind == JsonValueKind.Object)
            foreach (var p in ls.EnumerateObject())
                if (p.Value.TryGetProperty("linhas", out var lines) && lines.ValueKind == JsonValueKind.Array)
                    snapshot.Listings[p.Name] = lines.EnumerateArray().Select(l => Num(l, "preco")).OfType<double>().Where(v => v > 0).Order().ToList();
        if (root.TryGetProperty("history", out var hs) && hs.ValueKind == JsonValueKind.Object)
            foreach (var p in hs.EnumerateObject())
                if (p.Value.TryGetProperty("linhas", out var lines) && lines.ValueKind == JsonValueKind.Array)
                    snapshot.History[p.Name] = lines.EnumerateArray().Select(SaleOf).OfType<(DateTime, double)>().ToList();
        snapshot.GemValue = GemValue(rows);
        snapshot.Chances.AddRange(Find(rows, goldTax, snapshot.GemValue));
        snapshot.Chances.Sort((a, b) => b.Profit.CompareTo(a.Profit));
        return snapshot;
    }

    /// <summary>
    /// Uma venda da Tabela de preços: quando e o preço por unidade. O "bruto" é o valor da venda; com mais de uma unidade
    /// (o "qtd", ou o "10x" no começo da descrição), divide.
    /// </summary>
    private static (DateTime, double)? SaleOf(JsonElement l)
    {
        if (Num(l, "em") is not double em || Num(l, "bruto") is not double gross || gross <= 0)
            return null;
        double unit = Num(l, "unitario") is double u and > 0 ? u
            : Int(l, "qtd") is int q and > 1 ? gross / q
            : Str(l, "descricao") is string d && System.Text.RegularExpressions.Regex.Match(d, @"^\s*(\d+)\s*[x×]") is { Success: true } m
              && int.TryParse(m.Groups[1].Value, out int n) && n > 1 ? gross / n
            : gross;
        return (DateTimeOffset.FromUnixTimeMilliseconds((long)em).LocalDateTime, unit);
    }

    /// <summary>
    /// Confere as oportunidades com o que a página trouxe: as vendas reais recentes do item na moeda da revenda e o
    /// anúncio mais barato de outro vendedor. O preço de revenda passa a ser o menor entre a mediana das vendas reais e um
    /// pouco abaixo do concorrente (para vender, é preciso estar abaixo dele); o lucro é refeito com ele. A que deixa de
    /// dar lucro sai da lista. <paramref name="checking"/>: as que estão na fila (ganham o "conferindo…").
    /// </summary>
    public static List<MarketChance> Refine(Snapshot s, IReadOnlyCollection<string> checking)
    {
        var result = new List<MarketChance>();
        var since = DateTime.Now.AddDays(-7);
        foreach (var c in s.Chances)
        {
            string key = $"{c.ItemId}:{c.SellKey}";
            if (!s.Listings.TryGetValue(key, out var listing) || !s.History.TryGetValue(key, out var sales))
            {
                result.Add(checking.Contains(key) ? c with { Checking = true } : c);
                continue;
            }
            bool sameCurrency = c.BuyCurrency == c.SellCurrency;
            // Na revenda na mesma moeda, o 1º anúncio é o que você compra: o concorrente é o seguinte.
            double? competitor = listing.Skip(sameCurrency ? 1 : 0).Cast<double?>().FirstOrDefault();
            var recent = sales.Where(x => x.At >= since).Select(x => x.Unit).Order().ToList();
            double? median = recent.Count == 0 ? null
                : recent.Count % 2 == 1 ? recent[recent.Count / 2] : (recent[recent.Count / 2 - 1] + recent[recent.Count / 2]) / 2;
            double? undercut = competitor is double comp
                ? c.SellKey == "orb" ? (comp >= 3 ? comp - 1 : comp) : Math.Floor(comp * 0.99)
                : null;
            var candidates = new[] { median, undercut }.OfType<double>().ToList();
            double sell = candidates.Count > 0 ? candidates.Min() : c.AverageSell;
            double tax = c.SellKey == "orb" ? OrbTax : s.GoldTax;
            double net = sell * (1 - tax);
            double gem = s.GemValue ?? 0;
            (double profit, double cost) = c.Kind switch
            {
                "Coins viram gemas" when gem > 0 => (net - c.Buy / gem, c.Buy / gem),
                "Gemas viram coins" when gem > 0 => (net - c.Buy * gem, c.Buy * gem),
                _ => (net - c.Buy, c.Buy),
            };
            if (profit <= 0)
                continue;
            string confidence = recent.Count >= 5 ? "alta" : recent.Count >= 2 ? "média" : "baixa";
            string money(double v) => c.SellKey == "orb" ? Gems(v) : Gold(v);
            string check = (median is double md ? $"Vendas reais nos últimos 7 dias: {recent.Count}, mediana de {money(md)} por unidade. "
                               : "Nenhuma venda real nos últimos 7 dias. ")
                           + (competitor is double cp ? $"Anúncio mais barato de outro vendedor: {money(cp)}. " : "Ninguém mais está vendendo nessa moeda. ")
                           + $"Preço de revenda considerado: {money(sell)} (a média do jogo dizia {money(c.AverageSell)}).";
            result.Add(c with
            {
                Sell = sell,
                Profit = profit,
                Percent = profit / cost,
                Checked = true,
                Confidence = confidence,
                RecentMedian = median,
                RecentCount = recent.Count,
                Competitor = competitor,
                Explain = c.Explain + "\n\nConferido: " + check,
            });
        }
        result.Sort((a, b) => b.Profit.CompareTo(a.Profit));
        return result;
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
                        $"{r.SoldOrb} vendidos). Depois da taxa de {OrbTax:P0} você recebe {Gems(net)}: lucro de {Gems(profit)} por unidade.") { AverageSell = ao };
            }

            // Gold vira gema: compra em gold e revende em gemas pelo menor de hoje (ou pela média, sem anúncio em gemas).
            if (gem is double value && r.MinGold is double g && (r.MinOrb ?? r.AvgOrb) is double sellOrb && r.SoldOrb > 0)
            {
                double net = sellOrb * (1 - OrbTax);
                double perGem = g / net;
                double profit = net - g / value;
                if (profit > 0)
                    yield return new MarketChance(r.Id, r.Name, r.Icon, true, "Coins viram gemas", g, "gold", sellOrb, "gemas", profit, profit / (g / value),
                        r.SoldOrb, $"cada gema sai por {GoldShort(perGem)} (o mercado paga {GoldShort(value)})",
                        $"Compre em coins pelo menor anúncio ({Gold(g)}) e anuncie em gemas por {Gems(sellOrb)}. Depois da taxa de {OrbTax:P0} " +
                        $"você recebe {Gems(net)}: cada gema sai por {Gold(perGem)}, e o mercado paga {Gold(value)} por gema. " +
                        $"Em gemas, o ganho é de {Gems(profit)} por unidade.") { AverageSell = sellOrb };
            }

            // ---- Ganhar gold ----

            // Revenda em gold: compra o menor anúncio em gold e revende pela média de 7 dias.
            if (r is { MinGold: double g2, AvgGold: double ag, SoldGold: > 0 })
            {
                double net = ag * (1 - goldTax);
                double profit = net - g2;
                if (profit > 0)
                    yield return new MarketChance(r.Id, r.Name, r.Icon, false, "Revenda em coins", g2, "gold", ag, "gold", profit, profit / g2,
                        r.SoldGold, $"vende em média por {GoldShort(ag)}",
                        $"Compre pelo menor anúncio em coins ({Gold(g2)}) e anuncie de novo pela média dos últimos 7 dias ({Gold(ag)}; " +
                        $"{r.SoldGold} vendidos). Depois da taxa de {goldTax:P0} você recebe {Gold(net)}: lucro de {Gold(profit)} por unidade.") { AverageSell = ag };
            }

            // Gema vira gold: compra em gemas e revende em gold pelo menor de hoje (ou pela média).
            if (gem is double value2 && r.MinOrb is double o2 && (r.MinGold ?? r.AvgGold) is double sellGold && r.SoldGold > 0)
            {
                double cost = o2 * value2;
                double net = sellGold * (1 - goldTax);
                double profit = net - cost;
                if (profit > 0)
                    yield return new MarketChance(r.Id, r.Name, r.Icon, false, "Gemas viram coins", o2, "gemas", sellGold, "gold", profit, profit / cost,
                        r.SoldGold, $"cada gema rende {GoldShort(net / o2)} (o mercado paga {GoldShort(value2)})",
                        $"Compre em gemas pelo menor anúncio ({Gems(o2)}, que valem {Gold(cost)}) e anuncie em coins por {Gold(sellGold)}. " +
                        $"Depois da taxa de {goldTax:P0} você recebe {Gold(net)}: lucro de {Gold(profit)} por unidade.") { AverageSell = sellGold };
            }
        }
    }

    public static string Gold(double value) => Math.Round(value).ToString("N0", Br) + " coins";

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
