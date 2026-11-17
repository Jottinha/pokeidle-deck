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

  // Os ícones dos itens (o mesmo arquivo que o jogo usa), para os cartões da janela do deck.
  let icons = null;
  function loadIcons() {
    if (icons) return;
    icons = {};
    fetch('/assets/items-icons.json')
      .then((r) => r.json())
      .then((j) => { icons = j || {}; })
      .catch(() => { icons = null; });
  }

  const wait = (ms) => new Promise((r) => setTimeout(r, ms));
  async function until(fn, ms = 4000) {
    const end = Date.now() + ms;
    while (Date.now() < end) {
      const v = fn();
      if (v) return v;
      await wait(120);
    }
    return null;
  }
  let lastOpen = null;

  // Leva a tela do jogo até o item no Mercado da Comunidade, com os mesmos cliques que a pessoa faria: abre o mercado,
  // a aba Itens, todas as categorias, o item e a moeda. Não compra: a compra continua no botão do jogo.
  async function open(itemId, moeda) {
    const q = (s) => document.querySelector(s);
    const menu = q('button[data-modal="community"]');
    if (!menu) return 'o botão do mercado não está na tela (a conta está no jogo?)';
    if (!q('#cm-lista')) menu.click();
    if (!(await until(() => q('#cm-lista')))) return 'o mercado não abriu';
    const tipo = await until(() => q('.cm-filtro[data-tipo="item"]'), 2500);
    if (tipo && !tipo.classList.contains('on')) tipo.click();
    const cat = await until(() => q('.cm-filtro[data-cat=""]'), 1500);
    if (cat && !cat.classList.contains('on')) cat.click();
    let row = await until(() => q(`.cmi-linha[data-item="${Number(itemId)}"]`), 2500);
    if (!row) {
      // Um filtro (a busca, "ocultar sem estoque") pode esconder o item: limpa e tenta de novo.
      q('#cm-limpar')?.click();
      await wait(300);
      q('.cm-filtro[data-tipo="item"]:not(.on)')?.click();
      row = await until(() => q(`.cmi-linha[data-item="${Number(itemId)}"]`), 2500);
    }
    if (!row) return 'não achei o item na lista do mercado';
    row.scrollIntoView({ block: 'center' });
    row.click();
    const sw = await until(() => q(`[data-swmoeda="${moeda === 'orb' ? 'orb' : 'gold'}"]`), 2500);
    if (sw && !sw.classList.contains('on')) sw.click();
    return 'ok';
  }

  window.__deckMercado = {
    /** Pede o resumo de todos os itens (menor preço e média de 7 dias). false: sem conexão ou cedo demais. */
    scan: () => send({ t: 'market.itens' }),
    /** Abre o item no mercado do jogo; o resultado fica em summary().lastOpen. */
    open(itemId, moeda) {
      lastOpen = { at: Date.now(), result: 'abrindo' };
      open(itemId, moeda)
        .then((result) => { lastOpen = { at: Date.now(), result }; })
        .catch((e) => { lastOpen = { at: Date.now(), result: String(e) }; });
      return true;
    },
    /** Pede os anúncios de um item numa moeda ('gold' ou 'orb'). */
    listings: (itemId, moeda) => send({ t: 'market.item', itemId: Number(itemId), moeda: moeda === 'orb' ? 'orb' : 'gold' }),
    /** O que o deck lê. */
    summary() {
      late();
      loadIcons();
      const ids = new Set(catalog.map((i) => String(i.id)));
      const itemIcons = {};
      if (icons) for (const [id, rel] of Object.entries(icons)) if (ids.has(id)) itemIcons[id] = rel;
      return {
        connected: socket?.readyState === 1,
        taxGoldPct,
        catalog,
        icons: itemIcons,
        items,
        lastOpen,
      };
    },
  };
})();
