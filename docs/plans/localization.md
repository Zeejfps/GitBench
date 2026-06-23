# Plan: localization (i18n) for ZGF.Gui, dogfooded on GitBench

Goal: make every user-facing string in GitBench translatable and switchable **at
runtime** with no restart, by mirroring the existing theme stack. Translations
live in **files** (translator-friendly); strongly-typed accessors are
**auto-generated** from those files (compile-time safety, no runtime missing-key).

## Status

- **Phase 1 — DONE** (builds clean, tests pass; GUI re-render not yet eyeballed on
  macOS). Infra, source generator, preferences persistence, macOS Language menu,
  About dialog conversion.
- **Phase 2 — DONE** (builds clean, 112 tests pass incl. 11 localization). Generator
  now supports plural + parameterized entries; `PluralRules`/`PluralForms`/`Format`
  added; real `es.json` (Spanish) translation set; relative-time formatter and the
  LocalChanges plural context menus converted. `LOC004` parity verified to fail the
  build on a missing translation.
- **Phase 3 — DONE (modulo a small documented tail + macOS eyeball).** Build clean,
  113 tests pass. The full string surface was swept feature-by-feature: catalog grew
  from ~19 to **448 keys** (`en.json` + `es.json`, full parity, no identifier
  collisions). Converted: the native macOS menu (now **rebuilds on locale switch** —
  the Phase 1 deferral), every Branches/Commits/Diff/LocalChanges/Stash/Repos/
  Operations/Submodules/Worktrees/Identity/StatusBar/Toolbar/App surface (~55 files),
  and the shared dialog widgets (`Dialog`/`DialogShell` Cancel, copy/close tooltips now
  resolve `Common*` while honoring call-site overrides). Custom-painted views
  (`BranchesView`, `CommitsView`, `CommitDetailsView`, `DiffContentView`,
  `DiffWindowToolbar`) now **rebind `loc.Strings` → rebuild row data + `SetDirty()`**, so
  the Phase 2 custom-paint caveat is resolved. Context-menu VMs
  (`BranchesViewModel`, `RepoNodeViewModel`, `StatusBarViewModel`, …) take an injected
  `ILocalizationService`; menus rebuild on open. Bold menu-label segments (Merge/Rebase/
  Fast-forward) are preserved by re-bolding the interpolated values inside the *localized*
  label (`BoldSegments`). Error-dialog titles across all features are localized.
