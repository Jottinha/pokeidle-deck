# DeckPokeIdle — Plano de implementação

Deck pessoal para rodar 4 contas do [pokeidle.io](https://pokeidle.io/app) numa janela só.
Base: [artbruno35/PokeIdleMulti](https://github.com/artbruno35/PokeIdleMulti) (C# .NET 8, WinForms, Edge WebView2, licença MIT).

Cada fase termina com o deck **compilando e testado por você nas 4 contas** antes de passar para a próxima.

---

## Fase 0 — Preparação

- [x] Instalar o **.NET 8 SDK** e o **git** (`winget`).
- [x] Copiar o código do PokeIdleMulti para esta pasta, mantendo o `LICENSE` (o MIT exige manter o aviso do autor).
- [x] Compilar **sem nenhuma mudança** e abrir, para confirmar que a base funciona nesta máquina.
- [x] `git init` + primeiro commit ("base PokeIdleMulti"), para dar para comparar e voltar atrás em qualquer fase.

## Fase 1 — Limpeza e identidade

- [x] **Tirar os testes automáticos (3.4):** `ConsumptionTest.cs`, `TestDesktop.cs`, `FakePubSub.cs`, os modos
      `--self-test` e `--consumo` em `Program.cs` e o código do autoteste dentro de `DeckForm.cs`.
- [x] **Tirar as lives da Twitch e da Kick (por enquanto):** `LivesWindow.cs`, `TwitchLive.cs`, `KickLive.cs`,
      `TwitchPubSub.cs`, `TwitchWatcher.cs`, `KickWatcher.cs`, `PointsRedeemer.cs`, `RedeemLog.cs`, `BonusGuard.cs`,
      `bonus.js`, `BulkRedeemDialog.cs`, `ChannelsDialog.cs`, `VideoQuality.cs`, `AdBlock.cs`, e os botões e menus
      delas em `DeckForm.cs` e `AccountPane.cs`. O código fica guardado no commit da Fase 0, para voltar se você decidir.
- [x] Não trazer a pasta `nudes/`.
- [x] Renomear para **DeckPokeIdle**: nome da janela, exe, pasta de dados `%LOCALAPPDATA%\DeckPokeIdle`
      (separada da do deck original, para os dois não se misturarem) e a trava de "um deck aberto por vez".
- [x] Compilar e conferir que nada mais quebrou (testado nas 4 contas).

## Fase 2 — Barra de cima enxuta

- [x] **Esquerda:** nome do deck + layouts: **Grade** (com "Colunas" na setinha), **Foco**. (**Principal** entra na Fase 4.)
- [x] **Direita, sempre à vista:** **Ultra**, **Eco**, **Mercado** (só aparece quando a Fase 5 existir), **⚙**.
- [x] **Menu ⚙** junta o resto: Extensões, Sniper, Zoom, Tela cheia (F11 continua), Diagnóstico e **Sobre** (com o crédito "by: artbruno", que sai da barra de baixo, e o link do projeto original).
- [ ] Saem da barra os 4 botões das contas: a troca de conta passa a ser pelas miniaturas, e o menu de cada conta
      passa para o "⋯" do cabeçalho dela (Fase 3). Ficam na barra até lá, para não perder o acesso.

## Fase 3 — Cabeçalho de cada conta

- [ ] **Sempre visível:** bolinha de status (verde caçando · amarelo no login ou carregando · vermelho desligada),
      nome, tempo rodando, e só 3 botões: **⟳ recarregar**, **som** e **⋯**.
- [ ] **🛒 e ⚔ só aparecem quando estão ligados** (como indicador, clicável).
- [ ] **Menu ⋯:** ampliar, desligar, recompra, fila de PvP, voto no TopIdle, botões das extensões,
      renomear, apagar os dados da conta, diagnóstico.
- [ ] Os avisos ("na tela de login", "conta aberta em outro lugar") viram a cor da bolinha + a dica dela.

## Fase 4 — Layout Principal (uma conta grande, 3 pequenas)

- [ ] Novo layout: a principal ocupa ~75% da largura com zoom de 100%; as outras 3 empilhadas à direita.
- [ ] **Clicar numa miniatura troca ela com a principal.** O deck lembra qual é a principal e o layout ao reabrir.
- [ ] **Miniaturas em Ultra automático** (opção, ligada de fábrica): ao virar miniatura a conta liga o Ultra do jogo;
      ao virar principal volta ao normal (se o Ultra geral não estiver ligado).
- [ ] **Cartão de números na miniatura:** nome, status, **XP/h** e **lucro/h** da sessão (os mesmos do painel
      "Sessão" do jogo), lidos da página da conta.
- [ ] **Barra de baixo como resumo:** "4 contas: 3 caçando, 1 no login" · RAM total · quais contas podem votar.

## Fase 5 — Analisador do Mercado da Comunidade (depois)

Só leitura e aviso. **Nenhuma compra automática** — comprar fica sempre com você.

- [ ] Janela **Mercado** (botão na barra), lendo o mercado pelo socket de uma conta escolhida (as mensagens que o
      próprio jogo usa: `market.itens`, `market.listar`, `market.medias`, `market.historicoGlobal`).
- [ ] Respeitar o limite de atualização do mercado do jogo (não pedir mais rápido que o botão Refresh deixa).
- [ ] **Oportunidades que ele marca:**
  - **Gold × gemas:** o mesmo item anunciado nas duas moedas; calcula quanto vale 1 gema em gold e mostra o
    anúncio barato numa moeda em relação à outra.
  - **Abaixo da média:** anúncio abaixo da média de 7 dias do jogo, já descontada a taxa de venda
    (15% em gold; em gemas, 15% para itens e de 13% a 7,5% para pokémon). Só conta se comprar < 85% da revenda.
  - **Fora da curva na espécie:** o mesmo pokémon com atributos parecidos (IV, P5, nível) bem abaixo dos outros.
- [ ] Lista ordenada pelo lucro estimado, com favoritos e um aviso quando aparece algo bom.

---

## O que fica como está no deck original

Recompra (🛒), fila de PvP (⚔), voto no TopIdle, extensões e Rotom Sniper, Eco, Ultra e menos animações,
recarregar limpo, apagar os dados da conta, vigia do jogo que não abre e diagnóstico. Só mudam de lugar na tela.

## Fora por enquanto (decidir no futuro)

- **Lives da Twitch e da Kick** (bônus de XP, baú, resgate automático, guarda do bônus, resgate em lote). Saem na
  Fase 1; o código original fica no commit da Fase 0.

## Cuidados

- Uma conta do jogo em um lugar só: com o deck aberto, não abra as mesmas contas no Chrome.
- A recompra gasta ouro de verdade: conferir os valores antes de ligar em cada conta.
