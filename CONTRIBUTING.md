# Contributing

Thanks for your interest in DotnetCronner.

## Build & test

```bash
dotnet build DotnetCronner.slnx -c Release
dotnet test DotnetCronner.slnx -c Release
```

Requires the .NET 10 SDK. Pull requests run build + test automatically (`.github/workflows/pr.yml`).

## Project layout

| Path | Package | Released? |
| --- | --- | --- |
| `src/DotnetCronner` | `DotnetCronner` (core + bundled analyzer) | yes |
| `src/DotnetCronner.Abstractions` | `DotnetCronner.Abstractions` | yes |
| `src/DotnetCronner.Stores.Redis` | `DotnetCronner.Stores.Redis` | yes |
| `src/DotnetCronner.Stores.EntityFrameworkCore` | `DotnetCronner.Stores.EntityFrameworkCore` | yes |
| `src/DotnetCronner.Analyzers` | bundled inside `DotnetCronner` | no |
| `samples/…`, `tests/…` | — | no |

## Releasing a package (independent versioning)

Every package is versioned and released **independently**, so a small fix ships without releasing
everything. Releasing is a **deliberate maintainer action** — merging a PR never publishes anything, and
there are no release tags for contributors to push. This keeps a wrong version or a rogue publish out of
the picture: only someone with write access can run the release workflow, and the NuGet push waits on an
approval gate.

#### How to cut a release (maintainers)

From the repo's **Actions** tab → **Release Package** → **Run workflow**:

1. **package** — `abstractions` | `core` | `redis` | `efcore`.
2. **bump** — `patch` (default) | `minor` | `major`. The workflow reads the package's latest release tag
   and computes the next version, so you don't look anything up. For a bug-fix release just leave it on
   `patch`.
3. **version** *(optional)* — an explicit SemVer (e.g. `1.0.0`) that overrides `bump`; use it for the first
   release or a specific number.
4. **prerelease** *(optional)* — publishes `‹version›-preview.‹run›` and marks the GitHub Release as a
   prerelease.

The **prepare** job prints the exact computed version (e.g. `core v1.2.4`, tag `core-v1.2.4`) in its
summary and runs the full build + tests. The **publish** job — the actual NuGet push, tag, and GitHub
Release — is gated on the `release` environment, so you (or a required reviewer) get one last **Approve**
click confirming the version before anything irreversible happens.

Only the tagged package is packed; its dependencies keep their committed floor `<Version>` (via the
`CRONNER_RELEASE_KEY` / `CRONNER_RELEASE_VERSION` mechanism in `Directory.Build.props`). No `.csproj` edit
is needed to release.

> One-time setup: create a `release` environment (Settings → Environments) and add yourself as a
> **required reviewer** so the approval gate is active. Publishing uses **NuGet trusted publishing (OIDC)** —
> there is no API key to store (see "Publishing credentials" below).

### Versioning: fully pipeline-driven

**No `.csproj` carries a `<Version>`** — versions are owned entirely by the release workflow, and each
package has its **own independent version line** (releasing one never bumps another). You set a package's
version per release, either by `bump` (`patch`/`minor`/`major`, auto-computed from that package's last
release tag) or by typing an explicit `version`. `core` can be at `2.1.0` while `redis` is at `0.4.2`.

Cross-package **dependency ranges are handled automatically**. When the workflow packs a package, it feeds
in each dependency's *last published* version (from its tag), so e.g. releasing `DotnetCronner.Stores.Redis`
emits `DotnetCronner >= <core's last published version>` — no floor to maintain and no csproj edit. Publish
in dependency order (`abstractions` → `core` → `redis`/`efcore`) so each dependency's tag exists when its
dependents are packed and the range is as tight as possible. (Locally, `dotnet pack` with no pipeline env
produces `0.0.0` packages — fine for inspection.)

### Publishing credentials — trusted publishing (no stored key)

The release workflow authenticates to NuGet.org with **trusted publishing (OIDC)**: GitHub mints a
short-lived token per run, which the `NuGet/login` step exchanges for a temporary, scoped API key. There is
**no `NUGET_KEY` secret to store or rotate.** One-time setup:

1. **NuGet.org → Account → Trusted Publishing**: add a policy binding your package id(s) to the GitHub repo
   `nfMalde/DotnetCronner`, the workflow `release.yml`, and (recommended) the `release` environment.
2. **Repo → Settings → Secrets and variables → Actions → Variables**: set `NUGET_USER` to your NuGet.org
   username.

A stored-API-key fallback is kept (commented) in `release.yml` as break-glass if trusted publishing is ever
unavailable.

## Using AI tools

Use whatever workflow you like, with or without AI assistance. You are responsible for the correctness
and licensing of what you submit, and every change must pass build and tests.
