# Analisi delle pagine Razor

VisualMCP legge le pagine Razor come **markup**, non come codice C#: i file `.cshtml` e `.razor` non
sono documenti del progetto Roslyn (sono `Content`, e per Razor SDK vengono compilati da un altro
toolchain), quindi non compaiono in `solution.Projects[*].Documents`. Il motore li enumera da disco a
partire dalle cartelle dei progetti e li scandisce con un parser Razor/HTML dedicato, senza aggiungere
pacchetti: nessun `Microsoft.AspNetCore.Razor.Language`, nessun `dotnet` esterno.

Tre tool di analisi, tutti read-only:

| Tool | Cosa risponde |
|---|---|
| `razor_pages` | Quali pagine esistono e come sono collegate: `page`/`view`/`layout`/`partial`/componente Blazor, `@page` route, `@model`, layout, sezioni, `@inject`, handler `OnGet`/`OnPost`, code-behind, conteggi di markup |
| `analyze_razor_pages` | Cosa non va nel markup: HTML rotto, accessibilità, uso improprio di Razor, manutenibilità — regole `RZ2xxx` |
| `find_razor_vulnerabilities` | Cosa è sfruttabile: XSS, CSRF, segreti, URL insicuri, CSP — regole `RZ1xxx`, ognuna con un CWE |

Il **lato scrittura** è `razor_edit` (`RazorEditService.cs`, nella stessa cartella): applica una modifica
verificata per volta (attributo, direttiva o intervallo di righe), ri-scandisce il file prima di
scrivere e riporta quali finding ha risolto o introdotto, usando le stesse regole di questa pagina.
Questo documento copre l'analisi; i dettagli delle operazioni sono nella descrizione del tool.

`projectName` filtra un progetto della soluzione. `path` accetta una cartella o un singolo file, e in
quel caso **non serve una soluzione caricata**: è la via per analizzare un repo che VisualMCP non ha
aperto.

## Architettura

Tutto vive in `SNITCH/Languages/Razor/` e viene registrato da `AddSnitch`:

| File | Ruolo |
|---|---|
| `RazorModel.cs` | Il modello: direttive, espressioni, code block, albero degli elementi HTML (con padre/figli), posizioni 1-based |
| `RazorScanner.cs` | Lo scanner: separa markup e C#, conosce `@* *@`, `@{ }`, `@( )`, `@await`, `@code`/`@functions`, `@section`, `@if`/`@foreach`/…, tag HTML con attributi, `<script>`/`<style>` raw |
| `RazorRuleCatalog.cs` | Id, categoria, titolo, severità, CWE e remediation di ogni regola — i finding prendono la metadata da qui |
| `RazorRules.cs` | Le regole: una passata per documento, finding con `file`, `line`, `column`, `snippet` |
| `RazorAnalysis.cs` | Scoperta dei file (cartelle dei progetti, esclusioni `bin`/`obj`/…), classificazione, ordinamento e forma JSON dei finding |
| `RazorPagesService.cs` / `AnalyzeRazorPagesService.cs` / `FindRazorVulnerabilitiesService.cs` | I tre entry point |
| `RazorEditService.cs` | Il lato scrittura (`razor_edit`): splice verificati, ri-scan prima della scrittura, edit registrati in `edit_history` |

