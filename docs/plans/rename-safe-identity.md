# Rename-safe identity

> **Status: proposed.** Supersedes the identity analysis in `rename-to-pecia.md`, which assumed a
> `packId` change silently strands existing installs. It does not — see Verified below.

The app now presents as **DiffDino** while its update identity, data folder and executable are still
`GitBench`. The goal is not merely to finish that rename, but to make *every future* rename a
display-string change that existing installs follow automatically.

## Verified

Velopack has no first-class rename feature, but it also enforces no identity anywhere in the update
path. A rename is therefore a soft change, not a break. Every claim below was read out of Velopack
`1.1.1` (the pinned version) or current `develop`:

| Claim | Evidence |
|---|---|
| The release feed is keyed on **channel only** | `GitBase.GetReleaseFeed` builds the asset name from `CoreUtil.GetVeloReleaseIndexName(channel)`; the `appId` parameter is accepted and never used |
| `UpdateManager` never compares feed `PackageId` to local `AppId` | No such comparison in `CheckForUpdatesAsync`, `DownloadUpdatesAsync` or `ApplyUpdates` |
| The Rust core never compares them either | No id check in `lib-rust/src/locator.rs`, `bins/src/commands/apply.rs`, or `apply_windows_impl.rs` |
| Apply is **in place** — it rewrites the install it finds, whatever it is called | macOS: "during the apply step the `.app` bundle will be extracted and replaced"; Windows: `update_uninstall_entry(old_locator, &new_locator)` + `create_or_update_manifest_lnks(&new_locator, Some(old_locator))` |
| Shortcuts and Programs & Features **retitle themselves** from the new manifest | Same two calls; the shortcut epic (#118) is closed |
| The maintainer says so outright | velopack#119: *"you can just change the `--packId` and start rolling releases as normal, it won't break anything, but the currently installed location in `%LocalAppData%` will not be updated"* |
| Renaming an installed macOS `.app` is safe | velopack#186, contributor: *"I just renamed .app file at the first opening with my human-friendly name. Updates are working fine"* — the locator resolves the bundle from the running process path, not by name |

**The consequence:** changing `packId` costs a *stale install path*, not a broken update chain.
`%LocalAppData%\GitBench` keeps its name; the app inside it keeps updating, and its shortcut and
uninstall entry take on the new title at the next update.

**The one value that must never change** is the `--channel` (`win-x64`, `osx-arm64`, `osx-x64`,
`linux-x64`). That — not `packId` — is the join key between an install and its feed. It is already
brand-free, which is why this works at all.

**Caveat:** this behaviour is emergent, not contracted. Velopack could add id validation later.
Mitigations: `vpk` and the `Velopack` package are already version-pinned in lockstep
(`release.yml:97`, `GitBench.csproj:22`), and Phase 4 re-runs the proof on every upgrade of either.

## Design: three tiers

Every name in the tree sorts into exactly one tier. The rename procedure is then mechanical.

| Tier | Values | Cost to change |
|---|---|---|
| **Frozen** | `--channel` values; the fact that a feed exists at the redirector | Breaks updates. Never change. |
| **Identity** | `packId`, `bundleId`, main exe name, install path, data folder | Free for updates; leaves a stale path behind. Change deliberately. |
| **Display** | `packTitle`, `CFBundleName`, `AppInfo.DisplayName`, window title, About, menus, shortcut and uninstall names | Free, and propagates to existing installs on the next update. |

## Phase 1 — the rename seam

Today the seam is half-built: `AppInfo.DisplayName` already centralises the display name and — worth
noting — **no localisation JSON hardcodes the product name**, so the 6-locale rule does not bite
here. What is missing is the identity half.

Replace `GitBench/App/AppInfo.cs` with `AppIdentity`, the single file a rename touches:

```csharp
internal static class AppIdentity
{
    public const string DisplayName = "DiffDino";

    // Append-only, newest first. A rename prepends the outgoing folder name; first run then
    // migrates from the newest legacy folder that exists.
    public const string DataFolderName = "DiffDino";
    public static readonly string[] LegacyDataFolderNames = ["GitBench"];

    public const string DataDirEnvVar = "DIFFDINO_DATA_DIR";
    public static readonly string[] LegacyDataDirEnvVars = ["GITBENCH_DATA_DIR"];
}
```

- [ ] `AppPaths.AppDataPath` resolves through it: honour `DataDirEnvVar`, then each legacy env var in
      order, then `%APPDATA%/{DataFolderName}`.
- [ ] One-time migration on first run: if the current data folder is absent and a legacy one exists,
      **copy** it (not move) so a rollback to an older build still finds its state. Accept that the
      two then diverge; document it rather than trying to sync.
- [ ] Keep `GITBENCH_DATA_DIR` working indefinitely — `.claude/skills/verify/SKILL.md` and the
      automation/test tooling set it.
- [ ] Headless test: first run with an existing legacy folder preserves prefs, repo list and layout.
      This is the case that silently reads as data loss.

The data folder is ours, not Velopack's, so this phase alone removes the old name from the only
place a user is likely to look — and it exercises the legacy-list machinery immediately, so the next
rename is a one-line prepend.

## Phase 2 — feed behind our own domain

`UpdateFeed.cs:14` hardcodes `github.com/Zeejfps/GitBench`. A repo rename survives (the GitHub API
301s to `/repositories/{id}/` and `HttpClient` follows), but an owner move or a deleted repo strands
every install permanently. Put an indirection we control in front of it.

- [ ] Stand up `updates.builtbyzee.com/{channel}/*` as a redirect to
      `https://github.com/Zeejfps/GitBench/releases/latest/download/*` (a single Cloudflare wildcard
      rule). This resolves because CI uploads every channel's assets to each release and packs
      full-only packages, so the newest feed and the package it names are always on the latest
      release.
- [ ] Swap `GithubSource` for `SimpleWebSource(new Uri("https://updates.builtbyzee.com/{channel}/"))`.
      It requests `{baseUri}/releases.{channel}.json` **with `arch`/`os`/`rid`/`id`/`localVersion`
      query parameters appended** — confirm the redirect rule tolerates a query string — and then
      fetches each asset as `{baseUri}/{FileName}`.
- [ ] GitHub Releases stays the source of truth and the storage. The redirector is pure indirection,
      so there is no hosting cost and no new failure domain beyond DNS.
- [ ] Ship this **before** any repo rename. Installs already in the wild keep asking GitHub directly;
      only new builds carry the redirector, so the population converges over time. Until it has, a
      repo *rename* is safe (301) but an *owner move* is not.

Fallback if the wildcard proves too blunt: `SimpleWebSource` uses `releaseEntry.FileName` verbatim
when it is already an absolute URL, so a CI step could rewrite the feed's `FileName` fields to
tag-pinned GitHub URLs and the redirector would only ever serve JSON. More robust, more moving
parts — only reach for it if the `latest/download` form fails the Phase 4 proof.

## Phase 3 — take the identity to DiffDino (optional, and now cheap)

Given Phase 1 and 2, this is no longer load-bearing: it buys a clean internal name, not correctness.
It is safe to do because, per Verified, existing installs keep updating throughout.

- [ ] `release.yml`: `--packId GitBench` → `DiffDino`, `--packTitle GitBench` → `DiffDino`,
      `--bundleId com.builtbyzee.gitbench` → `com.builtbyzee.diffdino`, matrix `exe:` →
      `DiffDino` / `DiffDino.exe`, and the bundle assembled at `bundle/DiffDino.app`.
- [ ] `Info.plist.in`: `CFBundleExecutable` and `CFBundleIdentifier` follow the above.
- [ ] Keep the version line **monotonic** across the change. A version that does not increase is the
      one way to genuinely strand installs here, and it is easy to trip by "restarting" versioning
      alongside a new identity.
- [ ] Namespaces, project and solution names are cosmetic; sweep them separately (~1,720 `GitBench`
      references in `.cs` alone) so a functional change is never mixed into that diff.

What existing users see afterwards: the shortcut and the Programs & Features entry retitle to
DiffDino on the next update; the install stays at `%LocalAppData%\GitBench` and
`/Applications/GitBench.app`.

**Do not chase the stale paths.** The maintainer's suggested Windows fix is a detached `move.bat`
that renames the program directory out from under the running process, letting Velopack repair the
registry on the following update. The upside is a folder name nobody looks at; the downside is a
bricked install with no self-repair path. Same verdict for renaming the macOS bundle at launch: it
is confirmed to work (#186), but a `bundleId` change already resets the app's `NSUserDefaults`
domain, keychain items and TCC permission grants, so stacking a bundle rename on top compounds the
blast radius for a cosmetic win. Let new installs get the clean paths and let old ones age out.

## Phase 4 — prove it before users depend on it

The premise here is emergent behaviour, so it gets a standing proof rather than a one-off check.
On a scratch repo, not the real one:

1. Publish `v0.0.1` packed with `--packId GitBench --packTitle GitBench`.
2. Install it. Note the install path, the shortcut name and the Programs & Features entry.
3. Publish `v0.0.2` packed with `--packId DiffDino --packTitle DiffDino` and a renamed main exe,
   same channel.
4. Launch the install and confirm: it finds the update, applies it, relaunches into the **renamed
   exe**, the shortcut and uninstall entry now read DiffDino, and the install path is unchanged.
5. Repeat with `SimpleWebSource` against the redirector to cover Phase 2, including a query-string
   feed request.

- [ ] Re-run this whenever `Velopack`/`vpk` is bumped — it is the regression test for the one
      assumption everything else rests on.
- [ ] Also cover per platform: win-x64, osx-arm64, osx-x64, linux-x64.

## Release sequence

Nothing here needs to land at once. Each release is independently useful, independently revertible,
and changes one variable so a regression has one suspect.

| Release | Changes | Visible outcome | Before shipping the next |
|---|---|---|---|
| **R1** | Phase 1 seam + data-folder migration, and `--packTitle` → `DiffDino` | Data moves to `%APPDATA%/DiffDino`; shortcut and Programs & Features retitle on apply | Confirm an existing install kept its prefs, repo list and layout |
| **R2** | Phase 2 — `SimpleWebSource` at the redirector | None | — |
| **R3** | Any ordinary release | None | **This is the proof of R2**: an R2 install must reach R3 through the redirector, not GitHub |
| **R4** | Phase 3 — `--packId` and main exe name | Install path stays; relaunches into the renamed exe | Run the Phase 4 scratch-repo proof *first* — do not learn this on real users |
| **R5** | `bundleId` (macOS, optional) | Resets `NSUserDefaults`, keychain and TCC grants for existing Mac installs | — |

Two properties make this safe to stretch out:

**There is never a cutover.** The redirector points at GitHub Releases, which stays the storage and
the source of truth. An install on the old `GithubSource` and one on the redirector read the same
bytes, so both paths work forever with no extra publishing. An install that never launches simply
stays on the GitHub path indefinitely — that is fine, not a stranded user.

**The GitHub repo name becomes permanent at R2.** Pre-R2 installs resolve through
`github.com/Zeejfps/GitBench` (or its 301) forever, so the repo may be *renamed* at any point but
the old name must never be re-occupied and the owner must not change. If an owner move is ever
wanted, it waits until the pre-R2 population is negligible — release asset download counts are a
crude but adequate signal.

One direction-of-travel caveat: `AllowVersionDowngrade` is off, so a bad release is fixed by
shipping forward, never by rolling back. Keep versions monotonic across every row above.

## The payoff: renaming later

With Phases 1–2 done, a future rename is:

1. `AppIdentity.DisplayName` → the new name.
2. Prepend the outgoing folder name to `LegacyDataFolderNames`; set `DataFolderName`; add the new
   `DataDirEnvVar` and demote the old one.
3. `--packTitle`, `CFBundleName`, `CFBundleDisplayName` → the new name.
4. Icons and artwork.
5. Rename the GitHub repo if desired — and **never re-occupy the old repo name**, which would drop
   the 301 that pre-redirector installs still resolve through.

Existing installs keep updating throughout and retitle themselves. `packId`, `bundleId` and the exe
name are optional; leaving them alone costs nothing but a stale path, and changing them costs
nothing but a stale path either.
