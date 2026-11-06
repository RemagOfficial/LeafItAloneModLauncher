# Publish your own mod repo

You can share your Leaf it Alone mods without waiting for us to bundle them: publish a
GitHub repository that follows the [mod source spec](MOD_SOURCE_SPEC.md), and users can
add your repo from **Settings -> Mod sources**. The launcher lists, installs, updates
and self-heals mods from your repo the same way it handles our built-ins.

This guide walks through the shortest path from "I patched a value in a bundle" to
"third-party users can install my mod".

---

## 1. Fork the sample

Clone [docs/sample-source-repo/](sample-source-repo/) (in this repo) into your own GitHub
repo. It has one tiny working mod that only edits a numeric field so you can see every
required file:

```
sample-source-repo/
  content-mods/
    sample-bigger-bags/
      mod.json
      apply.ps1
      revert.ps1
      patch_sample_bags.py
      README.md
  .gitignore
  README.md
```

Rename `sample-bigger-bags` to your mod's kebab-case id (e.g. `neon-leaf-glow`).

## 2. Write your patcher

The launcher's engine is UnityPy against the game's `.bundle` files (see MODLOG.md in the
launcher repo for the whole pipeline). The sample's `patch_sample_bags.py` is ~30 lines:

1. `UnityPy.load(bundle_path)`
2. Find the MonoBehaviour whose typetree carries the field you want.
3. `obj.read_typetree()` -> mutate the dict -> `obj.save_typetree(tree)`.
4. `env.save(pack="original", out_path=staged_dir)`.

Copy the shape from the built-in mods in
[content-mods/](../content-mods/) (`bag-capacity`, `sakura-petals-everywhere`,
`faster-bag-tying`, `more-leaves`): they already handle staging, backups, verification
and rollback through `safe-base/base.psm1`.

## 3. Reuse the safe-base SDK

Every `apply.ps1` in our built-ins starts with:

```powershell
Import-Module (Join-Path $PSScriptRoot "..\..\safe-base\base.psm1") -Force
Backup-Original -BundlePath $bundlePath
Assert-SafePreconditions -GamePath $gamePath -ConfigPath $configPath
```

The launcher ships safe-base in `tools/` (published) or `safe-base/` (dev). Users install
it once before any mod. Your mod does **not** need to bundle its own copy of `base.psm1`;
the launcher locates it and passes it via `-Python` / `-SafeBaseDir` arguments (see
`sakura-petals-everywhere/apply.ps1` for the canonical argument contract).

## 4. Manifest

`mod.json` needs `id`, `name`, `version`, `description`, `configPath`, `config.fields`,
`apply.script`, `revert.script`, `requires` at minimum. Add
`sourceRepo: "your_github/your_repo"` so users see provenance on the Details pane. Set
`gameVersion: "1.1.7"` to warn if a future patch changes the bundle layout. The spec
document lists every field.

## 5. Test locally

Install the launcher, add your repo (you can use `--local` mode: point it at a
`file://` path or a folder on disk while iterating is not supported yet - push to
GitHub from a private repo and add that URL). Then:

1. Settings -> Mod sources -> allow non-default -> Add your URL -> Refresh all.
2. Mods page -> Browse online -> select your mod -> Install.
3. Mods page -> Installed -> toggle Enable, set your config values, Apply to game.
4. Launch the game and confirm.
5. Revert, delete `remote-mods/<your-key>/<your-mod>/` by hand, Apply again — the
   self-heal should re-download exactly what you deleted.

## 6. Version discipline

Use semver. When you change a config field's key, that is a breaking change for
anyone with a saved config — either bump the major version and ship a migration in
`apply.ps1` (read the old key, write the new one, delete the old one) or add a new
field and keep the old one marked deprecated in the manifest.

Bumping the version string in `mod.json` is what makes the Mods page show
"pending - config changed" correctly across updates.

## 7. Ask for a featured listing

Optional. If you want us to point at your repo in the launcher's README, open a PR
against `docs/PUBLISH_YOUR_MODS.md` adding a link + one-line description. We will not
curate or audit your code — the trust prompt is what protects users.

---

## What not to do

- **Do not bundle game files.** Your mod reads the user's own install; shipping
  `.bundle` files breaks the licence and the launcher's own rollback logic.
- **Do not extract decompiled code into your mod folder.** It is fine to reference
  internal names in comments, but shipping a `.dll` or `.cs` from a decompiler
  is not.
- **Do not require the user to run an .exe** — every file that runs should be a
  `.ps1` or a `.py`. Anything more (a `.dll`, a native binary) triggers the trust
  modal and looks like malware.
- **Do not touch `%APPDATA%\Steam\`, browser profiles, or anything outside**
  `%LOCALAPPDATA%\LeafItAloneSafe\` + the game's own install directory.

## Support

Ask in the launcher repo's Issues tab. We can help with UnityPy typetree recipes and
safe-base integration; the actual game-value tuning is on your side.
