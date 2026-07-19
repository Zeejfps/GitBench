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
`packId`, new `bundleId`, new install directory, and a bridge release that hands existing installs
over to Pecia. The alternative — freezing `--packId GitBench` forever inside a product called
Pecia — was considered and rejected as a permanent wart for a one-time cost.

Existing installs will not *auto-update* to Pecia in the Velopack sense; the bridge in Phase 1
installs it for them instead.

## Phase 0 — reserve the namespace

Nothing else starts until this is done; a rename to a name someone else buys first is wasted work.

- [ ] Confirm `pecia.dev` at a registrar and buy it (see the DNS caveat above).
- [ ] Buy `peciaapp.com` (or `getpecia.com`) as the `.com` presence.
- [ ] Reserve NuGet ID `Pecia` and npm name `pecia` even if unused.
- [ ] Create the GitHub org/handle (`peciaapp` — `pecia` is squatted).
- [ ] Trademark sanity check before promoting.

## Phase 1 — bridge release (installer handoff)

Velopack cannot chain one packId to another. `UpdateOptions` exposes only `ExplicitChannel`,
`AllowVersionDowngrade` and `MaximumDeltasBeforeFallback` — no appId override — and
`UpdateManager.AppId` is read-only ("the currently installed application Id"). A custom
`IUpdateSource` *could* lie about appId, since `GetReleaseFeed` receives it as a parameter, but the
apply step unpacks into a directory keyed to the installed AppId against package metadata naming a
different app. That is undocumented behaviour and the failure mode is an install that cannot
self-repair. Do not do it.

Instead the bridge performs a **handoff**: it is an ordinary GitBench-packId release that installs
Pecia alongside itself, then steps aside. Every step is a supported operation.

1. Existing installs auto-update to the bridge normally — same packId, ordinary release.
2. On launch the bridge downloads the Pecia installer for its platform.
3. It runs that installer, which lays down Pecia as a proper Velopack install under the new identity.
4. Pecia starts, runs the Phase 4 data migration, reads the copied settings.
5. The bridge reports that Pecia is installed and GitBench can be removed.

**Fetch from a stable URL, not a GitHub one.** At the time the bridge is built and shipped, the
Pecia releases may not exist yet, and the bridge cannot be re-released afterwards to fix a URL.
Point it at `pecia.dev/download/{channel}` — a redirect that is controlled independently of release
ordering and survives any future repo move.

Per-platform, matching the asset names `vpk` produces:

| Platform | Asset | Action |
|---|---|---|
| Windows | `Pecia-win-x64-Setup.exe` | Execute; confirm the silent-install flag against the vpk version in use |
| macOS | `Pecia-osx-arm64-Setup.pkg` / `Pecia-osx-x64-Setup.pkg` | `open` the pkg — cannot be silent without signing/notarisation and an admin prompt |
| Linux | `Pecia-linux-x64.AppImage` | Not an installer. Download beside the existing app, `chmod +x`, launch, exit |

- [ ] Reuse the existing channel detection in `GitBench/App/UpdateFeed.cs:19` (`RuntimeChannel()`)
      to pick the asset — it already resolves win-x64 / osx-arm64 / osx-x64 / linux-x64.
- [ ] Download to temp, verify the file is non-empty and executable before running it.
- [ ] Handle the user declining the macOS prompt — the bridge must stay usable, not wedge.
- [ ] Fall back to opening the download page if the handoff fails for any reason.
- [ ] Tag and publish on the current `GitBench` packId.
- [ ] Release notes for the final tag say the same thing, for anyone who reads them instead.

A user who never launches the bridge never migrates. That is acceptable — they install manually.

## Phase 2 — code rename

~1,757 `GitBench` references across the tree. Mechanically simple; no logic changes.

- [ ] Projects and namespaces: `GitBench` → `Pecia`, `GitBench.Tests` → `Pecia.Tests`,
      `GitBench.Automation` → `Pecia.Automation`, `GitBench.Localization.Generator`,
      `GitBench.sln` → `Pecia.sln`, `GitBenchHost` → `PeciaHost`.
- [ ] `GitBench/GitBench.csproj`: `ApplicationIcon`, both `InternalsVisibleTo` entries.
- [ ] `GitBench/App/UpdateFeed.cs:13` — `GithubSource` URL to the renamed repo. Verified: the
      GitHub API 301s a renamed repo to its stable `/repositories/{id}/` form and `HttpClient`
      follows redirects by default, so pre-rename installs keep updating. New builds should still
      point at the real URL rather than depend on a redirect indefinitely.
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

- [ ] Rename the GitHub repo (redirects are preserved automatically). **Never create a new repo at
      `Zeejfps/GitBench`** — GitHub drops the rename redirect the moment the old name is reoccupied,
      which would strand any install still resolving through it.
- [ ] Set up `pecia.dev/download/{channel}` before shipping the Phase 1 bridge — the bridge depends
      on it and cannot be re-released to fix a bad URL.
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
