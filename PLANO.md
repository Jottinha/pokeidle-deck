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

- [x] **Esquerda:** nome do deck + layouts: **Grade** (com "Colunas" na setinha), **Principal** e **Foco** (com a conta na setinha).
- [x] **Direita, sempre à vista:** **Ultra**, **Eco**, **Mercado** (só aparece quando a Fase 5 existir), **⚙**.
- [x] **Menu ⚙** junta o resto: Extensões, Sniper, Zoom, Tela cheia (F11 continua), Diagnóstico e **Sobre** (com o crédito "by: artbruno", que sai da barra de baixo, e o link do projeto original).
- [x] Saem da barra os 4 botões das contas: a troca de conta passa a ser pelas miniaturas (e pela setinha do Principal e do Foco), e o menu de cada conta
      passa para o "⋯" do cabeçalho dela (Fase 3). Ficam na barra até lá, para não perder o acesso.

## Fase 3 — Cabeçalho de cada conta

- [x] **Sempre visível:** bolinha de status (verde caçando · amarelo no login ou carregando · vermelho desligada),
      nome, tempo rodando, e só 3 botões: **⟳ recarregar**, **som** e **⋯**.
- [x] **🛒 e ⚔ só aparecem quando estão ligados** (como indicador, clicável).
- [x] **Menu ⋯:** ampliar, desligar, recompra, fila de PvP, voto no TopIdle, botões das extensões,
      renomear, apagar os dados da conta, diagnóstico.
- [x] Os avisos ("na tela de login", "conta aberta em outro lugar") viram a cor da bolinha + a dica dela. O voto só aparece no cabeçalho quando está liberado; o tempo que falta fica no menu ⋯.

## Fase 4 — Layout Principal (uma conta grande, 3 pequenas)

- [x] Novo layout: a principal ocupa ~75% da largura com zoom de 100%; as outras 3 empilhadas à direita.
- [x] **Clicar numa miniatura troca ela com a principal.** O deck lembra qual é a principal e o layout ao reabrir.
- [x] **Miniaturas em Ultra automático** (opção, ligada de fábrica): ao virar miniatura a conta liga o Ultra do jogo;
      ao virar principal volta ao normal (se o Ultra geral não estiver ligado).
- [x] **Cartão de números na miniatura:** nome, status, **XP/h** e **gold/h** da sessão (os mesmos do painel
      "Sessão" do jogo, ou da caixa do Ultra), lidos da página da conta. O gold/h é o bruto: a caixa do Ultra não mostra o lucro.
- [x] **Barra de baixo como resumo:** "4 contas: 3 caçando, 1 no login" · RAM total · quais contas podem votar.

## Ajustes depois da Fase 4

- [x] **Visual mais legível:** cores com mais contraste, cabeçalho das contas num tom próprio, layout em uso e Eco/Ultra
      ligados como "pílulas" preenchidas, a conta principal com borda roxa, fontes um pouco maiores.
- [x] **/hunt for PokeIdle nas 4 contas:** pelo ⚙ → Extensões → + Adicionar → Importar do meu Chrome, com "Instalar em
      todas as contas" ligado (o deck já faz isso; não precisa de código).

## Fase 5 — Analisador do Mercado da Comunidade

Só leitura e aviso. **Nenhuma compra automática** — comprar fica sempre com você.

- [x] Janela **$ Mercado** (botão na barra), lendo o mercado pelo socket de uma conta escolhida (`mercado.js`, com a
      mesma leitura `market.itens` da tela do jogo: o menor preço de cada item em gold e em gemas e a média de 7 dias).
- [x] Respeitar o limite de atualização do mercado do jogo (no máximo 1 leitura a cada 5 s, como o Refresh; o
      automático lê 1 vez por minuto).
- [x] **Oportunidades em itens:**
  - **Abaixo da média (gold ou gemas):** o menor anúncio abaixo da média de 7 dias, já descontada a taxa de quem revende.
  - **Gold → gemas** e **gemas → gold:** o mesmo item mais barato numa moeda do que vale na outra, com a gema convertida
    pelo valor que o próprio mercado dá a ela (mediana de média em gold ÷ média em gemas).
- [x] Janela em cartões, separada em **Ganhar gemas** (em destaque) e **Ganhar gold**, com ícone do item, lucro por
      unidade, filtros de lucro mínimo e de vendidos em 7 dias, e **Abrir no jogo** (leva a conta escolhida até o item no
      mercado do jogo, sem comprar). Lê sempre pela conta principal (ou a primeira no jogo); clique no cartão
      explica a conta.
- [x] **Conferência com vendas reais:** as 10 melhores de cada aba são conferidas com a Tabela de preços do jogo
      (vendas que aconteceram nos últimos 7 dias) e com o anúncio mais barato de outro vendedor. O preço de revenda vira o
      menor entre a mediana das vendas reais e um pouco abaixo do concorrente; cada cartão ganha confiança alta/média/baixa.
- [x] Abas **Ganhar gemas** / **Ganhar coins**; "Abrir no jogo" funciona de novo depois da 1ª vez.
- [ ] **Pokémon:** o mesmo pokémon com atributos parecidos (IV, P5, nível) bem abaixo dos outros (mais difícil: cada
      pokémon é diferente; fica para uma próxima versão).
- [ ] Favoritos e um aviso quando aparece algo bom.

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
