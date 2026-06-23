# Plan: localization (i18n) for ZGF.Gui, dogfooded on GitBench

Goal: make every user-facing string in GitBench translatable and switchable **at
runtime** with no restart, by mirroring the existing theme stack. Translations
live in **files** (translator-friendly); strongly-typed accessors are
**auto-generated** from those files (compile-time safety, no runtime missing-key).

## Status

- **Phase 1 — DONE** (builds clean, 3 xUnit tests pass; GUI re-render not yet
  eyeballed on macOS). Infra, source generator, preferences persistence, macOS
  Language menu, and the About dialog conversion are in. See the Phase 1 section
  for what shipped vs. deferred.
- **Phases 2–4 — not started.**

Key decision resolved during Phase 1: **the catalog bakes values into generated C#
at compile time (no runtime JSON)**, rather than loading per-locale files at
runtime. NativeAOT (Release) + the existing source-gen'd `System.Text.Json` made a
baked model the clean choice — it mirrors `ThemeStyles.Dark`/`.Light` static
instances exactly, has zero runtime parsing, and keeps files as the source of
truth. Trade-off: changing a translation requires a recompile (fine for a shipped
desktop binary). The runtime *language switch* is unaffected.

## Scope (phased, locked with user)

1. **Phase 1–3 — Latin only** (e.g. en, fr, de, es). Needs the i18n infra +
   string sweep + formatters. **No** font-fallback or RTL work.
2. **Phase 4 — CJK** (zh/ja/ko). Adds a font-fallback chain so non-Latin glyphs
   don't silently vanish. Architect for it now; build it later.
3. **Deferred — RTL** (ar/he). Needs BiDi reordering + layout mirroring. Out of
   scope until explicitly prioritized.

## Why this is tractable here

The reactive plumbing, the runtime-switch pattern, persistence, and the shaping
engine already exist — this is mostly assembly, not invention.

- **`Text` is already reactive.** `Text.Value` is `Prop<string?>`
  (`framework/ZGF.Gui/Widgets/Text.cs:8`) — accepts a constant, observable,
  projection, or context-deferred service value. **No widget changes needed.**
- **Theme is a working blueprint.** `ThemeService` wraps a `State<ThemeMode>` in a
  `Derived<ThemeStyles>` (`GitBench/Theming/ThemeService.cs:12-16`); widgets author
  themed values via `Theme.Color(s => s.X)` ≡
  `Prop.Deferred(ctx => ctx.Theme().Styles.Bind(select))`
  (`GitBench/Widgets/Theme.cs:16-17`). A theme swap already re-renders the whole
  tree. **Localization is this, 1:1.**
- **Service wiring is a known recipe** (`GitBench/App/AppServices.cs:31-34`).
- **Preferences are persistence-ready** (`GitBench/App/PreferencesService.cs`,
  `Preferences.cs`) — immutable record + debounced save; add one field.
- **Real HarfBuzz shaping exists** (`framework/ZGF.Fonts/FreeTypeFontBackend.cs`),
  so complex-script *shaping* already works; the gaps are fallback + BiDi.
- `Prop.cs`'s own doc already names **"theme, locale"** as the deferred use case.

## Locked design decisions

1. **Source of truth = per-language JSON files.** `en.json` is the canonical
   reference (defines the key set + parameter/plural shape). One file per locale.
2. **Strongly-typed accessors are generated from the JSON** (Roslyn source
   generator — DONE; the standalone-tool fallback proved unnecessary). Values are
   **baked into generated C# at compile time** as static `Strings.En` instances
   (and a derived `Strings.Pseudo`); the generated type is the *schema*, the file
   is the *data*. No runtime JSON parsing → NativeAOT-clean. (See Status for why
   baked beat the runtime-load model originally sketched below.)
3. **Build-time validation — PARTIAL.** Cross-locale key *parity* is still Phase 2
   (needs a second file to diff against `en.json`). But the Phase 1 generator already
   fails the build on the two single-locale invariants: a `Locale` enum case with no
   baked catalog (`LOC003`, drift guard) and a key that collides with another key or a
   built-in member (`LOC002`, e.g. a `for`/`en`/`pseudo` key). Both surface as clear
   diagnostics naming the offending key/case instead of cryptic `csc` errors.
