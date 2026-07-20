# Rename: name research

## Why this is actually happening — the SEO driver (2026-07-19)

This whole exercise started from a concrete failure, not a wordmark preference: **`gitbench.builtbyzee.com` is
not indexed by Google**, and "GitBench" is drowning in git-related noise. Both are confirmed. They are *two
different problems* and only one is a naming problem.

**Problem 1 — the name is lost, worse than "bench = benchmark" implied. GitKraken owns it.**
`gitbench.gitkraken.com` is GitKraken's live LLM Git-benchmark suite — your single biggest competitor, sitting
on your exact name, running an actual benchmark product. The doc treated the benchmark reading as a soft
connotation; in reality a well-resourced rival has already planted that flag. Add `gitbench.com`,
`gitbenchltd.com`, `gitbenchhq/bench`, `zcyc/git-bench`, GitTaskBench, and a `GitBench` GitHub org. **You will
never outrank GitKraken for your own name. This is the decisive argument for renaming** — and it retires the
"keep GitBench" recommendation below.

**Problem 2 — the non-indexing is NOT a naming problem, and a rename alone will not fix it.**
`site:gitbench.builtbyzee.com` returns zero pages — but so does `site:builtbyzee.com`. robots.txt, sitemap,
and server-rendered HTML are all fine. The cause is that **it's a subdomain of a zero-authority personal
domain**: no backlinks, no standing, so Google parks it in "Crawled – currently not indexed" and never
promotes it. Rename to `whippet.sh` and keep it a thin one-pager with no inbound links and no Search Console,
and it stays unindexed too.

**What the name actually buys on SEO:** not indexing itself — *whether indexing helps you*. Once indexed,
"GitBench" still ranks under GitKraken forever; a distinctive name (Whippet, Godwit, Jink) ranks **#1 for its
own name against zero competition** the moment it's crawled. That is the real search case for renaming.

