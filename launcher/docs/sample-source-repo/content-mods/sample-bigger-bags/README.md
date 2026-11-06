# Sample: Bigger small bags

Reference third-party mod. Raises the small bag's leaf capacity from 4 to a configured
value (default 8).

- Requirements: safe-base installed, game closed, Python 3.12 with UnityPy.
- Config: `%LOCALAPPDATA%\LeafItAloneSafe\sample-bigger-bags.json`
  - `smallCapacity` (int, 4-32, default 8)

## Files

| File                    | Purpose |
|-------------------------|---------|
| `mod.json`              | The manifest the launcher reads (schema + apply/revert command). |
| `apply.ps1`             | Stages the patch and registers it with safe-base; passes `-Yes`. |
| `revert.ps1`            | Hands the pristine bundle back to safe-base. |
| `patch_sample_bags.py`  | UnityPy: mutate one field, save to a staging dir, verify. |
| `sample-bigger-bags.default.json` | Config template copied to `%LOCALAPPDATA%\` on first enable. |

## Notes

- This mod is deliberately tiny: one MonoBehaviour, one field, one bundle. Real
  mods often touch 2-3 bundles (bundle + catalog + a second data bundle) which is
  what the launcher's built-in sakura-petals mod does.
- It never calls out to the network. Any mod you publish that does should say so in
  the README and in the mod's `description` field so the trust modal makes it obvious.