4. **Runtime switch mirrors theme exactly — DONE:** `State<Locale>` →
   `LocalizationService` (`Derived<Strings>`) → `L.T(...)` deferred `Prop` → bound
   into `Text.Value`/`Label`.
5. **Locale persisted in `Preferences`**, like `Theme`.
6. **Non-translatable by policy:** git refs, branch/tag names, SHAs, file paths,
   raw git output. Only chrome/labels/messages are localized.
7. **Pseudo-locale ships from day one** as a dev tool (finds un-externalized
   strings + stress-tests layout expansion before any real translation exists).

## The catalog

### File format (`GitBench/Localization/Strings/en.json`)

```json
{
  "commit.button": "Commit",
  "dialog.create_tag.title": "Create tag",
  "reset.move_head": "Move the '{branch}' branch HEAD to {sha}",
  "files.stage_n": {
    "one": "Stage {count} File",
    "other": "Stage {count} Files"
  }
}
```

- **Plain string** → generated **property**. *(Phase 1: the only form the generator
  currently supports.)*
- **String with `{placeholders}`** → generated **method** with one param per
  placeholder (name from placeholder; type `object`/`string` by default, see Open
  Questions for typed params). *(Phase 2.)*
- **Object keyed by plural categories** (`one`/`other`/… CLDR names) → generated
  **method** that selects a form via the locale's plural rule, then formats.
  *(Phase 2.)*

### Codegen (`GitBench.Localization.Generator`) — DONE (Phase 1)

A **Roslyn incremental source generator** (netstandard2.0 analyzer, referenced with
`OutputItemType="Analyzer" ReferenceOutputAssembly="false"`) consumes `en.json` as
an `AdditionalFile` and emits `Strings.g.cs`. It reads the JSON at compile time with
a tiny self-contained reader (`MiniJson`, flat string pairs only) — no JSON
dependency inside the analyzer, no JSON at app runtime.

It emits a `partial class Strings` with:

- one `required string` member per key (`about.view_on_github` → `AboutViewOnGithub`),
- a baked `static readonly Strings En` with the English literals,
- a derived `static readonly Strings Pseudo` (accented + length-padded English),
- `static Strings For(Locale locale)` switching over the baked instances; its default
  arm **throws** (`ArgumentOutOfRangeException`) rather than silently returning English,
  so an enum case the generator didn't bake a catalog for fails loudly.

