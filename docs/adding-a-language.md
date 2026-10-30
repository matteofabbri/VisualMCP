# Aggiungere un linguaggio al motore di stile

Il motore delle regole IDE è diviso in due strati: un **nucleo neutro** che non sa nulla di sintassi, e un
**livello di linguaggio** che fornisce solo ciò che cambia. Aggiungere Visual Basic significa scrivere il
secondo; il primo si tocca solo quando una preferenza del linguaggio non ha un campo neutro dove vivere (è
successo per `visual_basic_style_prefer_isnot_expression`, vedi sotto).

## Cosa è già condiviso (`Snitch/Languages/Style/`)

| Tipo | Ruolo |
|---|---|
| `StyleRewrite` | la classe base di una regola: `Rule`, `Rules`, `RequiredOption`, `IsOptIn`, `TryMatch(node, model)` / `TryMatch(node, context)`, più gli helper neutri (`Match`, `Removal`, `SameSymbol`, `IsUserDefinedOperator`) |
| `StyleRule`, `StyleMatch` | id/titolo/descrizione di una regola; il match con nodo, replacement, span finale e flag `Removes` |
| `StyleOptions` + i cinque enum | le preferenze già mappate fuori dai nomi delle chiavi: graffe, ordine dei modificatori, namespace, cast nel foreach, accessibilità, `IsNot` |
| `StyleContext` | modello semantico, opzioni, workspace, solution, `IsPristine` |
| `IStyleRuleSet` | `All`, `Supported`, `NotRewritten`, `SupportedIds` |
| `IStyleLanguage` | i quattro punti di contatto con la sintassi (sotto) |
| `StyleRewriteService` | **tutta la pipeline**: scope documento/progetto/solution, fino a 3 passate per documento, gate del compilatore con isolamento dei rifiuti, report, storia delle modifiche, scrittura dei file |

## Cosa deve fornire un linguaggio

1. **Il proprio base class delle regole** (`CSharpStyleRewrite` come modello): la classe neutra più gli
   helper che richiedono la factory di sintassi del linguaggio — parsare un'espressione o un'istruzione da
   testo, il binding speculativo per i controlli di qualificazione.
2. **`IStyleLanguage`**, quattro membri più l'estensione:
   ```csharp
   string Name { get; }                 // LanguageNames.VisualBasic
   string Extension { get; }            // ".vb"
   bool IsStatement(SyntaxNode node);   // distingue un'istruzione da un'espressione
   bool IsShapeWrapper(SyntaxNode replacement);      // un blocco parsato che contiene istruzioni
   IReadOnlyList<SyntaxNode> StatementsOf(SyntaxNode parent);
   SyntaxNode WithStatements(SyntaxNode parent, IReadOnlyList<SyntaxNode> statements);
   StyleOptions ReadOptions(Document document, SyntaxTree? tree);
   ```
   `StatementsOf` + `WithStatements` sono tutto ciò che serve al servizio per sostituire o rimuovere un
   intervallo di istruzioni: il resto dello splice (trivia, annotazioni, ordinamento) è condiviso.
3. **`IStyleRuleSet`**: il catalogo delle regole del linguaggio e la tabella `NotRewritten` con il motivo
   di ogni id che non si riscrive.
4. **Il parser delle opzioni**: mappa le chiavi del linguaggio (`visual_basic_*`, `dotnet_style_*`) sui
   campi di `StyleOptions`.
5. **Il servizio concreto**: una classe di tre righe che eredita `StyleRewriteService` passando rule set,
   language e il nome del refactoring — come `SimplifyCodeService` per il C#.

## Cosa resta invece per forza al linguaggio

Le regole stesse: ogni matcher lavora sui tipi di nodo e sulle `SyntaxKind` del suo linguaggio, e il testo
che emette è sintassi di quel linguaggio. Non c'è nulla da astrarre lì, e non va forzato.

## Stato attuale

- **C#**: 87 id, 39 non riscritte con il motivo, `Snitch/Languages/CSharp/Style/`.
- **Visual Basic**: 15 id, `Snitch/Languages/VB/Style/` (`VbStyleLanguage`, `VbStyleRuleSet`,
  `VbStyleRewrites.cs`, `VbSimplifyCodeService`). I test sono `src/VisualMCP.Tests/VbStyleTests.cs`, su un
  `AdhocWorkspace` con un progetto VB vero: per ogni id supportato un caso che si riscrive e uno che deve
  restare intatto. Il servizio si registra con `AddSnitchVisualBasic()`
  (`Snitch/Languages/VB/VbServiceCollectionExtensions.cs`) e non è cablato in `AddSnitch`: un host che
  vuole il VB lo chiede, e i due servizi non si contendono lo stesso `ISimplifyCodeService`.
