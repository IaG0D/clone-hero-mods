# HANDOFF — Mods Clone Hero (Radar + Backstage) · by IaG0D

Documento de continuação. Escrito em **2026-07-28** ao fim de uma sessão longa.
Serve para qualquer assistente (ou humano) retomar o trabalho sem redescobrir nada.
**Leia inteiro antes de tocar em código — as armadilhas daqui custaram um dia inteiro.**

---

## O que existe e funciona (testado no jogo real)

| Artefato | Estado | Onde |
|---|---|---|
| **Backstage Desktop** (app WPF) | ✅ completo e validado pelo usuário | `BackstageDesktop/` → `Backstage.exe` |
| **Backstage plugin** (BepInEx, painel F5 in-game) | ✅ completo (v0.6.2) | `BackstageMod/` → `BepInEx/plugins/Backstage.dll` |
| **Radar** (busca melhorada) | ⚠️ motor pronto; **instalada a v0 neutra** (não muda o jogo) a pedido do usuário; 1.0 no repo | `Radar/` |
| Motor Chorus (busca/download) | ✅ console-testado + produção | `Backstage/ChorusClient.cs` |
| Check do índice de busca | ✅ 17/17 | `Check/` (`dotnet run --project Check`) |
| Exe autocontido p/ distribuir | ✅ gerado | `publish/BackstageDesktop/` (fora do git; regenerar: ver Build) |

Repo: `github.com/IaG0D/clone-hero-mods` (privado). Histórico de commits conta a saga inteira.

## Ambiente

- **Jogo**: Clone Hero **v1.1.0.6142-final**, Unity 2022.3.62f2, **IL2CPP**, ofuscado com **Beebyte** (parcial: MonoBehaviours mantêm nome real, métodos não).
- **Pasta do jogo**: `C:\Users\iagov\Documents\Clone Hero` (log: `BepInEx\LogOutput.log`).
- **BepInEx**: 6.0.0-**be.785** (bleeding edge IL2CPP). Interop em `BepInEx\interop\`.
- **Biblioteca do usuário**: ~17.2k músicas. `Songs\Backstage\` recebe os downloads.
- dotnet SDKs 8/9/10 instalados. Build de tudo: `dotnet build <proj> -c Release`.

## Arquitetura do Backstage (o produto principal)

```
Backstage Desktop (WPF, bonita)          Plugin in-game (BepInEx)
  busca/filtros/capas/fila/download  ←→    painel IMGUI (F5) equivalente
  dedup lendo backstage_library.txt  ←──   exporta biblioteca no boot/rescan
  escreve backstage_cmd.txt "scan"   ──→   dispara Scan Songs nativo
                └── ambos escrevem .sng em Songs\Backstage\
```

- **Canal de comando**: arquivo `<jogo>\backstage_cmd.txt`, polled a cada 30 frames pelo plugin. Comandos: `show/hide/search X/dl N/scan/state`. Comando pendente com jogo fechado dispara no próximo boot (rescan automático!).
- **Export de biblioteca**: `<jogo>\backstage_library.txt`, linhas `artista|nome|charter` normalizados (lowercase/trim). Escrito em `RefreshOwned` a cada mudança do master.
- **Dedup**: por artista+nome (`≈ tem a música`) e artista+nome+charter (`✔ já tem este`). Sem hash — decisão deliberada (hashear 17k pastas localmente é caro; o caso real é coberto).

## API do Chorus Encore (contrato lido do código aberto do Bridge — Geomitron/Bridge)

- `POST https://api.enchor.us/search` — body: `{search, per_page:25, page, instrument, difficulty, drumType:null, drumsReviewed:true, sort:null, source:"bridge"}`
- `POST https://api.enchor.us/search/advanced` — busca por campo (artist/name/genre/charter/album); body completo com todos os campos (ver `ChorusClient.SearchFieldAsync`). **Não pagina.**
- Download: `GET https://files.enchor.us/{md5}.sng` (`_novideo` se `hasVideoBackground`). `.sng` = arquivo único, CH v1.1 lê nativo, **sem extração**.
- Capa: `https://files.enchor.us/{albumArtMd5}.jpg`.
- Página do chart (prévia com player oficial): `https://enchor.us/chart/{md5}`.
- Valores: instruments `guitar/guitarcoop/rhythm/bass/drums/keys/+ghl`, difficulties `expert/hard/medium/easy`. Resposta tem `diff_guitar/bass/drums/keys` (int, -1 = sem), `song_length` (ms).
- **ETIQUETA (inegociável)**: serviço bancado por doação. Cache de busca em memória (feito), User-Agent `Backstage/0.1 (Clone Hero mod; by IaG0D)` (feito), UMA busca por Enter. **Falar com o Geo no Discord do Chorus ANTES de qualquer release público.** Prévia nativa foi descartada de propósito: exigiria baixar o .sng inteiro por prévia.

