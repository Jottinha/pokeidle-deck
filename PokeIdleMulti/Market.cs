using System.Globalization;
using System.Text.Json;

namespace PokeIdleMulti;

/// <summary>Uma oportunidade de revenda no Mercado da Comunidade (só uma estimativa: o deck não compra nada).</summary>
internal sealed record MarketChance(
    int ItemId,
    string Item,
    string Kind,
    string Buy,
    string Sell,
    double ProfitGold,
    double Percent,
    int Sales7d,
    string Explain);

/// <summary>
/// O analisador do Mercado da Comunidade. O mercado.js da página lê, pelo socket do jogo, o menor preço de cada item em
/// gold e em gemas e a média de venda dos últimos 7 dias; aqui viram oportunidades:
/// <list type="bullet">
/// <item><b>Abaixo da média:</b> o menor anúncio está abaixo da média de 7 dias, já descontada a taxa de quem revende.</item>
/// <item><b>Gold → gemas</b> e <b>gemas → gold:</b> o mesmo item mais barato numa moeda do que vale na outra, com a gema
/// convertida pelo valor que o próprio mercado dá a ela (a mediana de média em gold ÷ média em gemas dos itens).</item>
/// </list>
/// O lucro é sempre em gold, depois da taxa da venda (15% em gold; 15% em gemas para itens).
/// </summary>
internal static class Market
{
    public static string Script { get; } = ReadScript();

