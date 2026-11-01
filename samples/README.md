# samples — runnable demonstrations

Each sample is a self-contained solution that exercises a part of VisualMCP against real code
instead of a synthetic snippet. Samples are **not** part of `VisualMCP.sln`: the product build is
unaffected, and the server discovers a sample as a second solution when it scans the working
directory (the root solution stays active until you call `load_solution`).

| Sample | Language | What it demonstrates |
|---|---|---|
| [VbDemo](VbDemo/README.md) | Visual Basic | the whole VB surface: load, build, test, run, navigation, text index, and the 15 `simplify_vb_code` rules on a deliberately unidiomatic `StyleGallery.vb` |
| [Cpp](Cpp/README.md) | C++ | the C++ metacode reader on real code: `metacode_translate` with `filter`/`clangArgs` (classes, templates, exceptions, operators, range-for — 0 diagnostics), `metacode_compare` against a C# port, and the `RAW[cpp]` list for what the model deliberately does not have |

## VbDemo

Three Visual Basic projects (`VbDemo.Core`, `VbDemo.Cli`, `VbDemo.Tests`, 27 MSTest tests) plus an
`.editorconfig` that sets `visual_basic_style_prefer_isnot_expression`, which is the direction
`IDE0084` needs. Every style rule has one member in `StyleGallery.vb`, and the tests pin the
behaviour of each member, so a rewrite is proven to preserve it.

```powershell
dotnet build samples\VbDemo\VbDemo.sln
dotnet test  samples\VbDemo\VbDemo.sln
```

The measured tool-by-tool walkthrough, including which features are still C#-only, is in
[`VbDemo/DEMO.md`](VbDemo/DEMO.md). If you run the style demo for real (`dryRun: false`), restore
the gallery afterwards with `revert_edit` or `git checkout -- samples/VbDemo/VbDemo.Core/StyleGallery.vb`;
do not tidy it by hand.

## Adding a sample

Keep the same shape: a solution that builds and tests on its own, a README that says what it
demonstrates, and — when it measures tool output — a `DEMO.md` with the commands and the observed
results. A sample that cannot be run from a clean checkout is documentation, not a sample.