## ⚠️ CAMPO MINADO IL2CPP — decorar antes de escrever qualquer código de UI/jogo

APIs **stripped** do build (lançam `NotSupportedException: Method unstripping failed` em runtime, compilam normal):
- `GUI.TextField` → campo de texto é desenhado na mão (Box+Label) e o teclado entra por `Input.inputString` no Update.
- `UnityEngine.Object.FindObjectOfType<T>(bool)` e `Resources.FindObjectsOfTypeAll<T>()` → usar `UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<T>(), true)` (nativo real, acha inativos) + `TryCast<T>()`.
- `GUI.DrawTexture(rect, tex, ScaleMode)` às vezes; a de 2 args funcionou → **sempre** guardar com try/catch e degradar pra `GUI.Box`.
- Padrão geral: **uma exceção no OnGUI mata o frame inteiro do painel** — todo call site arriscado é guardado; primeira falha desativa o recurso e loga.

Funciona: `GUI.Box/Label/Button`, `GUIStyle` customizado (`new GUIStyle(GUI.skin.button)` + texturas 1x1 + cores), `Texture2D.SetPixel/Apply`, `Input.GetKeyDown/inputString/mouseScrollDelta/mousePosition`, richtext nos labels (`<b> <color> <size>`), `Application.OpenURL`, HttpClient+System.Text.Json (SEM `System.Net.Http.Json`), Harmony em métodos interop e em Unity messages (`Update`, `OnDisable`).

## Âncoras no binário ofuscado (nomes gerados por assinatura — quebram se o CH atualizar)

Tudo centralizado em `Radar/Anchors.cs` e `BackstageMod/Anchors.cs` (cópias deliberadas, mods independentes):

- **Biblioteca estática de músicas**: classe `ObjectPublicAbstractSealedLi1SoDi2ObInLi1SoUnique`
  - Lista-mestre: `field_Public_Static_List_1_SongEntry_0` (sobrevive a filtros; usar SEMPRE ela pra indexar, nunca as seções visíveis).
  - Filtro nativo: `Method_Public_Static_Void_Func_2_SongEntry_Boolean_String_Boolean_0(pred, label, false)`.
- **SongEntry**: `Name/Artist/Album/Genre/Charter_StrippedTags`, `ChecksumString`, `PlayCount`, `intensities`, `dateAdded`, `songLength` — tudo pronto pra filtros futuros.
- **Scan**: `SongScan.Method_Public_Coroutine_Boolean_0(true)` = **rescan completo real** (o do Settings > Scan Songs). Com `false` = só valida cache, NÃO vê música nova. `Method_Public_Void_0()` = nada. Verificação: `SongScan.isScanning`. Achar instância: `FindObjectsOfType(Il2CppType.Of<SongScan>(), true)`.
- **Rebuild da view da lista**: `SongSelect.isReturningFromSearch = true` → o Update da tela reconstrói a view a partir dos dados. **Sem isso, mudar dados de lista deixa a tela defasada ou em branco.**
- **Busca nativa (type-to-jump)**: `SongSelectSearch.Method_Private_ValueTuple_2_..._Func_2_SongEntry_Boolean_0` — roda por tecla, recebe `List<Seção>` + predicado, devolve o 1º match. Query digitada: `SongSelectSearch.searchText.text` (placeholder `"Start typing..."`).
- **Input**: Rewired (não ofuscado, interop `Rewired_Core.dll`). Bloquear jogo: `ReInput.players.GetPlayer(i).controllers.maps.SetAllMapsEnabled(false)` + `SystemPlayer`. Control Remapper abre com Espaço FORA dos maps → patch prefix em `Rewired.UI.ControlMapper.ControlMapper.Open/Toggle` retornando false com painel aberto.
- Sufixo `_PDM_` em método interop = "potentially dead" → o alvo vivo NUNCA tem esse sufixo.

## Cicatrizes do Radar (por que a v0 neutra está instalada)

