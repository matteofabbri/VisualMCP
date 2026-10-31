# Snitch.Metacode — un solo pseudocodice per alberi di lingue diverse

`SNITCH/Libraries/Snitch.Metacode` trasforma un albero sintattico in **un unico modello** e lo stampa
come pseudocodice canonico. La pipeline del prodotto legge **C#** (Roslyn) e **Razor**
(`.cshtml`/`.razor`), più i due formati della libreria stessa: il testo canonico e lo snapshot di
Ghidra. Un tipo decompilato con ILSpy e un binario nativo entrano dalla stessa porta.

Il modello è neutro per costruzione, e due lettori **opt-in** lo dimostrano: Python (dump JSON del
modulo `ast`) e C++ (`clang -ast-dump=json`). Non stanno in `MetacodeTranslator.Default` perché quel
traduttore legge un sorgente, non un dump: i tool `translate_python_file` / `translate_cpp_file`
producono il dump e costruiscono un traduttore con il lettore che lo capisce. Lo stesso algoritmo
scritto in C#, Python e C++ esce comunque con lo stesso
corpo, riga per riga — è il test `The_same_algorithm_prints_the_same_body_from_csharp_python_and_cpp`,
eseguito con quei lettori.

La libreria non usa il modello semantico: legge la sintassi. Un file C# costa un parse, non una
compilazione, e non serve alcuna soluzione caricata. L'unica eccezione è l'[indice dei
simboli](#lindice-dei-simboli): "chi usa questo membro, e come" non si risponde leggendo la sintassi, e
l'indice è una capacità a sé che vive qui accanto al traduttore.

## L'architettura in una riga

```
albero (Roslyn | testo pseudocodice | snapshot Ghidra)
    -> frontend          (uno per linguaggio, produce solo nodi del modello)
    -> IMetacodePass     (normalizzazione: differenze di lingua eliminate qui)
    -> MetacodeDocument  (il modello unico, con origine e diagnostici)
    -> PseudocodeRenderer (testo canonico)  <->  PseudocodeFrontend (lo rilegge)
```

I lettori opt-in (`PythonFrontend`, `CppFrontend`) entrano esattamente nello stesso punto: sono
`ILanguageFrontend` come gli altri, semplicemente fuori dalla lista predefinita.

Un binario entra dalla stessa porta: ILSpy produce C# e Ghidra produce C, ma il modello che ne esce è
quello di sempre (vedi [Decompilazione](#decompilazione-da-un-binario-a-metacode)).

Ogni strato parla solo con quello dopo. Aggiungere una lingua significa aggiungere un
`ILanguageFrontend` alla lista di `MetacodeTranslator.Frontends`: passate, renderer e parser non si
toccano.

## Il modello

Il vocabolario è volutamente più piccolo di qualunque linguaggio (un costrutto che non ci sta viene
*desugared* o dichiarato opaco, non inventato):

| Gruppo | Tipi |
|---|---|
| Contenitori | `MetacodeDocument`, `MetacodeModule`, `MetacodeTypeDeclaration`, `MetacodeFunction` |
| Membri | `MetacodeField`, `MetacodeProperty`/`MetacodeAccessor`, `MetacodeEnumMember`, `MetacodeEvent`, `MetacodeTypeAlias`, `MetacodeParameter` |
| Istruzioni | `MetacodeBlock`, `MetacodeLocalVariable`, `MetacodeAssignment`, `MetacodeIf`, `MetacodeWhile`, `MetacodeDoWhile`, `MetacodeFor`, `MetacodeForEach`, `MetacodeSwitch`, `MetacodeTry`, `MetacodeUsing`, `MetacodeLock`, `MetacodeYield`, `MetacodeImport`, `MetacodeReturn`, `MetacodeThrow`, `MetacodeBreak`/`Continue`/`Goto`/`Label`, `MetacodeLocalFunction`, `MetacodeComment` |
| Espressioni | `MetacodeLiteral`, `MetacodeIdentifier`, `MetacodeMemberAccess`, `MetacodeInvocation`, `MetacodeBinary`, `MetacodeUnary`, `MetacodeConditional`, `MetacodeIndex`, `MetacodeObjectCreation`, `MetacodeCast`, `MetacodeTypeCheck`, `MetacodeCollection`, `MetacodeDictionary`, `MetacodeComprehension`, `MetacodeLambda`, `MetacodeAwait`, `MetacodeRange`, `MetacodeSwitchExpression`, `MetacodeInterpolatedString` |
| Onestà | `MetacodeOpaqueStatement`, `MetacodeOpaqueExpression`, `MetacodeOpaqueDeclaration`, `MetacodeDiagnostic` |

Ogni nodo porta `Origin` (linguaggio, file, riga, colonna): il pseudocodice resta tracciabile fino
alla riga originale.

**Un costrutto non supportato non sparisce mai.** Diventa un nodo `RAW[cs] … // motivo` e un
diagnostico `MC0002`–`MC0004`: la traduzione incompleta si vede, invece di sembrare completa.

### I costrutti che non sono più opachi

Il vocabolario è cresciuto dove una lingua vera lo chiedeva, e ogni aggiunta porta il suo test
(`MetacodeDepthTests`):

| Costrutto | Nodo | Da dove |
|---|---|---|
| `lock (gate) { … }` | `MetacodeLock` | C#. Non è un `USING`: protegge l'assenza di altri thread, non una risorsa |
| `yield return x` / `yield break` | `MetacodeYield` (valore `null` = break) | C# |
| `event` | `MetacodeEvent` | C#; solo la firma, gli accessor sono la convenzione della lingua |
| `foreach (var (k, v) in xs)` | `MetacodeForEach` con `VariableName = "(k, v)"` | C# |
| `await foreach` | `MetacodeForEach.IsAwait` | C#; senza il flag era una perdita silenziosa |
| `import os` / `from typing import List` | `MetacodeImport` (+ `MetacodeImportName`) | Python |
| `@decoratore` | `MetacodeFunction.Decorators` / `MetacodeTypeDeclaration.Decorators` | Python; il testo del decoratore, in ordine di sorgente |
| `[x for x in xs if p]`, `{…}`, `{k: v …}`, `(… )` | `MetacodeComprehension` (+ `MetacodeComprehensionClause`) | Python |
| parametri di template | `MetacodeTypeParameter` su funzione e tipo | C++ (`FunctionTemplateDecl`, `ClassTemplateDecl`) |
| `for (int x : xs)` | `MetacodeForEach` | C++ (`CXXForRangeStmt`, letto per ruolo, non per posizione) |
| lambda | `MetacodeLambda` | C++ (`LambdaExpr`, firma dal `operator()` della closure) |
| `using Count = int;` / `typedef` | `MetacodeTypeAlias` | C++ (gli alias impliciti del compilatore sono saltati) |

I tipi di parametro di un template sono gli stessi `MetacodeTypeParameter` che il parser del
pseudocodice già rileggeva in `<T>`: non serviva un nodo nuovo, serviva riempirlo.

## Esempio: lo stesso corpo in tre lingue

```csharp
int Sum(int n)
{
    var total = 0;
    for (var i = 0; i < n; i++)
        if (i % 2 == 0) total += i;
    return total;
}
```
```python
def sum(n):
    total = 0
    for i in range(n):
        if i % 2 == 0:
            total += i
    return total
```
```cpp
int sum(int n) {
    int total = 0;
    for (int i = 0; i < n; i++)
        if (i % 2 == 0) total += i;
    return total;
}
```

Tutti e tre diventano:

```
METHOD Sum(n : int) : int        (C#)
FUNCTION sum(n)                  (Python)
FUNCTION sum(n : int) : int      (C++)

  DECLARE total = 0
  DECLARE i = 0
  WHILE i < n DO
    IF i % 2 == 0 THEN
      total += i
    END IF
    i += 1
  END WHILE
  RETURN total
```

Il `for` conta ed è diverso dal `while` solo perché `NormalizeControlFlowPass` lo riscrive — e non lo
riscrive se nel corpo c'è un `continue`, perché l'incremento verrebbe saltato. Python
`for i in range(n)` diventa lo **stesso** nodo `MetacodeFor` del `for` C#, quindi il risultato è
identico senza casi speciali nel renderer.

## Usarla

```csharp
using Snitch.Metacode;

// Da un albero Roslyn già in memoria (nessuna compilazione).
MetacodeDocument document = MetacodeTranslator.Default.Translate(
    MetacodeSource.FromSyntaxTree(tree));

// Da sorgente C#.
string pseudocode = MetacodeTranslator.Default.Render(
    MetacodeSource.FromCSharp(source, "Sample.cs"));

// Da un dump JSON di CPython: il lettore non è nella pipeline predefinita, quindi si nomina.
var python = new MetacodeTranslator(new ILanguageFrontend[] { new PythonFrontend() })
    .Render(MetacodeSource.FromPythonAst(json, "sample.py"));

// Da clang -ast-dump=json, stesso discorso.
var cpp = new MetacodeTranslator(new ILanguageFrontend[] { new CppFrontend() })
    .Render(MetacodeSource.FromCppAst(json, "sample.cpp"));

// Da una pagina Razor o un componente Blazor.
var razor = MetacodeTranslator.Default.Render(
    MetacodeSource.FromRazor(File.ReadAllText("Index.cshtml"), "Index.cshtml"));

// Da pseudocodice già scritto (o prodotto da un altro tool).
var parsed = MetacodeSource.FromPseudocode(text);
```

Opzioni (tutte con un default sensato):

```csharp
var options = MetacodeOptions.Default with
{
    NormalizeControlFlow = true,   // for -> while
    FlattenNestedBlocks = true,    // blocco dentro blocco -> blocco
    IncludeComments = true,        // commenti visti dal frontend
    Passes = new IMetacodePass[] { new MyPass() },
};
```

## I lettori opt-in: Python e C++

Questi due lettori non stanno in `MetacodeTranslator.Default`, che resta la pipeline dei sorgenti che il
traduttore legge direttamente: `metacode_translate` non offre le lingue (`language` vale `cs`, `razor`,
`ghidra` o `pseudo`) e `MetacodeSource.FromFile` non le deduce dall'estensione — un `.json` non dice se
contiene un `ast` di CPython, un dump di clang o altro. Il dump lo producono i due tool dedicati, che
poi costruiscono il traduttore con il lettore giusto: `translate_python_file` esegue il dumper qui sotto,
`translate_cpp_file` esegue `clang -ast-dump=json`; entrambi accettano anche un dump già pronto
(`dumpJson`). Il dumper Python, se lo si vuole lanciare a mano — un file, nessuna dipendenza:

```python
import ast, json, sys

def dump(node):
    if isinstance(node, ast.AST):
        out = {"_type": type(node).__name__}
        for field in node._fields:
            out[field] = dump(getattr(node, field, None))
        if hasattr(node, "lineno"):
            out["lineno"], out["col_offset"] = node.lineno, node.col_offset
            out["end_lineno"], out["end_col_offset"] = node.end_lineno, node.end_col_offset
        return out
    if isinstance(node, list):
        return [dump(item) for item in node]
    return node

source = open(sys.argv[1], encoding="utf-8").read()
print(json.dumps(dump(ast.parse(source, filename=sys.argv[1])), indent=2))
```

C++:

```bat
clang++ -Xclang -ast-dump=json -fsyntax-only sample.cpp > sample.json
```

`MetacodeSource.FromFile` sceglie il lettore dall'estensione (`.cs`, `.cshtml`/`.razor`,
`.mc`/`.pseudo`) e per un `.json` riconosce solo uno snapshot di Ghidra (`imageBase` + `functions`).
Un dump Python o clang si passa da `FromPythonAst`/`FromCppAst` a un `MetacodeTranslator` costruito
con `PythonFrontend`/`CppFrontend`.

## Il testo canonico

Le parole chiave sono maiuscole e una istruzione sta su una riga:

```
MODULE nome
NAMESPACE nome
CLASS | STRUCT | INTERFACE | ENUM | RECORD | UNION | TRAIT [<parametri>] [EXTENDS …]
FUNCTION | METHOD | CONSTRUCTOR | DESTRUCTOR | OPERATOR | LOCAL FUNCTION [<parametri>]
PROPERTY, GET/SET (AUTO oppure blocco), FIELD, CONST, EVENT, ALIAS
@decoratore                 (una riga per decoratore, sopra la dichiarazione)
DECLARE x : tipo = valore, CONST x : tipo = valore
IF … THEN / ELSE IF … THEN / ELSE / END IF
WHILE … DO / END WHILE       REPEAT / UNTIL …
FOR init; condizione; incremento DO / END FOR
FOREACH x [: tipo] IN espressione DO / END FOREACH
AWAIT FOREACH x [: tipo] IN espressione DO / END FOREACH
SWITCH / CASE a, b / DEFAULT / END SWITCH
TRY / CATCH [(Tipo nome)] [WHEN condizione] / ELSE / FINALLY / END TRY
USING x : tipo = expr DO / END USING
LOCK espressione DO / END LOCK
YIELD RETURN espressione, YIELD BREAK
IMPORT a, b AS c            FROM modulo IMPORT a, b AS c
RETURN expr, THROW expr, BREAK, CONTINUE, GOTO label, LABEL nome
RAW[cs] testo // motivo
// commento
```

Le comprensioni stanno dentro le parentesi del contenitore che costruiscono, con le clausole in
ordine di sorgente — è l'ordine che dà senso a una comprensione annidata:

```
[x * 2 FOR x IN xs IF x > 0]
{k : v FOR k, v IN pairs}
{n FOR n IN xs}
(n FOR n IN xs)
```

Operatori, dal più debole: `? :`, `??`, `OR`, `AND`, `|`, `^`, `&`, `== != IN NOT IN`,
`< <= > >= IS IS NOT AS`, `<< >>`, `+ -`, `* / % DIV`, `**`, prefissi `NOT - + ~ AWAIT NEW CAST`,
postfissi `. ?. () []`.

`PseudocodeFrontend` rilegge questo testo; il test `The_rendered_pseudocode_reads_back_to_the_same_text`
verifica che il giro completo non cambi un carattere, e `MetacodeDepthTests` lo verifica per ogni
costrutto aggiunto — compresa la lambda con corpo a blocco, che il renderer stampa su più righe e il
parser ricompone: quando una riga lascia una parentesi o una graffa aperta, le righe successive
appartengono alla stessa istruzione, e il corpo fra graffe viene riletto dal lettore di istruzioni
(passato al parser delle espressioni) invece che dalla grammatica delle espressioni, dove non
avrebbe posto. È la porta per qualunque linguaggio per cui non esiste un frontend: basta produrre
questo testo.

## Aggiungere un linguaggio

1. Un `ILanguageFrontend` con `CanHandle` e `Translate` (vedi `CSharpFrontend` come riferimento: è
   il più corto perché Roslyn fa il parsing).
2. Mappare ogni costrutto su un nodo del modello; ciò che non ha nodo diventa
   `MetacodeOpaque*` + `MetacodeDiagnostic`, con il testo originale.
3. Aggiungerlo a `MetacodeTranslator.Default` **solo se il traduttore può leggere il sorgente
   direttamente**: è la regola per cui C# e Razor sono lì e Python/C++ no. Un lettore che dipende da un
   dump prodotto altrove si passa al costruttore (opt-in), com'è per `PythonFrontend` e `CppFrontend`;
   se il prodotto sa produrre quel dump (come `translate_python_file` / `translate_cpp_file`), il tool
   costruisce il traduttore con il lettore giusto e resta opt-in.
4. Aggiungere una fixture al test `The_same_algorithm_prints_the_same_body_from_csharp_python_and_cpp`:
   è la definizione operativa di "stessa architettura".

## Limiti noti (dichiarati, non silenziosi)

- **C++** (lettore opt-in): `goto` resta opaco, così come `using`/`using namespace`, `friend`,
  `static_assert`, le specializzazioni parziali di un template, `delete`, `sizeof` e le espressioni che
  il modello non ha (`StmtExpr`, `noexcept`, …): tutti `RAW[cpp]` con un diagnostico. Le catture di una
  lambda non sono modellate: il corpo sì, lo stato catturato no. Il preprocessore non esiste nel dump,
  e un `#include` non lascia traccia se non nelle dichiarazioni che porta.
- **Python** (lettore opt-in): `match`, `assert`, `global`/`nonlocal`, `del`, `yield` e gli argomenti
  sparsi (`*xs`, `**kw`) restano opachi; i decoratori sono letti come testo, non come chiamate.
  `from . import x` tiene i punti nel nome del modulo.
- **C#**: `fixed`, `unsafe`, `checked`, i `delegate`, le conversioni definite dall'utente e
  `goto case`/`goto default` sono opachi; i pattern complessi di `switch`
  (`case int n when n > 0`) restano testo dentro `CASE`.
- **Razor**: `@switch`, `@try`/`catch`/`finally` e `@do`/`while` restano **un solo** `RAW[razor]` con un
  diagnostico — le loro `case`/`catch`/`while` non sono blocchi `@`, quindi lo scanner non le vede come
  rami; un corpo di `@if`/`@foreach` senza graffe è markup, non un blocco. Il markup fra due costrutti è
  `RAW[razor]` (un segmento per riga, con i ritorni a capo come `\n`) e le direttive sono
  `MetacodeOpaqueDeclaration`: una pagina non è un algoritmo, e fingere che lo sia sarebbe peggio che
  dirlo.
- **Parser del pseudocodice**: una lambda con corpo a blocco è riletta, e le righe che seguono una
  parentesi o una graffa aperta appartengono alla stessa istruzione. Due cose restano fuori: una riga
  `RAW[…]` con parentesi sbilanciate non viene ricomposta (la riga `RAW` è sempre una riga sola), e un
  commento con parentesi sbilanciate dentro il corpo di una lambda non viene visto come commento
  durante la ricomposizione.
- **Commenti**: il C# li cattura dal trivia; il dump Python e quello clang non li contengono.
- **Decompilazione .NET**: il C# di ILSpy contiene costrutti che il frontend C# non modella — attributi
  (scartati: non sono nel modello) e `x with { … }` (che resta `RAW[cs]` con un diagnostico). I tipi
  generati dal compilatore sono esclusi dalla selezione.
- **Ghidra**: l'operatore virgola del C resta `RAW[cpp]`, i puntatori a più livelli si riducono a uno,
  le convenzioni di chiamata e i commenti del decompilatore si perdono, e il testo canonico non porta
  l'origine della singola istruzione (la funzione sì).

## Decompilazione: da un binario a metacode

La decompilazione è entrata nella libreria come **un altro modo di produrre un `MetacodeDocument`**, non
come una capacità separata: il C# che ILSpy ricostruisce viene letto dal `CSharpFrontend` che già
esisteva, quindi un tipo decompilato ha la stessa forma di un tipo letto da sorgente — namespace, tipi,
membri, corpi, `Origin` su ogni nodo. `Snitch` non può referenziare `Snitch.Metacode` (è il contrario),
per cui l'implementazione vive qui; il namespace pubblico `Snitch.Languages.CSharp.Decompilation` non è
cambiato, così i chiamanti esistenti compilano senza modifiche.

```
assembly .dll ──ILSpy──▶ C# ──CSharpFrontend──▶ MetacodeDocument ──renderer──▶ pseudocodice
binario nativo ──Ghidra headless──▶ C decompilato ──normalizzazione──▶ testo canonico ──PseudocodeFrontend──▶ MetacodeDocument
```

### Due contratti, una implementazione

`DecompileService` implementa entrambe le metà, e `AddSnitchMetacode()` registra l'una accanto all'altra:

| Contratto | Cosa restituisce |
|---|---|
| `IDecompileService` | il testo C# di prima, con le tre chiamate di sempre (`DecompileType`, `DecompileAssembly`, `DecompileTypes`) |
| `IMetacodeDecompileService` | `MetacodeDecompilation`: tipi decompilati con il loro pseudocodice, il `MetacodeDocument` unito, i diagnostici, i tipi nascosti e gli errori |

```csharp
using Snitch.Languages.CSharp.Decompilation;
using Microsoft.Extensions.DependencyInjection;

services.AddSnitchMetacode();   // dopo AddSnitch()

var result = service.DecompileTypeToMetacode(assemblyPath, "Newtonsoft.Json.JsonConvert");
result.Pseudocode;              // il tipo in pseudocodice canonico
result.Document;                // lo stesso albero, con l'origine di ogni nodo
result.Types[0].Members;        // quanti membri ha visto il modello
```

Ogni tipo è decompilato **da solo** e tradotto da solo: un tipo che il modello non sa leggere costa
quel tipo e non l'assembly. I tipi generati dal compilatore (`<>c`, `<PrivateImplementationDetails>`) non
entrano nella selezione; un tipo che ILSpy non riesce a stampare finisce in `Hidden`, uno che fallisce
in `Errors` — mai un'eccezione al chiamante.

**L'origine di un nodo decompilato** punta alla riga del C# che ILSpy ha prodotto, etichettata
`<assembly>:<tipo>.cs`: è la traccia che esiste davvero, perché il sorgente originale non c'è. Se serve
anche il file `.cs` su disco, `IDecompileService.DecompileTypes` continua a scriverlo.

## Ghidra: un binario nativo, lo stesso modello

Ghidra headless decompila un eseguibile in C; quel C viene **normalizzato nel testo canonico** e riletto
dal `PseudocodeFrontend`. È la scelta di "normalizzare prima", e non è un ripiego:

- il `CppFrontend` legge il JSON di `clang -ast-dump`, non del testo C, e il C di Ghidra non è un
  translation unit che un compilatore accetterebbe (`undefined8`, `FUN_140001008`, `LAB_…` sono nomi di
  Ghidra);
- normalizzare invece riusa il lettore documentato per "qualunque linguaggio per cui non esiste un
  frontend" (`PseudocodeFrontend`), quindi resta **un solo formato e un solo lettore**;
- il normalizzatore è un vero lettore di C, non una sostituzione di testo: un cast resta un `CAST`, una
  dereferenziazione resta una dereferenziazione, `p->f` diventa `p.f` — non un `p->f` diventato per caso
  un'altra cosa.

### Il servizio

```csharp
using Snitch.Metacode.Decompilation;

IGhidraService ghidra = provider.GetRequiredService<IGhidraService>();
var analysis = await ghidra.AnalyzeAsync(@"C:\Windows\System32\version.dll");

analysis.Functions;   // quante funzioni ha recuperato
analysis.Error;       // il motivo esatto se non è partita (mai un'eccezione)
analysis.LogPath;     // il log completo del processo
analysis.ToMetacode();// le funzioni come MetacodeDocument
```

- **Dove sta Ghidra**: `GhidraOptions.HeadlessPath`, poi `GhidraHome`, poi `GHIDRA_INSTALL_DIR`/
  `GHIDRA_HOME`, infine la cartella dell'installer (`%LOCALAPPDATA%\VisualMCP\tools\ghidra_*`, o
  `GhidraOptions.ToolsDirectory`). Il servizio non presume che sia nel `PATH`.
- **Progetto temporaneo**: uno per analisi, sotto `GhidraOptions.OutputDirectory` (default
  `%TEMP%\vmcp-ghidra\<sha256 del binario>`). Ghidra lo cancella a fine corsa (`-deleteProject`) e il
  servizio toglie la cartella rimasta; con `KeepProject = true` l'opzione **non** viene passata e il
  progetto resta, insieme allo script `ExportNativeGraph.java` che il servizio scrive lì: non dipende da
  un file esterno.
- **Timeout**: `TimeoutSeconds` è un tetto per l'intera corsa; allo scadere il processo **e il suo albero**
  vengono uccisi e il risultato dice che è andato in timeout, con il log.
- **JDK**: `JAVA_HOME_OVERRIDE` viene impostata per il processo figlio quando `GhidraOptions.JavaHome` è
  noto, oppure — come faceva la pipeline nativa precedente — dopo un primo tentativo fallito con un
  errore che nomina Java: in quel caso il servizio cerca un JDK (`JAVA_HOME` e le cartelle standard) e
  riprova una volta sola. Su questa macchina `JAVA_HOME` punta a un JDK 17 e Ghidra 12.2 parte
  comunque: il percorso di override esiste per le macchine in cui non parte.

### Installazione: Ghidra se non c'è

`GhidraOptions.AutoInstall` (default `true`) fa sì che `AnalyzeAsync` non si limiti a cercare Ghidra:
se non lo trova, `GhidraInstaller` scarica l'ultima release dal feed GitHub del progetto
(`ghidra_<versione>_PUBLIC_<data>.zip`, l'unico archivio che Ghidra pubblica — è indipendente dalla
piattaforma) e la estrae sotto la cartella strumenti dell'utente. `HostSystem` è l'unica risposta alla
domanda "che piattaforma è questa": decide il nome del launcher (`analyzeHeadless.bat` su Windows,
`analyzeHeadless` altrove) e, su Linux/macOS, ripristina il bit di esecuzione che uno zip estratto su
Windows non conserva (`RestoreUnixModes`). `GhidraHome`/`GHIDRA_INSTALL_DIR` vincono sempre e non
scaricano niente; `AutoInstall = false` è la scelta di chi gestisce Ghidra da sé.

`GhidraInstallation` restituisce `HeadlessPath`, `Home`, `Version`, `Source` (`explicit`, `ghidraHome`,
`environment`, `tools`) e l'errore con l'istruzione manuale quando il download non è possibile. Il
fallimento non è un'eccezione: `analyze_native_binary` e `metacoding` lo riportano con il log.

### Dove finisce il risultato

Un binario che non è un assembly gestito non ha C# da decompilare: `ManagedAssemblyDetector` legge
l'header PE (directory COM descriptor) e `metacoding action=decompile` lo instrada a Ghidra invece di
fallire dentro ILSpy. `analyze_native_binary` fa la stessa cosa per la sola lettura. In entrambi i casi
il risultato viene **persistito** nel database della soluzione (`.visualMCP/visualmcp.db`) in tabelle
dedicate, non mescolato al metacode dei documenti della soluzione:

| Tabella | Cosa contiene |
|---|---|
| `native_artifact` | un binario analizzato: `sha256` (l'identità), file, percorso, `image`, linguaggio del processore, entry point, image base, numero di funzioni, dimensione, versione di Ghidra, launcher, data |
| `native_function` | una riga per funzione recuperata: nome, indirizzo di ingresso, prototipo, dimensione, **C decompilato** di Ghidra, **pseudocodice canonico** del modello, cyclomatic/cognitive/max nesting/statement/branch |
| `native_call` | un arco per chiamata vista da Ghidra: `caller` e `callee` per indirizzo di ingresso |

La chiave è lo SHA-256 del file: lo stesso binario (anche copiato altrove) non viene decompilato due
volte, e `force=true` sostituisce le righe invece di aggiungerne una seconda copia. Il modello viene
tradotto una volta sola: ogni funzione prende la sua voce in `native_function`, con lo stesso
pseudocodice e la stessa complessità che avrebbe un albero della soluzione. La scrittura è transazionale
e degrada senza far fallire l'analisi se il database non è disponibile.

### La normalizzazione, costrutto per costrutto

| C di Ghidra | Pseudocodice | Nota |
|---|---|---|
| `void FUN_x(longlong a, byte * b)` | `FUNCTION FUN_x(a : longlong, b : byte*) : void` | la convenzione di chiamata (`__fastcall`) non è nel modello e viene scartata |
| `undefined8 x;` | `DECLARE x : undefined8` | `type name [32]` tiene la dimensione nel nome del tipo |
| `*p = 0;` | `(*p) = 0` | una scrittura attraverso un puntatore è un'assegnazione, non una dichiarazione |
| `(uint)*p << 0x18` | `CAST(uint, *p) << 0x18` | il cast resta un cast, l'esadecimale resta un numero |
| `p->f` | `p.f` | |
| `&DAT_…`, `*puVar7` | `&DAT_…`, `*puVar7` | due operatori unari nuovi del modello, `AddressOf` e `Dereference` |
| `L"testo"`, `L'\0'` | `"testo"`, `'\0'` | il prefisso wide è la codifica, non il valore |
| `if/else`, `while`, `do…while`, `for`, `switch` con `case`/`default`, `goto`/label | `IF/ELSE`, `WHILE`, `REPEAT…UNTIL`, `FOR`, `SWITCH`, `GOTO`/`LABEL` | la `for` poi diventa `WHILE` con la passata di normalizzazione, come per ogni altra lingua |
| `/* WARNING: … */` | — | i commenti di Ghidra parlano del decompilatore, non del programma |

**Quello che non entra** è dichiarato, non indovinato: un'espressione con la virgola di C
(`while (p = f(p), *p)`) non ha un nodo — è una sequenza, e il modello non ha espressioni sequenza — e
diventa `RAW[cpp] …` con un diagnostico `MC0008` che dice perché. Un tipo con più livelli di puntatore
(`char **`) si riduce al livello che `MetacodeTypeRef.IsPointer` sa rappresentare.

**L'origine di una funzione nativa** è ciò che serve per ritrovarla: il documento è il binario, la riga è
la posizione nel snapshot, lo `Snippet` è la firma di Ghidra. Il testo canonico invece non porta origini
(il lettore del pseudocodice non le segue), ed è per questo che la funzione — non la singola istruzione —
è l'unità tracciabile di un albero nativo.

### Come si prova

Ghidra non si invoca nei test: è lento e dipende dalla macchina. `DecompilationMetacodeTests` registra
un **snapshot vero** (due funzioni di `C:\Windows\System32\version.dll`, catturate con una corsa headless
il 29 ottobre 2026) e verifica su quello il parser del formato, il metacode prodotto, l'origine, il giro
completo renderer↔parser e il caso `RAW` dell'operatore virgola. `NativeCodeStoreTests` (7 casi) usa lo
stesso snapshot per le tabelle: le righe di `native_artifact`/`native_function`/`native_call`, la chiave
SHA-256, il riuso e la sostituzione forzata, la ricerca per nome o indirizzo, il routing di un file non
gestito attraverso `metacoding`, l'installer che riconosce una copia esistente e la scelta dell'asset
dal feed delle release. La verifica "Ghidra parte davvero" si fa a mano, una volta, e il suo esito va nel
report di chi la esegue.

## Il modello è cresciuto ancora un po'

| Costrutto | Nodo | Da dove |
|---|---|---|
| `*p`, `&x` | `MetacodeUnary` con `Dereference` / `AddressOf` | C, C++ e qualunque cosa decompili Ghidra; stampati come in C e riletti dal parser |
| `x with { A = y }` | `MetacodeWithExpression`, stampato `WITH { … }` | aggiornamento di un record/valore; l'emitter riscrive `with` |
| `new { A = x, y }` | `MetacodeAnonymousObject`, stampato `ANON { … }` | oggetto senza tipo dichiarato; lo shorthand conserva il nome dell'espressione |
| `x is not ("a" or "b")`, `x is { }`, `case > 0:` | `MetacodePatternMatch` / `MetacodePatternText`, stampati `MATCHES (…)` e `PATTERN (…)` | il pattern resta testo (la stessa scelta di `MetacodeSwitchArm`), l'operando è un nodo vero |
| `[.. xs, 1]` | `MetacodeSpread`, stampato `SPREAD xs` | spread di una collection expression |

Il lettore del testo canonico ha chiuso, in questa passata, quattro buchi che il C decompilato ha
rivelato: gli esadecimali (`0x100` non si spezza più in `0x` e `100`), i letterali carattere (`'\0'`),
la testata di un `FOR` con `<` (che non apre più una parentesi angolare, quindi `FOR i = 0; i < n; i += 1 DO`
si rilegge), e una riga `RAW[…]` riletta come **warning** invece che come errore — `RAW` è testo canonico
valido, non un fallimento del parser.

Con i cinque nodi sopra, il modello traduce **tutto il codice della libreria stessa**: i 48 file di
`SNITCH/Libraries/Snitch.Metacode` passano da 560 diagnostici a zero. `MetacodeLibraryCoverageTests`
è il guardiano: traduce ogni file e fallisce se un costrutto nuovo resta senza nodo, così il modello
non torna indietro. `MetacodeExpressionTests` copre i cinque costrutti uno per uno (testo canonico,
giro renderer↔parser, emissione C# rileggibile), e il caso "with, anonymous objects, patterns and
spreads" è nella teoria di `MetacodeGenerationTests` che verifica il giro completo emitter→C#→modello.

## Razor: una pagina è una classe che renderizza

Un `.cshtml`/`.razor` non è un albero di dichiarazioni: è markup con dentro C#. Il `RazorFrontend` lo
modella per quello che è — una classe con una pagina da disegnare:

- `@model` diventa la proprietà `Model`, ogni `@inject` una proprietà con lo stesso nome (o il nome del
  tipo); le altre direttive (`@page`, `@using`, `@layout`, `@attribute`, i tag helper) sono
  `MetacodeOpaqueDeclaration` con il loro testo, perché configurano la pagina, non la dichiarano.
- i membri di `@code`/`@functions` passano dal `CSharpFrontend`: la classe li contiene già, quindi il
  frontend li legge per davvero (metodi, proprietà, campi) con l'**origine sulla riga della pagina**.
- il corpo del markup è il corpo di un metodo `Render`: `@if`/`else if`/`else`, `@foreach`, `@for`,
  `@while`, `@lock` e `@using (…)` sono i nodi di controllo del modello (l'intestazione è C#, quindi la
  abbassa il frontend C# su una forma vuota, e il corpo è il markup), e l'HTML fra un costrutto e
  l'altro resta un `RAW[razor]`.

```razor
@model ProductListViewModel
<h1>Products</h1>
@if (Model.Any) { <ul>@foreach (var p in Model.Products) { <li>@p.Name</li> }</ul> }
```
```
MODULE Index
  PROPERTY Model : ProductListViewModel
    GET AUTO
  END PROPERTY
  METHOD Render
    RAW[razor] <h1>Products</h1> // HTML markup
    IF Model.Any THEN
      RAW[razor] <ul> // HTML markup
      FOREACH p IN Model.Products DO
        RAW[razor] <li>@p.Name</li> // HTML markup
      END FOREACH
      RAW[razor] </ul> // HTML markup
    END IF
  END METHOD
END MODULE
```

L'`else` è la ragione per cui il frontend rilegge il testo invece di fidarsi della lista dei blocchi: in
Razor `else`/`else if` non hanno la `@`, quindi lo scanner non li riporta, e la catena va ricostruita dai
brace. Per lo stesso motivo `@switch`, `@try` e `@do` restano opachi: le loro `case`/`catch`/`while` sono
markup, non rami che il modello possa rappresentare senza inventare.

## L'indice dei simboli

`Symbols/` risponde, dal database invece che dalla soluzione, alle domande che in Visual Studio sono *Go To
Definition*, *Find All References* e *Call Hierarchy*, e in ILSpy sono l'*Analyzer*: **dove è dichiarato un
membro, con quale firma, dove viene usato, come viene usato, chi lo usa e cosa usa**. Il costo di rispondere
la prima volta si paga una volta sola: poi sono query SQLite indicizzate, e un file che non è cambiato non
viene riaperto.

```
documento C# ──Roslyn (sintassi + modello semantico)──► SymbolIndexBuilder
                                                        │  dichiarazioni (firma, span, contenitore)
                                                        │  usi (come, dentro quale membro)
                                                        │  relazioni che la sintassi non scrive (override, implement)
                                                        │  alberi metacode, uno per dichiarazione
                                                        ▼
                        .visualMCP/visualmcp.db  (si_symbol, si_usage, si_metacode, si_document)
                                                        ▼
                SymbolIndexService ──► reference_index(action=find|describe|refs|callers|metacode|…)
```

### Cosa registra una dichiarazione

La riga porta la **firma come la scrive il sorgente** (`public static int Add(int a, int b)`), il tipo di
simbolo, accessibilità, modificatori (`static`, `abstract`, `virtual`, `override`, `async`, `const`,
`readonly`, `required`, `extension`…), il namespace, il **contenitore** (tipo o namespace), il `summary` XML
quando il progetto lo analizza, e lo span completo — riga, colonna, inizio, fine — così un chiamante può
leggere il corpo invece di cercarlo.

La **chiave** di un simbolo è `Kind:` + la sua firma completamente qualificata (`Method:global::Ns.C.Add(int)`).
Due dichiarazioni condividono una chiave solo se sono lo stesso simbolo: una classe parziale ha una chiave e
più righe, due overload hanno chiavi diverse. I generici costruiti e le extension method ridotte collassano
sulla definizione, così `List<int>` e `List<string>` sono una voce e `x.Ext()` cade sulla dichiarazione
`Ext(this X, …)`.

> `SymbolDisplayFormat.FullyQualifiedFormat` **non basta** in questa versione di Roslyn: omette le opzioni dei
> membri, quindi ogni metodo si formatta col solo nome e due overload finirebbero sulla stessa chiave. Il
> formato usato è costruito a mano in `SymbolKeys` e include contenitore, parametri e `ref`/`out`.

### Come viene usato: il vocabolario

Il "come" non lo dà Roslyn: va letto dalla sintassi attorno al nome, dal nodo più vicino verso l'alto, e il
simbolo decide solo dove serve (un'interfaccia si *implementa*, una classe base si *estende*). I valori sono
stabili — sono salvati nel database e restituiti dai tool — e un valore si aggiunge, non si rinomina:

| Gruppo | Valori |
|---|---|
| Membri | `call`, `method-group`, `instantiate`, `read`, `write`, `read-write`, `initializer-write`, `subscribe`, `unsubscribe`, `ref-argument`, `out-argument`, `in-argument`, `lock` |
| Tipi | `inherit`, `implement`, `type-argument`, `parameter-type`, `return-type`, `field-type`, `property-type`, `local-type`, `event-type`, `attribute`, `constraint`, `catch`, `cast`, `as`, `pattern`, `typeof`, `default`, `sizeof`, `array`, `nameof`, `deconstruction`, `using`, `type` |
| Relazioni | `override`, `implement` — le uniche due righe che il sorgente non scrive (sotto) |

`x.y.Z` è **un solo slot**: il nome e ogni accesso di membro sopra di lui rispondono alla stessa domanda
("è una chiamata, una scrittura, una lettura?"), quindi `x.M()` è `call` mentre `x.M` da solo è
`method-group`, `x.P = 1` è `write` e `x.P += 1` è `read-write`.

### Le relazioni che la sintassi non dice

`override` non nomina il membro base, e un'implementazione di interfaccia implicita è solo una firma che
combacia. Entrambe vengono ricostruite (`FindImplementationForInterfaceMember`, `OverriddenMethod`) e
registrate **come uso del membro base, alla posizione del membro che lo sovrascrive o lo implementa**:
il contenitore della riga è il membro derivato. Da lì:

- `implementedBy` di `IFoo.M` sono i membri che la implementano (l'override in una classe derivata compreso,
  perché si risale la catena degli override);
- `implementedBy` di `IFoo` sono i tipi che la implementano — registrata una volta sul tipo, così non dipende
  da quale file dichiara il membro.

### Lo stesso albero, in metacode

L'indice non si limita a leggere il sorgente: **lo abbassa anche in metacode**. Mentre il documento è in mano
(`MetacodeSource.FromSyntaxTree`), il traduttore produce il `MetacodeDocument`, e ogni dichiarazione del
modello viene accoppiata al simbolo che l'indice conosce — l'origine del nodo (riga e colonna, 1-based) è la
stessa posizione della dichiarazione Roslyn, con il nome sotto lo stesso contenitore come riserva per i nodi
che una passata ha spostato. Così `action=metacode` restituisce il **pseudocodice canonico di ciò che hai
trovato**, letto dal database:

```
reference_index(action=metacode, symbol="Counter")        → CLASS Counter … END CLASS   (con i membri)
reference_index(action=metacode, symbol="Counter.Add")    → PUBLIC METHOD Add(delta : int) … END METHOD
reference_index(action=metacode, path="…/Counter.cs")     → tutte le foreste del file, in ordine
```

Un tipo porta dentro i suoi membri, e ogni membro ha anche la **sua** riga: chiedere un metodo non significa
rigenerare il tipo. Il testo è quello del renderer — quindi la normalizzazione delle passate è inclusa, e il
`for` è già un `WHILE` — ed è lo stesso che si rilegge con `PseudocodeFrontend`. È gzip nel database (un tipo
ripete i suoi membri) e viene riscritto con il documento quando cambia l'hash.

`PseudocodeRenderer` ha per questo due overload pubblici — `Render(MetacodeDeclaration)` e
`Render(IReadOnlyList<MetacodeDeclaration>)` — che stampano una dichiarazione o una lista con le regole del
documento intero.

### Usarlo

```csharp
using Snitch.Metacode.Symbols;
using Snitch.Storage;
using Snitch.Workspace;

var index = new SymbolIndexService(workspace, store);   // o ISymbolIndexService dal container

await index.BuildAsync();                    // incrementale: hash di contenuto per documento
index.Find("Twice", partial: true);          // dichiarazioni, con quanti posti le usano
index.Describe("Counter");                   // firma, documentazione, span, usi per tipo, chiamanti
index.References("_value", kind: "write");   // ogni scrittura, con il membro che la contiene
index.Callers("Twice");                      // chi la usa, raggruppato per chiamante
index.Callees("Use");                        // cosa usa quel membro (declared=false ⇒ fuori soluzione)
index.Hierarchy("IShape");                   // basi, interfacce, derivati, implementatori, override
index.Outline(symbol: "Counter");            // i membri del tipo, in ordine di sorgente
index.Outline(path: "…/Counter.cs");         // le dichiarazioni del file
index.Metacode(symbol: "Counter.Add");       // l'albero metacode di quella dichiarazione
index.Metacode(path: "…/Counter.cs");        // gli alberi del file, in ordine di sorgente
index.Unused();                              // dichiarazioni non pubbliche che nessuno usa
```

Un documento ha un frontend per lingua (`ISymbolIndexFrontend`: quali nodi dichiarano, quali nomi usano un
simbolo, come si legge un uso, che firma si stampa); il builder, lo store e il servizio sono neutri. Il C# è
l'unica implementazione, quindi `build` conta i documenti di altre lingue come `unsupported` invece di
saltarli in silenzio.

Limiti dichiarati: locali, parametri e type parameter non sono indicizzati (la domanda è "chi usa questo
*membro*"); un gruppo di metodi con più overload e senza invocazione non si lega a un simbolo e viene
saltato; l'indice è uno **snapshot**, va ricostruito dopo le modifiche (le query non leggono i file, tranne
la riga di codice mostrata come `snippet`).

## Il metacode della soluzione, persistito

`reference_index build` produce gli alberi come effetto collaterale dell'indice dei simboli. Ma il
metacode della soluzione è un artefatto a sé, e ha un ciclo di vita proprio:

- **alla prima apertura della soluzione** viene generato per **ogni sorgente della soluzione** (il task in
  background di `Program.cs`, dopo `OpenEnvironmentAsync`): i documenti Roslyn di tutti i progetti, in
  qualunque lingua il workspace sappia caricare, più le pagine `.cshtml`/`.razor` lette da disco — sono
  content item, il workspace non le tiene come documenti. La selezione è per lingua, non per estensione
  `.cs`: decide il frontend.
- **quando un file viene salvato** viene aggiornato in modo incrementale: un giro ogni 2 secondi legge
  prima il timestamp, e solo se è cambiato il contenuto, quindi traduce **solo quel documento**. Vale
  anche per un salvataggio fatto in un altro editor, perché il testo viene letto dal file su disco, non
  dallo snapshot del workspace;
- **quando si compila** `build_project` fa un refresh esplicito dopo la build (sempre best-effort: un
  errore dell'indice non trasforma un build riuscito in un tool fallito).

Il risultato vive in `.visualMCP/visualmcp.db`, e ogni sorgente ci sta **due volte**: da un lato come la
scrive la sua lingua, dall'altro come la legge il modello.

| Tabella | Chiave | Cosa tiene |
|---|---|---|
| `source_document` | `path` | il file **nella sua lingua nativa**: testo, tag di lingua (`cs`, `vb`, `razor`), hash del contenuto, timestamp, caratteri e righe. C'è per **ogni** sorgente della soluzione |
| `mc_document` | `path` | hash del contenuto, timestamp, progetto, lingua, numero di dichiarazioni, diagnostici — solo per i documenti che un frontend ha letto |
| `mc_tree` | `declaration_key` + `path` + riga + nome | pseudocodice canonico della dichiarazione (gzip) e i fatti strutturali: cyclomatic, cognitive, nesting massimo, istruzioni, rami, espressioni |

Il lato nativo è il testo, non un albero serializzato: un `SyntaxTree` di Roslyn non si serializza, e
rileggerlo dal testo costa un parse. Quello che il database conserva è quindi la sorgente esatta (più il
suo hash), da cui il modello di Roslyn si ricostruisce identico — ed è quello che permette al workspace di
usare tutte le feature di Roslyn sul documento, mentre il metacode resta la vista neutra dello stesso file.

Una lingua che nessun frontend sa leggere — il Visual Basic oggi — **non sparisce**: `source_document`
tiene il file, `mc_tree` non ha righe, `mc_document` non ha la riga. `metacode_index action=status` lo dice
con `sources` accanto a `documents`, e il report della build con `unsupported`. Il giorno in cui esiste un
`VbFrontend`, la passata successiva lo trova già letto (l'hash è quello giusto) e lo traduce.

Il lato semantico "nativo" — dichiarazioni, usi, relazione di override e di implementazione — è l'indice
dei simboli nelle tabelle `si_*`, scritto dalla stessa apertura della soluzione e poi su richiesta di
`reference_index action=build` (costa una compilazione, il metacode no).

La chiave è sintetica ma stabile — namespace, tipi contenitori, nome e arietà
(`method:Demo.Counter.Sum(1)`, `indexer:Demo.Store.this[1]`, ``type:Demo.Store`1``) — quindi una ricerca
non richiede il modello semantico. Un tipo porta dentro i suoi membri e ogni membro ha anche la sua riga,
come nell'indice dei simboli. I fatti sono **colonne**, non prosa: un report è una query SQL.

### La complessità ciclomatica dal modello

`MetacodeComplexityAnalyzer` (`Analysis/`) calcola McCabe e la cognitive di Sonar sulle
`MetacodeStatement`/`MetacodeExpression`, con le stesse regole dell'analizzatore C# del repository:
un punto per `if`, ciclo, `catch`, ternario, `&&`/`||`, `??`, ramo di `switch`, `goto` e auto-chiamata
ricorsiva; l'`else if` costa un punto senza penalità di nesting, un `else` semplice zero percorsi e un
punto cognitivo. Le lambda e le funzioni locali sono unità a sé, come le vede il compilatore.

Il test `The_metacode_complexity_matches_the_CSharp_analyzer` confronta i due numeri, metodo per metodo,
su sei sorgenti che coprono i costrutti decisionali — con il modello semantico passato all'analizzatore
C#, perché la ricorsione la vede solo lì. È la prova che la metrica sul modello non è una seconda
implementazione approssimativa: è la stessa metrica, applicabile a qualunque lingua che entri nel modello.

`complexity_report` resta la vista C# completa (Halstead, maintainability index, contributi per riga);
`metacode_index action=complexity` è la vista persistita e neutra, letta dal database.

### Generare codice: metacode → C#

`CSharpEmitter` (`Generation/`) è l'inverso di `CSharpFrontend`: stesso giro dell'albero, stessa tabella
di precedenza, sintassi di destinazione diversa. Il criterio di accettazione è un **punto fisso**:
tradurre il sorgente, emettere C#, rileggere il C# emesso e ritradurlo deve produrre lo stesso
pseudocodice canonico — è quello che fanno i dieci casi di `The_emitter_round_trips_a_file`.

Ciò che il modello non può esprimere in C# viene abbassato alla forma più vicina che compila, con un
commento nel codice e un diagnostico nell'output: `**` diventa `Math.Pow`, `in` diventa
`Enumerable.Contains`, una set/dictionary literal diventa `HashSet<object>`/`Dictionary<object, object>`,
una comprehension a un solo `for` diventa una catena LINQ, un modulo di funzioni libere diventa una
`static partial class`. Una comprehension con più `for` non viene abbassata: emettere codice che non
compila sarebbe peggio che dichiarare di non averlo fatto. Una dichiarazione C# che il modello non ha
(un `delegate`, una conversione definita dall'utente) viene riscritta **verbatim**, perché è C# valido per
costruzione; un costrutto di un'altra lingua diventa `// TODO(metacode): …`.

### Usarlo

```csharp
using Snitch.Metacode.Indexing;

var index = new MetacodeIndexService(workspace, store);   // o IMetacodeIndexService dal container

await index.BuildAsync();                       // incrementale: timestamp, poi hash di contenuto
await index.RefreshAsync();                     // il giro economico del loop in background
index.Status();                                 // documenti, dichiarazioni, funzioni, tipi, range di complessità
index.Trees(symbol: "Counter.Sum");             // l'albero memorizzato, con i suoi numeri
index.Trees(path: "…/Counter.cs");              // tutte le dichiarazioni del file
index.Complexity(minCyclomatic: 10);            // i richiudibili, dal peggiore
index.Generate(symbol: "Counter.Sum");          // metacode memorizzato → C#
index.Generate(text: pseudocodice, language: "pseudo");  // nuovo codice da pseudocodice → C#
```

I due tool MCP corrispondenti sono `metacode_index` (`status|build|complexity|trees`) e
`metacode_generate` (metacode → C#, con i diagnostici delle due direzioni).

Limiti dichiarati: solo C# (Razor e VB non hanno un emitter, e i documenti `.cshtml`/`.razor` non sono
documenti Roslyn); le funzioni locali e le lambda non hanno una riga propria (la loro complessità è
misurata come parte del metodo che le contiene); un tipo non ha una complessità propria — ce l'hanno i
suoi membri.

## Dove sono i file

| File | Ruolo |
|---|---|
| `MetacodeModel.cs`, `MetacodeStatements.cs`, `MetacodeExpressions.cs` | il modello |
| `MetacodeDiagnostics.cs` | diagnostici, codici `MC0001`–`MC0008`, opzioni, `IMetacodePass` |
| `MetacodeRewriter.cs` | la base delle passate (sostituzione con splice dei blocchi) |
| `MetacodeSource.cs`, `ILanguageFrontend` | input e contratto del frontend |
| `MetacodeTranslator.cs` | la facciata: frontend giusto, passate, documento |
| `Frontends/CSharpFrontend.cs`, `CSharpLowering.Expressions.cs` | Roslyn |
| `Frontends/PythonFrontend.cs` | dump `ast` — lettore opt-in, non nella pipeline predefinita |
| `Frontends/CppFrontend.cs` | dump `clang` — lettore opt-in, non nella pipeline predefinita |
| `Frontends/RazorFrontend.cs` | pagine `.cshtml` e componenti `.razor`, sul modello dello scanner di `SNITCH/Languages/Razor` |
| `Frontends/PseudocodeFrontend.cs`, `PseudocodeExpression.cs` | lettura del testo canonico |
| `Passes/MetacodePasses.cs` | `NormalizeControlFlowPass`, `FlattenSingleStatementBlocksPass` |
| `Rendering/PseudocodeRenderer.cs` | stampa |
| `Decompilation/DecompileService.cs`, `IDecompileService.cs`, `MetacodeDecompilation.cs` | ILSpy → C# → modello (namespace pubblico invariato) |
| `Decompilation/GhidraService.cs` | Ghidra headless: processo, timeout, progetto temporaneo, `JAVA_HOME_OVERRIDE`, script di export |
| `Decompilation/GhidraInstaller.cs` | ricerca e installazione di Ghidra: feed delle release, estrazione, launcher per piattaforma (`HostSystem`), bit di esecuzione su Unix |
| `Decompilation/NativeCodeStore.cs` | le tabelle native del database della soluzione: `native_artifact`, `native_function`, `native_call` |
| `Decompilation/GhidraC.cs` | il lettore di C di Ghidra che emette testo canonico |
| `Decompilation/GhidraFrontend.cs`, `GhidraSnapshot.cs` | snapshot → funzioni → modello, e il parser del formato |
| `SnitchMetacodeServiceCollectionExtensions.cs` | `AddSnitchMetacode()`: traduttore, decompilatore (due contratti), Ghidra, indice dei simboli, indice del metacode |
| `Analysis/MetacodeComplexityAnalyzer.cs` | cyclomatic (McCabe) e cognitive (Sonar) sulle `MetacodeStatement`/`MetacodeExpression` |
| `Generation/CSharpEmitter.cs` | metacode → C#: l'inverso del frontend C#, con i diagnostici di ogni approssimazione |
| `Indexing/MetacodeIndexModel.cs`, `MetacodeIndexStore.cs`, `MetacodeIndexService.cs` | il metacode della soluzione: tabelle `mc_document`/`mc_tree`, build/refresh incrementale, complessità, generazione |
| `Symbols/SymbolIndexModel.cs` | chiavi dei simboli, righe di dichiarazione e di uso, alberi metacode, il vocabolario di `SymbolUsageKind` |
| `Symbols/CSharpSymbolIndexFrontend.cs` | il livello C# dell'indice: nodi che dichiarano, nomi che usano, classificazione dell'uso, firma |
| `Symbols/SymbolIndexBuilder.cs` | il giro sul documento: dichiarazioni, usi, relazioni `override`/`implement`, e l'albero metacode di ogni dichiarazione |
| `Symbols/SymbolIndexStore.cs` | tabelle `si_symbol`, `si_usage`, `si_metacode`, `si_document` e le query indicizzate |
| `Symbols/SymbolIndexService.cs` | le domande: `build`, `find`, `describe`, `refs`, `callers`, `callees`, `hierarchy`, `outline`, `metacode`, `unused` |
| `src/VisualMCP.Tests/MetacodeTests.cs` | 11 test: forma esatta, equivalenza fra lingue, giro completo, diagnostici |
| `src/VisualMCP.Tests/MetacodeDepthTests.cs` | 15 test: i costrutti aggiunti, uno per uno, più il giro completo di ogni documento |
| `src/VisualMCP.Tests/DecompilationMetacodeTests.cs` | 14 test: ILSpy→metacode, Ghidra a partire da uno snapshot registrato, puntatori, cablaggio DI |
| `src/VisualMCP.Tests/NativeCodeStoreTests.cs` | 7 test: tabelle native, chiave SHA-256, riuso/sostituzione, routing di un binario non gestito, installer e scelta dell'asset |
| `src/VisualMCP.Tests/SymbolIndexTests.cs` | 11 test: firma e posizione, i tipi di uso, chiamanti/chiamati, gerarchia, outline, build incrementale |
| `src/VisualMCP.Tests/MetacodeExpressionTests.cs` | 6 test: `with`, anonymous object, pattern match, case pattern, spread, emissione C# dei cinque nodi |
| `src/VisualMCP.Tests/MetacodeLibraryCoverageTests.cs` | 1 test: ogni file di `Snitch.Metacode` si traduce senza diagnostici (il guardiano del modello) |
| `src/VisualMCP.Tests/MetacodeGenerationTests.cs` | 24 test: round-trip a punto fisso su dodici sorgenti, complessità del modello contro l'analizzatore C#, approssimazioni dichiarate |
| `src/VisualMCP.Tests/MetacodeIndexTests.cs` | 6 test: build/refresh incrementale, righe per dichiarazione, complessità dallo store, generazione dallo store e dallo pseudocodice |
