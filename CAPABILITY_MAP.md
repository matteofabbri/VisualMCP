# VisualMCP — Capability Map vs The Competition

> Obiettivo: diventare il tool MCP numero uno per sviluppatori .NET.  
> Concorrenti principali: **ReSharper**, **Rider**, **VS IntelliCode**, **GitHub Copilot Workspace**, **SonarQube**, **NDepend**, **NCrunch**, **dotTrace/dotMemory**, **CodeScene**.

---

## Motore (2026-10-28)

VisualMCP gira su **Snitch** (namespace `Snitch.*`), che vive **dentro questo repository** in
`SNITCH\` e che `src\VisualMCP\VisualMCP.csproj` referenzia come `..\..\SNITCH\Snitch.csproj`
(righe 35-37). Il vecchio `Snitch.2o` (`D:\REPOSITORY\SNITCH2`) è stato ritirato e il suo codice
non esiste più su disco.

`AddSnitch` (`SnitchServiceCollectionExtensions`) registra `SnitchContext` — l'environment che
scopre e apre le soluzioni sotto la working directory — e i servizi `I*Service` che i tool
iniettano. `IRoslynWorkspace` non è un adapter separato: lo implementa `SnitchContext` stesso
(`public sealed class SnitchContext : IAsyncDisposable, IRoslynWorkspace`, `SnitchContext.cs:19`) ed
è registrato come tale in `SnitchServiceCollectionExtensions.cs:28`; il vecchio `SnitchContextLegacy`
è stato assorbito e non esiste più (`SnitchContext.cs:46-49`, e `find_symbol` non lo trova).

Molti servizi del motore restituiscono **oggetti anonimi**, non tipi nominabili: i tool ne leggono i
campi tramite `VisualMCP.Formatting.EngineJson`, che serializza il risultato e lo rilegge su un
record con binding case-insensitive. Fanno eccezione il livello di profiling e il metacode, che
restituiscono record nominati (`SlowdownReport`, `MetacodeDecompilation`, …) e che i tool rilanciano
così come sono, e `read_symbol`, che usa direttamente `IRoslynWorkspace` + Roslyn perché il motore
non espone più l'estrazione della sorgente di una dichiarazione.

Perdute con Snitch 2 e non ricostruite: `rider_inspect`, `read_vs_state` e la pipeline nativa
(`inspect_native_binary`). I file orfani sono stati rimossi il 2026-10-28 e restano recuperabili
dalla history di git.

---

## Snitch.Metacode (2026-10-29)

`SNITCH/Libraries/Snitch.Metacode` è una libreria separata che porta alberi sintattici di
lingue diverse **in un unico modello di pseudocodice**, esposta da **quattro tool MCP**:
`metacode_translate` (un sorgente → pseudocodice canonico, con i diagnostici in coda), `metacode_compare`
(due sorgenti → pseudocodice e diff riga per riga, che è il modo per dire "è lo stesso algoritmo?"),
`decompile_to_metacode` (un assembly .NET → ILSpy → C# → lo stesso modello) e `analyze_native_binary`
(un binario nativo → Ghidra headless → le funzioni recuperate nello stesso modello). La pipeline
predefinita legge i **tre linguaggi sorgente del prodotto** — **C#** e **Visual Basic** (Roslyn, sola
sintassi: nessuna compilazione, nessuna soluzione caricata) e **Razor** (`.cshtml`/`.razor`) — più i formati
della
libreria stessa: **Ghidra** (lo snapshot JSON che esporta il servizio) e il testo canonico che la
libreria stampa (`PseudocodeFrontend`), quindi qualunque altro linguaggio entra semplicemente
producendo quel testo. **Python** (dump JSON del modulo `ast`) e **C++** (`clang -ast-dump=json`)
hanno un lettore ma sono **opt-in**: nessun tool produce quei dump, quindi non stanno in
`MetacodeTranslator.Default` e `metacode_translate` non offre le lingue. Visual Basic ha il suo
frontend (`VisualBasicFrontend`): `metacode_translate`, `metacode_compare`, `metacode_index` e
`metacode_generate` leggono i `.vb`, e ciò che il modello non ha resta `RAW[vb]` con un diagnostico.

Lo stesso algoritmo scritto in C#, Python e C++ esce con lo **stesso corpo**, riga per riga: è il test
`The_same_algorithm_prints_the_same_body_from_csharp_python_and_cpp`, che per Python e C++ costruisce
il traduttore con il lettore corrispondente. Le differenze di lingua
spariscono in due passate (`for` → `while`, blocchi annidati appiattiti) e ogni nodo conserva la riga
di origine. Un costrutto senza nodo diventa `RAW[linguaggio] testo // motivo` **più un diagnostico**:
la traduzione incompleta si vede.

