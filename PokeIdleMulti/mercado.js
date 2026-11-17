// O analisador do Mercado da Comunidade do deck: SÓ LÊ. Acompanha o socket do jogo (como a recompra), guarda o
// catálogo e as taxas que vêm no `welcome`, e pede ao servidor as mesmas leituras que a tela do mercado pede
// (`market.itens`: o menor preço de cada item em gold e em gemas, e a média de venda dos últimos 7 dias; `market.item`:
// os anúncios de um item numa moeda). Nunca manda compra, anúncio nem nada que mude a conta.
//
// O deck chama `__deckMercado.scan()` para pedir uma leitura nova e `__deckMercado.summary()` para ler o resultado.
(() => {
  if (window.top !== window || window.__deckMercado) return;

  const GAP = 5000; // o mesmo intervalo mínimo do botão Refresh do mercado do jogo
  let socket = null;
  let catalog = [];
  let taxGoldPct = null;
  let items = null; // { at, resumo, medias }
  const listings = {}; // "itemId:moeda" → { at, linhas }
  let lastAsk = 0;

  const Native = window.WebSocket;
  if (typeof Native === 'function') {
    window.WebSocket = new Proxy(Native, {
      construct(target, args, newTarget) {
        const ws = Reflect.construct(target, args, newTarget);
        try {
          if (new URL(String(args[0]), location.href).host === location.host) attach(ws);
        } catch { }
        return ws;
      },
    });
  }

  function attach(ws) {
    socket = ws;
    ws.addEventListener('message', (e) => {
      if (ws === socket) receive(e.data);
    });
  }

  // O script pode chegar depois do socket (página já aberta): pega o que o jogo deixa à vista.
  function late() {
    if (socket?.readyState === 1) return;
    const ws = window.__activeGameWs;
    try {
      if (ws && ws.readyState === 1 && new URL(ws.url, location.href).host === location.host) attach(ws);
    } catch { }
  }

  function receive(data) {
    if (typeof data !== 'string') return;
    const head = /^\{"t":"([^"]*)"/.exec(data);
    if (head && head[1] !== 'welcome' && head[1] !== 'market') return;
    let m;
    try {
      m = JSON.parse(data);
    } catch {
      return;
    }
    if (m.t === 'welcome') {
      if (Array.isArray(m.mercado?.catalogo)) catalog = m.mercado.catalogo.map((i) => ({ id: i.id, nome: i.nome, categoria: i.categoria }));
      const pct = m.economia?.taxaMercadoGoldPct ?? m.mercado?.taxaGoldPct;
      if (Number.isFinite(pct)) taxGoldPct = pct;
    } else if (m.t === 'market' && m.aba === 'itens') {
      items = { at: Date.now(), resumo: m.resumo ?? {}, medias: m.medias ?? {} };
    } else if (m.t === 'market' && m.aba === 'item') {
      listings[`${m.itemId}:${m.moeda}`] = {
        at: Date.now(),
        linhas: (m.linhas ?? []).map((a) => ({ vendedor: a.vendedor, qtd: a.qtd, preco: a.preco })),
      };
    }
  }

  function send(obj) {
    late();
    if (socket?.readyState !== 1) return false;
    const now = Date.now();
    if (now - lastAsk < GAP) return false;
    lastAsk = now;
    socket.send(JSON.stringify(obj));
    return true;
  }

  window.__deckMercado = {
    /** Pede o resumo de todos os itens (menor preço e média de 7 dias). false: sem conexão ou cedo demais. */
    scan: () => send({ t: 'market.itens' }),
    /** Pede os anúncios de um item numa moeda ('gold' ou 'orb'). */
    listings: (itemId, moeda) => send({ t: 'market.item', itemId: Number(itemId), moeda: moeda === 'orb' ? 'orb' : 'gold' }),
    /** O que o deck lê. */
    summary() {
      late();
      return {
        connected: socket?.readyState === 1,
        taxGoldPct,
        catalog,
        items,
        listings,
      };
    },
  };
})();
