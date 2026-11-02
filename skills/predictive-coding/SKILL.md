---
name: predictive-coding
description: Use when working on the Predicting PCN solution (C:\REPOSITORY\Predicting) — adding a layer, changing an update rule, wiring MoE routing, introducing persistence or touching tensor math. Fixes the six design invariants of the network (free-energy minimization, local Hebbian updates only, temporal continuity, energy-based gating, lazy FASTER persistence, SIMD via System.Numerics.Tensors) and the checks that prove them.
---

# Predictive coding network — design invariants

Solution: `C:\REPOSITORY\Predicting\Predicting.slnx` — project `Predicting`, net10.0, PublishAot.
Layout: `PCN.cs` (inference/training loop) · `Layer.cs` (weights, activity, error, precision) ·
`Extensions/TensorOps.cs` (tensor math) · `Program.cs` (demo: learns `y = 2x`).

These six are constraints on the code, not style preferences. When a change cannot satisfy one,
stop and report the conflict instead of working around it.

## 1. Free energy minimization (Bogacz 2017, Friston 2005)

The only objective is variational free energy; the only error signal is the precision-weighted
prediction error `E = Π ⊙ (prediction − μ)`.

- Every residual is precision-weighted. An unweighted error term is a bug.
- No scalar loss differentiated globally; no objective object passed around.
- Inference runs until the free-energy delta is below a threshold, not merely for a fixed count.
- Verify: with weights frozen, `F = Σ Π ⊙ (pred − μ)²` must not increase across inference steps,
  and `E` must equal `−∂F/∂μ`. A test that removes `Π` from `E` must fail.

## 2. No global backpropagation — local Hebbian rules only

All learning is local. Weights change only in `Layer.UpdateWeights`:
`ΔW = η · E ⊗ x` (bottom-up), `ΔV = η · E ⊗ y_upper` (top-down).

- No autodiff package, no `Backward()`, no gradient buffer, no loss graph, no reverse pass.
- `PCN.Train` may iterate inference and then apply local updates; it may not compute `dL/dW`.
- Weight tensors are not writable from outside the layer that owns them.
- Verify: no gradient/autodiff type appears in the solution; weight writes are confined to
  `UpdateWeights`; no method signature takes a loss or a gradient.

## 3. Temporal continuity (Friston 2008)

The activity update carries the previous state:
`Δμ = dt · γ · E + Π_t(μ − μ_prev)`.

- Each layer keeps `μ_prev`; time advances in exactly one place (`UpdateActivity`).
- Do not recompute `μ` from scratch at every step.
- Verify: with identical `E`, a two-step update differs from the memoryless update by the
  temporal term; removing `μ_prev` must fail a numeric test.

## 4. Energy-based gating

MoE routing is `g = softmax(−β · F)`, one gate entry point, one softmax implementation.

- Gates are normalised, sum to 1, and sharpen as `β` grows (`β → ∞` approaches argmax).
- No hand-rolled top-k routing over unnormalised scores; no second softmax.
- Verify: `sum(g) = 1`, monotonicity in `−β·F`, temperature/`β` test.

## 5. Lazy FASTER persistence

State lives in an in-memory cache; the FASTER log is written in batches, never per inference step.

- One store abstraction owns checkpoint/restore; the engine performs no `System.IO` calls.
- Never flush inside `Train` or the inner inference loop.
- Verify: N updates produce a bounded number of flushes (fake store counts them); a round-trip
  restores bit-identical state; a crash between flushes loses at most one batch.

## 6. SIMD via `System.Numerics.Tensors`

Hot tensor math goes through `TensorPrimitives` over spans.

- No scalar element-by-element loops over tensors in the hot path; no per-element `int[]`
  allocation (the current `Indices()` helper allocates one array per element).
- `Microsoft.ML.OnnxRuntime`'s `DenseTensor` is not the engine's tensor type; the package is
  present only because `DenseTensor` comes from it today.
- Verify: hot methods call `TensorPrimitives`; outputs match a naive reference within tolerance;
  benchmark shows no throughput/allocation regression.

## Current compliance (2026-11-02 — re-check before trusting)

| Invariant | State | Evidence |
|---|---|---|
| Free energy | Partial | `Layer.ComputeError` computes `Π ⊙ (pred − μ)`; no explicit `F`, no convergence criterion |
| Local Hebbian | Yes | `Layer.UpdateWeights` only; no autodiff anywhere |
| Temporal continuity | No | `UpdateActivity` is memoryless (`A += dt·γ·E`) |
| Energy gating | No | no router / MoE type exists |
| Lazy FASTER | No | no FASTER reference in `Predicting.csproj` |
| SIMD | No | `TensorOps` is scalar `foreach`; `System.Numerics.Tensors` is imported in `Program.cs` but unused |

## Where each invariant must live

| Invariant | Single place that owns it |
|---|---|
| Free energy | `Layer.ComputeError` (E) + the inference loop in `PCN.Train` |
| Local Hebbian | `Layer.UpdateWeights` |
| Temporal continuity | `Layer.UpdateActivity` (+ a private `μ_prev`) |
| Energy gating | a dedicated router/gate type, not `PCN` |
| Lazy FASTER | a persistence/store type; engine stays IO-free |
| SIMD | `TensorOps`, rewritten over `TensorPrimitives`/spans |

## Automated enforcement

VisualMCP ships the `pcn` inspection pack — four Roslyn analyzers run through `inspect_code` (and
`background_analysis`) with no analyzer package in the inspected project:

| Rule | Invariant it enforces |
|---|---|
| PCN0001 | an error stored without the precision weight (`E = Π ⊙ residual`) |
| PCN0002 | a weight written outside the local update method, or any autodiff/backprop API |
| PCN0003 | an activity update with no `μ_prev` temporal term |
| PCN0004 | gating that is not `softmax(−β·F)`, or a softmax argument that is not `−β·F` |

The pack is auto-selected when the solution or a project is named like a PCN (`Predicting`,
`PredictiveCoding`, …); `packs=pcn` forces it anywhere. The rules are scoped to types with a PCN
signature (precision + error + activity/weight members, or a PCN name), so they do not fire on
unrelated code.

## Review checklist

1. `build_project` on `Predicting.slnx` — compiler + analyzer gate.
2. `inspect_code packs=pcn` (or the default run on a PCN-named solution) — PCN0001–PCN0004 are the
   hard gate for the four invariants above.
3. `find_performance_anti_patterns` — string/LINQ/allocation loops in hot paths.
4. `custom_inspections` with `.visualMCP/inspections.json` rules for extra repository-specific bans
   (`System.IO` in engine files, direct tensor writes) — the pcn pack already covers autodiff APIs.
5. `run_tests` — the invariant tests above (free-energy descent, gradient identity, temporal
   term, softmax properties, flush count, SIMD reference match).

If a principle has no test that fails when it is violated, it is documentation, not a constraint.