| Aspetto | Stato | Note |
|---|:---:|---|
| Modello unico (dichiarazioni, istruzioni, espressioni) | ✅ | `MetacodeDocument` + `Origin` su ogni nodo |
| Frontend C# da `SyntaxTree` | ✅ | classi, struct, interfacce, enum, record, metodi, proprietà, campi, `using`, switch, try/catch, lambda |
| Frontend Python da dump `ast` JSON | ✅ | `range()` → nodo `FOR` come il C#; `self` → `this`; prima assegnazione → `DECLARE` |
| Frontend C++ da dump `clang` JSON | ✅ | classi/struct/enum, access specifier, cicli, switch, try/catch |
| Frontend pseudocodice (rilettura) | ✅ | giro completo identico, carattere per carattere, nei test |
| Passate di normalizzazione | ✅ | `NormalizeControlFlowPass` (con guardia sul `continue`), `FlattenSingleStatementBlocksPass` |
| Diagnostici invece di perdite silenziose | ✅ | `MC0001`–`MC0008` |
| Template C++, range-for, lambda con corpo a blocco nel parser | ✅ | `MetacodeTypeParameter` riusato per i template (`FUNCTION max<T>(…) : T`, `CLASS Box<T>`), `FOREACH` per il range-for, corpo a blocco estratto da `PseudocodeExpression.Parse` |
| Costrutti che prima finivano in `RAW` | ✅ | C#: `lock`, `yield`, `event`, `foreach` con deconstruction, `await foreach`; Python: `import`/`from … import`, decoratori, comprehension (lista/set/dict/generator); C++: lambda, alias di tipo (`using`/`typedef`) |
| Decompilazione → metacode | ✅ | `DecompileService` implementa `IDecompileService` **e** `IMetacodeDecompileService`: il C# di ILSpy passa dal `CSharpFrontend` e dà `MetacodeDocument` + pseudocodice per tipo; `decompile_to_metacode` un tipo o una pagina di tipi, con `members`/`functions`, `hidden`, `errors` |
| Binario nativo → metacode | ✅ | `IGhidraService` esegue Ghidra headless (progetto temporaneo, script `ExportNativeGraph`, timeout che uccide l'albero, `JAVA_HOME_OVERRIDE` con un retry) e `GhidraFrontend` normalizza il C di Ghidra e lo rilegge col `PseudocodeFrontend`; `analyze_native_binary` restituisce le funzioni con prototipo e indirizzo, e lo snapshot resta rileggibile da `metacode_translate` |
| Tool MCP | ✅ | `metacode_translate` (path o testo + lingua, `showOrigin`, diagnostici in coda), `metacode_compare` (due sorgenti, diff LCS riga per riga con contesto compresso), `decompile_to_metacode` (assembly → pseudocodice) e `analyze_native_binary` (nativo → pseudocodice); nessuno dei quattro richiede una soluzione caricata |

Documentazione: [docs/metacode.md](docs/metacode.md). Test: `src/VisualMCP.Tests/MetacodeTests.cs`
(11 casi), `src/VisualMCP.Tests/MetacodeDepthTests.cs` (15 casi sui costrutti aggiunti e sui giri
completi) e `src/VisualMCP.Tests/DecompilationMetacodeTests.cs` (14 casi su decompilazione e Ghidra).
In questo cantiere non sono stati eseguiti — build e test sono vietati ai subagent, la
compilazione è serializzata dal workflow — quindi la prima esecuzione è quella del gate.

---

## Web dashboard (2026-10-29)

Il server ospita una vista web **di sola lettura** di sé stesso e del solution store, dentro lo
stesso processo MCP (nessun processo da avviare), su **http://127.0.0.1:4677**. Il tool NON è un
tool MCP: è una seconda istanza ASP.NET Core minimale accanto al trasporto stdio, con i provider
di log azzerati perché stdout appartiene al protocollo.

| Aspetto | Stato | Note |
|---|:---:|---|
| Porta | ✅ | 4677, altrimenti quella ricordata in `context` (`dashboard.port`), altrimenti una assegnata dal sistema; quella scelta viene risalvata, quindi due client sulla stessa soluzione non collidono |
| Pagina per ogni tabella | ✅ | Le tabelle si leggono da `sqlite_master`, non da una lista fissa: `command_log`, `applied_edit`, `context`, `board_*`, `si_*`, `ti_*`, `package_*`, `decompiled_artifact` e qualunque tabella futura; le shadow table FTS5 sono nascoste |
| Filtro, ordinamento, pagine | ✅ | Sottostringa su ogni colonna non-blob, intestazioni ordinabili, dimensione pagina 25–1000, `CSV`/`JSON` |
| Panoramica | ✅ | Processo (pid, uptime, memoria, runtime, trasporto), soluzioni caricate con progetti/documenti, conteggi, ultime chiamate e ultime modifiche |
| Catalogo dei tool | ✅ | I tool che il processo annuncia, con descrizione e argomenti (tipo, obbligatorietà, descrizione) e un filtro |
| Log live | ✅ | Coda di `debug.log` dal più recente, aggiornamento ogni 2,5 s, filtro testo e per tipo (tool/richieste/errori/warning) |
| Schema | ✅ | Colonne di ogni tabella come le riporta SQLite (tipo, not null, pk) |
| JSON API | ✅ | `/api/health`, `/api/overview`, `/api/tables`, `/api/tables/{table}` |
| Sicurezza | ✅ | Solo loopback, tutte GET, CSP ristretta, valori sempre encoded, nomi di tabella validati contro `sqlite_master` |
| Un probe di discovery non la avvia | ✅ | Stessa decisione dell'eager load: un probe non paga l'apertura dello store |
| Porta occupata | ✅ | Warning nel log, la sessione MCP non ne risente |

Documentazione: [docs/dashboard.md](docs/dashboard.md). Test: `src/VisualMCP.Tests/DashboardTests.cs`
(7 casi: scoperta delle tabelle, filtro/paging, rifiuto dell'injection, esclusione dei blob,
politica delle porte, porta ricordata, e un listener reale che serve ogni pagina).

---

## Legenda

| Simbolo | Significato |
|---------|-------------|
| ✅ | Implementato |
| ❌ | Non implementato |
| 🔧 | Implementato parzialmente / proof-of-concept |
| 🎯 | Priorità alta — da implementare |
| 💀 | Non applicabile (out of scope per un MCP server) |

---

## 1. Navigazione & Ricerca del Codice

| Feature | Implementato | Note |
|---------|:---:|-------|
| Cerca simbolo per nome (classe, metodo, campo) | ✅ | `find_symbol` |
| Trova tutti i riferimenti a un simbolo | ✅ | `find_references` |
| Trova tutti i caller di un metodo | ✅ | `find_callers` |
| Trova implementazioni di un'interfaccia | ✅ | `find_implementations` |
| Trova tipi derivati da una classe base | ✅ | `find_derived_types` |
| Info dettagliate su un simbolo (firma, doc, ereditarietà) | ✅ | `get_symbol_info` |
| Elenca i membri di un tipo | ✅ | `get_type_members` |
| Go-to-definition con il corpo (mostra il sorgente della dichiarazione) | ✅ | `read_symbol` — risolto semanticamente, sostituisce `search_text` + `read_file` |
| Go-to-line | ❌ | 💀 per MCP puro |
| Ricerca full-text + regex su tutti i file | ✅ | `search_text` (regex, legge da disco) |
| **Indice full-text del repository (FTS5)** | ✅ | `text_index` — una riga per riga di ogni file (sorgente, documentazione, decompilato) in `.visualMCP/visualmcp.db`, identificatori spezzati (`required` trova `GetRequiredService`), ranking BM25, risposta `file:riga` + la riga + le righe di contesto + i file che corrispondono di più; build incrementale per (lunghezza, mtime) e potatura dei file spariti |
| Ricerca per struttura AST (structural search) | ❌ | 🎯 ReSharper SSR — da implementare |
| Cerca per pattern semantico (es. "tutti i metodi che lanciano X") | ❌ | 🎯 |
| File structure / outline di un tipo | 🔧 | Coperto da `get_type_members` |
| Ricerca recente / MRU | ❌ | 💀 per MCP puro |

---

## 2. Refactoring

| Feature | Implementato | Note |
|---------|:---:|-------|
| Rename (con preview) | ✅ | `apply_rename` + `preview_rename` |
| Extract Method | ✅ | `extract_method` — blocco contiguo; parametri dedotti, un `return` finale; blocchi con `await` → `async Task[<T>]` + `await`; rifiuta salti/yield e ogni modifica che aggiunga errori di compilazione |
| Extract Method (suggerimenti) | ✅ | `extract_method_candidates` |
| Extract Interface | ✅ | `extract_interface` |
| Extract Variable | ✅ | `introduce_variable` |
| Inline Method / Variable | ✅ | `inline_symbol` |
| Change Signature | ✅ | `change_signature` — riordino/aggiunta/rimozione; riscrive i call site per nodo, anteprima per call site, rifiuta i method group, gate: compilatore |
| Encapsulate Field | ✅ | `encapsulate_field` |
| Pull Members Up | ✅ | `pull_members_up` |
| Push Members Down | ✅ | `push_members_down` — base con un solo derivato e membro usato solo lì |
| Move Type to file/namespace | ✅ | `move_type` (file) + `move_to_namespace` (namespace, con i `using` dei file che referenziano, gate: compilatore) |
| Rename namespace | ✅ | `rename_namespace` — rinomina l'ultimo segmento via `Renamer` di Roslyn: dichiarazioni, `using` e riferimenti qualificati (gate: compilatore) |
| Convert to auto-property | ✅ | `property_to_auto` (rifiuta se il campo è usato altrove) |
| Make partial | ✅ | `type_to_partial` (segna ogni dichiarazione del tipo) |
| Make static (`ChangeStaticness`) | ✅ | `make_static` — riscrive i call site, rifiuta se tocca stato d'istanza |
| Method → property (`Function2Property`) | ✅ | `function_to_property` — rifiuta se usato come method group o in `nameof` |
| Property → method (`Property2Function`) | ✅ | `property_to_function` — `GetX()`/`SetX(value)`; riscrive letture, scritture, `+=` e `++`/`--`; rifiuta `nameof`, ref/out e gli initializer |
| Inline parameter (`InlineParameter`) | ✅ | `inline_parameter` — solo se ogni call site passa la stessa espressione, indipendente dal call site e con lo stesso binding dentro il metodo (speculative binding); gate: compilatore |
| Static → extension (`Static2Extension`) | ✅ | `static_to_extension` — ruota i call site, aggiunge il `using` se serve |
| Introduce field | ✅ | `introduce_field` — solo espressioni senza contesto (niente locali/parametri/istanza) |
| Introduce parameter | ✅ | `introduce_parameter` — qualunque espressione fissa a compile time (letterali, costanti, `nameof`, `typeof`, `default(T)`); i call site ricevono la stessa |
| Invert if (`InvertBool`) | ✅ | `invert_if` — richiede un `else` |
| Add params modifier (`ParamsModifier`) | ✅ | `add_params` — sicuro: i call site esistenti restano validi |
| Change nullability (`ChangeNullability`) | ✅ | `change_nullability` — gate: rifiuta se introduce nuovi diagnostici CS86xx/CS87xx |
| Rename tuple component (`RenameTupleComponent`) | ✅ | `rename_tuple_component` — match semantico: non tocca `.Count` di altri tipi |
| Global usings (`GlobalUsings`) | ✅ | `global_usings` — gate: rifiuta se l'import globale introduce errori (ambiguità) |
| Implicit usings (`ToggleImplicitUsings`) | ✅ | `implicit_usings` — scrive `<ImplicitUsings>enable\|disable</ImplicitUsings>`; in disattivazione aggiunge i `using` espliciti dedotti dal modello semantico e verifica il progetto con il documento `*.GlobalUsings.g.cs` rimosso |
| Global → using locale (`ConvertToNonGlobalUsing`) | ✅ | `convert_to_non_global_using` — toglie un `global using X;` e aggiunge `using X;` solo ai file che smettono di compilare (guidato dal compilatore) |
| Change type (`ChangeType`) | ✅ | `change_type` — gate: scrive solo se il progetto continua a compilare |
| Move type to outer scope | ✅ | `move_type_to_outer_scope` — estrae il tipo annidato, riscrive `Outer.Inner` |
| Copy type | ✅ | `copy_type` — duplica il tipo sotto un nuovo nome (ctor rinominati) |
| Constructor → factory (`Constructor2FactoryMethod`) | ✅ | `constructor_to_factory` — ctor privato + `Create`, riscrive i `new` |
| Convert to primary constructor | ✅ | `convert_to_primary_constructor` — solo quando il ctor assegna parametri a campi |
| Use base type | ✅ | `use_base_type` — cambia il tipo con una base/interfaccia, se compila |
| Push members down | ✅ | `push_members_down` — base con un solo derivato e membro usato solo lì |
| Extract class | ✅ | `extract_class` — membri che leggono solo sé stessi → nuova classe, l'originale tiene un campo + inoltri (gate: compilatore) |
| Extract superclass (`ExtractSuperclass`) | ✅ | `extract_superclass` — i membri scelti vanno in una nuova classe base, il tipo eredita; la selezione deve essere autosufficiente (gate: compilatore) |
| Convert anonymous type to declared type | ✅ | `convert_anonymous_to_type` — `new { ... }` → classe con costruttore e proprietà readonly, creazione riscritta (gate: compilatore) |
| Move static members (`MoveStaticMembers`) | ✅ | `move_static_members` — metodi/proprietà/campi statici in un altro tipo; riscrive `Source.M(...)` e la forma nuda `M(...)`, importa il namespace (gate: compilatore) |
| Move instance method (`MoveInstanceMethod`) | ✅ | `move_instance_method` — diventa statico sul target con il ricevitore come primo parametro, `this`/membri impliciti ri-puntati, call site riscritti (gate: compilatore) |
| Inline class (`InlineClass`) | ✅ | `inline_class` — la classe helper usata solo come un campo del suo owner viene assorbita: membri dentro, `_helper.X` → `X`, campo e tipo rimossi; ogni altro uso è un rifiuto |
| Move to folder (`MoveToFolder`) | ✅ | `move_to_folder` — mette il file nella cartella del suo namespace (o in una esplicita) e aggiorna il modello; nuovo `IRoslynWorkspace.TryRemoveDocument` |
| Function ↔ indexer | ✅ | `function_to_indexer` / `indexer_to_function` — `T Get(int i)` ↔ `T this[int i]`, riscrittura dei call site; i call site devono essere nello stesso file |
| Introduce part of string | ✅ | `introduce_part_of_string` — una sottostringa di un letterale diventa una `private const` e resta una concatenazione (gate: compilatore) |
| Semplificazioni di stile | ✅ | `simplify_code` — 87 id IDE riscritti e 39 elencati con il motivo (tabella sotto), scope documento/progetto/soluzione, anteprima, gate: compilatore |
| Semplificazioni di stile Visual Basic | ✅ | `simplify_vb_code` — 15 id della colonna VB (14 riscritture: IDE0001, IDE0002, IDE0003, IDE0004, IDE0049, IDE0041, IDE0029/IDE0030, IDE0031, IDE0047, IDE0054, IDE0100, IDE0075, IDE0084, IDE0081), 34 id VB non riscritti con il motivo; scope documento/progetto/soluzione, anteprima, gate: compilatore, id `IDE0084` guidato da `visual_basic_style_prefer_isnot_expression` |
| Convert class to record | ✅ | `convert_to_record` — cambio di keyword, oppure forma posizionale (`record Point(int X, int Y)`) quando un costruttore assegna ogni parametro a un'auto-property omonima (gate: compilatore) |
| Convert abstract class ↔ interface | ✅ | `convert_type_kind` — le due direzioni in un tool: la dichiarazione e ogni derivato/implementatore sono riscritti insieme (`override` tolto o aggiunto); stato d'istanza, struct, record e default interface method sono rifiutati con il motivo (gate: compilatore su tutti i progetti toccati) |
| Flatten conditional / Remove nesting | ❌ | 🎯 |
| Use pattern matching | ❌ | 🎯 |
| Safe Delete | ✅ | `safe_delete` |
| Reorder Members | ✅ | `reorder_members` |
| Remove Regions | ✅ | `remove_regions` |
| Optimize Usings | ✅ | `optimize_usings` |
| Generate constructor / ToString / Equals / GetHashCode | ✅ | `generate_members` |
| Implement INotifyPropertyChanged | ✅ | `implement_inpc` |

### Regole IDE riscritte da `simplify_code`

Della tabella ufficiale Microsoft (code-style rules) queste sono le regole con una riscrittura
meccanica e verificata dal compilatore. Per i quattro limiti strutturali del motore, cosa è stato
tolto e cosa resta da fare, vedi [docs/engine-state.md](docs/engine-state.md).

| IDE | Riscrittura |
|-----|-------------|
| IDE0001 | `System.Collections.Generic.List<int>` → `List<int>` (nome semplice già in scope) |
| IDE0002 | `Type.Member` → `Member` quando il nome non qualificato lega allo stesso simbolo |
| IDE0003 | `this.Member` → `Member` |
| IDE0004 | `(T)x` → `x` quando `x` ha già quel tipo |
| IDE0010 | aggiunge i membri dell'enum che nessun `case` copre (solo quando manca `default`) |
| IDE0011 | aggiunge le graffe a un'istruzione incorporata quando `csharp_prefer_braces` lo chiede |
| IDE0016 | `if (x == null) throw …; target = x;` → `target = x ?? throw …;` |
| IDE0017 | `var x = new C(); x.A = 1; x.B = 2;` → `var x = new C { A = 1, B = 2 };` |
| IDE0018 | `int v; M(out v)` → `M(out int v)` quando `v` non è letto dopo |
| IDE0019 | `var t = x as T; if (t != null) …` → `if (x is T t) …` |
| IDE0020 / IDE0038 | `if (x is T) { … (T)x … }` → `if (x is T t) { … t … }` |
| IDE0021 | costruttore con corpo di una sola istruzione → `C(int a) => Age = a;` |
| IDE0022 | metodo con corpo di un solo `return`/espressione → `int F() => 1;` |
| IDE0023 | operatore di conversione → expression body |
| IDE0024 | operatore aritmetico → expression body |
| IDE0025 | proprietà con solo getter → `public int P => _p;` |
| IDE0026 | indicizzatore con solo getter → expression body |
| IDE0027 | accessore `get { return _p; }` → `get => _p;` |
| IDE0028 | `new List<T>()` → `[]`, `new List<T> { a, b }` → `[a, b]` |
| IDE0029 / IDE0030 | `x != null ? x : y` → `x ?? y` |
| IDE0031 | `x != null ? x.M() : null` → `x?.M()` |
| IDE0033 | `tuple.Item1` → `tuple.Name` quando la tupla dichiara il nome |
| IDE0034 | `default(T)` → `default` |
| IDE0035 | rimuove le istruzioni irraggiungibili dopo un salto incondizionato |
| IDE0036 | ordina i modificatori secondo `csharp_preferred_modifier_order` |
| IDE0037 | `new { name = name }` → `new { name }` |
| IDE0040 | rende esplicito il modificatore di accesso (o lo toglie se è il default) secondo `dotnet_style_require_accessibility_modifiers` |
| IDE0041 | `x == null` → `x is null` (solo per l'operatore predefinito) |
| IDE0042 | `var t = Get(); var a = t.Item1; var b = t.Item2;` → `var (a, b) = Get();` |
| IDE0044 | aggiunge `readonly` a un campo privato scritto solo nei costruttori del suo tipo |
| IDE0045 | `if (c) x = a; else x = b;` → `x = c ? a : b;` |
| IDE0046 | `if (c) return a; else return b;` → `return c ? a : b;` |
| IDE0047 | parentesi intorno a un'espressione primaria: rimosse |
| IDE0049 | `System.Int32` / `Int32` → `int` |
| IDE0051 | rimuove un membro privato che nessun file della solution referenzia |
| IDE0052 | rimuove un campo privato scritto e mai letto, insieme a ogni assegnazione che lo scrive |
| IDE0053 | lambda con corpo di una sola istruzione → `x => x + 1` |
| IDE0054 | `x = x + y` → `x += y` |
| IDE0055 | riformatta il file con il `Formatter` di Roslyn secondo l'`.editorconfig` (solo su richiesta: non fa parte di `rules=all`) |
| IDE0058 | `M();` dove il valore non è letto → `_ = M();` |
| IDE0059 | rimuove un'assegnazione il cui valore viene sovrascritto prima di essere letto (dataflow di Roslyn) |
| IDE0060 | rimuove un parametro che nessuno legge, insieme all'argomento a ogni call site — una suggestion che copre più file |
| IDE0056 | `a[a.Length - 1]` → `a[^1]` |
| IDE0057 | `s.Substring(a)` → `s[a..]`, `s.Substring(0, n)` → `s[..n]` |
| IDE0061 | funzione locale con corpo di una sola istruzione → expression body |
| IDE0062 | funzione locale che non cattura nulla → `static` |
| IDE0063 | `using (var x = …) { … }` → `using var x = …;` (solo corpo senza salti) |
| IDE0066 | `switch` con ogni ramo che produce un valore → `x switch { … }` |
| IDE0070 | `GetHashCode` scritto a mano → `System.HashCode.Combine(…)` |
| IDE0071 | `$"{x.ToString()}"` → `$"{x}"` |
| IDE0072 | aggiunge alla switch expression gli arm dei membri mancanti, copiando l'espressione del discard |
| IDE0074 | `if (x == null) x = y;` → `x ??= y;` |
| IDE0075 | `x ? true : false` → `x` |
| IDE0080 | toglie `!` (null-forgiving) dove il flow state dice già "non null" |
| IDE0082 | `typeof(X).Name` → `nameof(X)` (per i tipi keyword servono i nomi qualificati: `nameof(string)` non compila) |
| IDE0083 | `!(x is T)` → `x is not T` |
| IDE0090 | `new Foo(args)` → `new(args)` quando il tipo è già fissato dal contesto |
| IDE0100 | `x == true` → `x`, `x == false` → `!x` |
| IDE0120 | `xs.Where(p).Any()` → `xs.Any(p)` (e `First`, `Count`, `Single`, …) |
| IDE0121 | `xs.Where(o => o is T).Cast<T>()` → `xs.OfType<T>()` |
| IDE0150 | `x is object` → `x != null` |
| IDE0160 / IDE0161 | namespace block-scoped o file-scoped secondo `csharp_style_namespace_declarations` |
| IDE0170 | `{ A: { B: 1 } }` → `{ A.B: 1 }` |
| IDE0180 | `var t = a; a = b; b = t;` → `(a, b) = (b, a);` |
| IDE0200 | `x => M(x)` → `M` |
| IDE0220 | `foreach (T x in collezione)` con conversione nascosta → `System.Linq.Enumerable.Cast<T>(collezione)`, secondo `dotnet_style_prefer_foreach_explicit_cast_in_source` |
| IDE0230 | `Encoding.UTF8.GetBytes("x")` → `"x"u8` dove serve uno span |
| IDE0240 / IDE0241 | rimuove una direttiva `#nullable` che non cambia nulla rispetto allo stato in vigore (prima riscrittura a livello di trivia) |
| IDE0250 / IDE0251 | aggiunge `readonly` a una struct o a un suo membro, se il compilatore lo permette |
| IDE0260 | `(o as T)?.M == valore` → `o is T { M: valore }` (solo proprietà/campi e valori costanti) |
| IDE0280 | `new ArgumentNullException("x")` → `nameof(x)` |
| IDE0300 / IDE0301 | `new T[] { a, b }` → `[a, b]`, `new T[0]` → `[]` |
| IDE0302 | `stackalloc int[] { 1, 2 }` → `[1, 2]` |
| IDE0320 | lambda che non cattura nulla → `static` |
| IDE0340 | `nameof(List<int>)` → `nameof(List<>)` |
| IDE0380 | rimuove il modificatore `unsafe` quando nel membro non c'è nulla di unsafe |
| IDE0390 / IDE0391 | toglie `async` a un metodo che non fa `await` e avvolge i `return` in `Task.FromResult` / `Task.CompletedTask` (IDE0391 per override e implementazioni d'interfaccia) |

Sono **87 id** su 126 della tabella. Tre famiglie meritano una nota:

- **Option-driven** (`IDE0011`, `IDE0036`, `IDE0040`, `IDE0055`, `IDE0160`/`IDE0161`, `IDE0220`): il
  motore legge l'`.editorconfig` del progetto (`csharp_prefer_braces`,
  `csharp_preferred_modifier_order`, `dotnet_style_require_accessibility_modifiers`,
  `csharp_style_namespace_declarations`, `dotnet_style_prefer_foreach_explicit_cast_in_source`) e agisce
  solo se l'opzione è configurata. Il risultato riporta in `styleOptions` cosa ha trovato, così un id che
  non ha fatto nulla spiega perché invece di sembrare rotto. `IDE0055` è **opt-in**: riformatta il file
  intero, quindi non fa parte di `rules=all`.
- **Expression body** (`IDE0021`–`IDE0027`, `IDE0053`, `IDE0061`): scattano solo quando il corpo è
  una sola istruzione e non contiene commenti né direttive — la riscrittura elimina le graffe, e un
  commento dentro sparirebbe con loro.
- **`IDE0044` / `IDE0250` / `IDE0251`**: il verdetto finale resta al compilatore, ma `IDE0044` ora
  calcola prima l'insieme delle scritture sul symbol del campo. Prima indovinava e lasciava al gate il
  compito di rifiutare il *file*, il che faceva sparire anche tutte le altre riscritture di quel file.
- **Cattura e unsafe** (`IDE0062`, `IDE0320`, `IDE0380`): prima di aggiungere `static` o togliere
  `unsafe` il motore risolve ogni simbolo del corpo. Una funzione locale o una lambda che legge un
  locale o un parametro esterno, o un membro d'istanza senza ricevitore, non viene toccata; `unsafe`
  resta se nel membro c'è un puntatore, un function pointer, `stackalloc`, `fixed` o un `&`.

Le riscritture a livello di istruzione (`IDE0011`, `IDE0016`, `IDE0017`, `IDE0018`, `IDE0019`,
`IDE0020`, `IDE0038`, `IDE0042`, `IDE0045`, `IDE0046`, `IDE0063`, `IDE0066`, `IDE0180`) sostituiscono
più istruzioni con una; i nodi prodotti vengono riformattati dal `Formatter` di Roslyn, così
l'indentazione resta quella del file.

Ogni scrittura effettiva viene registrata nella storia della soluzione (`edit_history`, tabella
`applied_edit` di `visualmcp.db`): tool, regole applicate e hash del contenuto prima/dopo, così una
riscrittura è verificabile e non solo dichiarata.

Tutte le altre regole della tabella (`IDE0040` modificatori di accesso, `IDE1006` naming,
`IDE0073` file header, …) sono elencate con il motivo in `StyleRewriteCatalog.NotRewritten` e
il tool risponde con quel motivo invece di ignorare l'id: sono layout, scelte di team o cambi di
semantica, non riscritture meccaniche.

---

## 3. Code Analysis & Diagnostics

| Feature | Implementato | Note |
|---------|:---:|-------|
| Errori e warning del compilatore (Roslyn) | ✅ | `get_diagnostics` |
| Code smells (async void, empty catch, metodi troppo lunghi, ecc.) | ✅ | `find_code_smells` |
| Dead code (tipi/metodi/campi non usati) | ✅ | `find_dead_code` |
| Simboli non usati | ✅ | `find_unused_symbols` |
| Async anti-patterns | ✅ | `find_async_anti_patterns` |
| **Anti-pattern di allocazione** | ✅ | `find_performance_anti_patterns` — `string +=` in un loop, materializzatore LINQ dentro un loop, collezione o array a dimensione costante allocati per iterazione; verificati sul modello di tipo, non sul testo |
| Security issues statici | ✅ | `find_security_issues` |
| Inventario pagine Razor e componenti Blazor | ✅ | `razor_pages` — kind, `@page`, `@model`, layout, sezioni, `@inject`, handler, code-behind |
| Problemi di markup/accessibilità nelle pagine Razor | ✅ | `analyze_razor_pages` — regole `RZ2xxx` con severità, `file:riga` e correzione |
| Vulnerabilità nelle pagine Razor (XSS, CSRF, segreti, CSP) | ✅ | `find_razor_vulnerabilities` — finding con CWE, severità e remediation ([docs/razor.md](docs/razor.md)) |
| Modifica di una pagina Razor | ✅ | `razor_edit` — attributo/direttiva/intervallo di righe, `expect` come guardia, ri-scan prima della scrittura, storico revertibile |
| Duplicate code detection | ✅ | `find_duplicate_code` |
| Complexity hotspots (cyclomatic complexity) | ✅ | `get_complexity_hotspots` |
| **Complessità computazionale e spaziale (Big-O)** | ✅ | `analyze_complexity` — stima per metodo del tempo e della memoria: annidamento e limiti dei loop, catene LINQ, ordinamenti, ricerche lineari nei loop, forma della ricorsione, siti di allocazione e metodi chiamati nella soluzione; evidenze, confidenza e suggerimenti di ottimizzazione |
| Code metrics (LOC, maintainability index, ecc.) | ✅ | `get_metrics` |
| Run Roslyn analyzers (StyleCop, Roslynator, ecc.) | ✅ | `run_code_analysis` |
| Ispezioni stile ReSharper con id/severità Rider | ✅ | `inspect_code` (pack: roslynator, netanalyzers, asyncfixer, meziantou, stylecop) |
| **Audit dell'intera soluzione in una chiamata** | ✅ | `solution_audit` — ispezioni raggruppate per id, errori/warning del compilatore, metodi più complessi, file più grandi, grafo progetti + cicli, prime mosse |
| **Applica la correzione di un'ispezione** | ✅ | `apply_code_fix` — esegue il CodeFixProvider del pack (dry run di default) |
| Analisi coupling tra namespace | ✅ | `analyze_namespace_coupling` |
| Circular dependency detection | ✅ | `analyze_circular_namespace_dependencies` |
| Analisi DI container | ✅ | `analyze_dependency_injection` |
| Dipendenze tra progetti | ✅ | `analyze_dependencies` |
| Architecture rules / layer enforcement | ✅ | `check_layer_violations` |
| LINQ anti-patterns | 🔧 | `find_performance_anti_patterns` (materializzazione dentro un loop) e le regole IDE0120/IDE0121 di `simplify_code`; nessun rule set completo |
| Nullable reference analysis | ✅ | `analyze_nullability` — `!` abusati, nullable disabilitate, flussi non gestiti |
| Data flow analysis | ❌ | 🎯 (il dataflow esiste solo dentro `simplify_code` per IDE0059) |
| Taint analysis (sicurezza) | ❌ | 🎯 SonarQube-style |
| Code churn / hotspot storico (git-aware) | ❌ | 🎯 CodeScene-style |
| Cognitive complexity | ✅ | `complexity_report` e `get_complexity_hotspots` — McCabe e Sonar cognitive, con le righe che producono il punteggio |
| API surface diff (breaking change detection) | ✅ | `find_breaking_changes` (confronto con un ref git) |

---

## 4. NuGet & Dipendenze

| Feature | Implementato | Note |
|---------|:---:|-------|
| Lista pacchetti installati | ✅ | `list_nuget_packages` |
| Aggiungi pacchetto | ✅ | `add_nuget_package` |
| Rimuovi pacchetto | ✅ | `remove_nuget_package` |
| Aggiorna pacchetto | ✅ | `update_nuget_package` |
| Controlla aggiornamenti disponibili | ✅ | `check_nuget_updates` |
| Controlla vulnerabilità CVE | ✅ | `check_nuget_vulnerabilities` |
| Fix automatico vulnerabilità | ✅ | `fix_nuget_vulnerabilities` |
| Unifica versioni conflittuali tra progetti | ✅ | `unify_nuget_version` |
| Localizza DLL nel cache NuGet locale | ✅ | `locate_nuget_package` |
| Ispeziona API pubblica di un pacchetto | ✅ | `inspect_package` |
| Gestione Central Package Management (CPM) | 🟡 | `update_solution` aggiorna le righe `PackageVersion` di `Directory.Packages.props`; il file non viene creato né migrato |
| License compliance check | ❌ | 🎯 |
| Dependency graph visuale | ❌ | 🎯 |
| Analisi transitive oversharing | ❌ | 🎯 |

---

## 5. Testing

| Feature | Implementato | Note |
|---------|:---:|-------|
| Esegui tutti i test (`dotnet test`) | ✅ | `run_tests` |
| Parse risultati TRX (pass/fail/skip) | ✅ | `run_tests` |
| Test coverage map | 🔧 | `get_test_coverage_map` — dipende da tool esterni |
| Genera test unitari | ❌ | 🎯 |
| Analisi test flaky | ❌ | 🎯 |
| Mutation testing | ❌ | 🎯 Stryker integration |
| Esegui test specifico per nome/filtro | 🔧 | tramite `extraArgs` in `run_tests` |
| Live test feedback (NCrunch-style) | ❌ | 💀 per MCP puro |
| Snapshot testing helpers | ❌ | ❌ |

---

## 6. Build & Esecuzione

| Feature | Implementato | Note |
|---------|:---:|-------|
| Build progetto/soluzione | ✅ | `build_project` |
| Run progetto | ✅ | `run_project` |
| Estrai errori da build log | ✅ | `extract_build_log_errors` |
| Esegui comando arbitrario | ✅ | `run_command` |
| Stop server (rilascia lock su DLL) | ✅ | `stop_server` |
| Info ambiente (.NET SDKs, VS, OS) | ✅ | `get_environment_info` |
| Workflow deterministico (DAG di passi, in parallelo) | ✅ | `run_plan` — passi `tool` (qualsiasi tool del server, per nome, con gli stessi nomi di argomento del suo schema) e `command` (shell); `dependsOn`, `paths` (due passi con path in comune non girano mai insieme), `forEach`/`as`, `continueOnError`, `onFailure`; il piano è validato **prima** di partire (id duplicato, tool o dipendenza inesistenti, ciclo → niente eseguito) e il report aggrega stato, durata, exit code, errore e output per passo |
| Fan-out su file/progetti senza subagenti | ✅ | `run_plan` con `forEach`: un passo concreto per item, `${item}` sostituito in comando, tool, `workingDirectory`, `paths` e argomenti stringa; una sola risposta invece di N chiamate |
| Isolamento del lavoro parallelo | 🔧 | `run_plan` accetta `workingDirectory` per passo: puntando a worktree git separati il lavoro è isolato; creare i worktree resta `git_run` |
| Hot reload / watch mode | ❌ | 🎯 `dotnet watch` integration |
| Multi-target build | ❌ | 🎯 |
| Publish (dotnet publish) | ❌ | 🎯 |

---

## 7. Decompilazione & Reflection

Tutta la decompilazione vive in **`SNITCH/Libraries/Snitch.Metacode/Decompilation/`**, namespace
`Snitch.Metacode.Decompilation`: ILSpy (`DecompileService`, `IDecompileService`,
`IMetacodeDecompileService`), il composito dei pacchetti (`PackageInspectionService`) e Ghidra
(`GhidraService`). `Snitch.csproj` non referenzia più `ICSharpCode.Decompiler`: il pacchetto è solo del
progetto che decompila. `ManagedAssemblyDetector` (PE con header CLR) sta lì per rifiutare un binario
nativo con una spiegazione invece di lasciare che ILSpy fallisca nella metadata.

| Feature | Implementato | Note |
|---------|:---:|-------|
| Decompila tipo da assembly | ✅ | `decompile_type` — C# **compattato** di default: senza indentazione, a capo e commenti, stessi token, circa metà caratteri (`compact=false` per la forma leggibile) |
| Decompila assembly intero | ✅ | `decompile_assembly` (file unico, anteprima, o **una cartella → un file per tipo** con `_index.txt`); l'anteprima è compattata, i file scritti restano leggibili e finiscono subito in `text_index` |
| Elenca/seleziona tipi di un assembly (anche interni) | ✅ | `decompile_assembly` con `listOnly`, `nameFilter`, `namespaceFilter`, paginato con `skip`/`maxTypes` |
| Decompila tipo da pacchetto NuGet | ✅ | `decompile_package_type` |
| Ispeziona API assembly senza decompilare | ✅ | `inspect_assembly` |
| Decompila in pseudocodice (metacode) | ✅ | `decompile_to_metacode` — ILSpy → C# → il modello metacode: un tipo o una pagina di tipi, con `hidden`/`errors` per tipo e i diagnostici (`RAW`) in coda |
| Analizza un binario nativo (Ghidra headless) | ✅ | `analyze_native_binary` — Ghidra headless recupera le funzioni e le restituisce in pseudocodice con prototipo e indirizzo; un solo `function` o `maxFunctions`; lo snapshot (`functions.json`) si rilegge con `metacode_translate` senza Ghidra |
| Visualizza IL (Intermediate Language) | ❌ | 🎯 |
| Diff decompilato tra due versioni | ❌ | 🎯 |

---

## 8. Documentazione

| Feature | Implementato | Note |
|---------|:---:|-------|
| Leggi XML doc comments | ✅ | `get_xml_docs` |
| Trova API pubbliche non documentate | ✅ | `find_undocumented_public_api` |
| Leggi documentazione progetto (README, ecc.) | ✅ | `read_project_docs` |
| Genera XML doc comments automaticamente | ❌ | 🎯 |
| Genera documentazione Markdown/HTML | ❌ | 🎯 (DocFX, Sandcastle-style) |
| Changelog generation da git history | ❌ | 🎯 |

---

## 9. Git & GitHub

| Feature | Implementato | Note |
|---------|:---:|-------|
| Git status | ✅ | `git_status` |
| Git log | ✅ | `git_log` |
| Git diff | ✅ | `git_diff` |
| Git stage | ✅ | `git_stage` |
| Git commit | ✅ | `git_commit` |
| Crea branch | ✅ | `git_branch` (list/create/delete/rename/switch) |
| Squash commit | ✅ | `git_squash` — commit-tree + reset --soft: funziona anche squashendo l'intero branch |
| Rinominare un commit | ✅ | `git_amend` (HEAD) |
| Merge | ✅ | `git_merge` — default/no-ff/ff-only/squash, abort, conflitti riportati |
| Rebase | ✅ | `git_rebase` — start/continue/abort/skip |
| Cherry-pick / revert | ✅ | `git_cherry_pick`, `git_revert` |
| Reset | ✅ | `git_reset` — soft/mixed/hard; hard richiede `confirm=true` |
| Git stash | ✅ | `git_stash` — push/list/pop/apply/drop/show/clear |
| Tag | ✅ | `git_tag` — list/create/delete, annotati con messaggio |
| Remote | ✅ | `git_remote` — list/add/remove/set-url |
| Fetch / pull / push | ✅ | `git_sync` — force con `--force-with-lease` dietro `confirm=true` |
| Git show | ✅ | `git_show` |
| Git blame | ✅ | `git_blame` (con range di righe) |
| Comando git arbitrario | ✅ | `git_run` — copre reflog, worktree, bisect, submodule, filter-repo |
| GitHub repo | ✅ | `gh_repo` — list/view/create/fork/clone/edit/delete |
| GitHub PR | ✅ | `gh_pr` — list/view/create/merge/close/reopen/comment/diff/checkout/ready |
| GitHub issue | ✅ | `gh_issue` — list/view/create/close/reopen/comment |
| GitHub release | ✅ | `gh_release` — list/view/create/upload/delete |
| GitHub Actions | ✅ | `gh_actions` — list/view/logs/rerun/cancel/workflows/dispatch |
| GitHub secrets | ✅ | `gh_secret` — list/set/delete |
| GitHub CLI passthrough | ✅ | `github` (gh command) |
| PR diff analysis (Roslyn-aware) | ❌ | 🎯 |
| Conventional commits enforcement | ❌ | 🎯 |

---

## 10. Benchmarking & Performance

| Feature | Implementato | Note |
|---------|:---:|-------|
| Esegui BenchmarkDotNet | ✅ | `run_benchmarks` |
| Quick benchmark di snippet inline | ✅ | `quick_benchmark` |
| Profiling CPU (dotTrace-style) | ✅ | `profile_collect` (EventPipe, preset cpu) + `profile_analyze` — hotspot per metodo con CPU ms, share, stack e `file:line`; nativo, nessun tool esterno |
| Profiling memoria (dotMemory-style) | ✅ | `profile_heap` (tipi, LOH, pinned, finalizzabili, oggetti più grandi, stringhe duplicate) + `profile_dump` (snapshot congelato, heap + stack) |
| Analisi allocazioni (heap) | ✅ | `profile_analyze` — allocation tick per tipo, byte campionati, share; **`profile_allocations`** attribuisce i byte al metodo che alloca, con tipo, `file:line` e stack completo (richiede preset `gc`: Verbose + stackwalk); regole `allocation-hotspot` / `allocation-site` / `loh-allocation` |
| Timeline performance | ✅ | `profile_analyze` — bucket da 1 s con CPU ms, allocazioni campionate, GC (gen0/1/2 e pause), eccezioni e contention; regole `cpu-spike` / `allocation-spike` |
| Call tree inclusive/exclusive | ✅ | `profile_calltree` — albero con tempo inclusivo/esclusivo per nodo, catena dei chiamanti, export folded-stacks per FlameGraph/speedscope |
| Hot path multipli (VS-style, più fix) | ✅ | `profile_hotpaths` — lista dei metodi/righe da correggere aggregata su tutti gli stack (`Variants`), path root-to-leaf con frame utente, punti di split (`Branches`) dove il costo si divide; metriche cpu / allocations / exceptions / mixed |
| Blocchi di I/O per sito | ✅ | `profile_io` — intervalli `External` attribuiti allo stack bloccante: categorie File/Http/Network/Database/Pipe/Console/SyncOverAsync/Lock/Sleep/WaitHandle, call site con `file:line`, ms bloccati, share, thread, stack; idle del thread pool escluso; regole `io-block`, `sync-over-async`, `thread-pool-blocking`, `thread-blocking`, `io-pressure` |
| Confronto e regressioni | ✅ | `profile_compare` — due trace (CPU per metodo, allocazioni per tipo, GC, eccezioni, contention, backlog) o due heap JSON; regole `cpu-regression`, `allocation-regression`, `heap-growth`; `profile_heap writeJson=true` |
| Diagnosi multi-segnale | ✅ | `profile_diagnose` — counters + trace + stack + heap in una chiamata, finding correlati (`cpu-bound`, `gc-bound`, `pool-starvation`) e report unico |
| Verdetto "perché è lento" | ✅ | `profile_slowdown` — cpu/gc/io-wait/sync-over-async/lock-wait/blocked-native pesati nella stessa unità (secondi della finestra spiegati), un verdetto, prove ordinate e i candidati che hanno perso con il motivo; da processo vivo (counters + trace `diagnose` + stack) o da una traccia esistente |
| Verdetto "perché la memoria cresce" | ✅ | `profile_memory_growth` — growth/shrink/stable/single-snapshot; confronto di due heap JSON (`profile_heap writeJson=true`) o snapshot di un processo vivo che scrive il JSON per la volta dopo, con il tipo cresciuto correlato al sito di allocazione (`file:line`) quando c'è una traccia `gc` |
| Verdetto "il thread pool è affamato" | ✅ | `profile_threadpool` — starved/at-risk/burst/healthy/inconclusive; richiede insieme coda piena **e** thread bloccati, nomina il sito bloccante con `file:line`; counters + traccia `runtime` + stack |
| Verdetto "questa modifica è una regressione" | ✅ | `profile_review_change` — regression/improvement/unchanged/inconclusive su tassi normalizzati al secondo (CPU ms/s, allocazioni MB/s, eccezioni/s, …), soglie di significatività per metrica; è `inconclusive` solo quando le due finestre sono sotto 500 ms, mentre con meno di 100 campioni CPU la metrica CPU non entra nel giudizio (il verdetto può comunque venire dalle altre) |
| EventCounters live (dotnet-counters) | ✅ | `profile_counters` — serie temporali native con min/max/mean/last |
| Stack managed + lock contesi (dotnet-stack) | ✅ | `profile_stacks` — ogni thread, monitor con thread proprietario, regole di blocco |
| Dump collect/analyze (dotnet-dump) | ✅ | `profile_dump` — `WriteDump` + analisi ClrMD (heap, stack, sync block) |
| Installer della toolchain diagnostica | ✅ | `profile_tools` — stato/install/update dei 7 tool, con copertura nativa vs delegata |
| Simboli (dotnet-symbol) | ✅ | `profile_symbols` — CodeView, download dal symbol server, cache per la risoluzione `file:line` |
| dotnet-monitor (sidecar HTTP) | ✅ | `profile_monitor` — start/stop/probe su loopback (`--no-auth`) |

> Profiling introdotto il 2026-10-29 nella libreria separata `SNITCH/Libraries/Snitch.Profiling`
> (EventPipe + ClrMD + TraceEvent); documentazione in `docs/profiling.md`. Lo stesso giorno è stato
> aggiunto il livello avanzato: attribution delle allocazioni a sito/riga, call tree, timeline,
> confronto/regressione e diagnosi correlata. Il livello di risposta (`Verdicts/`: `ISlowdownService`,
> `IMemoryGrowthService`, `IThreadPoolStarvationService`, `IChangeReviewService`, con la busta comune
> `AnswerReport`) è esposto dai quattro verdetti qui sopra: i tool `profile_*` esistenti restano il
> modo per misurare un singolo segnale.

---

## 10bis. Piattaforma & manutenzione

| Feature | Implementato | Note |
|---------|:---:|-------|
| Info sul sistema | ✅ | `host_system` — famiglia OS (windows/linux/macos), architettura, a capo predefinito, case-sensitivity dei path |
| Stile a capo di un file | ✅ | `host_system path=…` — file esistente → `.editorconfig` → `.gitattributes` → file vicini → OS |
| Normalizzazione a capo | ✅ | `fix_line_endings` — scope file/directory/progetto/soluzione, `auto`/`lf`/`crlf`, anteprima |
| Scrittura con stile corretto | ✅ | tutti i writer passano da `FileText`: un refactoring non cambia più la convenzione del file, e i file nuovi seguono quella del repository |
| Log dei comandi | ✅ | tabella `command_log` nel DB SQLite (data, ora, tool, argomenti) scritta a ogni chiamata; si legge con `command_log` |

---

## 11. Comunicazione Runtime & Infrastruttura

| Feature | Implementato | Note |
|---------|:---:|-------|
| HTTP invocation (REST client) | ✅ | `http_invoke` |
| SignalR connect / disconnect | ✅ | `signal_r_connect` / `signal_r_disconnect` |
| SignalR subscribe a eventi hub | ✅ | `signal_r_subscribe` |
| SignalR invoke metodo hub | ✅ | `signal_r_invoke` |
| SignalR leggi eventi bufferizzati | ✅ | `signal_r_events` |
| Docker status + container list | ✅ | `docker_status`, `docker_ps` |
| Docker container: config e log | ✅ | `docker_inspect` (JSON completo), `docker_logs` (tail, timestamps, since) |
| Docker lifecycle | ✅ | `docker_control` (start/stop/restart/pause/unpause/kill), `docker_rm` (force, volumes) |
| Docker run | ✅ | `docker_run` — nome, porte, env, volumi, rete, comando, detach |
| Docker immagini | ✅ | `docker_images` (lista/ispezione), `docker_pull`, `docker_image_rm` |
| Docker reti / volumi / stats | ✅ | `docker_networks`, `docker_volumes`, `docker_stats` |
| Docker compose up/down/ps/logs | ✅ | `docker_compose` |
| Docker prune | ✅ | `docker_prune` — richiede `confirm=true` |
| Kubernetes integration | ❌ | ❌ (fuori scope) |
| gRPC testing | ❌ | 🎯 |
| WebSocket testing | ❌ | 🎯 |

---

## 12. File System & I/O

| Feature | Implementato | Note |
|---------|:---:|-------|
| Carica soluzione (.sln/.slnx) | ✅ | `load_solution` |
| Lista soluzioni nella directory | ✅ | `list_solutions` |
| Lista directory (con glob) | ✅ | `list_directory` |
| Albero directory a profondità N (prune bin/obj/.git) | ✅ | `read_tree` |
| Leggi file (con offset e limite righe) | ✅ | `read_file` |
| Leggi più file in una chiamata (budget di output) | ✅ | `read_files` |
| Sposta / rinomina path | ✅ | `move_path` |
| Info progetto (framework, output, packages) | ✅ | `get_project_info` |
| Valida struttura soluzione | ✅ | `check_solution` |
| Crea file da template | 🔧 | tramite `run_command` |
| Cancella file/cartella | ❌ | 🎯 |
| Watch file system (live changes) | ❌ | 💀 per MCP puro |

---

## 13. Scaffolding & Project Setup

| Feature | Implementato | Note |
|---------|:---:|-------|
| Aggiungi LICENSE | ✅ | `add_license` — 20 licenze (permissive, copyleft, public-domain, source-available) da template embedded; `list_licenses` per il catalogo con id SPDX, famiglia e brevetto |
| Aggiungi CONTRIBUTING.md | ✅ | `add_contributing` |
| Aggiungi GitHub Actions CI workflow | ✅ | `add_ci_workflow` |
| Aggiungi/rimuovi progetti a soluzione | ✅ | `add_projects_to_solution` / `remove_projects_from_solution` |
| Crea nuovo progetto (`dotnet new`) | 🟡 | `aspire(action='add')` crea AppHost e ServiceDefaults dai template Aspire; gli altri template restano da `run_command` |
| Scaffold CRUD da modello | ❌ | 🎯 |
| Scaffold EF Core migration | ❌ | 🎯 |
| `.editorconfig` / `global.json` template | ❌ | 🎯 |
| `.gitignore` template | ❌ | 🎯 |

---

## 14. Pianificazione

| Feature | Implementato | Note |
|---------|:---:|-------|
| Crea task checklist | ✅ | `create_task_checklist` |
| Aggiorna task checklist | ✅ | `update_task_checklist` |
| Kanban board (SQLite) | ✅ | `board_add`/`board_update`/`board_move`/`board_comment`/`board_list`/`board_show`/`board_board`/`board_delete` — 5 colonne, priorità, label, assegnatario, commenti, storia delle modifiche |
| Pianifica feature change (analisi impatto) | ✅ | `plan_feature_change` |

---

## 15. Self-Management

| Feature | Implementato | Note |
|---------|:---:|-------|
| Controlla aggiornamento disponibile | ✅ | `check_for_update` |
| Self-update da GitHub Releases | ✅ | `self_update` |
| Download file da URL | ✅ | `download_file` |
| Lista tool di analisi disponibili | ✅ | `list_analysis_tools` |

---

## 16. Modernizzazione della soluzione (2026-10-30)

| Feature | Implementato | Note |
|---------|:---:|-------|
| Installa Aspire su richiesta (template/workload) | ✅ | `aspire` — `dotnet new list aspire` e `dotnet workload list` per la diagnosi; `dotnet new install Aspire.ProjectTemplates` senza elevazione; il workload solo su SDK 8 o su richiesta |
| Scaffold Aspire nella soluzione | ✅ | `aspire(action='add')` — AppHost + ServiceDefaults, `dotnet sln add`, riferimenti dai progetti eseguibili, `AddProject<T>` e `AddServiceDefaults()`/`MapDefaultEndpoints()`, poi build dell'AppHost con rollback dei soli `Program.cs` |
| Aggiorna il target framework | ✅ | `update_framework` — cambia solo il valore fra i tag (nessuna riscrittura XML), mantiene i suffissi di piattaforma, non tocca `netstandard`/`net4x`, sincronizza `global.json`, anteprima di default |
| Aggiorna i pacchetti della soluzione | ✅ | `update_solution` — `dotnet list package --outdated`, versioni scritte nel csproj o nelle righe `PackageVersion` di `Directory.Packages.props`, un solo gate di build |
| Gate del compilatore con rollback totale | ✅ | `update_framework`/`update_solution` ricostruiscono la soluzione e, se la build fallisce, riscrivono ogni file modificato com'era |
| Scelta del TFM dagli SDK installati | ✅ | `auto` → `dotnet --list-sdks` → la banda più recente (`net10.0`) |

---

## Riepilogo per Categoria

Conteggio: una riga ✅ e una riga ❌ per ciascuna tabella di sezione; le righe 🔧 (parziali) e 💀
(fuori scope) non entrano nel totale, e ogni riga qui sotto è il conteggio della sua sezione.

| Categoria | Implementati | Mancanti | % Copertura |
|-----------|:---:|:---:|:---:|
| Navigazione & Ricerca | 9 | 4 | 69% |
| Refactoring | 38 | 4 | 90% |
| Code Analysis | 22 | 7 | 76% |
| NuGet & Dipendenze | 10 | 3 | 77% |
| Testing | 2 | 5 | 29% |
| Build & Esecuzione | 8 | 3 | 73% |
| Decompilazione | 7 | 2 | 78% |
| Documentazione | 3 | 3 | 50% |
| Git & GitHub | 26 | 2 | 93% |
| Benchmarking | 21 | 0 | 100% |
| Piattaforma & manutenzione | 5 | 0 | 100% |
| Runtime & Infrastruttura | 13 | 3 | 81% |
| File System & I/O | 9 | 2 | 82% |
| Scaffolding | 4 | 4 | 50% |
| Pianificazione | 4 | 0 | 100% |
| Self-Management | 4 | 0 | 100% |
| Metacode (pseudocodice) | 12 | 0 | 100% |
| Modernizzazione & upgrade | 6 | 0 | 100% |
| **TOTALE** | **202** | **42** | **83%** |

Le tabelle delle sezioni non contengono ancora i **14 tool aggiunti il 2026-10-30** — SSR, cleanup e
formattazione, famiglia "create" (`implement_members`, `create_from_usage`, `generate_docs`), baseline
delle ispezioni e le sette analisi (layer, export del grafo, LCOM4, test quality, validazione,
nullability, breaking changes): il totale effettivo è **219 implementati**.

---

## Gap Prioritari vs ReSharper

Aggiornato il 2026-10-30. Le quattro voci che stavano qui sono state chiuse: `structural_search`
(SSR), `check_layer_violations` (layer enforcement), `generate_docs` (XML doc) e `convert_to_record`
(già implementato, era rimasto elencato per errore). Nella stessa sessione sono arrivati anche
`code_cleanup`, `format_code`, `implement_members`, `create_from_usage`, `inspection_baseline`,
`export_dependency_graph`, `get_cohesion_metrics`, `analyze_test_quality`,
`find_missing_validation`, `analyze_nullability` e `find_breaking_changes`.

Restano, in ordine di impatto:

| Feature ReSharper / Rider | Priorità | Stato |
|---------------------------|:---:|-------|
| Debugger interattivo (breakpoint, stepping, watch, evaluate, debug di un test) | 🔴 Alta | ❌ assente; `profile_dump` / `profile_stacks` coprono solo lo snapshot |
| Ambito linguaggi/piattaforme: **XAML/WPF/WinUI/MAUI/Avalonia**, **JS/TS/HTML/CSS**, **SQL/EF Core migrations**, **Kubernetes/Helm**, **F#** | 🟡 Media | ❌ fuori dal perimetro C#: al massimo indicizzati come testo (`text_index`), nessun parser né analisi semantica. **VB** ha solo `simplify_vb_code` (15 id, 34 rifiutati con motivo): niente refactoring, ispezioni dei pack, formatter, symbol index o metacode |
| Flusso di test: debug di un test, continuous testing, "quali test coprono questo simbolo", scaffolding, mutation testing | 🟡 Media | ⚠️ `run_tests` + `get_test_coverage_map` coprono esecuzione e copertura per classe/metodo |
| Codice decompilato come workspace (go-to-definition, rename, find usages) e vista IL | 🟡 Media | ❌ `decompile_*` restituisce testo; `find_assembly_api` è il sostituto per cercare nell'API |
| Diagrammi ed esplorazione: grafo tipi/progetti interattivo, sequence diagram, structure view, code vision, TODO explorer | 🟢 Bassa | ⚠️ `export_dependency_graph` esporta DOT/Mermaid; il resto è assente |
| Resto della famiglia "create/generate" e context action: long tail (if→switch, pattern merging, LINQ shape, loop, interpolazione, nullable migration) e template live/postfix/file | 🟡 Media | ⚠️ `context_action` copre 13 id (import_type, guardie, wrap in try/catch/finally/if/using, string_to_raw/verbatim/regular); `implement_members`, `create_from_usage`, `generate_docs` coprono i casi frequenti |
| Amministrazione delle ispezioni (severità per scope, soppressioni, `.DotSettings`, daemon con compilazione calda) | 🟡 Media | ⚠️ `inspection_baseline` dà il gate CI, non la configurazione |
| Local history, merge assistito a 3 vie, GitLab/Azure DevOps | 🟢 Bassa | ⚠️ `local_history` copre la cronologia per file (`snapshot`/`history`/`diff`/`restore`); restano merge a 3 vie e gli altri host Git |
| Refactoring minori: Extract Members to Partial, Inline Field, Transform Parameters, Extension → static, Invert Boolean, Make non-static, Move Type into Another Type, Generify | 🟢 Bassa | ❌ |

L'inventario completo — linguaggio per linguaggio e area per area, con cosa esiste oggi, cosa manca
esattamente e il workaround — è in **[docs/gaps.md](docs/gaps.md)**. La fotografia ✅ categoria per
categoria resta in [docs/resharper-codemaid-coverage.md](docs/resharper-codemaid-coverage.md).

## Gap Prioritari vs SonarQube/CodeScene

| Feature | Priorità | Note |
|---------|:---:|-------|
| Taint analysis | 🟡 Media | SQL injection / XSS tracciato sul data flow; `find_security_issues` è pattern-based |
| Code churn hotspot (git-aware) | 🟡 Media | CodeScene killer feature |
| Mutation testing (Stryker) | 🟡 Media | Quality gate reale |

---

*Ultimo aggiornamento: 2026-10-30*
