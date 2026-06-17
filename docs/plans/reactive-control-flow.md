# Reactive Control Flow: `Show` / `Switch`

> Design from session on 2026-06-16, after the `Prop<T>` unification. Adds reactive
> *structural* control flow — swap a subtree when state changes — without a Flutter-style
> reconciler. `Prop<T>` already handles "a **value** changes"; this handles "the **shape**
> changes." `Each` already handles "a **list** changes."

## The core insight (why no reconciler)

ZGF is a **fine-grained reactive** framework (`State`/`Derived`/`Prop` = signals/memos),
i.e. the SolidJS / Svelte / Knockout family — **not** the React / Flutter VDOM family. The
VDOM family rebuilds subtrees and **diffs** them (Element tree, per-widget `Key`, `canUpdate`,
update-in-place) *because they have no fine-grained reactivity* and can't tell what changed.
We do. So we copy what Solid/Svelte actually do, which is **not** reconcile:

1. **Memoized conditionals** (`<Show>`, `{#if}`) — run the structural *decision* in a memo;
   swap the whole branch **only when the decision's value changes**.
2. **Scoped disposal** — the outgoing branch's subscriptions tear down; incoming builds fresh.
   ZGF already has this: it's `Unmount`.
3. **Reference-keyed lists** (`<For>`, keyed `{#each}`). ZGF already has this: it's `Each`.

**State preservation comes from memoization, not reconcile.** If the decision value is
unchanged, the branch is never rebuilt, so its focus/scroll/animation survive automatically —
not because a diff matched nodes, but because nothing recomputed. You only lose state when the
decision *actually changes*, which is exactly when a fresh subtree is wanted.

→ The heavyweight reconciler design (Element layer, per-widget `Key`, `canUpdate`, children
diff, `Prop.Reconcile`) is **rejected**. It solves a problem memoization already solves for ~all
real cases, at multiple weeks + viral per-widget cost. Do **not** build it.

## The design (≈80 lines, rides existing infra)

Two widgets over one behavior. `Subscribe` is already memoized (`State`/`Derived` only fire on
real value change); `Children.Add/Remove` already mount/unmount (attach/dispose bindings).

```csharp
// Memoized region: rebuilds its single child ONLY when the discriminator value changes.
internal sealed class SwapRegion<T>(Context ctx, SlotView host, IReadable<T> key, Func<T, IWidget> build) : IViewBehavior
{
    IDisposable? _sub; View? _current;
    public void Attach(View _) => _sub = key.Subscribe(k => {   // fires only on VALUE change (memoized)
        if (_current != null) host.Children.Remove(_current);   // unmount old → disposes its bindings
        _current = build(k).BuildView(ctx);
        host.Children.Add(_current);                            // mount new
    });
    public void Detach(View _) { _sub?.Dispose(); _current = null; }
}

public sealed record Show : Widget {
    public required IReadable<bool> When { get; init; }
    public required Func<IWidget> Then { get; init; }
    public Func<IWidget>? Else { get; init; }
    protected override View CreateView(Context ctx) {
        var host = new SlotView();
        host.Behaviors.Add(new SwapRegion<bool>(ctx, host, When,
            on => on ? Then() : (Else?.Invoke() ?? Empty.Widget)));
        return host;
    }
}

public sealed record Switch<T> : Widget {
    public required IReadable<T> Value { get; init; }
    public required Func<T, IWidget> Case { get; init; }
    public bool KeepAlive { get; init; }     // see "three shapes"
    protected override View CreateView(Context ctx) {
        var host = new SlotView();
        host.Behaviors.Add(new SwapRegion<T>(ctx, host, Value, Case));  // KeepAlive variant differs (below)
        return host;
    }
}
```

`SlotView` is a thin container view holding exactly one child slot (a trivial `View` subclass
exposing `public Children`). `Empty.Widget` is a zero-size no-op widget for the absent case.

Accept `Func<bool>`/`Func<T>` overloads too (wrap in a `Derived` to memoize a multi-source
decision), mirroring `Prop`'s constant-vs-`Prop.Bind` shapes.

## "Keys" without keys

The one real use of a key — "swap the detail pane when the selected **id** changes, keep it
when only contents change" — is just **what you choose to memoize on**, at the region:

```csharp
new Switch<Guid> {
    Value = vm.Selected.Select(s => s.Id),       // memoize on the id  ← this IS the key
    Case  = _ => new DetailPane { Item = vm.Selected },  // Item is a Prop → rebinds without rebuild
}
```

Same id → no swap (state kept). New id → swap (fresh pane). No `Key` type, no per-widget
plumbing. The complementary "keep the pane, show new data" case isn't a `Show`/`Switch` concern
at all — pass the data as a `Prop` and the binding updates it.

## The three shapes (decision table)

The survey of GitBench (below) found conditional structure splits into three buckets; only two
are `Show`/`Switch`:

| Shape | Want | Tool |
|---|---|---|
| **Swap** — build branch, tear down when gone | unmount hidden branch (free its bindings) | `Show` / `Switch` (default) |
| **Keep-alive swap** — both branches stay live, toggle shown | hidden branch keeps VM/subscriptions running | `Show` / `Switch` + `KeepAlive` |
| **Repaint** — one heavy view, always mounted, redraw contents | never swap; change what the single view shows | **`Prop`** (NOT `Show`) |

`KeepAlive` is **load-bearing, not optional** — `MainContentView` depends on it (inactive view
stays mounted so its VM keeps listening; otherwise a reload flash on switch). `KeepAlive`
implementation: cache built branch views in a `Dictionary<T, View>`; on switch, unmount-but-
**don't-dispose** the outgoing one and mount the cached/new incoming. Trade-off: cached branches
keep subscriptions live while hidden (they update off-screen). Off by default.