- **Phase 4 — DONE (implementation; the macOS GUI eyeball is the only open verification).**
  Build clean, **119 tests pass** (6 new). Shipped:
  - **Surrogate-pair fix** in `TextView.Ellipsize` — the binary-search truncation now refuses to
    cut inside a surrogate pair, so a truncated CJK/emoji string never ends on an orphaned high
    surrogate (tofu). (Re-confirmed the other two suspected sites are *not* surrogate-buggy:
    `TextWrapper.WrapLine` splits only on `' '` and `RenderedCanvasBase.MeasureTextWidth` only on
    `'\n'` — both BMP code units that can't fall inside a surrogate pair. The plan's claim on those
    two was over-stated. `TextWrapper`'s real CJK gap is *line-breaking*, not surrogates — deferred.)
  - **Font-fallback chain at the shape layer.** `ShapedGlyph` gained a per-glyph `FontId`;
    `FreeTypeFontBackend.ShapeText` now itemizes a line by **cmap coverage** across the primary
    plus `RegisterFallbackFont`-registered fonts (`SelectFontIndex` → first font whose
    `FT_Get_Char_Index` is non-zero, else primary), shapes each run with the font that covers it,
    and tags every glyph with its source font. The canvas draw loop rasterizes each glyph from
    `new FontHandle(sg.FontId)`. The glyph cache is already keyed `(fontId, glyphIndex)`, so no
    collisions; the existing shape cache stores the merged multi-font result. Fallbacks are
    resolved at the primary's pixel size / weight per call, so substituted glyphs match.
  - **`.ttc` collection support** in the loader: `LoadFontFromFile/Memory` take a `faceIndex`,
    stored on `FontEntry` and reused when deriving sized/emboldened variants (CJK system fonts
    are TrueType Collections).
  - **Per-platform OS-font resolution** (user decision: use system fonts, no bundling).
    `GitBench/Platform/SystemFonts.cs` returns ordered CJK candidates per OS (macOS Hiragino Kaku
    Gothic W3 → Hiragino GB → PingFang → Apple SD Gothic; Windows Yu Gothic/Meiryo/MS Gothic/
    Malgun/YaHei; Linux Noto CJK). `GuiApp.RegisterFallbackFont(path, size, faceIndex)` loads it
    onto the shared backend (so popups see it too); `Program.cs` wires it at startup (try/catch,
    non-fatal if absent).
  - **First CJK locale = Japanese** (smoke-test, mirroring how `es` proved the pipeline).
    `Locale.Ja` + full **`ja.json` (449 keys, parity verified — no missing/extra/shape/placeholder
    drift)** + the `View → Language: 日本語` menu item (rebuilds on switch like the others).
    Culture auto-derives from the `ja` stem. **No `PluralRules` change needed**: Japanese keeps
    `one`/`other` forms (bare-verb singular vs counted) and the generic `n==1→one` rule reproduces
    the English UX; Japanese has no grammatical plural so the counted "other" reads fine for all
    n>1. Persistence is automatic (`PreferencesStore` uses `UseStringEnumConverter`).
  - **Tests:** `FontFallbackTests` (loads real Helvetica + Hiragino, proves a mixed `"Aあ"` line
    splits across two font ids with non-`.notdef` glyphs, and that without the fallback `あ` stays
    glyph 0; plus the resolver finds a real font on macOS), Japanese catalog bake + live switch,
    Japanese plural/param/culture.
  - **Carried into Phase 5:** CJK **line-breaking** (no-space wrapping — `TextWrapper.WrapLine`
    only inserts break opportunities at `' '`, so a spaceless CJK string never wraps) and the
    `zh`/`ko` catalogs (the resolver + plumbing already cover them).
  - **Open verification (Phase 5 exit gate):** the macOS GUI eyeball (View → Language: 日本語 and
    confirm CJK renders via the Hiragino fallback — the load+shape path is proven by
    `FontFallbackTests`, only the on-screen render is unverified).
  - **Out of scope:** **color emoji** (the atlas/shader are single-channel alpha; emoji `.ttc` is
    rejected by the `FT_PIXEL_MODE_GRAY` check → dropped, not crashed).

### Phase 3 remaining tail (small, self-contained follow-ups)
These were either missed by the inventory sweep or deliberately deferred; none are
load-bearing and the build/tests are green without them:
- **`GitBench/Git/RefNameRules.cs`** — ~5 field-validation messages (`"{noun} names
  can't contain spaces."`, etc.) shown under name inputs. A shared static helper called
  inside `Derived<FieldStatus?>` computations; needs a `Strings`/`loc` thread-through to
  localize + live-switch. Not in any inventory.
- **`ResetCommitDialog` dirty-state hint** (`BuildDirtyHint`) — an assembled nested-plural
  string ("You have N staged and M unstaged local change(s)."); needs dedicated plural keys.
- **Bare `"Files"` header (count == 0)** in `DiscardChangesViewModel`/`StashDialogViewModel`
  — the `Files ({shown}/{total})` form is localized; the empty-list "Files" label has no key.
- **Persisted default group names** (`RepoRegistry` "New Group", `RepoStateStore`
  "Repositories") — intentionally left English: they become stored data, not live chrome.
- **Protocol names** HTTPS/SSH/URL and the brand "GitBench" — intentionally not translated.
- **macOS eyeball** still pending: flip View → Language and confirm the live re-render
  (the reactive path + custom-paint rebinds are proven by build + the switch tests).

Key decision resolved during Phase 1: **the catalog bakes values into generated C#
at compile time (no runtime JSON)**, rather than loading per-locale files at
runtime. NativeAOT (Release) + the existing source-gen'd `System.Text.Json` made a
baked model the clean choice — it mirrors `ThemeStyles.Dark`/`.Light` static
instances exactly, has zero runtime parsing, and keeps files as the source of
truth. Trade-off: changing a translation requires a recompile (fine for a shipped
desktop binary). The runtime *language switch* is unaffected.

## Scope (phased, locked with user)

1. **Phase 1–3 — Latin only** (en, es; fr/de plumbed). i18n infra + string sweep +
   formatters. **No** font-fallback or RTL work. **DONE.**
2. **Phase 4 — CJK pipeline** (font-fallback chain + first CJK locale, `ja`). Proves
   non-Latin glyphs render instead of silently vanishing. **DONE** (modulo macOS eyeball).
3. **Phase 5 — CJK completion** (line-breaking + `zh`/`ko` catalogs + the Phase 3 string
   tail). Turns the CJK smoke-test into something production-usable. **DONE** (modulo macOS eyeball).
4. **Phase 6 — RTL** (ar, he later). BiDi reordering + layout mirroring + Arabic catalog.
   The largest single effort. **IN PROGRESS:** the BiDi/shaping engine (6a) is **DONE**;
   layout mirroring (6b) and the Arabic catalog (6c) remain.

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
3. **Build-time validation — DONE (flat strings).** The generator fails the build on:
   a `Locale` enum case with no baked catalog (`LOC003`, drift guard); a key that
   collides with another key or a built-in member (`LOC002`, e.g. a `for`/`en`/`es`
   key); and a translation missing a reference key (`LOC004`) or carrying an unknown
   one (`LOC005`, warning) — the "find untranslated strings" net. All name the offending
   key/locale instead of emitting cryptic `csc` errors. *(Param/plural-shape parity waits
   on Phase 2, when those entry forms exist.)*
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

- **Plain string** → generated **property**.
- **String with `{placeholders}`** → generated **method**, one `object` param per
  placeholder (in first-appearance order), formatted via `string.Format(Culture, …)`.
  Placeholders become positional (`{count}` → `{0}`) at generation. *(Phase 2 — DONE.)*
- **Object keyed by plural categories** (`one`/`other`/… CLDR names) → generated
  **method** `Foo(int count)` that selects a form via `PluralRules.Select(Culture, …)`
  then formats. *(Phase 2 — DONE.)* Plural entries currently key off `{count}` only.

### Codegen (`GitBench.Localization.Generator`) — DONE (Phase 1)

A **Roslyn incremental source generator** (netstandard2.0 analyzer, referenced with
`OutputItemType="Analyzer" ReferenceOutputAssembly="false"`) consumes **every
`Localization/Strings/*.json`** as `AdditionalFile`s and emits `Strings.g.cs`. It reads
the JSON at compile time with a tiny self-contained reader (`MiniJson`, flat string pairs
only) — no JSON dependency inside the analyzer, no JSON at app runtime. `en.json` is the
**reference** (defines the key set, order, and member names); every other file is a
translation baked into its own instance.

It emits a `partial class Strings` with:

- one `required string` member per reference key (`about.view_on_github` → `AboutViewOnGithub`),
- a baked `static readonly Strings En`/`Es`/… per locale file (literals from that file; a
  key missing from a translation falls back to English and is flagged `LOC004`),
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

- `GitBench/Localization/Locale.cs` — DONE. Hand-authored `enum Locale { En, Es, Pseudo }`
  (must stay hand-authored — the `System.Text.Json` source gen for `Preferences` can't
  see a generated enum); the localization generator reads it and `LOC003`-fails the build
  if a case has no catalog. Grows as locales are added. A `Locale → CultureInfo` map
  arrives with formatting in Phase 2.
- `GitBench/Localization/ILocalizationService.cs` + `LocalizationService.cs` — DONE.
  Mirrors `IThemeService<T>` / `ThemeService`.
- `GitBench/Localization/L.cs` — DONE. `L.T(...)` mirror of `Widgets/Theme.cs`.
- `GitBench/Localization/LocalizationWidgetExtensions.cs` — DONE. `ctx.Localization()`.
- `GitBench/Localization/Strings/en.json` (+ `es.json`) + generated `Strings.g.cs` — DONE.
  `es.json` was added as a multi-locale smoke test: it proves the generator bakes more than
  one catalog, `For()` switches, and the runtime swap shows translated text (`Ver en GitHub`).
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

### Phase 2 — Second Latin language + formatting correctness — DONE
- ✅ **Multi-locale baking + key parity** (`LOC004`/`LOC005`/`LOC006`): the generator reads
  every `*.json`, bakes one catalog per file with English fallback, and fails the build on
  missing/extra/shape-mismatched keys. Verified `LOC004` fails the build on a dropped key.
- ✅ **Plural + parameterized entries** in the generator (positional `string.Format`; plural
  via `PluralRules`). Per-instance `CultureInfo` baked + exposed as `Strings.Culture`.
- ✅ **`PluralRules`/`PluralForms`** — `one`/`other` for en/es/de, `0,1→one` for fr/pt.
  Expandable per language.
- ✅ **`Format.RelativeTime`** (catalog-driven, culture-aware; deterministic `now` overload
  for tests) — converted `CommitsView`'s relative-time formatter to it.
- ✅ **Real `es.json`** (Spanish) incl. plural/parameterized translations.
- ✅ **Converted the LocalChanges plural context menus** (`Stage`/`Unstage`/`Discard`/`Stash`/
  `Mark Resolved`) to `Strings.FilesStage(n)` etc. via `ctx.Localization().Strings.Value`.
- ✅ Tests: plural selection, parameterized substitution, relative-time unit selection +
  localization, plural-rule category, per-catalog culture (11 localization tests total).
- `fr.json` not added (es is the second language); the fr plural rule is in place if wanted.
- Number/date display beyond relative-time/absolute-fallback is part of the Phase 3 sweep.
- **Exit criteria — met:** plurals, parameterized strings, and relative-time dates are
  correct in two languages (en/es), proven by tests.

### Phase 3 — Sweep the string surface — DONE (see Status for the summary + tail)
- Went feature-by-feature: all `Features/**/*Dialog.cs`, the app menu, `Controls`/
  `Widgets` shared dialog defaults, banners, custom-painted views, and context-menu VMs.
- Inline-ternary plurals (e.g. submodule "N out of date") and state ternaries (Fetch/
  Fetching, Commit/Committing) were converted to plural keys / state keys.
- A static straggler sweep (the offline stand-in for the Pseudo eyeball) returns no
  plain-ASCII UI-string assignments outside the intentional brand/sentinel set.
- **Exit criteria — met (modulo the documented tail + a macOS eyeball):** build passes
  key parity (448 keys, en/es in sync, no collisions); the static sweep is clean.
- **Authoring recipe** (for the tail + future locales): plain `Prop<string?>` UI →
  `L.T(s => s.Key)`; modal dialogs → read-once `var s = ctx.Localization().Strings.Value;`
  (rename any same-scope `Theme.Color(s=>…)` lambda to `t` to avoid CS0136); menu/error
  VMs → inject `ILocalizationService`, read `_loc.Strings.Value` per build; custom-painted
  views → `this.Bind(_loc.Strings, _ => { rebuild; SetDirty(); })`. The generator rejects
  `{new}`-style keyword placeholders — use `{old_name}`/`{new_name}` etc.

### Phase 4 — CJK pipeline — DONE (implementation; see Status for the detailed summary)
- ✅ **Surrogate-pair fix** — done in `TextView.Ellipsize` (the only true surrogate-slicing site;
  `TextWrapper`/`MeasureTextWidth` split on BMP delimiters and were never buggy).
- ✅ **Font-fallback chain** at the shape layer (`FreeTypeFontBackend.ShapeText` cmap itemization +
  per-glyph `ShapedGlyph.FontId`; draw loop rasterizes per source font). `.ttc` face-index support
  added. Proven by `FontFallbackTests` against real system fonts.
- ✅ **CJK font registration** — per-platform OS-font resolver (`SystemFonts`) + `RegisterFallbackFont`
  wired at startup (decision: system fonts, no bundling). macOS = Hiragino Kaku Gothic.
- ✅ **First CJK catalog = `ja.json`** (449-key parity) + `Locale.Ja` + Language menu item.
- → **macOS GUI eyeball**, **CJK line-breaking**, and the `zh`/`ko` catalogs all move to **Phase 5**.
- ⏳ **Color emoji** — out of scope (single-channel alpha atlas/shader; color glyphs are dropped,
  not crashed).

### Phase 5 — CJK completion & robustness — DONE (implementation; macOS GUI eyeball is the only open verification)
Phase 4 proved the *pipeline* (fallback chain + one CJK locale). Phase 5 makes CJK
actually usable and closes the leftover string tail. Build clean; of **121 GitBench tests**
109 pass (the 20 localization/font tests all green, +4 new) and **12 framework `TextWrapperTests`**
pass. (The 12 red `GitIdentityServiceTests` are pre-existing — verified identical on a stashed
baseline — and untouched by this work.) Shipped:

- **CJK line-breaking** — `TextWrapper.WrapLine` rewritten as a break-opportunity model: it
  tokenizes a line into atomic chunks at break opportunities (spaces **plus** boundaries
  where either side is a wide/CJK code point), iterating by **code point** so a surrogate
  pair is never split, then greedy-packs. **Minimal kinsoku** (`IsNoBreakBefore`/`IsNoBreakAfter`)
  keeps closing punctuation off the start of a line and opening punctuation off the end. **Perf:**
  widths accumulate per chunk (each chunk measured once) instead of re-measuring the whole
  candidate per code point — O(n) measures, and the conservative sum only ever wraps *earlier*,
  never overflows. `TextWrapperTests` (12) cover Latin word-wrap, long-word overflow, CJK
  inter-ideograph breaks, mixed Latin/CJK, both kinsoku rules, and supplementary-plane safety.
- **`zh-Hans` + `ko` catalogs** — `Locale.ZhHans`/`Locale.Ko` added; full-parity `zh-Hans.json`
  / `ko.json` authored (generator validates parity — build green); `View → Language` gained
  `简体中文` / `한국어` items (rebuild on switch like the others); culture auto-derives from the
  file stem (`GetCultureInfo("zh-hans")` / `("ko")`, both neutral cultures — `TwoLetterISOLanguageName`
  `zh`/`ko`, asserted in tests); no `PluralRules` change
  (the generic `n==1→one` collapses cleanly, same as `ja`). Switch + culture tests added.
- **Multi-script font fallback** — `SystemFonts.CjkFallback()` (returned only the *first*
  present font, i.e. the Japanese one — Hangul/SC-only glyphs would still tofu) became
  `CjkFallbacks()`, returning **one font per script family** (JP kana / Simplified Chinese /
  Korean Hangul). `Program.cs` registers each; the cmap-itemizing shape layer then resolves
  kana, Han, and Hangul. (Han-unification caveat from the plan stands: earliest-registered
  covering font wins for shared ideographs.)
- **Phase 3 string tail closed** — `RefNameRules` split into a locale-independent `IsValid`
  (gates) and `Validate(name, Strings, noun)` (localized message via parameterized `{noun}`
  keys); `loc` threaded through all 6 dialog VMs + construction sites, so messages live-switch.
  `ResetCommitDialog.BuildDirtyHint` now uses dedicated plural/param keys
  (`commits.reset_dirty_staged`/`_unstaged`/`_both`). Bare `"Files"` header (count == 0) in
  Discard/Stash VMs → `localchanges.files_header_empty`.
- **Git output encoding (the real diff-CJK bug)** — CJK in the diff rendered as mojibake
  (`언어` → `ì–¸ì–´`), which looked like a font/fallback gap but was an **encoding** bug:
  `GitProcessRunner` read git's stdout/stderr without `StandardOutputEncoding`, so .NET decoded
  git's UTF-8 with the OS default code page (CP1252 on Windows) — corrupting the text *before* it
  reached the renderer. Fixed by forcing `StandardOutputEncoding`/`StandardErrorEncoding =
  Encoding.UTF8` on both PSI builders, plus `StandardInputEncoding = UTF-8 (no BOM)` so hunk-stage/
  discard patches touching CJK apply byte-for-byte. Every git read routes through this runner
  (`GitService` is the `IGitRawConfigReader` too), so diff bodies, commit messages, and
  branch/author names are all fixed at once. macOS/Linux already defaulted to UTF-8 — this is a
  Windows fix.
- **Diff view CJK layout** (complementary to the encoding fix) — `DiffContentView` is a fixed
  monospace grid (`char × _monoAdvance`); once correct CJK flows through, the grid would still
  misplace it. Fixed: the horizontal extent now sizes by **East-Asian cell width** (`VisualCells`,
  wide = 2 cells) so long CJK lines scroll fully into view instead of clipping at the right;
  syntax-highlighted runs now position by **measured width** (running x), so colored runs abut
  where the shaper actually lays wide glyphs instead of overlapping. Plain (unhighlighted) lines
  were already correct (one `DrawText`). Latin output is pixel-identical. (Per-character *selection*
  over CJK columns is not yet cell-aware — a possible follow-up.)

**Open verification (exit gate):** the macOS GUI eyeball across en/es/ja/zh/ko — confirm (a) CJK
renders via the fallback and (b) CJK labels/bodies **wrap** instead of overflowing. The load+shape
path is proven by `FontFallbackTests` and the wrap logic by `TextWrapperTests`; only the on-screen
render is unverified (developing on Windows).

---

#### Original Phase 5 plan (for reference)

**1. CJK line-breaking** — the real rendering gap. `TextWrapper.WrapLine`
(`framework/ZGF.Gui/TextWrapper.cs:46-78`) only inserts break opportunities at `' '`, so a
spaceless CJK paragraph is kept whole and overflows / gets ellipsized instead of wrapping.
Implement a break-opportunity model (UAX-14-lite):
- iterate by **code point** (respect surrogate pairs — consistent with the Phase 4
  `TextView.Ellipsize` fix; never break mid-pair);
- allow a break *between* two CJK / "wide" code points (CJK Unified Ideographs, Hiragana,
  Katakana, Hangul syllables, CJK symbols/punctuation, fullwidth forms) **in addition to**
  the existing space breaks;
- (nice-to-have) minimal kinsoku: don't break *before* closing punctuation （、。」』）] etc.)
  or *after* opening punctuation.
- **Perf:** breaking at every code point means many `canvas.MeasureTextWidth` calls per line —
  measure incrementally / cache instead of re-measuring the whole candidate each step, since
  bodies can be long (commit messages, diffs).

**2. `zh-Hans` + `ko` catalogs.** Plumbing is already done (the `SystemFonts` resolver returns
PingFang/YaHei and Apple SD Gothic/Malgun; fallback is cmap-driven). Work:
- add `Locale.ZhHans`, `Locale.Ko` to the hand-authored enum (the generator `LOC003`-fails
  until the catalogs exist — that's the intended forcing function);
- author `zh-Hans.json` / `ko.json` at full key parity (currently 449);
- `PluralRules`: Chinese & Korean have a single `other` form; the generic `n==1→one` rule
  (same as `ja`) collapses cleanly — no rule change needed;
- culture map `zh-Hans`/`ko` → `zh-Hans` / `ko-KR`.
- **Han-unification caveat:** fallback order is fixed and cmap-driven, so a `zh` locale may pick
  a Japanese regional glyph variant for shared ideographs. Acceptable for now; *locale-aware
  fallback ordering* is a later refinement, not a blocker.

**3. Close the Phase 3 string tail** (cross-cutting; finishes the sweep — see "Phase 3 remaining
tail" above for the full notes):
- `GitBench/Git/RefNameRules.cs` — ~5 field-validation messages, thread `Strings`/`loc` through.
- `ResetCommitDialog.BuildDirtyHint` — nested-plural hint; needs dedicated plural keys.
- bare `"Files"` header (count == 0) in `DiscardChangesViewModel`/`StashDialogViewModel`.

**Exit gate:** macOS eyeball across en/es/ja/zh/ko — confirm (a) CJK renders via the fallback and
(b) CJK labels/bodies **wrap** instead of overflowing. Build stays green on key parity for all five
baked catalogs.

### Phase 6 — RTL — IN PROGRESS (engine done; mirroring + catalog remain)
The largest single effort, split into three sub-phases (locked with user: **Arabic first**,
**engine first**). The shaping layer was **less RTL-ready than earlier prose implied**: the old
`ShapeWithFallback` itemized runs by **font coverage only** and concatenated them in *logical*
order, and the sole direction handling was `GuessSegmentProperties()` *per run* — fixing glyph
order **within** a run but doing **no** cross-run BiDi reorder and no line-level direction.

#### Phase 6a — BiDi / RTL shaping engine — DONE
Build clean, **102 ZGF.Gui.Tests pass** (+17 new: 14 headless BiDi + 3 font-integration, was 85);
GitBench suite unchanged (109 pass; the 12 red `GitIdentityServiceTests` are the same pre-existing
baseline). Shipped:
- **`framework/ZGF.Fonts/Bidi.cs`** — a pragmatic subset of UAX #9: first-strong (or explicit)
  paragraph level (P2/P3), the weak rules (W1–W7), neutral rules (N1/N2; N0 bracket-pairing
  omitted), implicit levels (I1/I2), the L1 trailing-whitespace reset, and the L2 run reorder.
  Explicit embeddings/overrides/isolates are treated as boundary-neutral (they don't occur in app
  UI strings). The classifier resolves the bidi class without any BCL bidi API (.NET exposes only
  general category): a single ordered **`RtlRanges` table** maps every modern RTL block to R or AL
  (Arabic family → AL; Hebrew/N'Ko/Samaritan/Mandaic/Adlam/… → R), a handful of explicit number/
  separator overrides cover EN/AN/ES/ET/CS, and `UnicodeCategory` handles marks→NSM, format/control
  →BN, letters→L. Adding an RTL script is one table row; a historic block (Phoenician, Kharoshthi,
  …) is the same. Surrogate pairs share a level. Public API: `ContainsRtl`,
  `ResolveLevels(text, baseDir, out paragraphLevel)`, `ComputeVisualOrder(runLevels, order)`.
- **`FreeTypeFontBackend.ShapeBidi`** — gated behind `Bidi.ContainsRtl` so the **LTR hot path stays
  byte-identical** (no perf/render regression; the frame-diff optimization and existing shaping
  tests are untouched). It itemizes a line into `(level, font)` runs, shapes each with an **explicit
  HarfBuzz direction** from the run's level parity (script/language still come from
  `GuessSegmentProperties`; only `Direction` is overridden), then reorders the runs to visual order
  via `ComputeVisualOrder` and emits glyphs left-to-right. **Key contract:** `ShapeText` now returns
  glyphs in **visual L→R order**, so the canvas draw loop and `MeasureTextWidth` work unchanged
  (advances sum order-independently). Cluster ids stay **logical**, so selection mapping is
  unaffected. A new `ShapeText(..., BidiDirection)` overload (default `Auto`) and a `baseDir`-folded
  cache bucket let 6b later pass the locale direction without an API churn; `ShapeRun` gained an
  optional `Direction`.
- **Tests:** `BidiTests` (level resolution for pure/ mixed/ number/ surrogate cases, explicit vs
  auto base, L2 reorder permutations) and `BidiShapingTests` (real Inter primary + OS Arabic
  fallback: pure-Arabic renders from the fallback in visual order with non-increasing clusters;
  mixed Latin+Arabic splits across fonts and orders the RTL island correctly; no-fallback Arabic
  still BiDi-orders as `.notdef`).

#### Phase 6b — Layout mirroring — TODO
- **Base direction from locale:** expose the active locale's RTL-ness (e.g.
  `Strings.Culture.TextInfo.IsRightToLeft`) and thread a `BidiDirection` into the text draw/measure
  path so an empty-of-strong-chars or ambiguous label follows UI direction (today shaping
  auto-detects per line via first-strong, which is correct for single-script labels but not for the
  base of a mixed line).
- **Right-origin line layout** in the canvas draw loop; **flip default horizontal alignment**
  (currently only Left/Center exist — add trailing/RTL origin); mirror scrollbars, disclosure/chevron
  icons, and caret/selection direction. Broadest UI surface.

#### Phase 6c — Arabic catalog & completion — TODO (the proven es/ja/zh/ko recipe)
- `Locale.Ar` (hand-authored enum) + full-parity `ar.json` (LOC003 forces it).
- **Arabic CLDR plural selector** in `PluralRules.Category` — the full six-form set
  (`zero` n=0, `one` n=1, `two` n=2, `few` n%100=3..10, `many` n%100=11..99, `other`); the
  `PluralForms`/`PluralCategory` six-form machinery already exists, only the selector is missing.
  First locale to exercise more than `one`/`other`.
- **Arabic font fallback** in `SystemFonts` (alongside the CJK families; e.g. Windows
  Arial/Tahoma/Segoe UI, macOS Geeza Pro, Linux Noto Naskh) + `Program.cs` registration.
- `View → Language` Arabic menu item; culture auto-derives from the `ar` stem.
- Hebrew (`he`) is a later follow-up (mirrors ja→zh/ko).

**Exit gate (unchanged from prior phases):** the macOS GUI eyeball — flip `View → Language: العربية`
and confirm Arabic renders via the fallback **and** lays out right-to-left, with mixed
Latin/numbers ordered correctly. The shape+reorder path is proven headlessly by `BidiTests` /
`BidiShapingTests`; only the on-screen render is unverified (developing on Windows).

### Cross-cutting decisions (not phases — resolve when relevant)
- **Translator workflow.** The catalog is **baked into C# at compile time**, so a translator can't
  add or fix a language without a build. Resolved *as packaging*, open *as workflow*: keep
  recompile-per-change (current), or add an **optional runtime-JSON override** (overlay `*.json`
  from a known dir at startup onto the baked catalog) for translator iteration / community locales.
  The override path reintroduces runtime parsing — keep it AOT-careful and off the default load path.
- **Generator hardening** (open question #6): per-placeholder *param-set* parity across locales —
  today a translation's stray placeholder is escaped to a literal rather than diagnosed.
- **Latin breadth** (cheap, land anytime): `fr`/`de`/`pt` are translation-only — the plural rules
  are already in place; no infra work.

## Testing & tooling

- **Build-time key parity** (from the generator) is the primary safety net for
  untranslated keys. ✅ done (`LOC004`/`LOC005`/`LOC006`); verified a dropped key
  fails the build.
- **Pseudo-localization** as the manual completeness + layout-expansion check. ✅
  shipped (the `Pseudo` locale).
- Unit tests: plural-rule selection per locale; `Format.RelativeTime` boundaries. ✅
  done — `LocalizationFormatTests`.
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
6. **Generator entry forms — DONE.** Flat strings, parameterized strings, and plural
   objects are all supported; parity diagnostics `LOC004`/`LOC005` (key parity) and
   `LOC006` (plural-vs-flat shape mismatch, warns + falls back to English) are in.
   Remaining nicety: per-placeholder *param-set* parity across locales (today a
   translation's stray placeholder is escaped to a literal rather than diagnosed).
7. **Git output assumed UTF-8 (Phase 5).** `GitProcessRunner` now decodes git stdout/stderr
   (and writes stdin) as UTF-8 — the fix for diff-content mojibake on Windows. Trade-off: a repo
   whose files are genuinely *not* UTF-8 (legacy Shift-JIS/GBK/Latin-1 content) will now mojibake
   in the diff instead. UTF-8 is the correct default (matches every modern git GUI); per-file
   encoding detection / a `core` override is a possible later refinement, not a blocker.