    public const string ScanScript = "window.__deckMercado ? window.__deckMercado.scan() : null";
    public const string SummaryScript = "window.__deckMercado ? window.__deckMercado.summary() : null";

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
    }

    private sealed record Row(int Id, string Name, double? MinGold, double? MinOrb, double? AvgGold, int SoldGold, double? AvgOrb, int SoldOrb, int Listings);

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
        var names = new Dictionary<int, string>();
        if (root.TryGetProperty("catalog", out var catalog) && catalog.ValueKind == JsonValueKind.Array)
            foreach (var item in catalog.EnumerateArray())
                if (Int(item, "id") is int id)
                    names[id] = Str(item, "nome") ?? $"item {id}";

        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object)
            return new Snapshot { Connected = connected, GoldTax = goldTax };

        var at = Num(items, "at") is double ms ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms).LocalDateTime : (DateTime?)null;
        var rows = new List<Row>();
        var resumo = items.TryGetProperty("resumo", out var r) && r.ValueKind == JsonValueKind.Object ? r : default;
        var medias = items.TryGetProperty("medias", out var m) && m.ValueKind == JsonValueKind.Object ? m : default;
        var ids = new HashSet<string>();
        if (resumo.ValueKind == JsonValueKind.Object)
            foreach (var p in resumo.EnumerateObject())
                ids.Add(p.Name);
        foreach (var key in ids)
        {
            if (!int.TryParse(key, out int id))
                continue;
            var s = resumo.GetProperty(key);
            int listings = Int(s, "anuncios") ?? 0;
            if (listings <= 0)
                continue;
            double? avgGold = null, avgOrb = null;
            int soldGold = 0, soldOrb = 0;
            if (medias.ValueKind == JsonValueKind.Object && medias.TryGetProperty(key, out var avg) && avg.ValueKind == JsonValueKind.Object)
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
            rows.Add(new Row(id, names.GetValueOrDefault(id, $"item {id}"), Positive(Num(s, "minGold")), Positive(Num(s, "minOrb")),
                Positive(avgGold), soldGold, Positive(avgOrb), soldOrb, listings));
        }

        var snapshot = new Snapshot { Connected = connected, At = at, GoldTax = goldTax, ItemsWithListings = rows.Count };
        snapshot.GemValue = GemValue(rows);
        snapshot.Chances.AddRange(Find(rows, goldTax, snapshot.GemValue));
        snapshot.Chances.Sort((a, b) => b.ProfitGold.CompareTo(a.ProfitGold));
        return snapshot;
    }

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
            // Abaixo da média em gold: compra o menor anúncio, revende pela média.
            if (r is { MinGold: double g, AvgGold: double ag, SoldGold: > 0 })
            {
                double profit = ag * (1 - goldTax) - g;
                if (profit > 0)
                    yield return new MarketChance(r.Id, r.Name, "Abaixo da média (gold)", Gold(g), Gold(ag), profit, profit / g, r.SoldGold,
                        $"O menor anúncio em gold está a {Gold(g)} e a média de venda dos últimos 7 dias é {Gold(ag)} ({r.SoldGold} vendidos). " +
                        $"Revendendo pela média, depois da taxa de {goldTax:P0}, sobram {Gold(ag * (1 - goldTax))}.");
            }
            // Abaixo da média em gemas.
            if (r is { MinOrb: double o, AvgOrb: double ao, SoldOrb: > 0 })
            {
                double gems = ao * (1 - OrbTax) - o;
                if (gems > 0)
                    yield return new MarketChance(r.Id, r.Name, "Abaixo da média (gemas)", Gems(o), Gems(ao), gem is double v ? gems * v : 0,
                        gems / o, r.SoldOrb,
                        $"O menor anúncio em gemas está a {Gems(o)} e a média de venda dos últimos 7 dias é {Gems(ao)} ({r.SoldOrb} vendidos). " +
                        $"Revendendo pela média, depois da taxa de {OrbTax:P0}, sobram {Gems(ao * (1 - OrbTax))}" +
                        (gem is double g2 ? $" (≈ {Gold(gems * g2)} de lucro em gold)." : "."));
            }
            if (gem is not double value)
                continue;
            // Gold → gemas: compra em gold e anuncia em gemas pelo menor de hoje (ou pela média, sem anúncio em gemas).
            if (r.MinGold is double buyGold && (r.MinOrb ?? r.AvgOrb) is double sellOrb && r.SoldOrb > 0)
            {
                double profit = sellOrb * (1 - OrbTax) * value - buyGold;
                if (profit > 0)
                    yield return new MarketChance(r.Id, r.Name, "Gold → gemas", Gold(buyGold), Gems(sellOrb), profit, profit / buyGold, r.SoldOrb,
                        $"Comprando em gold por {Gold(buyGold)} e anunciando em gemas por {Gems(sellOrb)}, depois da taxa de {OrbTax:P0} " +
                        $"ficam {Gems(sellOrb * (1 - OrbTax))} ≈ {Gold(sellOrb * (1 - OrbTax) * value)} (1 gema ≈ {Gold(value)}).");
            }
            // Gemas → gold: compra em gemas e anuncia em gold pelo menor de hoje (ou pela média).
            if (r.MinOrb is double buyOrb && (r.MinGold ?? r.AvgGold) is double sellGold && r.SoldGold > 0)
            {
                double cost = buyOrb * value;
                double profit = sellGold * (1 - goldTax) - cost;
                if (profit > 0)
                    yield return new MarketChance(r.Id, r.Name, "Gemas → gold", Gems(buyOrb), Gold(sellGold), profit, profit / cost, r.SoldGold,
                        $"Comprando em gemas por {Gems(buyOrb)} (≈ {Gold(cost)}) e anunciando em gold por {Gold(sellGold)}, depois da taxa de " +
                        $"{goldTax:P0} ficam {Gold(sellGold * (1 - goldTax))} (1 gema ≈ {Gold(value)}).");
            }
        }
    }

    public static string Gold(double value) => Math.Round(value).ToString("N0", Br) + " gold";

    public static string Gems(double value) => Math.Round(value).ToString("N0", Br) + " gemas";

    public static string N(double value) => Math.Round(value).ToString("N0", Br);

    private static double? Positive(double? value) => value is > 0 ? value : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static int? Int(JsonElement e, string name) => Num(e, name) is double d ? (int)Math.Round(d) : null;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string ReadScript()
    {
        using var stream = typeof(Market).Assembly.GetManifestResourceStream("mercado.js")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
