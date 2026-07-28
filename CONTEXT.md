# Contexto do projeto — Mods para Clone Hero

Documento de handoff. Cole no Claude Code (ou salve como `CONTEXT.md` na raiz do repo) antes de começar.

---

## Objetivo

Dois mods para Clone Hero, feitos em C# como plugins BepInEx:

1. **Radar** — substitui a busca interna da lista de músicas por uma busca real, incremental, com fuzzy matching e busca multi-campo.
2. **Backstage** — buscador e downloader de charts online direto de dentro do jogo, consumindo a API do Chorus Encore (enchor.us).

Se surgir código comum aos dois, ele mora numa pasta `Shared/` do repo e é **linkado por código-fonte** (`<Compile Include="..\Shared\*.cs" />`), não distribuído como DLL. Cada mod continua saindo como um arquivo único. Plugin separado só se um dia precisar de estado em runtime entre os mods.

Namespaces / IDs de plugin **fechados**: `com.iag0d.radar`, `com.iag0d.backstage`.
**Não mudar isso depois** — o padrão do ecossistema de mods do CH usa esse formato nos arquivos de config, e alterar quebra a config de quem já instalou.

Crédito na UI: linha `by IaG0D` no rodapé de cada painel.

Versão do jogo alvo: **v1.1.0.6142-final** (de `settings_schema.json`). Anotar em todo relatório de bug — as âncoras são específicas por versão.

---

## Ordem de execução (decidida)

1. Radar primeiro. É menor, resolve dor real de quem tem biblioteca grande, e gera reputação/feedback.
2. Backstage depois.
3. Em ambos: **lógica primeiro com UI feia (IMGUI), UI bonita só depois** que o miolo estiver sólido.

---

## Passo zero, antes de escrever qualquer código

Determinar se o build atual do Clone Hero é **Mono** ou **IL2CPP**. Isso muda todo o stack:

- Existe `Clone Hero_Data/Managed/Assembly-CSharp.dll` → **Mono** → BepInEx 5.4.x, ILSpy/dnSpy para inspecionar, Harmony para patch. Caminho tranquilo.
- Existe `Clone Hero_Data/il2cpp_data` → **IL2CPP** → BepInEx 6 (bleeding edge) + Il2CppInterop. Bem mais chato e quebra a cada update do jogo.

Anotar também a versão exata do Unity (aparece no topo do `output_log.txt`) — é obrigatória caso um dia se use AssetBundle.

