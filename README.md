# Clone Hero Mods — by IaG0D

Dois mods para **Clone Hero v1.1**, em C# sobre BepInEx 6 (IL2CPP).

| Mod | O que faz | Estado |
|---|---|---|
| **Radar** | Busca de verdade na lista de músicas — fuzzy, sem acento, multi-campo | 🟡 miolo pronto e validado, falta a UI |
| **Backstage** | Busca e baixa charts do Chorus Encore direto do jogo, marca o que você já tem e manda o jogo reescanear | ⚪ não começou |

---

## Radar

A busca do Clone Hero não é busca, é *type-to-jump*: ela varre a lista com um predicado e pula
para o **primeiro** resultado. Se você digitar o nome do artista, pular uma palavra do título, ou
o título começar com "The", ela não acha nada. E não filtra a lista — o resto continua lá.

O Radar troca isso por uma busca real:

- **Sem acento** — `motorhead` acha Motörhead, `coracao` acha Coração
- **Pontuação não atrapalha** — `ac dc` acha AC/DC
- **Artigo inicial ignorado** — `trooper` acha The Trooper
- **Multi-campo** — `metallica master` acha *Master of Puppets*, cruzando artista e título na
  mesma query. É exatamente o que a busca nativa não consegue fazer.
- **Ranking** — match exato > prefixo do título > palavra no título > substring > fuzzy.
  Sem isso, digitar `one` devolveria centenas de resultados em ordem aleatória.
- **Incremental** — cada letra nova filtra o resultado anterior, não a biblioteca inteira.
  Apagar uma letra desempilha em vez de recalcular.

### Velocidade

| Biblioteca | Varredura fria |
|---|---|
| 20.000 músicas | ~15 ms |

Teclas seguintes são mais rápidas, porque filtram só o resultado anterior.

---

## Backstage

Buscador e downloader de charts do [Chorus Encore](https://enchor.us) dentro do jogo.

Planejado:

- Lista com música, artista, charter, instrumentos e dificuldade
- **Marca o que você já tem**, comparando checksum — sem isso o mod vira gerador de duplicata
- Fila de download que continua enquanto você navega o menu
- Aba de *browse* (top da semana, recentes, por gênero) que funciona 100% no D-pad, sem teclado
- **Reescaneia sem reiniciar** — o jogo já tem `Settings > General > Scan Songs` e ele roda ao vivo;
  o Backstage só aperta esse botão por código no fim da fila

O Chorus é bancado por doação. O Backstage vai usar cache local agressivo, debounce nas buscas e
User-Agent identificando o mod — e não sai do ar de teste sem conversar com o pessoal do Chorus antes.

---

## Compilando

Precisa do [.NET SDK 8+](https://dotnet.microsoft.com/download) e de uma instalação do Clone Hero
com [BepInEx 6 IL2CPP](https://builds.bepinex.dev/projects/bepinex_be) já rodado uma vez
(a primeira execução gera os *interop assemblies* contra os quais os mods compilam).

```sh
# Se o jogo não estiver em Documents\Clone Hero:
export CLONEHERO_DIR="D:/caminho/para/Clone Hero"

dotnet build Radar/Radar.csproj -c Release
cp Radar/bin/Release/net6.0/Radar.dll "$CLONEHERO_DIR/BepInEx/plugins/"
```

O check do miolo da busca roda fora do jogo:

```sh
dotnet run --project Check
```

---

## Sobre quebrar em updates

Clone Hero não tem API de mods. Tudo aqui é patch via Harmony sobre um binário IL2CPP
**ofuscado**, e vai quebrar quando o jogo atualizar. O projeto assume isso:

- Todo nome frágil-a-versão vive em **um arquivo só** (`Radar/Anchors.cs`). O conserto é lá e em
  mais nenhum lugar.
- Âncora perdida **loga e desiste** — o mod fica inerte e o jogo continua normal. Nunca derruba o menu.
- Versão alvo atual: **v1.1.0.6142-final** (Unity 2022.3.62f2). Cite ela em qualquer report de bug.

---

by **IaG0D**
