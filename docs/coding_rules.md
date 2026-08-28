# Coding Rules

*Language-agnostic. Three rules, a review order, and a translation table.*

> **"The checker"** below means whatever the strongest automatic verifier in your language is: a static type system, a borrow checker, a linter, a runtime contract, or — at the weak end — only your test suite.
>
> The rules get *more* valuable as the checker gets weaker, not less. What a checker won't catch, a construction-time invariant or a test has to, and the cheapest place to put either one is exactly where these rules point.

---

## Rule 1 — Make illegal states unrepresentable, and never lie to the checker

Given a choice, pick the type or API that makes a whole category of error impossible to write. Prefer a design the caller cannot misuse over one that is correct only while the caller remembers a rule.

- **Parse at every boundary; never assert across one.**

  A boundary is anywhere data enters the program without the checker having seen it produced: network responses, env vars, CLI args, config and data files, DB rows, queue messages, IPC, FFI, deserialization, user input.

  Convert once at the edge with a function that *accepts the untrusted shape* and validates it, and **derive** the internal representation from the validation rather than declaring it twice. Never declare a type and then tell the checker an unvalidated payload already is one.

  In a language with no static types the rule is unchanged: build a domain object at the edge instead of threading a raw dict/hash/map through the program. One place fails loudly; everything downstream is trusted for a reason rather than by habit.

- **Model with sum types, not optional-field bags.**

  ```
  YES:  Loading | Ok(T) | Error(E)
  NO:   { loading: bool, data?: T, err?: E }   // 8 states, 5 are nonsense
  ```

  A record with N optional fields has 2^N states and you meant about three of them. Every extra representable state is a branch someone must handle — or silently won't.

  Then make the checker enforce coverage: an exhaustive `match`/`when`/`switch`, so a later variant breaks the build at every call site. If your language has no exhaustiveness check, the fallback is a default branch that *throws* — loudly, in production, not a log line — plus one test per variant.

  If your language has no sum types at all, encode one: a tag field, one constructor per case, and a single interpreter function that switches on the tag. Keep those constructors the only way to build the thing.

- **Give every domain identifier and unit its own type.**

  `UserId` and `OrgId` as bare strings are freely interchangeable, and swapping them is a genuinely common production bug that reads fine at the call site. Same for `Money` vs float, `Millis` vs `Seconds`, `Email` vs string.

  In a nominally typed language this is nearly free — a newtype, a value class, a record wrapper. In a structurally typed one you need an explicit brand or phantom tag to defeat the structural comparison. In a dynamic one it's a tiny class whose constructor validates. All three cost ~nothing at runtime and are the highest-yield type you will introduce.

- **Don't reach for the escape hatches — they are the bug class itself.**

  Unchecked casts, force-unwraps, `any`-typed holes, checker suppressions, reflection by string name, and catch-all exception swallowing are the exact points where this whole rule is switched off. Reach for the checked alternative first: a type guard or narrowing construct, a total match on an optional, a constraint that checks a literal without widening or asserting, or a parse at the boundary.

  If you genuinely need one, isolate it in the smallest possible function, make that function total, and flag it in your reply (see Rule 3). The [appendix](#appendix--the-mechanism-in-each-language) lists what these are called in your language.

---

## Rule 2 — Structure abstractions by coupling, not by shape

Where a unit sits in the dependency graph matters more than anything about its internals. Class-or-function, module-or-package, file layout — all syntactic choices, none of them the lever.

- **Minimize fan-out, not file count.**

  What counts is how many distinct modules a unit depends on and how many depend on it. Before adding an import, check the edge is actually needed; before splitting a file, note that *more files at the same edge count is neutral, more edges is not*. Read the graph with a real tool and find the cycles.

  Best form of this rule: encode the intended boundaries as machine-checked rules run in CI, so a violation fails the build instead of waiting on review. Every ecosystem has a tool for this (appendix).

- **Never add global mutable state or ambient control flow.**

  Implicit control flow and shared mutable global state are the two structures that go wrong most often, and they defeat the checker and the reader at the same time. The forms this takes:

  - module-level mutable singletons, class statics, thread-locals and async-context globals
  - service locators and string-keyed DI containers
  - ambient pub/sub where the handler set isn't statically knowable
  - monkey-patching, import-time side effects, implicit registries

  Pass the dependency in. If that makes a signature ugly, the ugliness was already there — it was just invisible.

- **Don't add structure you haven't been forced into.**

  Guessing the wrong axis of variation is a net cost — worse than none.

  - **No inheritance depth.** Composition instead. Where your language needs an interface for substitutability, define it at the *consumer*, keep it one level, and don't grow a hierarchy behind it.
  - **No cohesion refactors.** "This module does two things" is an aesthetic complaint, not a defect prediction. Don't volunteer that split.
  - **No abstraction before the variation is in the code.** Write the second and third case, then factor. A record plus a function you can inline later is cheaper to be wrong about than a hierarchy.

  God Class is a fan-out problem, not a size problem.

---

## Rule 3 — Review the checker's blind spots first

Before handing back code you wrote or changed, re-read it in this order. The checker has already reviewed most of it; your attention belongs where it stopped.

1. **Every place you silenced or bypassed the checker** — each cast, suppression, force-unwrap, unsafe block, raw string query, swallowed exception. These are the *only* places checking stopped. Code with none has already been mostly reviewed for you; code with ten is where all the effort goes. Justify each in a comment or remove it.
2. **Changed public signatures and exported types** — non-local blast radius that isn't visible in the code in front of you. Check the call sites.
3. **New boundary crossings** — a new request, env read, deserialization, DB query, file read, or message payload. Each needs a parse, not an assertion.
4. **New shared mutable state, concurrency, or ordering assumptions** — anything two things can touch at once, and anything that must happen in an order the code doesn't force. Most type systems check none of this for you, which is exactly why it belongs this high.
5. Everything else.

- **Report anything you hit at levels 1–4**, rather than leaving the user to find it.

- **Don't add re-export hubs**, or route new imports through an existing one when the direct module path works.

  Barrel files, `__init__.py` re-exports, wildcard imports, prelude modules, facade packages. They create implicit fan-out: the dependency graph goes opaque, so level 2 above can't be checked by reading. Where the re-export is erased at build time the cost is review opacity only, not shipped code — which is still a cost.

---