Passaggio dei dati: `RazorAnalysis.CollectAsync` → `RazorScanner.Scan` → `RazorRules.Run` → filtro del
servizio. Lo scanner **non** scende dentro `@{ … }` e `@code { … }` (sono C# puro): i problemi di
markup lì dentro non vengono visti, ed è una scelta dichiarata. Il corpo di `@if`, `@foreach`,
`@section` e `@using (...)` invece è markup e viene analizzato, comprese le variabili di ciclo, così
`@item.Name` dentro un `<script>` viene riconosciuto come dato del modello.

## Regole di sicurezza (`RZ1xxx`)

| Id | Severità | CWE | Cosa rileva |
|---|---|---|---|
| `RZ1001` | High | 79 | `@Html.Raw(...)` / `new HtmlString(...)` con un valore non codificato (anche dentro un attributo) |
| `RZ1002` | High | 79 | `@Model…`, `@ViewBag…` o una variabile di `@foreach` scritti dentro `<script>` |
| `RZ1003` | High | 79 | Dato del modello dentro un handler inline (`onclick="…@ Model…"`) |
| `RZ1004` | High | 79 | URI `javascript:`, `vbscript:` o `data:text/html` in `href`/`src`/`action` |
| `RZ1005` | Medium | 352 | Form POST senza token: niente `@Html.AntiForgeryToken()`, niente tag helper (`asp-*`), oppure `asp-antiforgery="false"` / `[IgnoreAntiforgeryToken]` |
| `RZ1006` | Medium | 598 | Input `type="password"` in un form GET (password in URL e log) |
| `RZ1007` | Low | 1022 | `target="_blank"` senza `rel="noopener noreferrer"` |
| `RZ1008` | High | 798 | Credenziale letterale nel markup (`type="password" value="…"`, `data-api-key="…"`, assegnazione in un code block) |
| `RZ1009` | Medium | 319 | Risorsa caricata via `http://` (mixed content); `w3.org` e gli schemi XML sono esclusi |
| `RZ1010` | Medium | 79 | `src` di un `<iframe>` costruito da dati |
| `RZ1011` | Low | 693 | `<script>`/`<style>` inline, attributo `style` o handler inline: bloccati da una CSP stretta |

`RZ1001` non scatta su un letterale né su un valore già passato a `Encode`/`Sanitize`/`HtmlEncoder`/
`JavaScriptEncoder`/`Json.Serialize`. `RZ1002` non scatta su `@Json.Serialize`, `@Html.Encode`,
`@Url.Action` e simili.

## Regole di markup, accessibilità e manutenibilità (`RZ2xxx`)

| Id | Categoria | Severità | Cosa rileva |
|---|---|---|---|
| `RZ2001` | Markup | Medium | Elemento mai chiuso (i tag con fine opzionale, es. `<li>`, `<p>`, sono esclusi) |
| `RZ2002` | Markup | Medium | Chiusura orfana o non corrispondente |
| `RZ2003` | Markup | Medium | `id` duplicato nello stesso file |
| `RZ2004` | Accessibility | Low | `<img>` senza `alt` |
| `RZ2005` | Accessibility | Low | `<label>` senza `for` e senza controllo annidato |
| `RZ2006` | Markup | Low | `<button>` in un form senza `type` (default: submit) |
| `RZ2007` | Markup | Medium | Controllo di un form senza `name`/`asp-for`: non viene inviato |
| `RZ2008` | Accessibility | Low | Manca `<meta name="viewport">` |
| `RZ2009` | Accessibility | Low | `<html>` senza `lang` |
| `RZ2010` | Maintainability | Low | `ViewBag`/`ViewData` (non tipizzati) |
| `RZ2011` | Maintainability | Low | Helper legacy `Html.Partial`/`RenderPartial`/`Action`/`RenderAction`/`BeginForm` |
| `RZ2012` | Maintainability | Low | Code block `@{ }`/`@code`/`@functions` oltre 25 righe |
| `RZ2013` | Markup | Medium | Form annidato |
| `RZ2014` | Markup | Low | Attributo duplicato sullo stesso elemento |
| `RZ2015` | Razor | High | Sintassi Razor non bilanciata (blocco, espressione, commento, stringa, tag) |
| `RZ2016` | Markup | Low | Form senza `action` e senza `asp-*` in una view MVC |
| `RZ2017` | Markup | Low | Elemento o attributo obsoleto (`center`, `font`, `marquee`, `align`, …) |

Le regole `RZ2006`, `RZ2007`, `RZ1005` si applicano solo ai `.cshtml`: in Blazor (`<EditForm>`,
`@bind`) non avrebbero senso e sono escluse per non produrre rumore.

## Filtri e output

`analyze_razor_pages` accetta `category` (`Markup`, `Accessibility`, `Maintainability`, `Razor`),
`rule` (es. `RZ2004`), `minSeverity` (default `Low`) e `maxFindings` (default 200). Restituisce i
conteggi per severità/categoria/regola **sul totale filtrato**, più i finding restituiti — quindi
`truncated: true` non nasconde la dimensione del problema.

`find_razor_vulnerabilities` accetta `minSeverity` e `cwe` (`79` o `CWE-79`) e restituisce
`bySeverity`, `byCwe`, `byRule` e i finding.

Ogni finding ha la stessa forma nei due tool:

```json
{
  "ruleId": "RZ1001", "category": "Security", "title": "Unencoded HTML output (XSS)",
  "severity": "High", "cwe": "CWE-79",
  "message": "'Html.Raw(Model.Html)' writes 'Model.Html' to the response without HTML encoding.",
  "suggestion": "Let Razor encode the value (@Model.X) or sanitise it …",
  "file": "…/Views/Product/Index.cshtml", "line": 12, "column": 9,
  "snippet": "@Html.Raw(Model.Description)"
}
```

## Limiti

- È un'analisi **euristica**: nessun data flow. Un valore che arriva da un partial, da un view model o
  da JavaScript non viene tracciato; un risultato pulito non è una prova di sicurezza.
- Il markup dentro `@{ … }`/`@code { … }` non viene disceso (è C#).
- `id` duplicati e token anti-forgery sono verificati **per file**: se il token è in un partial
  renderizzato dentro il form, `RZ1005` può segnalarlo comunque.
- I valori dinamici (`method="@Model.Method"`) non vengono indovinati: le regole che ne dipendono
  tacciono invece di indovinare.
- I file oltre 2 MB e le cartelle non leggibili vengono riportati in `skipped`/`unreadable`, non
  analizzati.

## Aggiungere una regola

1. Aggiungere la voce in `RazorRuleCatalog.Rules` (id `RZxxxx`, categoria, severità, CWE se è di
   sicurezza, remediation) — i finding non possono esistere senza metadata.
2. Aggiungere il controllo a `RazorRules.Run`, con `Add(findings, document, "RZxxxx", message, line, column)`.
3. Aggiungere in `RazorTests` un caso che scatta e uno che **non** deve scattare.
4. Aggiornare la tabella in questo file.
