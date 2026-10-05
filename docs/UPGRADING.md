# Upgrading from 1.x

2.0.0 renames the plugin. Every `matchzy_*` console variable and command is now `at_*` (for example
`matchzy_loadmatch_url` is `at_loadmatch_url`), and the old names are not read: rename them in your
own config files. Auto Tournament 3.0 needs 2.0.0, because the plugin now authenticates with the
`X-Auto-Tournament-Token` header. Update the plugin and the platform together.

Before the first start of 2.0.0, delete `addons/counterstrikesharp/plugins/MatchZy/MatchZy.dll`
so two copies never load. Keep the rest of that folder until 2.0.0 has started once, because the
first start moves your SQLite database out of it. Then remove the folder.

On its first start 2.0.0 carries an existing install over, logging every step as `[CarryOver]`:

- `cfg/MatchZy/` moves to `cfg/AutoTournamentCS2/`. If the new folder already exists (the release
  zip creates it), each file moves on its own unless the new folder already has a file with that
  name. Those stay where they are, and a warning lists them.
- `plugins/MatchZy/matchzy.db` is renamed to `plugins/AutoTournamentCS2/auto_tournament_cs2.db`.
- The `matchzy_*` tables are renamed to `at_*`, on MySQL in a single `RENAME TABLE`.
- Saved settings keyed by `matchzy_*` names are renamed to their `at_*` names.

Nothing is overwritten or deleted. When an old and a new name both exist, both are left alone, a
warning is logged, and the new one is used. The release zip does not include `database.json`, so
extracting it never replaces your database settings; the plugin writes a default one if there is
none.

