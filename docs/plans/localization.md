# Plan: localization (i18n) for ZGF.Gui, dogfooded on GitBench

Goal: make every user-facing string in GitBench translatable and switchable **at
runtime** with no restart, by mirroring the existing theme stack. Translations
live in **files** (translator-friendly); strongly-typed accessors are
**auto-generated** from those files (compile-time safety, no runtime missing-key).

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
2. **Strongly-typed accessors are generated from `en.json`** (Roslyn source
   generator; standalone tool as fallback — see Codegen). Values are loaded from
   the active locale's file at runtime; the generated type is the *schema*, the
   file is the *data*. This is the hybrid: type-safe keys + file-based translations.
3. **Build-time key parity validation.** The generator diagnoses any locale file
   that is missing/extra/param-mismatched vs `en.json`. This *is* the
   "find untranslated strings" tooling — free, and it fails the build.
4. **Runtime switch mirrors theme exactly:** `State<Locale>` → `LocalizationService`
   (`Derived<Strings>`) → `L.T(...)` deferred `Prop` → bound into `Text.Value`.
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

- **Plain string** → generated **property**.
- **String with `{placeholders}`** → generated **method** with one param per
  placeholder (name from placeholder; type `object`/`string` by default, see Open
  Questions for typed params).
- **Object keyed by plural categories** (`one`/`other`/… CLDR names) → generated
  **method** that selects a form via the locale's plural rule, then formats.

### Codegen (`GitBench.Localization.Generator`)

Recommended: a **Roslyn incremental source generator** consuming the `*.json`
files as `AdditionalFiles`. It emits:

- `partial class Strings` with a member per key
  (`commit.button` → `string CommitButton`,
  `files.stage_n` → `string StageN(int count)`).
- `StringsLoader.Load(Locale)` — parses the active locale's JSON into a frozen
  lookup and constructs a `Strings` bound to it.
- Compile diagnostics for key parity / param mismatches across locales.

Generated members are dict-backed lookups by constant key; because parity is
validated at build time, runtime missing-key is impossible:

```csharp
public string CommitButton => _s["commit.button"];
public string StageN(int count) => Plural(_culture, count, _s, "files.stage_n");
```

Fallback if source-generator friction is high: a tiny committed console tool
(`dotnet run`) or a pre-build MSBuild target that writes `Strings.g.cs`. Same
output, reviewable diff, slightly more manual. Start here if needed, promote later.

## Runtime architecture (mirror the theme stack)

```
Preferences.Language  ──load/save──►  PreferencesService.SetLanguage
        │
        ▼
State<Locale>  (registered in AppServices, like State<ThemeMode>)
        │
        ▼
LocalizationService : ILocalizationService
    _strings = new Derived<Strings>(() => StringsLoader.Load(_locale.Value));
    IReadable<Strings> Strings => _strings;
        │
        ▼
L.T(s => s.CommitButton)  ≡  Prop.Deferred(ctx => ctx.Localization().Strings.Bind(select))
        │
        ▼
new Text { Value = L.T(s => s.CommitButton) }   // re-renders on switch
```

New files (app-side, alongside the theme equivalents):

- `GitBench/Localization/Locale.cs` — `enum Locale { En, Fr, De, Es, Pseudo }`
  + `Locale → CultureInfo` map (for number/date formatting).
- `GitBench/Localization/ILocalizationService.cs` + `LocalizationService.cs`
  — mirrors `IThemeService<T>` / `ThemeService`.
- `GitBench/Localization/L.cs` — `L.T(...)` (mirror of `Widgets/Theme.cs`).
- `GitBench/Localization/LocalizationWidgetExtensions.cs` —
  `ctx.Localization()` (mirror of `ThemeWidgetExtensions.cs`).
- `GitBench/Localization/Strings/*.json` + generated `Strings.g.cs`.
- `GitBench/Localization/PluralRules.cs` + `Format.cs` (number/date/relative-time).

Wiring in `AppServices.cs` (right next to the theme block at lines 31-34):

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
count), compose with the existing compute form so both the count and the locale
are tracked:

```csharp
new Text { Value = Prop.Deferred(ctx =>
    Prop.Bind(() => ctx.Localization().Strings.Value.StageN(count.Value))) }
```

> Phase-1 verification: confirm `IReadable<T>.Bind(select)` runs `select` under
> dependency tracking (so reads inside `select` register too). If yes, the simple
> `L.T` form covers more dynamic cases; if not, use the compute form above.
> (`.Select` in `ReadableExtensions.cs:12` is the `IReadable`-returning sibling.)

### Formatters (`Format.cs`)

- Numbers/dates: `string.Format(culture, …)` / `ToString(culture)` using the
  active `Locale`'s `CultureInfo`.
- **Relative time**: replace the hardcoded `"5m ago"`/`"2d ago"` unit strings in
  `CommitsView` with `Format.RelativeTime(delta, strings, culture)` driven by
  catalog entries (`time.minutes_ago` plural, etc.). Keep the existing 30s refresh.

## Phased execution

### Phase 1 — Infra + live switch, English only (prove the loop)
- Add `Locale`, `State<Locale>`, `LocalizationService`, `L.T`, `ctx.Localization()`.
- Add `Preferences.Language` + `SetLanguage` + wiring in `AppServices`.
- Stand up the JSON catalog with `en.json` + codegen producing `Strings`.
- Add a **language menu item** (mirror the existing theme toggle in
  `Platform/PlatformServices.cs`); rebuild the **native macOS app menu** on switch
  (it's built once at startup — needs a rebuild hook).
- Convert **2 screens** (e.g. About dialog + one Features dialog) to `L.T`.
- Ship the **Pseudo locale** and switch to it to confirm the loop end-to-end.
- **Exit criteria:** switching language live re-renders the converted screens.

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
  untranslated keys.
- **Pseudo-localization** as the manual completeness + layout-expansion check.
- Unit tests: plural-rule selection per locale; `Format.RelativeTime` boundaries.
- A switch test: render a converted screen, flip `State<Locale>`, assert text
  changed (proves the reactive path, like a theme-swap test).

## Open questions / risks

1. **Typed params in generated methods.** Placeholders give names but not types.
   Options: default everything to `object` (simplest), or annotate types in the
   key (`"files.stage_n|count:int"`), or a sidecar schema. Recommend `object`
   for Phase 1, revisit if call sites want stronger typing.
2. **CLDR plural completeness.** .NET has no built-in CLDR plural rules. Hand-code
   selectors for the targeted locales now; consider a small lib if the language
   set grows.
3. **Catalog packaging.** Embed locale JSON as embedded resources (precedent:
   `EmbeddedAssets.LoadFontBytes`) vs. ship as content files (hot-reloadable). Lean
   embedded for release, optionally content in dev.
4. **`Bind` tracking semantics** — verify in Phase 1 (see Authoring API note).
5. **Section 4 rendering specifics** (surrogate bugs, fallback, BiDi) came from a
   sub-agent read of `RenderedCanvas`/`TextWrapper`; re-confirm exact locations when
   Phase 4 starts. The *directional* conclusion (shaping ✓, fallback ✗, RTL ✗) is
   solid; the reactive/Prop/Theme/Preferences findings above were verified directly.
