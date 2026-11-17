namespace PokeIdleMulti;

/// <summary>
/// O socket.js: pega o socket do jogo uma vez só e lê cada mensagem no máximo uma vez para os scripts do deck (recompra,
/// PvP e mercado). Vai na frente de cada um deles: o primeiro que roda cria o <c>window.__deckSocket</c>, os outros
/// só usam (e o script injetado de novo numa página que carregou sem ele também encontra).
/// </summary>
internal static class SocketHub
{
    public static string Script { get; } = Read("socket.js");

    /// <summary>O script de um recurso com o socket.js na frente.</summary>
    public static string With(string resource) => Script + "\n" + Read(resource);

    private static string Read(string name)
    {
        using var stream = typeof(SocketHub).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