The generator also reads the hand-authored `Locale` enum (the `Strings` catalog isn't its
only input) and emits `LOC003` for any case it has no catalog for — enum and catalog can't
silently drift. (The enum stays hand-authored because the `System.Text.Json` generator that
serializes `Preferences` references `Locale` and can't see a *generated* enum.)

```csharp
public required string AboutViewOnGithub { get; init; }
public static readonly Strings En = new() { AboutViewOnGithub = "View on GitHub", … };
public static readonly Strings Pseudo = new() { AboutViewOnGithub = "[Víéw óñ GítHúb ····]", … };
public static Strings For(Locale locale) => locale switch { Locale.Pseudo => Pseudo, _ => En };
```

This mirrors `ThemeStyles.Dark`/`.Light`: the `Derived<Strings>` in the service just
calls `Strings.For(locale.Value)`. Referencing a key that isn't in `en.json` is a
compile error. *(The standalone-tool fallback was not needed; the source generator
wired up cleanly under net10.)*

## Runtime architecture (mirror the theme stack)

```
Preferences.Language  ──load/save──►  PreferencesService.SetLanguage
        │
        ▼
State<Locale>  (registered in AppServices, like State<ThemeMode>)
        │
        ▼
LocalizationService : ILocalizationService
    _strings = new Derived<Strings>(() => Strings.For(_locale.Value));
    IReadable<Strings> Strings => _strings;
        │
        ▼
L.T(s => s.AboutViewOnGithub)  ≡  Prop.Deferred(ctx => ctx.Localization().Strings.Bind(select))
        │
        ▼
new Text { Value = L.T(s => s.AboutViewOnGithub) }   // re-renders on switch
```

Files (app-side, alongside the theme equivalents):

- `GitBench/Localization/Locale.cs` — DONE. Hand-authored `enum Locale { En, Pseudo }`
  (must stay hand-authored — the `System.Text.Json` source gen for `Preferences` can't
  see a generated enum); the localization generator reads it and `LOC003`-fails the build
  if a case has no catalog. Grows as locales are added. A `Locale → CultureInfo` map
  arrives with formatting in Phase 2.
- `GitBench/Localization/ILocalizationService.cs` + `LocalizationService.cs` — DONE.
  Mirrors `IThemeService<T>` / `ThemeService`.
- `GitBench/Localization/L.cs` — DONE. `L.T(...)` mirror of `Widgets/Theme.cs`.
- `GitBench/Localization/LocalizationWidgetExtensions.cs` — DONE. `ctx.Localization()`.
- `GitBench/Localization/Strings/en.json` + generated `Strings.g.cs` — DONE.
- `GitBench.Localization.Generator/` (separate analyzer project) — DONE.
- `GitBench/Localization/PluralRules.cs` + `Format.cs` (number/date/relative-time)
  — Phase 2, not yet created.

Wiring in `AppServices.cs` (right after the theme block) — DONE:

```csharp
var locale = new State<Locale>(preferences.Current.Language);
locale.Changed += preferences.SetLanguage;
context.AddService(locale);
context.AddSingleton<ILocalizationService, LocalizationService>();
```

`Preferences.cs`: add `public Locale Language { get; init; } = Locale.En;`.
`PreferencesService.cs`: add `public void SetLanguage(Locale l) => Mutate(p => p with { Language = l });`.

## Authoring API

```csharp
// static label
new Text { Value = L.T(s => s.CommitButton) }

// parameterized
new Text { Value = L.T(s => s.ResetMoveHead(branchName, shortSha)) }

// plural (count is constant at build of this widget)
new Text { Value = L.T(s => s.StageN(fileCount)) }
```

For a string that *also* depends on other reactive state (e.g. a `State<int>`
count): **resolved** — `PropExtensions.Bind(select)` runs `select` inside a tracked
`Prop.Bind(() => …)` compute (`Prop.cs:217-218`), so reads of other observables
*inside* the selector register as dependencies too. That means the plain `L.T`
form already covers dynamic cases — `L.T(s => s.StageN(count.Value))` re-fires on
both a locale change and a `count` change. No special compute form needed.

### Formatters (`Format.cs`)

- Numbers/dates: `string.Format(culture, …)` / `ToString(culture)` using the
  active `Locale`'s `CultureInfo`.
- **Relative time**: replace the hardcoded `"5m ago"`/`"2d ago"` unit strings in
  `CommitsView` with `Format.RelativeTime(delta, strings, culture)` driven by
  catalog entries (`time.minutes_ago` plural, etc.). Keep the existing 30s refresh.

## Phased execution

### Phase 1 — Infra + live switch, English only (prove the loop) — DONE
Shipped:
- ✅ `Locale`, `State<Locale>`, `LocalizationService`, `L.T`, `ctx.Localization()`.
- ✅ `Preferences.Language` + `SetLanguage` + persistence in `PreferencesStore`
  (string-enum) + wiring in `AppServices`.
- ✅ JSON catalog `en.json` + source generator producing `Strings` (En + Pseudo).
- ✅ macOS **Language menu items** in the View menu (English / Pseudo) that flip
  `State<Locale>`.
- ✅ **Pseudo locale** for finding un-localized text / layout testing.
- ✅ About dialog converted (`AboutViewOnGithub`, `AboutCopyright`).
- ✅ 3 xUnit tests proving the reactive switch (`LocalizationServiceTests`).

Deferred out of Phase 1 (rolled into later phases):
- ⏳ **Native macOS menu rebuild on switch.** The menu *has* language items, but the
  menu's own titles are still hardcoded English and don't re-render on switch
  (native bar is built once at startup). Rebuild hook → Phase 3.
- ⏳ **Second screen.** Only the About dialog was converted (kept the increment
  small); the broader screen conversion is the Phase 3 sweep.

- **Exit criteria — met (modulo eyeballing):** build is clean and the reactive
  switch is proven by tests. The on-screen re-render (open About → View → "Language:
  Pseudo (test)") still needs a manual run on macOS (`dotnet run --project GitBench`).

### Phase 2 — Second Latin language + formatting correctness
- Add `fr.json` (or de). Implement `PluralRules` for targeted locales
  (Latin set is mostly one/other; fr folds 0,1→one).
- Convert the relative-time formatter + a count/plural-heavy screen
  (`LocalChanges` context menus: `Stage N Files`, etc.).
- Validate number/date formatting via `CultureInfo`.
- **Exit criteria:** plurals, interpolation, and dates correct in two languages.

### Phase 3 — Sweep the string surface (~190 static + ~86 interpolated, ~50–70 files)
- Go feature-by-feature: `Features/**/*Dialog.cs` (~19 dialogs), the app menu,
  `Controls` labels/tooltips/placeholders, error messages (centralize as you go).
- Refactor inline-ternary plurals and `string.Join(" and ", …)` into catalog
  methods.
- Use the Pseudo locale repeatedly to surface stragglers (un-wrapped strings render
  in plain ASCII; wrapped ones look accented/padded).
- **Exit criteria:** Pseudo locale shows no plain-ASCII UI strings; build passes
  key parity.

### Phase 4 — CJK (when prioritized)
- Fix the **surrogate-pair bugs** first (cheap, also fixes emoji in English):
  `TextWrapper` and `TextView.Ellipsize` iterate by UTF-16 `char` index → switch to
  `Rune` enumeration.
- Build a **font-fallback chain** at the shape/draw layer in
  `framework/ZGF.Gui` (`RenderedCanvas`)/`framework/ZGF.Fonts` so a glyph missing
  from the UI font resolves from a fallback (CJK/emoji) font instead of being
  silently dropped.
- Add CJK fonts to the registration step; add `zh`/`ja`/`ko` catalogs.

### Deferred — RTL (ar/he)
- BiDi reordering + right-origin line layout + alignment/scrollbar/icon mirroring.
  Largest effort; not started until explicitly prioritized.

## Testing & tooling

- **Build-time key parity** (from the generator) is the primary safety net for
  untranslated keys. *(Phase 2 — needs a second locale to cross-check.)*
- **Pseudo-localization** as the manual completeness + layout-expansion check. ✅
  shipped (the `Pseudo` locale).
- Unit tests: plural-rule selection per locale; `Format.RelativeTime` boundaries.
  *(Phase 2.)*
- A switch test: flip `State<Locale>`, assert the catalog value changed (proves the
  reactive path, like a theme-swap test). ✅ done — `LocalizationServiceTests`.

## Open questions / risks

1. **Typed params in generated methods.** Placeholders give names but not types.
   Options: default everything to `object` (simplest), or annotate types in the
   key (`"files.stage_n|count:int"`), or a sidecar schema. Recommend `object`
   for Phase 1, revisit if call sites want stronger typing.
2. **CLDR plural completeness.** .NET has no built-in CLDR plural rules. Hand-code
   selectors for the targeted locales now; consider a small lib if the language
   set grows.
3. **Catalog packaging — RESOLVED.** Values are baked into generated C# at compile
   time; there are no locale files at runtime to embed or ship. (Originally framed
   as embedded-resource vs. content-file; the baked model makes it moot. The only
   cost is recompile-to-change-a-translation, accepted in Status.)
4. **`Bind` tracking semantics — RESOLVED.** `PropExtensions.Bind` runs the
   selector inside a tracked compute (`Prop.cs:217-218`), so `L.T` tracks
   observables read inside the selector. See Authoring API.
5. **Section 4 rendering specifics** (surrogate bugs, fallback, BiDi) came from a
   sub-agent read of `RenderedCanvas`/`TextWrapper`; re-confirm exact locations when
   Phase 4 starts. The *directional* conclusion (shaping ✓, fallback ✗, RTL ✗) is
   solid; the reactive/Prop/Theme/Preferences findings above were verified directly.
6. **Generator schema is flat-string-only today.** Plural/parameterized entry
   support (object values, `{placeholders}` → methods) and cross-locale key-parity
   diagnostics both land in Phase 2 when the second locale arrives.
