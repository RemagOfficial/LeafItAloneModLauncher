# Mod Source Spec

A **mod source** is any GitHub repository the Leaf it Alone launcher can browse and install
mods from. This document defines the layout, the manifest fields and the versioning rules a
source must follow so the launcher's "Browse online" tab can list, install and update its mods.

Third-party mod authors fork or clone the [sample repo](sample-source-repo/) and publish
their own. Adding it from **Settings -> Mod sources -> Add source** is what turns the launcher
on to it. Until the user opts in, the launcher browses only the built-in
`RemagOfficial/LeafItAloneModLauncher` source.

---

## Repository layout

```
<your repo>/
  content-mods/
    <mod-id>/
      mod.json           # required, the manifest
      apply.ps1          # required by the manifest's apply.script
      revert.ps1         # required by the manifest's revert.script
      patch_<something>.py      # optional UnityPy patcher
      README.md          # optional, shown on the mod's Details pane
      ...                # any additional files you want shipped with the mod
    <another-mod>/
      ...
```

Rules:

- The **`content-mods/`** directory at the repo root is the only path the launcher scans.
- Each mod is a **sub-folder of `content-mods/`**, and each folder must contain a
  valid `mod.json`. Any sub-folder without a `mod.json` is silently ignored (so scratch
  or WIP folders don't break the listing).
- The `<mod-id>` folder name is used only as a fallback when the manifest omits `id`.
- Sub-paths inside a mod (nested folders) are allowed. Files at the mod root are
  downloaded verbatim into `remote-mods/<sourceKey>/<mod-id>/`.
- The launcher **never writes to the source repo**. All installs go into the user's
  `%LOCALAPPDATA%\LeafItAloneSafe\remote-mods\` tree.

Anything outside `content-mods/` (a `docs/` folder, `launcher/` source, README, etc.)
is ignored for mod discovery. It's still downloaded if you list it inside a mod folder,
but the launcher won't scan the rest of the tree.

---

## `mod.json` fields

Required for install. The launcher skips a mod whose manifest is missing these or fails
to parse.

| Field        | Type   | Notes |
|--------------|--------|-------|
| `id`         | string | Stable, kebab-case (`my-cool-mod`). Used as the folder name in `remote-mods/` and the key in `state.json`. Falls back to the folder name if omitted. |
| `name`       | string | Display name shown in the Mods page and Browse tab. |
| `version`    | string | Semver-ish, `1.2.0`. Display + `LauncherState.mods[id].appliedVersion`. |
| `description`| string | One or two sentences. |
| `configPath` | string | Absolute path with env-var tokens (e.g. `%LOCALAPPDATA%\LeafItAloneSafe\my-mod.json`). Where the live config for this mod is stored. Preserved across updates. |
| `config`     | object | `{ fields: [ ... ] }`. See the [built-in mods](../content-mods/) for field shapes. |
| `apply`      | object | `{ script: "apply.ps1", args: [...] }`. Path is relative to the mod folder. |
| `revert`     | object | `{ script: "revert.ps1", args: [...] }`. |
| `requires`   | object | `{ safeBase: bool, gameNotRunning: bool, python: bool }`. Mirrors what the built-in mods declare. |

Optional:

| Field                  | Type   | Notes |
|------------------------|--------|-------|
| `order`                | int    | Lowest applies first in an "Apply & Launch" sequence. Default 0. |
| `homepage`             | string | Link shown in Details. Also the natural place for your "source repo" URL. |
| `sourceRepo`           | string | `owner/repo` this mod was authored against. Cosmetic only; the launcher already knows the repo it was fetched from. Set it so a mod copied between repos still carries provenance. |
| `minLauncherVersion`   | string | The lowest launcher version you tested against. The Browse tab warns (does not block) if the user's launcher is older. |
| `gameVersion`          | string | Game version you authored against, e.g. `"1.1.7"`. Shown as a compat hint. |

Any unknown JSON field is preserved when the launcher re-reads the manifest, so you can
add your own keys without breaking discovery.

---

## Versioning rules

- **Mods follow semver** as far as it makes sense: `major` for breaking changes in the
  mod's config schema (a rename in `config.fields[].key` is breaking because a user's
  saved JSON stops mapping), `minor` for new fields or new behavior, `patch` for fixes.
- **The source repo does not need its own version.** The launcher tracks installs by
  **commit SHA**, not by tag. When the repo's default branch (or the tag you pin the
  source to) advances, installed mods show an "update available" badge.
- **Refs are per-source, not per-mod.** You cannot pin one mod to tag `v1` and another
  to `v2` from the same source entry. If you need that, add the repo twice with
  different ref values (the launcher derives a different `key` for each).
- **Backward-compat of the manifest**: only *add* fields to `mod.json`. The launcher's
  deserializer ignores unknowns but refuses mods missing the required set above, so
  removing or renaming a required field is a breaking change for downstream users.

---

## Trust model

Installing a mod from a source **copies scripts onto the user's disk**; the launcher does
not run them until they press Apply. Because of that the launcher gates installs at two
points:

1. **Non-default sources require an opt-in.** `Settings -> Mod sources -> "Allow
   installs from non-default sources"` must be ON before the "Add source" button is
   enabled. Off is the default so a user's very first run can only browse ours.
2. **The first install from a source shows the file list.** Repo URL, commit SHA and
   every script the mod ships (`apply.ps1`, `revert.ps1`, any `*.py`) are shown, and
   the user must type `INSTALL` to proceed. Once a source is marked `trusted`, later
   installs from it skip the prompt.

Never bundle anything that reads the user's Steam credentials, browser profile, or
`%APPDATA%` outside `LeafItAloneSafe\`. Never call out to the network. If a mod must
shell out to something unusual, document it in the mod's README and in the trust modal.

---

## Publish checklist

- [ ] `content-mods/<your-mod>/mod.json` parses (test against the launcher's own
      `ModDiscovery` or just install it locally and hit Rescan).
- [ ] `apply.ps1` accepts `-Yes` (and `-Python` if it needs UnityPy) and honors the
      safe-base preconditions via the shared `base.psm1`.
- [ ] `revert.ps1` restores the pristine bundle for every file `apply.ps1` touched.
- [ ] README in the mod folder explaining what it changes and how to tweak it.
- [ ] Push a tag (`v1.0.0`) so users who want a pinned ref can add your repo with
      `ref=v1.0.0` and never auto-update.
- [ ] Ask the launcher maintainer to add a "featured" listing (optional; nothing else
      is required to appear in Browse - users can add any repo by URL).

---

## Notes on non-GitHub sources

The v1 fetcher (`GitHubSourceService`) uses the GitHub REST API exclusively. GitLab,
Codeberg and Bitbucket are not supported yet, but the service is deliberately named for
GitHub so an adapter for another host is a follow-on milestone rather than a rewrite.
For now: host your repo on GitHub.
