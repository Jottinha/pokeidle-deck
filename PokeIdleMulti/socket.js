// O socket do jogo, uma vez só para os scripts do deck (recompra, PvP e mercado). Antes cada um punha o seu Proxy no
// WebSocket e lia (JSON.parse) cada mensagem por conta própria: o `estado`, que chega o tempo todo, era lido duas vezes
// por conta. Aqui o socket do jogo é pego uma vez e cada mensagem é lida no máximo uma vez, só se algum script quiser.
//
// Cada script se inscreve com `__deckSocket.subscribe({ wants(t, data), receive(m), attach(ws) })`: `wants` olha o tipo
// (`t`, do começo da mensagem) antes de ler; `receive` recebe a mensagem já lida (a MESMA para todos: ninguém muda ela);
// `attach` avisa do socket novo (também na inscrição, se já houver um). `__deckSocket.late()` pega o socket que o jogo
// abriu antes deste script (o Rotom guarda em `window.__activeGameWs`); `seen` conta as mensagens do socket atual.
//
// Vai na frente de cada um dos três scripts (o deck junta): o primeiro que roda cria, os outros só usam.
(() => {
  if (window.top !== window || window.__deckSocket) return;

  const subs = [];
  let socket = null;
  let seen = 0;

  const same = (url) => {
    try {
      return new URL(String(url), location.href).host === location.host;
    } catch {
      return false;
    }
  };

  function attach(ws) {
    if (!ws || ws === socket) return;
    socket = ws;
    seen = 0;
    for (const s of subs) {
      try {
        s.attach?.(ws);
      } catch { }
    }
    ws.addEventListener('message', (e) => {
      if (ws === socket) receive(e.data);
    });
  }

  function receive(data) {
    if (typeof data !== 'string') return;
    seen++;
    // A mensagem sem o `{"t":"..."` no começo vai para todos (como antes, cada script lia ela inteira).
    const head = /^\{"t":"([^"]*)"/.exec(data);
    let m;
    let read = false;
    for (const s of subs) {
      try {
        if (head && !s.wants(head[1], data)) continue;
      } catch {
        continue;
      }
      if (!read) {
        read = true;
        try {
          m = JSON.parse(data);
        } catch {
          return;
        }
        if (!m || typeof m !== 'object') return;
      }
      try {
        s.receive(m);
      } catch { }
    }
  }

  function late() {
    if (socket?.readyState === 1) return;
    const ws = window.__activeGameWs;
    if (ws && ws.readyState === 1 && same(ws.url)) attach(ws);
  }

  // O Proxy mantém o resto igual para quem vier depois (a classe, o prototype, o instanceof): o Rotom também olha esse socket.
  const Native = window.WebSocket;
  if (typeof Native === 'function') {
    window.WebSocket = new Proxy(Native, {
      construct(target, args, newTarget) {
        const ws = Reflect.construct(target, args, newTarget);
        try {
          if (same(args[0])) attach(ws);
        } catch { }
        return ws;
      },
    });
  }

  Object.defineProperty(window, '__deckSocket', {
    value: Object.freeze({
      subscribe(sub) {
        subs.push(sub);
        if (socket) {
          try {
            sub.attach?.(socket);
          } catch { }
        }
      },
      late,
      get seen() {
        return seen;
      },
    }),
  });
})();
