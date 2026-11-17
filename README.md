# DeckPokeIdle

Deck pessoal para jogar 4 contas do [pokeidle.io](https://pokeidle.io/app) numa janela só.

Baseado no [PokeIdleMulti](https://github.com/artbruno35/PokeIdleMulti), de artbruno35 (licença MIT, veja `LICENSE`).
O README original, que descreve todas as funcionalidades herdadas, está em
[docs/README-PokeIdleMulti.md](docs/README-PokeIdleMulti.md). As lives da Twitch e da Kick e os testes automáticos
foram retirados. O que muda e o que falta fazer está em [PLANO.md](PLANO.md).

## Compilar

Precisa do .NET 8 SDK. Dentro de `PokeIdleMulti`:

```
dotnet publish PokeIdleMulti.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o ..\publish
```

O exe sai em `publish\DeckPokeIdle.exe`. Os logins e as configurações ficam em `%LOCALAPPDATA%\DeckPokeIdle`
(não compartilhe essa pasta: ela tem os logins das contas).