**Indexing fixes to do regardless of the name — the rename does not replace these:**
1. **Google Search Console — already set up; status is confirmed "Crawled – currently not indexed."** This is
   the important finding: it means the technical side is **perfect** — Google fetched the page, found no
   noindex, no robots block, no canonical/redirect issue — and then made a *value* judgment that it isn't worth
   an index slot. Not a bug to fix, a verdict to overturn. Submission and *Request Indexing* do not move this
   status; only authority and substance do (#2, #3).
2. **Authority backlinks — the primary lever.** The standout is a **`git-scm.com/tools/guis` listing**: that
   page is a GitHub-hosted data file, you submit a PR, and it's the canonical source for exactly this category —
   authority *and* topical relevance in one link. It flips "crawled – not indexed" more reliably than anything
   else. Then AlternativeTo, a Show HN, Product Hunt, r/git, dev.to, and the repo README link.
3. **Substance — the second lever.** A single thin landing page is the textbook "crawled not indexed" victim.
   Add real interlinked surface: docs, a changelog, a features page. Three or four substantive pages read as "a
   real project," not "a splash page."
4. **An apex domain, not a subdomain** — the rename plan already assumes buying `whippet.sh` / `godwit.sh`;
   that alone helps more than the wordmark change.
5. **Verify the raw `<head>` has a strong unique `<title>` + `<meta name="description">`** — a fetch couldn't
   confirm them, and a thin page with weak metadata is a "currently not indexed" magnet.

**Sequencing — do the rename before building any links.** Every backlink earned to `gitbench.builtbyzee.com`
is authority poured into a URL you're about to abandon; it does not transfer cleanly to `whippet.sh`. Order:
pick the name → buy the apex domain → ship the site (with #3's extra pages) there → *then* submit the git-scm
PR and do the Show HN. Otherwise the hard part (link-building) gets done twice.

> **Bottom line:** rename **and** fix indexing. The rename wins the "search my name → find me, not GitKraken"
> battle; Search Console + a git-scm.com listing win the "get indexed at all" battle. Neither substitutes for
> the other.

---

## Verdict on Pecia

**Not salvageable.** Its only distinguishing asset is that it's the authentic historical term, and every
repair that fixes the phonetics destroys that asset: *Pecha* collides with Pecha Kucha and severs the
etymology; *Pesha* reads as a personal name with no meaning left; *Pec* is a permanent gym joke; *Peci*
adds a second ambiguity without removing the first. There is no version that is both sayable and still
*Pecia*.

**Keep the story.** The pecia system — a stationer holds the authorized *exemplar* split into numbered
pieces, scribes rent pieces in parallel, every copy verified against the master — is distributed version
control described in 1228. It belongs on the About page under whatever name wins.

---

## Viable names

One list, ranked. Every row is verified by live probe. **NuGet is the registry that matters** (C# desktop
app); npm and GitHub handles are noise and are omitted. **No single real word in this register has a free
bare `.com`** — confirmed twice independently across 143 words, so `.com` is settled and not a discriminator.

### Tier 1 — recommended

| # | Name | Says | Domains free | NuGet |
|---|---|---|---|---|
| 1 | **Whippet** | Small, light, fastest thing off the line — the Electron-vs-native argument in one word | `.sh` `.tools` + `get-`/`use-`/`-app.com` | ✅ |
| 2 | **Swoop** | Dive in, take it, gone | `.sh` | ✅ |
| 3 | **Pennant** | A signal flag streaming at speed | **`.dev`** `.sh` | ✅ |
| 4 | **Skiff** | The lightest boat that still carries you | `skifftools.com` | ✅ |
| 5 | **Glint** | A flash of light off a moving edge | `glinttools.com` | ✅ |

**Whippet** has the deepest coverage of anything found, exact semantics, and the only genuine **mascot** in
400 candidates — a category the diagnosis flagged as never explored, and the most memorable asset class in
dev tooling (GitKraken's kraken, Ghostty's ghost). A mascot also dissolves the lettermark question instead
of arguing it away. Ship on `whippet.sh`, hold `getwhippet.com` for marketing.

**Swoop** and **Skiff** and **Glint** were all reached independently by multiple generators — the only real
convergence signal in the project. Skiff's image does the whole pitch unprompted: the small fast boat versus
the heavy vessel you don't need.

### Tier 2 — viable, with a named cost

| # | Name | Says | Domains free | NuGet | Cost |
|---|---|---|---|---|---|
| 6 | **Flick** | The fastest gesture in English; also the interaction model | `flickgit.com` | ✅ | Light in tone for a tool holding source code |
| 7 | **Gust** | Weightless *and* propulsive | `gusthq.com` | ✅ | "Transient" is a mild wrong note |
| 8 | **Scamper** | Small, quick, gets there first | `.sh` `.tools` `use-.com` | ✅ | Slightly juvenile |
| 9 | **Skitter** | Darts across, never settles | `.sh` `.tools` + all 3 `.com` modifiers | ✅ | Implies erratic — off-message for *reliably* instant |
| 10 | **Skate** | Glide over it effortlessly | `.sh` `.tools` `use-.com` | ✅ | EA's *Skate* franchise; "skate by" = evade |
| 11 | **Scoot** | Move, quickly, no fuss | `.sh` | ✅ | Reads cute rather than serious |
| 12 | **Brio** | *Con brio* — with vigour and spirit | none | ✅ | No domain at all; faint Breo/Brio spelling risk |
| 13 | **Swivel** | Turn to face another repo without getting up | `.sh` `.tools` `swivelgit.com` | ✅ | `zumba/swivel` is a PHP feature-toggle lib; Swivel Secure is an auth vendor (cl. 9 unchecked) |
| 14 | **Shunt** | The railway verb for switching a moving thing between lines | `.sh` `.tools` `getshunt.com` `shuntgit.com` | ✅ | Medical sense leads for non-devs; "shunt aside" = sideline. Register is technical, not plain |
| 15 | **Covey** | A dozen quail bursting into the air *at once* — parallel, instant, light | **`covey.sh`** `coveygit.com` `coveytools.com` | ✅ | Mascot asset. But *covy/covie* — squarely in the spell-from-hearing danger zone |
| 16 | **Flotilla** | Many small fast boats — continues the Skiff pitch | `useflotilla.com` `flotillagit.com` `flotillatools.com` | ✅ | Three syllables, double-L/single-T wobble, no mascot |

**Swivel and Shunt are the first verbs to survive.** The diagnosis in [Why the first 200 failed](#why-the-first-200-failed)
flagged that not one of ~200 candidates was a verb; these two clear the same coverage shape as Whippet
(`.sh` + `.tools` + a `-git.com`). Swivel wins on register — every developer already says it. Shunt wins on
namespace depth and on metaphor precision: switching tracks *is* the product.

**Covey** is the only new mascot candidate, and it competes with Whippet on Whippet's own axis (mascot + free
bare `.sh`) while being strictly worse on the gate the doc calls hard. **Flotilla** is the best semantic fit
in the many-at-once lane and the weakest brand.

### Tier 3 — invented compounds (every domain free, but that's *why*)

| # | Name | Says | Domains free | NuGet |
|---|---|---|---|---|
| 17 | **Windskiff** | Tiny boat moving on air alone | `.com` `.dev` `.sh` | ✅ |
| 18 | **Gustline** | A burst along a line ("line" is native Git vocabulary) | `.com` `.dev` `.sh` | ✅ |
| 19 | **Wingdart** | Small thing flung fast | `.com` `.dev` `.sh` | ✅ |

> ⚠️ These clear every tier — and that is the warning, not the selling point. Availability is easy here
> precisely because demand is low. This is the same vein as Pinegraft and Pathbraid, already rejected once
> for feeling invented.

### Second sweep (2026-07-19) — verbs, mascots, speed words

Five lanes run against the three gaps the diagnosis named: no verbs, no mascots, nothing meaning fast/light/small.
Ranked survivors, all live-verified. None decisively beat Whippet, but several are legitimate Tier-1/2 peers.

| Name | Lane | Says | Domains free | NuGet | Cost / risk |
|---|---|---|---|---|---|
| **Godwit** | mascot | The bird that flies farther nonstop than anything alive — 11,000 km, never lands | `.dev` `.tools` `.sh` `getgodwit.com` `godwitgit.com` | ✅ | **Cleaner namespace than Whippet** (Whippet had no bare `.dev`; this has `.dev` + `getgodwit.com`). Two tiny inactive namesakes, neither git/.NET. "Never stops, never lands" is a strong story; "wit" is a memory hook |
| **Jink** | verb | Aviation: a quick evasive dodge — one syllable, types like a command | `jink.sh` `jink.tools` `getjink.com` `jinkgit.com` | ✅ | Cleanest verb found; no dev/git collision. Lower-frequency word — some won't know it, though it spells clean from hearing |
| **Spryly** | speed | Moving in a spry, light-footed way — the way the app opens | **`.dev`** `.sh` `.tools` `getspryly.com` `sprylygit.com` | ✅ | **Only name in the whole project with a free bare `.dev` plus `.sh`** — a namespace shape even Whippet lacked. Whole risk is register: it's an adverb, and adverbs are free *because* nobody names products with them |
| **Hotfoot** | verb | "Hotfoot it" = move fast, leave now | `hotfoot.sh` `hotfoot.tools` `gethotfoot.com` `hotfootgit.com` | ✅ | Emptiest namespace of the verbs; most literal "won't waste your time." Casual/idiomatic register, two syllables |
| **Sanderling** | mascot | The shorebird that sprints the surf line and clears out before the wave returns — the most literal "opens before you let go of the mouse" image found | `.dev` `.tools` `.sh` `getsanderling.com` `usesanderling.com` | ✅ | **Real cost:** `Arcitectus/Sanderling` is an established **C#/.NET** framework — same ecosystem, different audience. Best pitch in the sweep, priced by the namesake. 3 syllables, weak icon |
| **Bilby** | mascot | Small, light, fast desert marsupial | `.dev` `.tools` `.sh` `bilbygit.com` | ✅ | **Best mascot/logo in the sweep** — iconic, big-eared, instantly drawable. But bare `.com` **and** `getbilby.com` are gone (only `bilbygit.com` left); mild Bilbo echo + Bilbee/Bilbie spell risk |
| **Cusp** | abstract | On the cusp — the edge you're about to cross | `cusp.sh` `cusp.tools` `cuspgit.com` `cuspapp.com` | ✅ | Only abstract survivor that passes spell-from-hearing with **zero** risk and still feels like a modern wordmark (Bun/Zed/Vite register). But says *nothing* about fast/native/light — and CUSP is an NVIDIA HPC C++ lib (not .NET/git) |
| **Dunlin** | mascot | Small fast shorebird; flocks wheel in perfect unison — parallelism made visible | `.dev` `.tools` `.sh` `dunlingit.com` | ✅ | Clean 2-syllable spell (DUN-lin). Namesakes exist (clinical R tool, k8s CNI) — none git/.NET-central, but a busier shelf than Godwit |
| **Mote** | speed | A speck in a shaft of light — tiny *and* weightless, the whole pitch in one image | `motegit.com` only | ✅ | Elegant meaning but namespace one-domain-deep (Mote Software, Pimoroni Mote, Google Mote all took the rest) |

**Reading the sweep.** The verb lane vindicated the diagnosis hardest: **Jink** and **Hotfoot** join Swivel/Shunt
as clean verbs, a category that was 0-for-200 before. **Godwit** and **Spryly** are the two that arguably
out-*namespace* Whippet (Godwit on story + coverage, Spryly on the unheard-of free bare `.dev`), each with one
named weakness — Godwit's obscurity, Spryly's adverb register. **Bilby** is the pick if the mascot/logo leads
the brand and a `-git.com` is acceptable. Everything else is a peer, not an upgrade — the sweep widened the
shortlist, it did not dethrone Tier 1.

> The speed lane confirmed its own warning: **Trice, Whoosh, Brisk(ly), Grain, Glance, Jiffy, Gossamer** all
> looked free on the registries and were each already a shipping tool *marketing on being fast* — you have to
> search the name **with the pitch words**, because the collision hides in the tagline, not the wordmark.

### Third sweep (2026-07-19) — run against the SEO driver

Five more lanes (fast-flyer birds, small watercraft, optics, archaic quick-words, winds), scored with **own-your-
search-term as the #1 gate** and a new **no-lewd/juvenile-sound-alike gate** (the one that just killed Jink). This
sweep produced the first candidates to genuinely challenge Whippet — because "distinctive enough to own the term"
is exactly what a rare-but-real word does best.

| Name | Lane | Says | Domains free | NuGet | Owns its term? | Cost / risk |
|---|---|---|---|---|---|---|
| **Martlet** | bird | The heraldic swift, drawn **with no feet because it never lands** — "always in flight, never idle" *is* the native-vs-Electron pitch, and it's already a stylized geometric bird | `martlet.sh` `.tools` + `get`/`use`/`git`/`app`/`hq`-`.com` | ✅ | **Yes — empty software namespace** (top hits are generic "best git client" listicles) | **Best mascot/logo + story in the whole project.** Spells clean (MAR-tlet; rhymes harlot/scarlet, no lewd read). Non-software noise only: McGill Martlets, a student paper. `.dev` taken (ship on `.sh`) |
| **Glisk** | optics | Scots: *a gleam of sunshine; a fleeting glimpse* — a flash of light **and** an instant in one word. The purest Glint sibling found | `glisk.sh` `.tools` `getglisk.com` `glisktools.com` `gliskgit.com` | ✅ | **Yes — zero products named Glisk**; GitHub shelf empty (max ★3) | **Spell-clean AND ownable** — the rare name that wins both. Reads as a modern coined wordmark (Zed/Vite/Bun register). Cost: obscure meaning, needs the footnote. No bare `.dev` |
| **Gleg** | archaic | Dictionary word (M-W/Collins), Old Norse root: *quick in movement; nimble; keen, alert*. "Gleg at the uptake" = quick-witted | **`gleg.dev`** (bare) `.sh` `.tools` + `getgleg`/`usegleg`/`glegtools`/`glegapp`-`.com` | ✅ | **Best in the project — essentially zero commercial/software use anywhere** | The strongest *pure* SEO play, out-owns even Whippet (a dog breed with endless noise). Cost: so obscure it reads as a typo/fragment without its footnote; single-vs-double-g wobble; **no mascot** |
| **Shallop** | boat | A light open boat for shallow fast water — "the smallest boat that still carries you," continues the Skiff pitch past Skiff | **`shallop.dev`** (bare) `.sh` `.tools` + `get`/`use`/`git`/`app`-`.com` | ✅ | **Yes — term wide open**, GitHub `in:name` = dead test repos | Deepest namespace in the sweep (bare `.dev`!). Cost: spell-from-hearing (SHAL-up → some write "scallop/shallup") — its one real risk, exactly what the 3-stranger gate settles |
| **Dotterel** | bird | Small fast plover — and "**dot**" is a ready-made logo *and* on-brand for a .NET app | `dotterel.sh` `.dev` `.tools` + all six `.com` modifiers | ✅ | Mostly — one niche Android steno app | Fullest domain sweep of the birds. Archaic "gullible fool" sense is obsolete → cleared. Round-plover mascot, weaker than Martlet |
| **Sundog** | optics | The bright spot beside the sun (parhelion) — instant, bright, catches the eye | `sundog.sh` `usesundog.com` `sundoggit.com` | ✅ | Mostly — but **Sundog Software** (3D-graphics SDKs) is adjacent dev tooling | Distinctive, mascot-able, clean spell. `.tools` taken; thinner namespace |
| **Ponente** / **Meltemi** | wind | West wind / the Aegean wind sailors ride for days — light, steady, carries you | both **bare `.dev`** + `.sh` `.tools` + `.com` modifiers | ✅ | Yes on the English term | Both carry a free bare `.dev`. Cost: foreign register (Ponente = "presenter" in Romance langs); 3 syllables |

**How this sweep changes the picture.** Two names now rival Whippet on the axis that started this whole rename:
- **Martlet** is the one that beats it *on Whippet's own terms* — a better mascot, a sharper story (the footless bird that never lands vs. a fast dog), and a genuinely empty in-category namespace, while spelling just as cleanly.
- **Glisk** is the one that beats it *on the SEO gate specifically* — spell-clean, carries the pitch (light + instant), and returns **zero** competing products, where "whippet" fights dog-breed noise forever.
- **Gleg** wins ownership outright but loses sayability — it reads as a fragment without its gloss, edging toward the Tier-4 "wrong register" trap. Keep it as the high-risk/high-SEO option, not the front-runner.

> The three lanes that produced **nothing** worth carrying — deeper winds, most raptors, most watercraft — failed
> the same way: the clean-spelling words were namespace-taken and the free words were spell-risky or foreign. That
> tension (obscure enough to own ⇄ clear enough to say) is now the central trade-off. Martlet and Glisk are the two
> names that resolve it.

### Tier 4 — strong namespaces, wrong register

Retained because the data is real and expensively earned. All are words the average developer does not know —
which is the failure mode described in [Why the first 200 failed](#why-the-first-200-failed).

| Name | Says | Notable |
|---|---|---|
| **Pometka** | The note scribbled in a margin | **Zero trademark records worldwide. Free on `.com` `.dev` `.app` `.sh` `.tools`, GitHub, NuGet, npm, PyPI, crates — everything.** Cleanest namespace in the project |
| **Planar** | A graph drawn with no crossing edges | The logo would *illustrate* the name — planar embedding is the hardest rendering problem in a Git GUI. `planar.dev` registered but dark |
| **Prent** | Every commit is a printed impression | `.dev` `.sh` free; reads as a misspelled "print" |
| **Catchword** | Every page points to the next — existed *to verify leaf order* | Free on `.dev` `.sh` GitHub NuGet |
| **Plica** | A fold in parchment | `.dev` `.sh` free; inherits Pecia's lineage without its defect |
| **Solmu** | Where two branches tie together (Finnish: knot) | `.dev` `.sh` free; sharpest metaphor in the project |
| **Pravka** · **Prival** | A revision pass · where a column halts to make camp | Both trademark-**clear**, `.dev`/`.sh`/NuGet free |

### The incumbent

> ⚠️ **This section is retired by the [SEO driver](#why-this-is-actually-happening--the-seo-driver-2026-07-19).**
> It argued for keeping GitBench on spell-from-hearing and search-intent grounds. Two facts, both verified
> 2026-07-19, override it: **GitKraken owns `gitbench.gitkraken.com`** (an actual benchmark product — so "Git"
> is *burdening* this name, not carrying it), and **the site is genuinely unindexed** at zero domain authority.
> Kept below for the reasoning, not the recommendation. The rename is on.

**GitBench** scores a perfect 5 on the exact gate that killed Pecia — 100% spell-from-hearing, no gloss, no
phone repeats — while every challenger is graded on a curve against it. "Git" is the highest-intent keyword
in the category and this is a $0-marketing-budget solo project: GitKraken, GitLens and GitButler aren't
burdened by that prefix, they're carried by it. *(Counter-evidence, added after this was written: they're
carried by it because they earned authority first; a zero-authority newcomer sharing "GitBench" with GitKraken
is buried by it. The prefix rewards incumbents and punishes entrants.)*

Its one real defect, unnamed until late: **"Bench" reads as benchmarking**, which is actively wrong for a
product selling launch speed. Plus mild lookalike drift toward GitButler.

> ⚠️ **New exposure (2026-07-19): the "Git" portmanteau is against Git's own trademark policy.** The binding
> mark is not GitHub's — it's **GIT itself**, US Reg. 4680534, held by the Software Freedom Conservancy. Its
> policy forbids using the mark "as part of a portmanteau (e.g. 'Gitalicious', 'Gitpedia')" and names
> **GitHub and GitLab as grandfathered exceptions** with pre-existing licences. So GitKraken/GitLens/GitButler
> are *not* clean precedent — they're grandfathered, licensed, or simply un-pursued. Enforcement is light
> (thousands of Git\* tools ship unbothered) and the mark holder is a software-freedom nonprofit, not a
> litigious rival — but **the policy is written and the mark is live, and this applies equally to keeping
> GitBench.** The "carried by the prefix" argument holds for SEO; it does not hold for trademark. Verify at
> `git-scm.com/about/trademark`, *not* GitHub's policy.

Two fixes that cost almost nothing:

- **Fix the positioning line, not the wordmark** — *"GitBench — opens before you let go of the mouse. No
  Electron, no runtime, 12 MB."* Repairs the benchmark ambiguity, the derivativeness, and the missing
  differentiator at once. **Cost: an afternoon.**
- **Shorten to Bench** — keeps every "Git" search surface as the front door, repo can stay `Zeejfps/GitBench`.
  Highest return per unit of risk on the table. *(Docked once by the multi-repo finding: **Bench/Workbench**
  now sits one synonym from GitKraken's "Workspaces" headline.)*
- **Rename to GitBrisk** — the *conservative* option, for anyone who has decided the Git prefix is
  non-negotiable. "Brisk" is the literal antonym of the benchmark-slow defect, spells perfectly from hearing,
  and its **entire namespace is free** — `gitbrisk.com` (a *bare* `.com`, rare for this project) `.dev` `.sh`
  NuGet, and a clean GitHub handle. **It beats GitBench-as-is.** It does **not** beat "keep GitBench + ship the
  positioning line": it still carries the identical SFC-portmanteau flag, still reads as a derivative Git\<X\>
  in a category that has moved to plain words (Fork, Tower, Sublime Merge, Lazygit), and still demands the one
  irreversible rename step — to fix a connotation the tagline fixes for free. Negative expected value against
  the afternoon fix; positive only if a full rename is happening regardless.

---

## The multi-repo angle

*Asked directly: multi-repo / instant repo-swapping is a standout feature — can the name say so?*

**No. Name the pitch, not the feature.** Four agents ran the literal `multi-`/`poly-`/`omni-` morpheme, the
switching verbs, the many-at-once metaphors, and the competitive prior art. Three findings, in order of how
much they cost:

**1. The shelf is saturated at three layers.** Every generic word is already a shipping tool: `repo`
(Google/Gerrit), `meta` (which also holds the bare npm name), `mr`/myrepos, `mu-repo`, `gita`, `gr`, `mani`,
`mrgit`, `polygit`, and **two independent** claimants on `polyrepo`. Above them sit Nx, Turborepo, Lerna, Rush
and Bazel, owning the adjacent mindshare and all the SEO. And `Aspire.PolyRepo` is a .NET multi-repo library —
your exact ecosystem.

**2. GitKraken already markets this feature under a name: Workspaces.** Multi-repo grouping with bulk
fetch/pull/clone, synced across Desktop, GitLens, the CLI and gitkraken.dev, with a dedicated feature page
*and* a dedicated "manage multi-repos" problem page. Naming yourself after multi-repo picks a fight on the
pitch with the best-funded incumbent in the category, and loses the search term to it and to a dozen CLIs
simultaneously.

**3. The prefix is descriptive, which is the weakest possible mark.** `Omni-` is pre-empted by The Omni Group's
enforced portfolio *and* by **Omnissa** (USPTO SN 98365706, class 9/42, registered 2025-12-02 — live, directly
over your classes). `Poly-` is Polylith's CLI binary. `Multi-` reads as *multiplexer* — tmux — to precisely
your audience.

> **The deeper reason it fails is the doc's own diagnosis, restated.** "Many" is a **capacity** claim; the
> product's argument is **speed**. Multitude, Manifold, Polyrepo all say *holds a lot* — which is the Electron
> accusation dressed up as a virtue. The pitch is *instant, native, 12 MB*.

**What survived is the by-product, not the angle:** probing the switching *verbs* produced **Swivel** and
**Shunt** (Tier 2, #13–14), the first verbs in the entire project. They name the *gesture* of moving between
repos without naming the feature — the plain-word-with-a-deep-footnote shape.

**Use it as a positioning line instead.** *"Switch repos instantly"* costs nothing, works under any name, and
is the same conclusion already reached for GitBench itself: fix the line, not the wordmark.

> ⚠️ **This damages the cheapest fallback.** GitKraken's *Workspaces* puts **Workspace/Workbench/Bench** nearer
> this crowded shelf than assumed. "Shorten to Bench" is still the highest return per unit of risk, but the
> risk is no longer ~zero — *bench* now sits one synonym from an incumbent's headline feature name, on top of
> the benchmarking ambiguity already recorded.

---

## How to choose

Two steps this document cannot do for you:

1. **Say the finalists aloud to three developers.** Whichever comes back without a follow-up question wins.
   Fifteen minutes, and it is the exact test Pecia failed. *≥2 of 3 must spell it correctly from hearing* —
   this is a hard gate, not a score.
2. **Buy a domain within the week.** `whippet.sh` is ~$15. ~~If a week passes and no domain is bought, keep
   GitBench and ship the positioning line.~~ **Retired by the [SEO driver](#why-this-is-actually-happening--the-seo-driver-2026-07-19):**
   "keep GitBench" is no longer a legitimate fallback — GitKraken owns the name and the site can't rank on it.
   If the week passes with no pick, the answer is *pick one anyway from Tier 1 and buy the domain that day* —
   inaction now means staying on a name a competitor owns, not a safe default.

---

## Why the first 200 failed

Worth reading before generating any more names, because it will happen again otherwise.

The search had three hard gates — phonetically clean, namespace-clear, trademark-clear. Run that over
English and only **rare words survive**; common words were eliminated before scoring ever began. Weighting
availability low in the rubric changed nothing, because the damage was upstream in *generation*. The result
was ~200 candidates rejected one at a time, when what was actually being rejected was the output
distribution of a broken filter.

What that filter produced:

- All ~200 were **nouns**. Not one verb, imperative, or adjective.
- Nearly all named a **static artifact** — a fold, knot, mark, piece, tenon, quire.
- Nearly all came from **deliberately slow** domains — joinery, weaving, bookbinding, scribal copying.
- **Not one meant fast, light, instant, or small** — which is the entire product pitch.

A woodworking name says *"I took my time on this."* This product says *"I won't waste yours."*

Second mismatch: the register was **erudite** (Colophon, Postilla, Punctum, Exemplar). On a funded team that
reads as taste; on a solo MIT-licensed, donation-funded project it reads as pretension. The existing logo —
clean, geometric, minimal — was in direct opposition to the ornamental corpus.

> **The target shape: a plain word carrying a deep footnote — not a deep word requiring a plain footnote.**
> Pecia was backwards: an opaque label that had to be *rescued* by its story.

**The P-lettermark constraint was a fossil** — it existed because of Pecia, and Pecia is dead. It was
demoting the project's own best candidates. The logo concept (commit graph through a letterform stem) is
letter-agnostic and arguably works better on a **B**. Dropped.

---

## Kill list

### Fatal — collision in your own domain

| Name | Why |
|---|---|
| **GitBench** *(the incumbent)* | **GitKraken owns `gitbench.gitkraken.com`** — a live LLM Git-benchmark product from your biggest competitor, on your exact name. Plus `gitbench.com`, `gitbenchltd.com`, `gitbenchhq/bench`, `zcyc/git-bench`, GitTaskBench, a `GitBench` GH org. Unrankable for your own brand |
| **Kestrel** | ASP.NET Core's web server. Fatal for a C# app |
| **Fleet** | JetBrains IDE — direct category collision |
| **Quilt** | Linux `quilt` manages *stacks of patches* and is Debian's standard `3.0 (quilt)` source format — installed on your target user's machine |
| **Stet** | Best semantic fit in existence ("let it stand" = revert). getstet.app and a `stet` TUI are both "review changes before you accept them" |
| **Gutter** | The editor margin where diff markers render. GitGutter is 3.9k★ |
| **Pact** | pact.io is *the* contract-testing framework — same audience |
| **Orchard** | Orchard Core is a major .NET OSS project — your exact ecosystem |
| **Splice** | Trademark explicitly covers "computer software development tools" |
| **Register** | CPU registers, container registries. Unownable |
| **Prow** | Kubernetes' CI system — gates every k8s PR |
| **Braid** | Already a git vendor-branch tool |
| **Vilka** | Russian for "fork" — i.e. naming yourself after fork.dev, a leading competitor, in translation. Russian devs say *форк* anyway; вилка is the kitchen utensil |
| **Etch** · **Sprig** | Both are existing Git GUIs — see [Competitors](#competitors-found-incidentally) |

| **Roundhouse** | RoundhousE is a well-known **.NET** database migration tool. The best "many things at once" image in the language, and unusable |
| **Octopus** | Octopus Deploy — a flagship **.NET** CI/CD product. Kills the best mascot in the multi lane |
| **Switchboard** | Two collisions: a .NET 8/10 reverse proxy, *and* `doctly/switchboard`, a desktop app for managing coding sessions across every project — nearly your exact pitch |
| **Juggler** · **Juggle** | `juggler.studio` is a live **native desktop app for developers**; also Puppeteer's Firefox protocol |
| **Meta** · **Repo** · **Multirepo** · **Polyrepo** · **Monorepo** · **Poly** | Generic category terms, each with 2+ shipping claimants. `poly` is Polylith's binary; `meta` owns the bare npm name. Unownable — the **Register** failure mode |
| **Workspace** · **Workbench** | GitKraken markets *Workspaces* as its multi-repo headline. Also VS Code/Eclipse vocabulary |
| **Polyglot** | .NET Interactive ships as *Polyglot Notebooks*. Your exact ecosystem |
| **Polybase** | A SQL Server feature |
| **Multipass** | Canonical's VM tool — installed on your target user's machine |
| **Hoppscotch** *(kills Hopscotch)* | Major open-source API client with a desktop app. One *p* or two — fails spell-from-hearing outright |
| **Flip** | Flipt (git-native feature flags) + `FlipIt`, a .NET feature flipper. "Flip" *is* feature-flag vocabulary |
| **Ferry** | `deployferry.io` (funded dev platform) + Dart's `ferry` GraphQL client |
| **Dovecote** | **Dovecot** is the IMAP server |
| **Flock** | `flock(2)` is *the* file-locking syscall — git uses it |
| **Hydra** | Meta's Python config framework; also Nix's CI |
| **Shuffle** | `shuffle.dev` + Shuffle SOAR |
| **Shimmy** | Popular Rust inference server; "shim" is loaded dev vocabulary |

Also: **Clasp** (`@google/clasp`) · **Trunk** (trunk.io) · **Score** (score.dev) · **Keel** (keel.sh) ·
**Stride** (C# game engine) · **Nimble** (Nim) · **Grit** · **Flit** · **Hurl** · **Truss** · **Crux** ·
**Weld** · **Astral** · **Rush** (confirmed: Microsoft's monorepo orchestrator, not a generic word) ·
**Posit** (RStudio) · **Arborist** (npm's dep-tree manager) · **Sidestep** (AV-evasion tool; also *means avoid*) ·
**Bounce** (.NET build framework; NuGet taken) · **Quiver** (dev notebook app) · **Multiplex** (reads as tmux) ·
**Manifold** · **Myriad** (Adobe typeface + live F# NuGet source generator) · **Omnia** · **Plural** (plural.sh) ·
**Multiplicity** (Stardock — desktop software for controlling multiple machines) · **Manyfold** · **Nest** ·
**Bevy** · **Swarm** · **Bundle** · **Harbor** · **Depot** · **Rack** · **Caddy** · **Loom** · **Warp** ·
**Harness** · **Relay** · **Shuttle** · **Conductor** · **Turnstile** · **Snap** · **Yank**

**Fatal — the `Omni-` prefix, entirely.** The Omni Group (OmniGraffle, OmniOutliner, OmniPlan, OmniWeb) runs an
actively enforced portfolio, and **Omnissa** — the VMware spinout — holds USPTO SN 98365706 in classes 9/42,
registered 2025-12-02. Do not enter this prefix under any construction.

**Fatal — second-sweep collisions (verified this pass):**
- **Skim** — `skim-rs`, the Rust fuzzy finder (`sk`), on your user's machine · **Swerve** — two CLI libs
  (`m4ntis/swerve`, `SpikeHD/swerve`) · **Pluck** — `git-pluck` exists, plus `schollz/pluck` · **Glide** — the
  old Go package manager · **Riffle** — `sharkdp/riffle` (author of bat/fd/hyperfine) · **Veer** — CHIPS
  Alliance VeeR RISC-V cores
- **Darter** — embeds "**Dart**" and `darter.dev` is a Dart/Flutter tool (the Swift trap, generalized) ·
  **Borzoi** — `automation-co/borzoi` is a **git polyrepo manager**, your exact lane · **Harrier** — Microsoft's
  open-sourced embedding models (Apr 2026) · **Saluki** — DataDog's Rust telemetry toolkit · **Jerboa** —
  `LemmyNet/jerboa`, a well-known native FOSS app · **Turnstone** — a trending LLM-agent orchestrator
- **Trice** — `rokath/trice`, fast C/C++ tracing, identical register · **Whoosh** — the full-text search lib ·
  **Brisk/Briskly** — `BrisklyDev/brisk`, an "ultra-fast desktop download manager" (nearly your exact pitch) ·
  **Glance** — `glanceapp/glance` · **Grain** — the Wasm language · **Gossamer** — 3+ firmware/Polkadot frameworks
- **GitLeap** — a shipping GitLens fork · **GitFlick / GitGlide / GitDart** — each has a squatted org or repos
  the domain probe misses. *Any Git\<PlainTier1\> (GitSwoop, GitSkiff, GitPennant) is strictly dominated by the
  bare word — you re-solve a solved sub-problem and add the portmanteau flag.*
- **Nimbly** — Nimbly Technologies, a $4.6M-funded retail-ops platform, holds the exact name

**Fatal — DPML-locked (unregisterable, not free):** `nab.sh`, `glide.sh` and other short dictionary words return
Identity Digital's *DPML Brand Protection* string rather than a record — operationally locked.

**Fatal — third-sweep collisions (verified this pass):**
- **Shearwater** — Shearwater Research ships "Shearwater Desktop/Cloud" (real desktop apps); owns the SERP ·
  **Avocet** — `avocet-shores/laravel-rewind` is literally "version control for Eloquent models, diff/restore" ·
  **Curlew** — embeds `curl`, plus a shipping Linux desktop GUI · **Shrike** — NuGet + `.sh` taken · **Fleck** —
  a popular **C# WebSocket library** (your ecosystem) · **Wisp** / **Glisten** — both **Gleam-language** packages
  (Gleam is a fatal collision) · **Snell** — Surge's proxy protocol, `snell.sh` is a 1.4k★ repo · **Alacrity** —
  homophone of **Alacritty**, the GPU terminal · **Solano** — one letter from **Solana**; also a git-CI tool ·
  **Ostro** — sound-alike to **OSTree** ("git for OS binaries") · **Chinook** — the canonical .NET sample DB ·
  **Zonda** — Pagani + zondacrypto own the term · **Levanter** — a 706★ Stanford ML framework · **Felucca** — a
  live Cairo package manager + Ultima Online lore
- **Lewd/juvenile gate (the Jink gate), could not clear:** **Pinnace** (reads "penis") · **Cutter** (self-harm
  slang) · **Smack** (heroin) · **Shamal** (unclearable sound-alike) · **Glister** (archaic = enema) · **Noddy**
  (juvenile) · **Dory** (the *forgetful* fish — self-defeating for version control)
- **Spell-from-hearing, confirmed dead:** Yare (yair/yar) · Ketch (=catch) · Wherry (=ferry) · Wight (=white) ·
  Foehn (=fern) · Vivace/Veloce (English cold-reads them wrong) · Tiercel · Vireo

### Fatal — pronunciation or spelling (the Pecia failure, repeated)

**Pliego** — the most painful cut: Spanish for a folded printing sheet, essentially *pecia*. But Spanish says
PLYEH-go and English cold-reads PLEE-go. Shipping it would repeat the mistake with extra steps.

**Poset** (three-way split; `.dev`+`.sh`+NuGet free is the bait) · **Pfad** (`pf-` doesn't exist in English) ·
**Falz** · **Plooi** · **Kvisl** · **Merkle** ("Merkel" is the likelier spelling) · **Hasse** · **Trie**
(spells as "try") · **Treap** · **Keccak** · **Clique** · **Klados** · **Punto** · **Punkt** · **Ponte** ·
**Piega** · **Quoin** (writes as "coin") · **Forme** · **Quire** (homophone of "choir") · **Caret** ·
**Obelus** · **Pilcrow** · **Preorder** · **Luge** (loozh/looj) · **Whir** (whir/whirr)

**Homophone kills:** Peak/peek · Plait/plate · Plumb/plum · Purl/pearl · Vise/vice · Vale/veil · Pane/pain ·
Pier/peer · Soar/sore · Knit/nit · Yoke/yolk · Copse/cops · Scull/skull · Hare/hair · Tern/turn · Yawl/y'all ·
Dinghy/dingy · **Sail/sale** (kills Skimsail, Flicksail, Gustsail despite free domains)

### Fatal — brand or trademark

**Lark** (ByteDance) · **Rook** (CNCF) · **Almanac** ("a doc editor with version control") · **Vellum**
(book-formatting software) · **Lattice** (HR unicorn + NASDAQ semiconductor) · **Vertex** (scorched earth) ·
**Deckle** & **Caret** (both paid cross-platform desktop editors — your exact business model) · **Exemplar**
(exemplar.dev is a live AI dev platform) · **Pica** (nodeca/pica, 2.1M npm downloads/mo — *note: the
PIE-ka/PEE-ka split does not exist; RP and GenAm agree on /ˈpaɪkə/*) · **Spine** (Esoteric Software) ·
**Marginalia** (the search engine your audience loves) · **Kata** (143k repos)

### Fatal — unfortunate meaning

**Puu** (Finnish "tree" — reads as "poo") · **Piste** (opens with "piss") · **Spoor** (in English hunting
usage, includes droppings) · **Rama** (a major Hindu deity + Unilever margarine) · **Merk** (UK slang) ·
**Naht** (homophone of "naught") · **Rug** · **Prensa** (*La Prensa* — unwinnable in Spanish) · **Scud**
(missile) · **Jib** (to balk) · **Lunge** (aggressive/stabbing — *note: `lunge.dev` is free, the only bare
`.dev` in the register; rejected on tone alone*)

### Fatal — Russian-specific

**Pamyat** (the late-Soviet ultranationalist and openly antisemitic organization) · **Papka** (baby-talk for
"daddy") · **Parus** (major Russian ERP vendor) · **Pivo** ("beer") · **Vetka** (literally what Russian devs
call a git branch — phonetically flawless, every TLD gone, and too literal anyway)

### Weakened by verification

- **Kerf** — the "kerf1 trademark" **does not exist** (prior research conflated a GitHub project with a
  filing). Reality is worse: `kevinlawler/kerf1` is unarchived with 2026 activity, a live class-42 KERF
  application was filed 2026-04-16, and `getkerf.com` ships an AI workshop assistant.
- **Dowel** — "no software namesake" is **wrong**. `@dowel/dowel` is a shipping architecture linter.
- **Bowline** — US software lane empty, but a live **German** cl. 9/42 registration (expires 2028) and an
  active `CivicActions/bowline` CI tool.
- **Tenon** — *improved*: Tenon.io is **dead**, retired Aug 2023, absorbed by Level Access.
- **Katern** — Dutch for a quire; the most faithful heir to *pecia* and the emptiest namespace tested. **It
  will keep tempting you.** kuh-TERN vs KAT-ern is a diluted form of the exact defect you already paid for.

---

## Competitors found incidentally

Unrelated to naming, but worth your time:

- **`github.com/billdenney/sprig`** — Git GUI for macOS/Windows, Finder-first, shells out to `--porcelain=v2`,
  tiered confirmations, snapshots, local-first AI via Ollama / Apple Foundation Models.
- **`github.com/Joselay/etch`** — verbatim "A fast Git GUI for macOS, Windows and Linux."

---

## Verification methodology

Reusable. Several agents independently burned a full pass on the traps below.

| Target | Method |
|---|---|
| `.dev` / `.app` | `https://pubapi.registry.google/rdap/domain/<name>.dev` — 404 = free |
| `.com` | `https://rdap.verisign.com/com/v1/domain/<name>.com` — no rate limit, use for bulk screening |
| `.sh` | `whois -h whois.nic.sh <name>.sh` — **no RDAP service exists for `.sh`** |
| NuGet | `https://api.nuget.org/v3-flatcontainer/<lowername>/index.json` |
| GitHub | authenticated `gh api users/<name>` — **not** the HTML page |
| `.tools` | `https://rdap.identitydigital.services/rdap/domain/<name>.tools` — 404 = free. **`whois name.tools` reproduces the `.dev` trap exactly** and is worthless |
| Trademarks | `POST https://www.tmdn.org/tmview/api/search/results` with `{"page":"1","pageSize":"100","criteria":"C","basicSearch":"<name>","fOffices":["US"]}` — ⚠️ **returned an empty body with no error on 2026-07-19.** Treat as down, not as "no marks found" |

**Traps, all hit live:**

1. **`whois name.dev` is worthless.** `.dev` has an *empty* IANA `refer:` field — Google's registry is
   RDAP-only, so `google.dev` and a nonsense control return **identical stubs**. The original
   `rename-to-pecia.md` caveat ("whois port 43 was blocked") was a misdiagnosis: nothing was blocked, the
   referral doesn't exist.
2. **RDAP rate-limits to 429 above ~5 rapid queries, and 429 ≠ taken.** One agent falsely condemned 7 good
   names by parallelizing. Sequential at 9–13s with retry.
3. **Missing nameservers never prove availability.** `moss.sh`, `perch.sh`, `puffin.sh` all have no NS and are
   registered.
4. **`github.com/<anything>` returns 200** — use the API. The anonymous quota (60/hr) exhausts fast and then
   returns **403 for everything including `torvalds`**; the API also intermittently 503s.
5. **`tmsearch.uspto.gov` has no public API** — it's a static S3-hosted SPA, which is why those paths 404
   rather than erroring on auth. Justia and Trademarkia 403. Use TMview above.
6. **Search engines block automation** — DuckDuckGo returned empty results even for control queries, Mojeek
   blocked, WIPO sat behind a CAPTCHA. This is why **domain/registry data here is trustworthy and
   brand/SEO notes are not.** **Amended 2026-07-19:** this applies to *fetching* those engines directly. The
   **WebSearch tool works cleanly** and produced the entire multi-repo prior-art table above — so
   [Open item 3](#open-items) is now cheap and actionable.
7. **macOS has no `timeout(1)`.** `timeout 25 whois …` fails silently → empty output → every `.sh` reports
   free, including known-registered controls. Use `gtimeout` or nothing. This falsely cleared an entire lane
   before the doc's own known-taken controls caught it.
8. **`curl` without `--max-time` hangs indefinitely** against rate-limited Google RDAP — two probe runs
   produced zero output over ~15 minutes before anyone noticed.
9. **Parallel agents share both the RDAP quota and the scratchpad.** The "9–13s" spacing is per-*machine*, not
   per-script; two agents at 12s each is effectively 6s. And sibling agents writing `results.txt` truncated
   each other mid-run. **Namespace output files per lane**, and screen on the unlimited endpoints first
   (Verisign `.com`, NuGet, `.sh` whois), spending `.dev` probes only on survivors.
10. **`.sh` "free" is easy to mis-grep.** nic.sh returns `Domain not found.` — which does not match a
    `^NOT FOUND` anchored pattern and silently falls through as "no response," i.e. as *taken*. Case-insensitive,
    unanchored `grep -i "domain not found"` at 2s spacing ran 20 queries with zero empties — robust.
11. **`.com` is a live discriminator for *portmanteaus*, unlike real words.** `gitbrisk.com`/`gitswoop.com` are
    free bare `.com`s while ~40% of Git\<X\> candidates are taken. Screen `.com` first in that lane — it does
    real filtering there that it can't do for plain words.
12. **The binding Git\<X\> constraint is a squatted *repo*, not a taken handle.** GitLeap/GitDart/GitFlick have
    free domains and 404 handles but existing orgs/repos. Run `search/repositories?q=<name>+in:name`.
13. **Collisions in the speed lane hide in the tagline, not the name.** Trice/Whoosh/Brisk pass every registry
    probe yet each ships as a tool *marketing on speed*. Search `"<name>" fast OR tiny OR desktop`, not just
    `"<name>" software`.
14. **Adverb (`-ly`) and pure-coinage namespaces are systematically clean — that's a red flag, not a find.**
    Briskly/Nimbly/Spryly had near-complete free namespaces because the form is unnatural for a product. Same
    "cheap availability = low demand" signal as the Tier-3 invented compounds.
15. **DPML is a third bucket.** Identity Digital returns a *"protected by the DPML Brand Protection policy"*
    string for short dictionary words — neither `Domain not found.` nor a registration record. A two-way grep
    misclassifies it as UNKNOWN; it means *unregisterable*.
16. **Validate an endpoint with a control confirmed taken *on that TLD*.** `google.tools` is 404 (Google never
    bought it) — using it as a known-taken control would falsely suggest `.tools` reports everything free. Use
    `osprey.tools` (200) instead.
17. **Watch for silent partial batches.** The `.tools` RDAP endpoint and un-`--max-time`'d `.dev` loops both
    returned fewer rows than names, exit 0, no error. Assert expected-line-count vs actual before trusting.
18. **zsh does not word-split unquoted variables.** `for n in $names` iterates *once* over the whole string
    under this repo's default zsh (unlike bash) — curl gets embedded spaces and returns `000` for every name,
    falsely condemning a whole lane. Use an array `names=(...)` or `${=names}`. Hit independently by 3 agents.
19. **Embedded-tool substrings generalize past languages.** The Swift/Dart trap extends to Unix commands and
    prefixes: Curlew ⊃ `curl`, Needletail ⊃ `tail`, Proa ⊂ `pro*` (10k GitHub hits), Imbat ⊂ `im`+`bat`.
20. **Own-your-term ≠ no-product-collision, in both directions.** A name with zero *product* rivals can still be
    unownable from dictionary/celebrity/surname frequency (Sheen, Wherry); and a "free" `<name>.sh` apex can
    still be a marquee repo name (`snell.sh`). Weight GitHub `total_count` magnitude *and* read the top repos.
21. **Whole naming veins are pre-claimed by big non-software brands.** Maserati names Mediterranean winds
    (Levante, Ghibli, Shamal, Khamsin); Pagani took Zonda. The Gleam language claims light-words as package
    names (wisp, glisten) — each of which is then itself your fatal collision. WebSearch the bare word.
22. **A hardware vendor's companion desktop app owns the SERP the registries can't see.** Shearwater cleared
    every registry and had near-zero GitHub stars, yet "Shearwater Desktop" fails the own-your-term gate. Always
    WebSearch `"<name>" desktop` even when GitHub looks empty.

**Control-test every probe** against a known-free nonsense string *and* a known-taken name before trusting a
single result.