Tentativas de filtrar a lista visível do jogo, em ordem, e por que falharam:
1. `SongEntry.filtered` — campo morto, setar não muda a tela.
2. Reescrever `List<SongEntry>` das seções — esconde, mas quebra o scroller virtualizado (índices pré-calculados).
3. Forçar `inFilterMode` — filtra, mas o fechamento da busca corrompe a tela.
4. Filtro nativo com **rótulo vazio** (`""`) — **corrompe a lista de forma irreversível na sessão**. Rótulo NUNCA pode ser vazio.
5. O que funciona: filtro nativo com rótulo + `isReturningFromSearch = true` depois (o commit `08cb188` "Radar 1.0.0" tem exatamente isso, base aprovada + 2 correções). O usuário cansou dos testes e pediu a v0; a 1.0 está pronta pra reinstalar quando ele quiser: `dotnet build Radar/Radar.csproj -c Release` (checkout do commit certo) + copiar `Radar.dll`.

## Build & instalar (cheat sheet)

```sh
# plugin in-game
dotnet build BackstageMod/BackstageMod.csproj -c Release
cp BackstageMod/bin/Release/net6.0/Backstage.dll "$CLONEHERO_DIR/BepInEx/plugins/"

# desktop
dotnet build BackstageDesktop/BackstageDesktop.csproj -c Release   # leve (precisa .NET 8)
dotnet publish BackstageDesktop/BackstageDesktop.csproj -c Release -r win-x64 \
  --self-contained -p:PublishSingleFile=true -o publish/BackstageDesktop  # 154MB, zero deps

# checks fora do jogo
dotnet run --project Check       # indice de busca do Radar (17/17)
dotnet run --project Backstage   # motor Chorus (1 busca + 1 download reais — usar com moderacao)
```

Jogo precisa rodar 1x após instalar BepInEx pra gerar `BepInEx/interop/` (os csproj referenciam de lá; `CLONEHERO_DIR` sobrescreve o caminho).

## Ferramentas de depuração que já existem (usar, não recriar)

- **Canal de comando** (`backstage_cmd.txt`) — dirige o plugin sem teclado.
- **Screenshot da janela mesmo coberta**: scratchpad `gameshot.ps1` (maior janela do processo, PrintWindow flag 3) e `deskshot.ps1`. Cuidado: o console do BepInEx às vezes vira MainWindow.
- **Log**: tail de `BepInEx\LogOutput.log` com grep nos prefixos `[Radar]`/`[Backstage]`.
- Descompilar tipo interop: `ilspycmd -t <Tipo> "<jogo>/BepInEx/interop/CloneHero.dll"` (dump inteiro do assembly TRAVA o ILSpy; ir tipo a tipo). Lista de tipos: scratchpad da sessão tinha `types.txt`; regenerar com `ilspycmd -l c`.
- **Protocolo que salvou a sessão**: NUNCA entregar build sem antes rodar o ciclo sozinho (cmd → screenshot → log). O usuário testa só o que já foi visto funcionando.

## Próximos passos, em ordem de valor

1. **Polimento desktop conforme uso real** (feedback do usuário manda).
2. **Falar com o Geo** (Discord do Chorus) — bloqueia qualquer distribuição.
3. **README de release** + repo com screenshots.
4. **Radar 1.0 de volta** quando o usuário topar re-testar (base já validada, commit `08cb188`).
5. **"Chefão sigma"**: UI clonando prefabs nativos do CH (uGUI + navegação de guitarra). Começar pelo menor passo: uma entrada no menu principal que abre o painel. Usar `Resources.FindObjectsOfTypeAll` p/ pescar `TMP_FontAsset`/sprites nativos (nunca importar fonte própria).
6. Filtros locais ricos no Radar (`unplayed`, `diff:>7`, `inst:prodrums`, `added:7d`) — dados já mapeados no SongEntry.

## Regras do projeto (decididas com o usuário, não renegociar sem ele)

- IDs de plugin travados: `com.iag0d.radar`, `com.iag0d.backstage`. `by IaG0D` na UI.
- Código comum: por FONTE (`Compile Include`), nunca DLL compartilhada.
- Cada mod/app = um artefato único de instalação.
- Lógica primeiro com UI feia, bonita depois; motor sempre validável fora do jogo.
- Falhar limpo: âncora perdida → loga e vira observador; jogo nunca crasha por causa do mod.
