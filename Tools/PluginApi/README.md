# TraceSoul2.PluginApi

Stable contracts for independently developed TraceSoul2 runtime plugins.

Plugin projects should depend on this package only. Do not reference the Host project or copy role data into a plugin source repository. At runtime, the Host supplies the shared API assembly to each collectible plugin load context.

See the main repository's `docs/PLUGINS.md` for package layout, manifest fields and lifecycle rules.