Do **not** convert bucket-3 sites (`CommitsView`, `LocalChangesPanel` empty state) — they
deliberately keep one heavy view mounted and just repaint; that's `Prop`, not `Show`.

## Locked decisions

1. **No reconciler / no Element tree / no per-widget `Key`.** Memoize on a discriminator at the
   region; swap whole subtrees. State preserved by memoization (stable decision → no rebuild).
2. **Two primitives**: `Show` (bool) and `Switch<T>` (value). `Each` stays as-is for lists
   (it's already reference-keyed reconcile; share nothing for now, converge later).
3. **`KeepAlive` flag** on `Switch` (and `Show`) for the keep-both-mounted case. Default off.
4. **Memoize on the discriminator, not on `Derived<IWidget>`.** Memoizing the produced widget
   would refire on every fresh closure; memoizing the bool/enum/key swaps only on real change.
5. **Compose with `Prop`**: in the build closure, reading `.Value` = "rebuild on this"; passing
   an observable into a child `Prop` = "bind it, don't rebuild." Keeps rebuilds rare + surgical.

## Known limitation (shared with Solid/Svelte)

A *stateful* widget that moves between structural positions (e.g. a focused input that's bare in
one branch, wrapped in a container in another) is destroyed+rebuilt on swap, losing focus. Solid
and Svelte don't preserve that either. Idiom: hoist the stateful widget out of the conditional,
or drive its state via a `Prop`/`State` so the rebuild is harmless. Rare; accept it.

## Invariants / gotchas

- **Never remount a reused view.** Reuse keeps it mounted; only swap/keep-alive toggle touch
  mount state. (Honors the "widget-built views are single-mount" rule.)
- **Re-entrancy**: a build closure must not synchronously write the state it's memoized on.
- Replaced/removed branches must dispose any `Context` scope they own (mirror `Each` disposal)
  if keyed-children scoping is ever added; plain `Show`/`Switch` build against `ctx` directly.

## Implementation order (each step independently shippable + testable)

1. `SlotView` + `Empty` + `SwapRegion<T>` + `Show`. Prove on **target #1** (a lone banner).
2. `GroupHeaderRow` rename (**#2**) — validates swap of a *stateful* subtree (rename field focus).
3. `Switch<T>` + `KeepAlive`. Prove on **#3** (`MainContentView`) — the reload-flash test.
4. **#4** (`OperationBannerView`) — memoization/animation stress test (spinner must not restart).

## Test targets (ranked) — from the 2026-06-16 GitBench sweep

1. **Lone banner — smoke test (`Show<bool>`).** `DetachedHeadBannerView.cs:71`,
   `ErrorBarView.cs:22`, or `SubmoduleStatusBannerView.cs:74`. Single widget toggled by
   `Visible = Prop.Bind(() => cond)`. `Show { When = cond, Then = () => banner }` swaps instead
   of keeping a hidden view mounted. No state to preserve — validates build/swap/dispose.
2. **`GroupHeaderRow` rename — poster child (`Show<bool>`).** `GroupHeaderRow.cs:35-38` already
   hand-rolls `Show` via `nameSlot.Children.BindChildren(() => new[]{vm.IsRenaming.Value}, …)`.
   Replace with `Show { When = vm.IsRenaming, Then = () => new GroupRenameField{…}, Else = () => name }`.
   The `Then` branch is **stateful** (rename field needs focus) → tests swapped-in mount /
   swapped-out dispose. Currently view-land `CreateView`; decide during impl whether to lift the
   name slot to widget-land or add a view-level `Show` helper.
3. **`MainContentView.cs:28-33` — enum `Switch` + `KeepAlive` test.** `Switch<MainViewMode>`,
   History ↔ LocalChanges. **Needs `KeepAlive=true`** (comment: inactive view stays mounted so
   its VM keeps listening — else reload flash, which is the observable failure if KeepAlive is wrong).
4. **`OperationBannerView.cs:95-131` — stretch (`Switch<RepoOperationState>` + `Show`).**
   Per-state label, conditional Continue button, **spinner animating while busy**. When state is
   unchanged (busy→busy), memoization must keep the spinner view so the animation doesn't restart.
5. **Dialog empty-state — clean swap (`Show<bool>`).** `DiscardChangesDialog.cs:74-88` or
   `StashDialog.cs:82-96`: `files.Count == 0 ? placeholder : rows`. Self-contained, swap-correct.
   (NB: `LocalChangesPanel`/`FileChangesSection` look similar but keep the list mounted for scroll
   → those are `KeepAlive`, not plain `Show`.)

### Other sites found (not first targets)
- More lone `Show<bool>`: `ActionButton.cs:99` (badge), `UpdateBannerView.cs:37`,
  `LfsBadgeView.cs:48`, `RowChrome.cs:31`, `DiffWindowToolbar.cs:143`, split-pane toggles in
  `CommitDetailsView.cs:108,162` and `LocalChangesContentView.cs:147,210,222`.
- Enum `Switch` render-states: `CommitDetailsView.cs:166-177`, `DiffView.cs:64-75`
  (Placeholder/Loaded/Conflict).
- Derived `BindChildren` (coarse swap): `GroupSection.cs:23`, `RepoEntry.cs:32`,
  `SubmoduleEntry.cs:33`, `WorktreeEntry.cs:30`.
- **Repaint (bucket 3 — leave alone)**: `CommitsView.cs:233-248,362-375`,
  `LocalChangesPanel.cs:257-267` keep one heavy view mounted and redraw — use `Prop`, not `Show`.
