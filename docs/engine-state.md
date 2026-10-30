# Stato del motore e del solution store

Ultimo aggiornamento: 2026-10-30. Questo documento dice, senza giri di parole, **cosa esiste già**, **cosa
manca** e **perché manca** — le quattro limitazioni strutturali del motore di riscrittura, la copertura
della tabella IDE di Microsoft, e lo stato del database di soluzione (`<solution>/.visualMCP/visualmcp.db`).

Legenda: ✅ fatto · 🟡 parziale · ⬜ mancante · ❌ fuori scope per scelta.

---

## 1. Cosa c'è oggi

| Componente | Dove | Stato |
|---|---|---|
| Motore delle regole IDE (`simplify_code`) | **nucleo neutro** `SNITCH\Languages\Style\` (`StyleCore.cs`, `StyleOptions.cs`, `StyleRewriteService.cs`) + **livello C#** `SNITCH\Languages\CSharp\Style\` (`StyleRewrites*.cs`, `CSharpStyleLanguage.cs`, `CSharpStyleRuleSet.cs`) | ✅ **87 id** riscritti, **39** elencati con il motivo, 126 totali (conteggio su `SNITCH\Languages\CSharp\Style\`); il livello VB li estende ([docs/adding-a-language.md](adding-a-language.md)) |
| **Livello Visual Basic** (`simplify_vb_code`) | `SNITCH\Languages\VB\Style\` (`VbStyleLanguage.cs`, `VbBlocks.cs`, `VbStyleRewrites.cs`, `VbStyleRewriteCatalog.cs`, `VbSimplifyCodeService.cs`) + `VbServiceCollectionExtensions.AddSnitchVisualBasic()` | ✅ **15 id** VB (14 riscritture: IDE0001, IDE0002, IDE0003, IDE0004, IDE0049, IDE0041, IDE0029/IDE0030, IDE0031, IDE0047, IDE0054, IDE0100, IDE0075, IDE0084, IDE0081) e **34** elencati con il motivo; stesso `StyleRewriteService`, quindi stesso gate e stessa storia delle modifiche |
| Tool MCP | `simplify_code(scope, filePath, projectName, rules, dryRun)` per il C# · `simplify_vb_code(scope, filePath, projectName, rules, dryRun)` per il VB | ✅ due tool distinti: il container tiene un servizio di stile per lingua (`ISimplifyCodeService` / `IVbSimplifyCodeService`), così nessuno dei due può oscurare l'altro |
| **Context action** (`context_action`) | `SNITCH\Languages\CSharp\ContextActions\` (`ContextActionService.cs` + i quattro partial: `Usings`, `NullChecks`, `Wrapping`, `Strings`) | ✅ **13 azioni** in quattro famiglie che né le riscritture IDE né i refactoring raggiungevano: import del tipo, guardie sugli argomenti, surround-with su intervallo di righe, conversioni di string literal. Ogni azione risolve il bersaglio da una riga, fa il preview, e passa dal gate del compilatore; l'inventario delle 307 context action C# di ReSharper è in [resharper-context-actions.md](resharper-context-actions.md) |
| **Decompilazione → metacode** | `SNITCH\Libraries\Snitch.Metacode\Decompilation\` (`DecompileService.cs` con `IDecompileService` + `IMetacodeDecompileService`, `MetacodeDecompilation.cs`) + `SnitchMetacodeServiceCollectionExtensions.AddSnitchMetacode()` | ✅ il C# di ILSpy passa dal `CSharpFrontend` e dà `MetacodeDocument` + pseudocodice per tipo; tool `decompile_to_metacode`. L'estensione è l'unica che registra `IDecompileService` e `IPackageInspectionService` (tolte da `AddSnitch`) |
| **Binario nativo → metacode** | `SNITCH\Libraries\Snitch.Metacode\Decompilation\` (`GhidraService.cs`, `GhidraFrontend.cs`, `GhidraC.cs`, `GhidraSnapshot.cs`) | ✅ Ghidra headless (progetto temporaneo, `ExportNativeGraph.java`, timeout che uccide l'albero, `JAVA_HOME_OVERRIDE` con un retry) + normalizzazione del C e rilettura col `PseudocodeFrontend`; tool `analyze_native_binary`, snapshot rileggibile da `metacode_translate` |
| Gate del compilatore | `StyleRewriteService.ErrorsAsync` (`StyleRewriteService.cs:608`): per progetto, tutti i documenti insieme e poi uno per uno per isolare il colpevole; il file rifiutato non viene toccato | ✅ per documento, **non** per singola modifica |
| Formattazione dell'output | `Formatter.FormatAsync(..., Formatter.Annotation)`: riformatta **solo** i nodi prodotti dal motore, non il file | ✅ |
| Canale delle opzioni | `StyleOptions` legge da `AnalyzerConfigOptions` **cinque** chiavi C# (`CSharpStyleLanguage.cs:49-73`): `csharp_prefer_braces`, `csharp_preferred_modifier_order`, `csharp_style_namespace_declarations`, `dotnet_style_prefer_foreach_explicit_cast_in_source`, `dotnet_style_require_accessibility_modifiers`; il livello VB ne legge tre proprie (`visual_basic_preferred_modifier_order`, `dotnet_style_require_accessibility_modifiers`, `visual_basic_style_prefer_isnot_expression`) | 🟡 cinque chiavi C# e tre VB su tutte quelle dell'IDE |
| **Indice dei simboli** | `SNITCH\Libraries\Snitch.Metacode\Symbols\` (`SymbolIndexBuilder`, `CSharpSymbolIndexFrontend`, `SymbolIndexStore`, `SymbolIndexService`) + tabelle `si_symbol`, `si_usage`, `si_metacode`, `si_document` nello stesso `visualmcp.db`; tool `reference_index` (`build`/`status`/`find`/`describe`/`refs`/`callers`/`callees`/`hierarchy`/`outline`/`metacode`/`unused`) | ✅ incrementale per hash di contenuto: dove è dichiarato un membro, ogni uso **con il come** (`call`, `read`, `write`, `instantiate`, `inherit`, `implement`, `override`, `attribute`, …), il membro che lo contiene, e l'**albero metacode** di ogni dichiarazione ([metacode.md](metacode.md#lindice-dei-simboli)) |
| **Sorgenti nel database** | tabella `source_document` in `visualmcp.db`, scritta dall'indice del metacode (la stessa passata che legge il file) | ✅ ogni sorgente della soluzione — documenti Roslyn di qualunque lingua più Razor letto da disco — con testo, lingua, hash e timestamp; una lingua senza frontend resta qui e non in `mc_tree`, quindi il buco si vede invece di sparire |
| Fatti sui pacchetti | tabelle `package_fact` e `package_dependency` + tool `package_facts` | ✅ da `obj/project.assets.json`, senza restore né rete |
| Decompilati | tabella `decompiled_artifact` + `decompile_assembly(persist=true)` | ✅ chiavati sull'MVID, riusati |
| Storia delle modifiche | tabella `applied_edit` + tool `edit_history`, con il testo compresso di prima | ✅ e revertibile |
| Annullamento | tool `revert_edit` sull'id della storia, con controllo che il file non sia cambiato dopo | ✅ |
| Log delle chiamate | tabella `command_log` + tool `command_log` | ✅ |
| Cache dei report di ispezione | tabella `context`, chiavata su path del documento + mtime | ✅ |
| Kanban della soluzione | tabelle `board_item`, `board_comment`, `board_history` | ✅ |
| Test | **574** metodi `[Fact]`/`[Theory]` in `src/VisualMCP.Tests` (conteggio sul sorgente), che `dotnet test` esegue come **742** casi; di cui **17** sulla famiglia `context_action` (`ContextActionTests`) e **13** sullo store (`SolutionStoreTests` 8 + `StoreTests` 5) | ✅ |

---

## 2. I quattro limiti strutturali

Sono i limiti che spiegano perché la maggior parte delle regole IDE restanti non è implementabile "per
come è fatto il motore". Tre sono stati tolti; resta parziale il solo #3, per scelta (le preferenze
`var` e il naming non sono riscritture).

| # | Limite | Stato | Cosa servirebbe per toglierlo | Cosa esiste già come pezzo |
|---|---|---|---|---|
| 1 | **Una sola regione contigua, in un solo documento.** `StyleMatch` è `(Node … Last) → Replacement` | ✅ | — | `StyleMatch.SpanEdits` più la pianificazione in tre fasi descritta sotto: un suggerimento multi-file viene risolto contro **tutta la soluzione**, ogni progetto toccato passa dal gate, e il suggerimento è applicato **tutto o niente** |
| 2 | **Il gate prova solo che compili, non che sia equivalente.** Un errore di semantica che compila passa | ✅ | — | il gate semantico descritto sotto: confronto `IOperation` per le regole che dichiarano l'equivalenza |
| 3 | **Nessun canale per le opzioni del repository** | 🟡 | leggere `AnalyzerConfigOptions` — **fatto** per cinque chiavi C# e tre VB; mancano le preferenze `csharp_style_var_*` (che però si contraddicono: IDE0007 vs IDE0008) e le opzioni di naming (che richiedono un rename, non una riscrittura) | `StyleContext` + `StyleOptions` + `RequiredOption` + `IsOptIn` |
| 4 | **Non esiste il verbo "cancella".** Una regola sostituisce un nodo, non lo rimuove | ✅ | — | `StyleMatch.Removes`: IDE0035 (codice irraggiungibile), IDE0051 (membro privato mai referenziato), IDE0052 (campo scritto e mai letto) |

### 1. Workspace edit: come è fatto adesso

La pianificazione avviene in tre fasi (`StyleRewriteService.SimplifyAsync`), perché un suggerimento
multi-file non appartiene a un progetto:

1. **Riscritture sintattiche** per ogni documento, raccolte per progetto.
2. **Suggerimenti multi-documento** (`StyleMatch.SpanEdits`) risolti contro la soluzione intera: ogni
   file nominato è cercato in *qualunque* progetto (`FindDocument`), e i documenti prodotti finiscono nel
   piano del progetto che li possiede.
3. **Gate del compilatore** per ogni progetto toccato, usando `CompilationCache`. Poi un suggerimento
   multi-file sopravvive **solo se ogni suo documento è stato accettato**: se uno è rifiutato, gli altri
   vengono ritirati dal piano prima di qualsiasi scrittura.

Un file già riscritto come albero sintattico è un blocco: gli offset di un altro suggerimento punterebbero
al testo che sta per sparire, quindi il secondo viene rifiutato con il motivo.

### 2. Gate semantico: cosa prova e cosa no

`StyleRule.ClaimsEquivalence` marca le regole che devono lasciare intatto il significato a livello di
operazioni (oggi IDE0001, IDE0002, IDE0003, IDE0047, IDE0049). Per quelle, `StyleRewriteService` prende
la regione riscritta, la sostituisce in un albero usa e getta, e confronta l'albero `IOperation` prima e
dopo (`SemanticEquivalence`):

- si ignorano le operazioni implicite e il ricevitore di un accesso qualificato (`this._x` e `_x` sono la
  stessa cosa);
- si confrontano tipo di operazione, operatore, valore costante, simbolo referenziato e numero di figli;
- una differenza **rifiuta il file** con regola, riga e primi percorsi diversi; il risultato la riporta in
  `semantics { compared, verified, failures }`.

Un nodo senza operazione propria (un nome di tipo in una firma, un'espressione fra parentesi) **non è
confrontabile**: viene saltato e non conta né come verificato né come fallito, invece di produrre una
prova finta. Le regole non marcate restano al solo gate del compilatore: il campo `semantics` dice quante
ne sono state confrontate, non quante ne esistono.

### Cosa sbloccherebbe ciascuno

- **(1) workspace edit** → la metà del catalogo ReSharper che è intrinsecamente multi-file (rename, change
  signature, spostare un membro e aggiornare i chiamanti).
- **(2) gate semantico + cache** → le regole il cui errore *compila* sono confrontate a livello di
  `IOperation` quando dichiarano l'equivalenza; l'isolamento per singola modifica è economico perché il
  "prima" viene dalla cache e il "dopo" è un riparse.
- **(3) opzioni** → già usato da IDE0011, IDE0036, IDE0040, IDE0055, IDE0073, IDE0160/IDE0161.
- **(4) cancellazione** → IDE0035, IDE0051, IDE0052, IDE0110 resta fuori con il motivo.
- **(5) composizione** → IDE0005, IDE0032 e IDE0290 sono raggiunti da `simplify_code` per delega.

---

## 3. Copertura della tabella IDE

**87 riscritti + 39 con motivo scritto = 126 id**, cioè la tabella ufficiale è interamente rendicontata.
Il dettaglio riga per riga è in [CAPABILITY_MAP.md](../CAPABILITY_MAP.md); qui solo il riassunto.
**Il livello Visual Basic conta a parte** — è la colonna VB della tabella (`dotnet_style_*` condivise +
`visual_basic_style_*`): 15 id riscritti e 34 con il motivo in `VbStyleRewriteCatalog`, esposti da
`simplify_vb_code`; gli id che il VB non definisce affatto (pattern matching, graffe, expression
bodies) restano nel catalogo C#.

| Gruppo | Quanti | Esempi | **Cosa manca all'engine** (non "è fuori per scelta") |
|---|---|---|---|
| Delegati ad altri tool | 3 | IDE0005, IDE0032, IDE0290 | ✅ `simplify_code` li accetta e li affida a `optimize_usings` / `property_to_auto` / `convert_to_primary_constructor` |
| Coppie che si contraddicono | 3 | IDE0007 vs IDE0008, IDE0048 vs IDE0047 | **un'opzione**: sono scelte di squadra, non difetti |
| Cambiano il contratto | 3 | IDE0050, IDE0064, IDE0110 | una **decisione di API** (un record diventa tupla, un campo diventa scrivibile, un discard può essere voluto) |
| Layout e policy | 13 | IDE0065, IDE0130, IDE0210/0211, IDE0221, IDE0360, IDE2000–2006 | **decisioni di progetto**: dove stanno gli using, namespace vs cartella, forma dei blank line |
| Liste di target documentati | 2 | IDE0076, IDE0077 | i **target ammessi** da Microsoft per `SuppressMessage` |
| Feature specifica | 8 | IDE0078, IDE0303/0304/0305, IDE0330, IDE0350, IDE0410, IDE1005 | builder contract, analisi async, pattern di lock, conversione della lambda |
| Non applicabili al C# | 3 | IDE0081/0084/0140 (VB) | sono Visual Basic: due dei tre **ci sono** nel livello VB (IDE0081, IDE0084) |
| Naming, compilatore, Copilot | 3 | IDE1006, IDE1007, IDE3000 | un **rename**, una diagnostica del compilatore, una suggestione non deterministica |
| Coperto da un altro id | 1 | IDE0370 | ✅ Microsoft l'ha divisa in IDE0240/IDE0241, già riscritte |

**Nessun id resta nel gruppo "implementabile senza feature nuove":** l'ultimo (IDE0220) è stato fatto, e
anche IDE0010, IDE0072, IDE0120 e IDE0121, che erano classificati come "analisi" per pigrizia e non per
limite tecnico.

**Correzione di impostazione:** "fuori per scelta" era una classificazione comoda e sbagliata. A parte i 5
non applicabili (Visual Basic, diagnostica del compilatore, Copilot), ogni gruppo è fermo su **una feature
mancante**, non su una decisione. La tabella sotto dice quale, e cosa sblocca.

### Le feature mancanti, in ordine di resa

| Feature | Stato | Cosa sblocca |
|---|---|---|
| **Rimozione di un nodo** (`StyleMatch.Removes`) | ✅ fatta | IDE0035, IDE0051, IDE0052 |
| **La soluzione nel contesto** (`StyleContext.Solution` + `IsPristine`) | ✅ fatta | le regole la cui risposta è fuori dal documento |
| **Completamento degli switch su enum** (semantica, nessuna feature nuova) | ✅ fatto | IDE0010 e IDE0072 |
| **Catene LINQ** (riscritture di espressione) | ✅ fatto | IDE0120, IDE0121, IDE0220 |
| **Nullability flow** (`Nullability.FlowState`) | ✅ fatto | IDE0080 |
| **Modifiche al trivia** (direttive `#nullable`) | ✅ fatto | IDE0240, IDE0241 (e IDE0370, che Microsoft ha dividiso in queste due) |
| **Opzione accessibilità** (`dotnet_style_require_accessibility_modifiers`) | ✅ fatto | IDE0040 |
| Indice dei riferimenti persistente | ✅ fatto | rende quelle risposte veloci e ripetibili |
| **Cache di compilazione incrementale** | ✅ fatta | il "prima" del gate è gratis e ogni candidato è giudicato riparsando il solo documento cambiato (`CompilationCache`) |
| **Gate semantico** (`IOperation`) | ✅ fatto per le regole che dichiarano l'equivalenza | rende sicure le regole il cui errore compila; per le altre resta il solo compilatore |
| **Workspace edit** multi-documento | ✅ fatto | qualunque regola può toccare più file della soluzione, in modo atomico |
| **Dataflow di Roslyn** (`AnalyzeDataFlow`) — assegnazioni morte | ⬜ | non blocca più nessun id: IDE0059 è stato fatto per altra via |
| **Classificazione lettura/scrittura** sui riferimenti | ✅ fatta | IDE0052 |
| **Modifiche al trivia** per le soppressioni | ✅ fatta | IDE0079: solo la forma `[SuppressMessage]` e solo id del compilatore (`CS####`), giudicati con `WithReportSuppressedDiagnostics(true)` — il compilatore onora l'attributo, quindi la lista normale è già filtrata |
| **`file_header_template`** | ✅ fatta | IDE0073 — inserisce l'intestazione e non la sostituisce mai, perché quella presente è testo del repository |
| **Composizione dei tre tool** | ✅ fatta | IDE0005, IDE0032, IDE0290: i due servizi per-simbolo sono raggiunti da un'enumerazione dei candidati dentro `SimplifyCodeService`; non fanno parte di `rules=all` |

