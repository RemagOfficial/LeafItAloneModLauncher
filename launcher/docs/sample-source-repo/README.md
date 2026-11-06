# Sample mod source repo

Fork this into your own GitHub repository, replace the sample mod with your own under
`content-mods/`, and add the URL in the Leaf it Alone launcher's Settings -> Mod sources.
Users will then see your mods on the Browse online tab.

Layout, manifest fields and versioning rules: [MOD_SOURCE_SPEC.md](../MOD_SOURCE_SPEC.md).
Step-by-step walkthrough: [PUBLISH_YOUR_MODS.md](../PUBLISH_YOUR_MODS.md).

The one mod in `content-mods/sample-bigger-bags/` is a **working example**. It patches
`bagCapacities.small` from 4 to 8 in the game's actors bundle - small, harmless, and
shows every file the launcher needs (mod.json + apply.ps1 + revert.ps1 + patcher.py +
README.md).

## Delete the sample before publishing

```
rm -rf content-mods/sample-bigger-bags
```

Then drop in your own mod folder. The launcher's Browse tab will list whatever you
push to your repo's default branch.
