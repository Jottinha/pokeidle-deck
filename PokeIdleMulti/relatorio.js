// O relatório da conta para o Telegram do deck: o nick, o nível, o gold e as gemas (do `welcome` e dos deltas de
// `estado`) e as capturas (o evento `capturado` das mensagens `batalha`, o mesmo que o jogo usa no log da caça).
// Só lê: manda cada novidade para o deck com `chrome.webview.postMessage('deckrel:' + JSON)`; quem decide o que vira
// mensagem no celular é o Telegram.cs.
//
//   { k: 'conta', nick, level, gold, diamonds }          — quando o nick ou o nível mudam, e no máximo 1x por minuto o resto
//   { k: 'captura', nome, level, quality, shiny, depot }  — cada pokémon capturado
(() => {
  if (window.top !== window || window.__deckRelatorio || !window.__deckSocket) return;
  window.__deckRelatorio = true;

  const KEYS = ['nick', 'level', 'gold', 'diamonds'];
  const WANTED = new Set(['welcome', 'estado', 'batalha']);
  let account = {};
  let sent = null; // o último { nick, level, gold, diamonds } mandado
  let sentAt = 0;

  const post = (o) => {
    try {
      chrome.webview.postMessage('deckrel:' + JSON.stringify(o));
    } catch { }
  };

  window.__deckSocket.subscribe({
    // A batalha chega o tempo todo: só vale ler a que traz uma captura.
    wants: (t, data) => WANTED.has(t) && (t !== 'batalha' || data.includes('"k":"capturado"')),
    attach() { },
    receive(m) {
      if (m.t === 'welcome') merge(m.estado, true);
      else if (m.t === 'estado') merge(m.estado, false);
      else if (m.t === 'batalha') {
        for (const e of m.ev ?? []) {
          if (e?.k !== 'capturado' || !e.pokemon) continue;
          const p = e.pokemon;
          post({
            k: 'captura', nome: String(p.nome ?? '?'), level: Number(p.level) || 0,
            quality: p.quality ?? null, shiny: !!p.shiny, depot: !!e.paraDepot,
          });
        }
      }
    },
  });

  // Chave ausente é "não mudou"; `cheio` (welcome, reconexão) recomeça do zero.
  function merge(state, full) {
    if (!state || typeof state !== 'object') return;
    if (full || state.cheio) account = {};
    for (const key of KEYS) if (key in state) account[key] = state[key];
    report(false);
  }

  function report(force) {
    if (account.level == null) return;
    const now = { nick: account.nick ?? null, level: Number(account.level) || 0, gold: Number(account.gold) || 0, diamonds: Number(account.diamonds) || 0 };
    const changed = !sent || sent.nick !== now.nick || sent.level !== now.level;
    const stale = sent && (sent.gold !== now.gold || sent.diamonds !== now.diamonds) && Date.now() - sentAt > 60000;
    if (!force && !changed && !stale) return;
    sent = now;
    sentAt = Date.now();
    post({ k: 'conta', ...now });
  }

  // O gold muda a cada luta: o que ficou para trás sobe no próximo minuto.
  setInterval(() => report(false), 60000);
})();
