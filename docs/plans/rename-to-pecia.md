# Rename: GitBench → Pecia

> **Pecia** (PEH-cha) is the medieval university system, first attested in the Vercelli contract
> of 1228, where a stationer held an authorized master copy — the *exemplar* — split into numbered
> pieces. Each piece was rented in parallel to different scribes who copied simultaneously; if a
> piece was already out, you took the next and left a gap to fill later. Every copy was verified
> against the master. It is distributed version control described in the 13th century.

> **Status: not started.** Phase 0 gates everything else.

## Verified

| Check | Result |
|---|---|
| `pecia.dev` / `.sh` / `.app` / `.io` | No nameservers — likely free |
| `peciaapp.com`, `getpecia.com` | Likely free |
| `pecia.com` | Registered to a drop-catch broker (DOMAINRECOVER nameservers) |
| npm `pecia` | Free |
| NuGet `Pecia` | Free |
| `github.com/pecia` | **Taken** — squatted zero-repo account; use `peciaapp` |
| SEO | Page one is Wikipedia, OED, Encyclopedia.com, Oxford Academic. Zero software |

**All domain checks were DNS-inferred — whois port 43 was blocked during verification.** A
registered domain with no delegation is indistinguishable from an unregistered one. Confirm at a
registrar before trusting.

## Identity decision: clean break

Velopack keys an installation on `--packId`. Shipping a new packId means existing installs poll for
`GitBench` packages, find none, and silently stop updating — no error, no prompt.

The userbase is small and an interruption is acceptable, so **take the clean identity**: new
`packId`, new `bundleId`, new install directory, and one bridge release to tell existing users where
to go. The alternative — freezing `--packId GitBench` forever inside a product called Pecia — was
considered and rejected as a permanent wart for a one-time cost.

This means **existing installs will not auto-update to Pecia.** That is the accepted trade, and
Phase 1 exists to soften it.

## Phase 0 — reserve the namespace

Nothing else starts until this is done; a rename to a name someone else buys first is wasted work.

- [ ] Confirm `pecia.dev` at a registrar and buy it (see the DNS caveat above).
- [ ] Buy `peciaapp.com` (or `getpecia.com`) as the `.com` presence.
- [ ] Reserve NuGet ID `Pecia` and npm name `pecia` even if unused.
- [ ] Create the GitHub org/handle (`peciaapp` — `pecia` is squatted).
- [ ] Trademark sanity check before promoting.

## Phase 1 — bridge release (before any renaming)

Ship one final GitBench-branded release, on the **existing** packId, whose only new content is an
in-app notice: the project is now Pecia, here is the download link, your data will carry over.

This is the last thing existing users receive automatically. It must go out before the identity
changes, or there is no channel left to reach them.

- [ ] In-app banner or dialog pointing at the new site.
- [ ] Tag and publish on the current `GitBench` packId.
- [ ] Update the GitHub release notes for the final tag to say the same thing.

## Phase 2 — code rename

~1,757 `GitBench` references across the tree. Mechanically simple; no logic changes.

- [ ] Projects and namespaces: `GitBench` → `Pecia`, `GitBench.Tests` → `Pecia.Tests`,
      `GitBench.Automation` → `Pecia.Automation`, `GitBench.Localization.Generator`,
      `GitBench.sln` → `Pecia.sln`, `GitBenchHost` → `PeciaHost`.
- [ ] `GitBench/GitBench.csproj`: `ApplicationIcon`, both `InternalsVisibleTo` entries.
- [ ] `GitBench/App/UpdateFeed.cs:13` — `GithubSource` URL to the renamed repo. GitHub preserves
      redirects after a repo rename, but new builds should point at the real URL rather than depend
      on a redirect indefinitely.
- [ ] `.mcp.json`, README, `docs/`, LICENSE header.

## Phase 3 — release pipeline

All in `.github/workflows/release.yml`:

- [ ] `--packId GitBench` → `Pecia` (this is the identity break; see above).
- [ ] `--packTitle GitBench` → `Pecia`.
- [ ] `--bundleId com.builtbyzee.gitbench` → `com.builtbyzee.pecia` (both macOS matrix rows).
- [ ] Matrix `exe:` entries → `Pecia` / `Pecia.exe`.
- [ ] Three hardcoded `--repo Zeejfps/GitBench` occurrences in the publish job.
- [ ] Icon paths as they are renamed in Phase 5, including `png-to-icns.sh` arguments.
- [ ] Reset or continue versioning deliberately — a new packId means the version line can restart.

## Phase 4 — user data migration

The only phase with real logic. `GitBench/App/AppPaths.cs:12` resolves to `%APPDATA%/GitBench`.
Rename it blind and every user silently loses preferences, window layout and repo list — it presents
as a fresh install, which reads as data loss.

- [ ] One-time shim: if the `Pecia` data dir is absent and `GitBench` exists, copy it across before
      first read. Copy rather than move, so a rollback leaves the old install intact.
- [ ] Keep `GITBENCH_DATA_DIR` working as a deprecated alias alongside a new `PECIA_DATA_DIR`.
      The automation and test tooling depends on the existing variable.
- [ ] Cover the migration headlessly — first-run-with-existing-data is the case that matters.

## Phase 5 — assets

- [ ] `commit_bench_icon.ico` / `.png` / `.rgba` / `_mac.png` / `_mac.icns` → `pecia_icon.*`,
      updating `GitBench.csproj` `None Update` entries and the `release.yml` matrix `icon:` paths.
- [ ] Artwork itself can change later; the rename should not block on a new logo.

## Phase 6 — outward-facing

- [ ] Rename the GitHub repo (redirects are preserved automatically).
- [ ] About field → `pecia.dev`; refresh topics.
- [ ] New site at `pecia.dev`; **301** from `gitbench.builtbyzee.com`. The old subdomain was never
      indexed, so the redirect is courtesy for humans holding old links, not equity preservation.
- [ ] Update the GitBench project entry on `evasilyev.com`.
- [ ] Add the meta description the old site was missing.

## Verification

Ordered by how much each would hurt if skipped:

1. **First run with existing GitBench data** — preferences, repo list and layout survive. This is
   the one that silently destroys user state.
2. **Full suite green** after the namespace sweep.
3. **Clean install per platform** from the new packId — win-x64, osx-arm64, osx-x64, linux-x64.
4. **Update within the new identity** — tag two Pecia pre-releases and confirm the second updates
   the first. This proves the new feed works before real users depend on it.
5. macOS bundle launches under the new `bundleId` without Gatekeeper complaint.

## Not doing

- Preserving auto-update continuity from GitBench to Pecia. Explicitly traded away; Phase 1 is the
  mitigation.
- Chasing `pecia.com` from the drop-catch broker, or a GitHub name-release request for
  `github.com/pecia`. Both are nice-to-have and neither gates anything.