**Il modo più economico per salire di copertura** era comporre i tre servizi già esistenti dentro
`simplify_code` (IDE0005, IDE0032, IDE0290): fatto, e sono contati negli 87. Il passo successivo non è
un altro id IDE — è la famiglia `context_action` (`docs/resharper-context-actions.md`): le azioni che né
una riscrittura di stile né un refactoring raggiungono, con l'inventario delle 307 context action C# di
ReSharper e le 13 implementate.

---

## 4. Il solution store

Il database è `<solution>/.visualMCP/visualmcp.db`, SQLite, scoping per soluzione. Regola di progetto:
**ci finisce solo ciò che è derivato e ricostruibile, con l'hash dei suoi input**; il codice su disco resta
l'unica verità. Un dato senza hash di freschezza produce analisi stantie, e le analisi stantie sono
peggiori della lentezza perché sono silenziose.

| Dato | Stato | Chiave | A cosa serve |
|---|---|---|---|
| Report di ispezione | ✅ | path documento + mtime | non ricalcolare `inspect_code` a ogni chiamata |
| Log delle chiamate | ✅ | id crescente | audit, "cosa è stato fatto e quando" |
| Storia delle modifiche + testo di prima | ✅ | file + timestamp | audit delle riscritture **e revert** (`revert_edit`), solo se il file non è cambiato dopo |
| Fingerprint dei documenti | ✅ | path, con l'hash del contenuto | dice quali file sono cambiati: è la chiave di freschezza di indice, gate e cache future |
| Indice dei riferimenti (simbolo → documenti e span) | ✅ | hash del file, ricostruito solo per i file cambiati | `who` (chi usa un simbolo), `unused` (membri non pubblici mai referenziati), impatto di una modifica |
| Fatti sui pacchetti NuGet (versione risolta, TFM, archi) | ✅ | id + versione + TFM, da `project.assets.json` | "questa API esiste nella versione che targettiamo", chi tira dentro un pacchetto |
| Artefatti decompilati | ✅ | MVID dell'assembly | il decompilato non si perde e non si rilancia ILSpy due volte |
| **Cache di compilazione incrementale** | ✅ | riferimenti già chiusi + hash per file (`SNITCH/Languages/Gate/CompilationCache.cs`) | rende economico l'isolamento per singola modifica nel gate. Nota: gli alberi Roslyn non sono serializzabili, quindi una "parse cache" fra processi non esiste — ciò che si può persistere è il fingerprint, ed è quello che c'è |
| **Superficie pubblica per versione di pacchetto** | ⬜ | id + versione | richiede di leggere gli assembly del pacchetto: si appoggia agli artefatti decompilati, quindi va dopo |
| **Soluzione sintetica dai decompilati** | ❌ | — | il pipeline nativo (Ghidra) che la costruiva non è più in questo repo: lo store è pronto (artefatti per MVID) se torna |