- Una regola VB potrà dichiarare lo stesso id IDE di una C#: gli id appartengono alla tabella Microsoft,
  non al linguaggio, e la tabella ha colonne separate per C# e VB. Per questo
  `VbStyleRewriteCatalog.NotRewritten` copre la **colonna VB** (le regole `dotnet_style_*` condivise più le
  `visual_basic_style_*`): un id che il VB non definisce affatto — pattern matching, graffe, expression
  body — non è una regola VB e non compare lì, mentre tutto ciò che il VB definisce e non riscriviamo ha
  un motivo, uno per riga.

### Cosa ha richiesto una generalizzazione del nucleo neutro

- `StyleOptions` ha un campo in più, `PreferIsNot`
  (`IsNotPreference.None | PreferIsNot | PreferNot`): `visual_basic_style_prefer_isnot_expression` è
  un'opzione VB, ma il posto in cui vive una preferenza già mappata è il record neutro — come `Braces` per
  il C# — e la regola non legge il nome della chiave.
- `StyleRewriteService` spiegava gli id fermi per opzione con una frase che elencava
  IDE0011/IDE0036/IDE0160/IDE0161, i quattro del C#. Ora la nota si costruisce dagli id selezionati in
  quella run, e accanto a `braces`/`namespaces`/`modifierOrder` compare `preferIsNot`. Per il C# la lista e
  il campo `braces` (su cui un test asserisce) restano identici.
- Nient'altro è cambiato: `StyleRewrite`, la pipeline, il gate del compilatore, lo splice delle istruzioni
  e la storia delle modifiche erano già neutri e il VB li usa così come sono.

## Trappole già pagate che valgono anche per il VB

- Dal secondo passaggio l'albero non è più quello della solution: `StyleContext.IsPristine` è vero solo
  alla prima passata, e una regola che consulta la soluzione deve rifiutare il resto.
- `ReplaceNodes` ricostruisce **tutto** l'albero: i match sulle istruzioni trovati prima vanno ricalcolati
  dopo (lo fa già il servizio).
- Un replacement sulla radice (direttive) non va annotato con `Formatter.Annotation`, o si riformatta il
  file intero.

## Trappole del VB, pagate scrivendo `Languages/VB/`

- **Non esiste un `BlockSyntax` pubblico con `Statements`.** Il C# ha un solo tipo che possiede una lista
  di istruzioni; il VB ne ha sedici (`MethodBlockBaseSyntax`, `MultiLineIfBlockSyntax`, `CaseBlockSyntax`,
  `TryBlockSyntax`, `DoLoopBlockSyntax`, …) e nessuna base comune. `VbBlocks` è l'unico posto che li
  conosce: un tipo mancante lì non è un errore di compilazione, è una regola sulle istruzioni che
  silenziosamente non fa nulla. Quando si aggiunge una regola che splices statements, il caso di test deve
  stare in un blocco il cui tipo è nella lista.
- **La factory dice `ParseExecutableStatement`, non `ParseStatement`.** `SyntaxFactory.ParseStatement`
  esiste in C# e non in VB: il compilatore lo segnala, ma il typo "ovvio" è proprio quello.
- **`TypeSyntax` deriva da `ExpressionSyntax`.** Un nome di tipo VB è anche un'espressione, quindi
  `ParseTypeName(...)` entra nelle API che prendono un `ExpressionSyntax` (binding speculativo compreso) e
  non serve una seconda strada.
- **Il test del null è `Is Nothing`, e `= Nothing` non è equivalente ovunque.** Per una `String` il VB
  confronta i valori (`"" = Nothing` è vero); per un value type `= Nothing` è il valore di default. IDE0041
  scatta solo su un tipo di riferimento diverso da `String`, o cambierebbe il significato.
- **La coalescenza è `If(a, b)` e la condizione ternaria è `If(c, a, b)`**: due tipi di nodo diversi
  (`BinaryConditionalExpressionSyntax` e `TernaryConditionalExpressionSyntax`), e IDE0029 deve riscrivere
  il ternario nel binario, non il contrario.
- **IDE0084 ha una direzione, non una forma.** `Not (o Is other)` e `o IsNot other` sono entrambe corrette:
  quale delle due si scrive lo dice `visual_basic_style_prefer_isnot_expression`, quindi la regola legge
  `StyleContext.Options.PreferIsNot` e dichiara `RequiredOption`; senza opzione non tocca nulla. La forma
  senza parentesi (`Not o Is C`) è la stessa cosa: `Is` lega più stretto di `Not`.
- **`Formatter.FormatAsync` per il VB funziona solo se l'assembly `Microsoft.CodeAnalysis.VisualBasic.Workspaces`
  è nella composizione MEF.** `MefHostServices.DefaultAssemblies` lo carica per nome, quindi è il
  `PackageReference` nel `.csproj` a renderlo disponibile: senza quel package il documento VB si parsa ma
  la formattazione non trova il servizio.
- **Nessuna delle regole C# "di sintassi" si porta dietro.** Niente graffe, niente expression body, niente
  pattern property, niente `??=`: gli id corrispondenti non sono "non implementati", sono id che il VB non
  ha. La tabella VB è volutamente corta (15 id) e ogni id non riscritto ha il suo motivo.