Caminhos úteis:
- Log do jogo: `%USERPROFILE%\AppData\LocalLow\srylain Inc_\Clone Hero\output_log.txt`
- Log do BepInEx: `<pasta do jogo>\BepInEx\LogOutput.log`
- Plugins: `<pasta do jogo>\BepInEx\plugins\`
- Configs: `<pasta do jogo>\BepInEx\config\`

---

## Passo zero — RESULTADO (verificado em 2026-07-28)

| Item | Valor |
|---|---|
| Pasta do jogo | `C:\Users\iagov\Documents\Clone Hero\` |
| Runtime | **IL2CPP** — existe `Clone Hero_Data/il2cpp_data/`, **não** existe `Managed/` |
| Unity | **2022.3.62f2** (`7670c08855a9`) |
| `GameAssembly.dll` | 66,6 MB |
| `global-metadata.dat` | 14,9 MB, magic `AF 1B B1 FA`, versão **31** — **não criptografado**, dumpável com Cpp2IL/Il2CppDumper |
| Símbolos | **Ofuscados.** O log mostra `GameLogManager:ʽʲʼʿʺʼʼʵʺˀʿ()` e `ʴˀʽʲˀʽʿʽʴʵʴ:ʹʿʿʸʴʸʺʸʹʶʼ()` — nomes de método e parte das classes viraram lixo Unicode. Alguns sobrevivem (`GameInit`, `GameLogManager`) |
| BepInEx | **não instalado** ainda |
| Biblioteca local | 17.554 pastas sob `Songs\`, `songcache.bin` de 6 MB — bom banco de teste |
| dotnet SDK | 8.0.421 / 9.0.314 / 10.0.300 disponíveis |

**Correções ao que este doc dizia antes:**
- O log do jogo é `%USERPROFILE%\AppData\LocalLow\srylain Inc_\Clone Hero\Player.log` (não `output_log.txt`).
- O caminho "tranquilo" (Mono + BepInEx 5.4 + dnSpy + Harmony) **não se aplica**. É o caminho IL2CPP.

**Consequências no stack:**
- BepInEx 6 só existe para IL2CPP em *bleeding edge* (`builds.bepinex.dev`, hoje na faixa `6.0.0-be.78x`). Não há release estável. Builds após `be.697` trouxeram quebras de API.
- Inspeção do código: Cpp2IL → dummy DLLs → ILSpy. Não dá pra editar/recompilar, só ler.
- Ofuscação é o risco maior do projeto: **patch por nome de método não sobrevive a update do jogo.** Achar os alvos por referência de string, assinatura e ordem de campos, e isolar toda a resolução num único arquivo de "âncoras" pra reparo em um lugar só.
- Il2CppInterop permite regex de renomeação — dá pra dar nomes estáveis aos alvos depois de identificados.

---

## Contexto do ecossistema (importante)

- **Não existe API oficial de mods** no Clone Hero. Tudo é patch via Harmony e vai quebrar periodicamente quando o jogo atualizar. Assumir isso no design: falhar de forma limpa e logar bem, em vez de crashar o jogo.
- O ecossistema clássico de mods (**BepInEx + BiendeoCHLib**, com Extra Song UI, Accuracy Indicator etc., repo `Biendeo/My-Clone-Hero-Tweaks`) foi feito para a versão **0.23.2.2** e nunca foi oficialmente portado para o v1.0+. Serve como referência de arquitetura e de padrões de config, **não** como dependência.
- Do v1.0 em diante o jogo mudou bastante — scanner reescrito, novos instrumentos (drums / pro-drums), mudança de local do `settings.ini`.
- Referência de código aberto para consultar: `Biendeo/My-Clone-Hero-Tweaks` (arquitetura de mod), `Geomitron/Bridge` (cliente do Chorus), `Geomitron/scan-chart` (parsing e hashing de charts).

---

## Mod 1 — Radar

### Problema

O que existe hoje não é busca, é *type-to-jump*: match de **prefixo do título** que pula para o item mais próximo, com timeout que reseta o buffer digitado. Por isso 3 letras funcionam e a 4ª "perde" a música — se o usuário digitou o nome do artista, pulou uma palavra, ou o título começa com "The", não acha nada. Não há filtragem real da lista.

### Solução

**Índice pré-computado**, montado uma vez após o scan, paralelo à lista de músicas:

```csharp
struct SearchEntry {
    public int songIndex;
    public string title, artist, album, genre, charter; // já normalizados
    public string all;                                  // concatenado, p/ match rápido
}
```

**Normalização** (aplicar no índice e na query):
- minúsculas com cultura invariante
- remover acentos: normalizar para `FormD` e descartar os caracteres `NonSpacingMark` (assim "coracao" acha "Coração", "motorhead" acha "Motörhead")
- remover pontuação
- remover artigo inicial ("the", "a", "o", "os")

**Filtro por token:** quebrar a query em palavras; cada token precisa dar match em *algum* campo. AND entre tokens, OR entre campos. Isso faz `metallica master` achar "Master of Puppets" — artista + título na mesma query, que é justamente o que a busca atual não consegue.

**Performance:** ao adicionar uma letra, filtrar o *resultado anterior*, não a lista inteira. Manter uma pilha de resultados; ao apagar uma letra, desempilhar. Com 10k músicas uma varredura linear já leva ~2-3 ms, mas isso mantém instantâneo até 30k+.

**Ranking obrigatório** (sem isso a busca é inútil — digitar "one" devolve 400 resultados em ordem aleatória):
match exato > prefixo do título > prefixo de palavra no título > substring > subsequência fuzzy (estilo fzf).

**Input:** debounce de ~80 ms; se o filtro passar de 5 ms, rodar em task separada para não engasgar o menu.

### UI

A melhor UI aqui é a que quase não aparece. **Não criar tela nova.**
- Barra fina de query no topo da lista existente
- Contador de resultados ao lado: `847 de 10.234`
- **Destacar o trecho que deu match** em cada linha — é isso que faz o usuário entender por que aquela música apareceu; sem o destaque, fuzzy matching parece bug
- `Esc` limpa, `Backspace` volta um nível da pilha

Navegação por controle de guitarra funciona de graça, já que a lista continua sendo a nativa.

---

## Mod 2 — Backstage

### Backend

O Chorus Encore (enchor.us) é o buscador de charts que a comunidade usa hoje para Clone Hero e YARG. O **Bridge** é o cliente desktop oficial dele (Electron + Angular) e é **open source**: `Geomitron/Bridge`.

→ Não fazer engenharia reversa de nada. Ler o código do Bridge para descobrir endpoints, parâmetros de busca e formato do JSON. Isso economiza a maior parte do trabalho.

`Geomitron/scan-chart` (mesmo autor) expõe os hashes de chart — útil para deduplicação, ver abaixo.

### Etiqueta com a API (não opcional)

O serviço é bancado por doação e os custos de API e hospedagem saem do Patreon. Portanto:
- cache local agressivo dos resultados
- debounce nas requisições de busca
- User-Agent identificando o mod e a versão
- falar com o Geo no Discord do Chorus **antes** de lançar publicamente

### Problemas técnicos a resolver

1. **Download + extração** dentro do processo do jogo, async, sem travar o menu. Priorizar `.sng` — é arquivo único, muito mais simples que pasta.
2. **Rescan sem reiniciar** — resolvido, não é mais o calcanhar de aquiles. **O jogo já tem `Settings > General > Scan Songs`, que roda ao vivo, sem reiniciar.** Então não escrever scanner incremental: achar esse método e chamá-lo. Fallback se a âncora quebrar: avisar na tela "3 charts baixados — abra Settings > General > Scan Songs". Nunca ficar em silêncio.
   Custo real: rescan completo (17,5k músicas aqui) não é instantâneo, mas o `songcache.bin` valida por cache. Disparar em lote no fim da fila de download, nunca por música, e mostrar progresso.
3. **Deduplicação.** Com 10k músicas locais, metade dos resultados já está no HD. Comparar por hash de chart e marcar com um check discreto. Sem isso o mod vira gerador de duplicata.

### UI

**Lista, não grade.** Colunas: música, artista, charter, instrumentos, dificuldade.

Três elementos que definem a qualidade:
- indicador de "você já tem esse chart"
- fila de download com progresso visível, que continua rodando enquanto o usuário navega o menu ou joga
- feedback claro do que acontece depois do download

**Problema central de UX: digitar sem teclado.** Boa parte da galera joga com a guitarra na mão e o teclado longe. Opções:
- teclado virtual navegável na tela (chato, mas funciona)
- assumir que o Backstage é "mod de mouse" e deixar isso explícito
- **melhor ideia:** uma aba de *browse* — top da semana, adicionados recentemente, por gênero — que funciona 100% no D-pad e nem precisa de texto. Provavelmente vai ser mais usada que a busca em si.

---

## Estratégia de UI (vale para os dois)

Três caminhos possíveis:

| Abordagem | Quando usar |
|---|---|
| **IMGUI (`OnGUI`)** | Só protótipo e tela de config. Parece Windows 98, não aceita controle, some em fullscreen exclusivo. |
| **uGUI clonando prefabs do jogo** | **Alvo principal.** `Instantiate` de um painel de menu existente e trocar o conteúdo. Herda fonte, espaçamento, cores, transições e navegação de graça. |
| **AssetBundle via Unity Editor** | Só se o Backstage virar tela grande de verdade. Exige a versão *exata* do Unity do jogo — versão errada = bundle não carrega ou shader rosa. |

Para pescar assets nativos:

```csharp
var fonts   = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
var sprites = Resources.FindObjectsOfTypeAll<Sprite>();
```

Logar os nomes, achar os que o menu usa, reusar.
**Nunca importar fonte própria** — é o que mais denuncia mod amador.

**Navegação por controle de guitarra é requisito, não polimento.** Se só funciona com mouse, é inutilizável no meio de uma sessão.

---

## Alternativa considerada (e descartada, mas registrada)

O **YARG** é open source, roda os mesmos charts e o mesmo Chorus, e um PR lá nunca quebraria por update. Se o objetivo fosse apenas *usar* essas features, seria o caminho mais curto. A decisão foi fazer mod para o Clone Hero especificamente — mantendo consciência de que haverá manutenção recorrente a cada release do jogo.

---

## Primeiras tarefas concretas

1. Detectar Mono vs IL2CPP e anotar a versão do Unity.
2. Montar o projeto BepInEx mínimo (plugin que só loga "carregado") e confirmar que aparece no `LogOutput.log`.
3. Descompilar `Assembly-CSharp.dll` e localizar: a classe da lista de músicas, o método de filtro/type-to-jump, e o método de scan.
4. Radar v0: índice + normalização + filtro por token + ranking, exposto em IMGUI feio, só para validar velocidade e qualidade dos resultados com uma biblioteca real de 10k.
5. Só então partir para o uGUI.