Nota operativa: **`visualmcp.db` non è più tracciato da git** (è uscito dall'indice): un file binario che
cambia a ogni chiamata sporcava ogni diff, e la cartella viene ricreata al bisogno.

---

## 5. Ordine consigliato

| Passo | Stato | Costo | Sblocca |
|---|---|---|---|
| 1. Fingerprint dei documenti + indice dei riferimenti | ✅ fatto | medio | `who`, `unused`, impatto di una modifica; ed è la chiave per un gate incrementale |
| 2. Artefatti decompilati per MVID | ✅ fatto | piccolo | il decompilato si riusa invece di rilanciare ILSpy |
| 3. Fatti sui pacchetti + grafo | ✅ fatto | piccolo | versione risolta per TFM, chi tira dentro un pacchetto |
| 4. Storia delle modifiche con il testo + `revert_edit` | ✅ fatto | piccolo | ogni scrittura è annullabile (se il file non è cambiato dopo) |
| 5. Cache di compilazione incrementale per il gate | ✅ fatto (`SNITCH/Languages/Gate/CompilationCache.cs`) | medio | isolamento per singola modifica: una regola sbagliata smette di rifiutare il file intero |
| 6. Compose dei tre tool esistenti dentro `simplify_code` | ✅ fatto (`SimplifyCodeService` delega e il servizio di stile accetta gli id) | piccolo, ma per-simbolo | IDE0005, IDE0032, IDE0290 |
| 7. Regole residue a basso costo | ✅ fatte: IDE0073, IDE0079, IDE0039 | piccolo | +3 id |
| 8. Workspace edit (multi-documento, atomico) | ✅ fatto | medio | la famiglia multi-file dei refactoring |
| 9. Gate semantico (`IOperation` diff) | ✅ fatto per le regole che dichiarano l'equivalenza | grande | le regole il cui errore compila |

---

## 6. Come verificare lo stato da soli

- Regole attive e relativi perché: `simplify_code` con un id non supportato restituisce `notRewritten` con
  il motivo; `StyleRewriteCatalog.Supported` / `.NotRewritten` sono le due liste nel motore.
- Regole Visual Basic: le stesse due liste sono `VbSimplifyCodeService.Rules` / `.NotRewritten`
  (`VbStyleRewriteCatalog`); `simplify_vb_code` con un id che il VB non riscrive restituisce
  `notRewritten` con il motivo, e con `scope=solution` tocca solo i file `.vb` della soluzione caricata.
- Opzioni lette: il risultato di `simplify_code` include `styleOptions` quando si chiede una regola
  guidata dall'`.editorconfig` (per il VB riporta anche `preferIsNot`).
- Cosa c'è nell'indice: `reference_index(action=status)` riporta documenti, simboli, dichiarazioni, usi e i
  conteggi per tipo (dichiarazioni e usi).
- Dove è dichiarato un membro, come si chiama la sua firma, chi lo usa e **come**: `reference_index` con
  `action=find|describe|refs`; `action=callers|callees` per la gerarchia delle chiamate, `action=hierarchy`
  per basi/interfacce/derivati/override, `action=outline` per la forma di un tipo o di un file, `action=unused`
  per ciò che nessuno usa.
- Pacchetti e dipendenze: `package_facts(action=list|dependencies)` dopo un `action=refresh`.
- Storia e annullamento: `edit_history` (ogni riga dice se è `revertible`) e `revert_edit(id)`.
- Decompilazione in pseudocodice: `decompile_to_metacode(assemblyPath, typeFullName=…)` restituisce
  l'intestazione con `type(s)`, `hidden` ed `error(s)`; `analyze_native_binary` restituisce le funzioni
  recuperate, i percorsi di snapshot e log e i diagnostici `RAW`/`MC0008`.
- Da dove viene ogni numero di questo documento: `StyleRewriteCatalog.cs`, `VisualMcpStore*.cs` e i test
  in `src/VisualMCP.Tests/` (`SimplifyCode*.cs`, `SolutionStoreTests.cs`).
