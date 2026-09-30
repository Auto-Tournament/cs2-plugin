<div align="center">

  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/logo/me-wordmark-dark.svg">
    <source media="(prefers-color-scheme: light)" srcset="assets/logo/me-wordmark-light.svg">
    <img src="assets/logo/me-wordmark-light.svg" alt="MatchZy Enhanced" height="56">
  </picture>

# MatchZy Enhanced

[![Build](https://github.com/Auto-Tournament/matchzy-enhanced/actions/workflows/build.yml/badge.svg)](https://github.com/Auto-Tournament/matchzy-enhanced/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/Auto-Tournament/matchzy-enhanced)](https://github.com/Auto-Tournament/matchzy-enhanced/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

</div>

<div align="center">

### Sponsor Auto Tournament

Running tournaments or LANs with Auto Tournament? Your organisation can keep it growing.
Auto Tournament is built and maintained by one person — sponsorships pay for development, test servers and infrastructure.

[![Sponsor on GitHub](https://img.shields.io/badge/Sponsor-GitHub-ea4aaa?logo=githubsponsors&logoColor=white)](https://github.com/sponsors/sivert-io)
[![Support on Ko-fi](https://img.shields.io/badge/Support-Ko--fi-ff5e5b?logo=kofi&logoColor=white)](https://ko-fi.com/sivert)
[![Become a sponsor](https://img.shields.io/badge/Become%20a%20sponsor-Discord-5865F2?logo=discord&logoColor=white)](https://discord.gg/n7gHYau7aW)

</div>

MatchZy Enhanced is the CS2 server plugin for [Auto Tournament](https://github.com/Auto-Tournament/auto-tournament).

> **New setups: use [Ready Up](https://github.com/Auto-Tournament/ready-up).** Ready Up is the native match plugin that Auto Tournament 3.0 talks to over its fleet link. MatchZy Enhanced stays supported for servers that already run it and for Auto Tournament 2.x.
Auto Tournament runs tournaments across a pool of CS2 servers and needs to set up, control and track
matches from outside the game.

It adds match events, a match report API, retries for events that fail to send, per-server config
in a shared database, and player-facing features such as auto-ready and pause limits. The full list
is below.

Documentation is at [docs.sivert.io/docs/me](https://docs.sivert.io/docs/me).

## Installing

The easiest way is [CS2 Server Manager](https://github.com/Auto-Tournament/cs2-server-manager), which
sets up servers with the plugin already installed and configured.

To install by hand:

1. Download the [latest release](https://github.com/Auto-Tournament/matchzy-enhanced/releases).
2. Extract it into your server's `game/csgo/` directory.
3. Restart the server.

The plugin lives in `addons/counterstrikesharp/plugins/AutoTournamentCS2/` and its config in
`cfg/AutoTournamentCS2/`. Console variables and commands start with `at_`, and chat messages are
tagged `[Auto Tournament]`.

### Upgrading from 1.x

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

## What it adds

For tournament automation:

- More events, so an external tool can follow a match in real time.
- A match report API that returns the match state as structured JSON.
- A pull API for reading match stats directly.
- Thread-safe operations, so automation calls don't trip over each other.
- An event retry queue: events that fail to send are queued and sent again.
- Server tracking, with health monitoring and status events.
- A simulation mode for testing and demos.

For players:

- Auto-ready, so a match can start without everyone typing `.ready` (optional).
- Pause limits per team, timeouts, and unpausing that needs both teams.
- A timer on the side choice after the knife round. If it runs out, the side is picked automatically.
- `.gg`: a team can vote to forfeit.
- Forfeit (FFW) handling when a whole team disconnects.
- A shorter 10 second restart delay when demos are disabled.
- Important events shown in the center of the screen, with countdowns.

### Queued match loads

`at_loadmatch_url` (or `at match load`) sent while the current series is in postgame
doesn't load right away. The match is queued and loads after the series resets. The reply ends with
`queued_match=<id>`, where `<id>` is the config file name without its extension (for
`/api/matches/r2m1.json` that is `r2m1`). The `at_tournament_next_match` convar holds the
same id. Sending another URL while one is queued replaces it.

Only the automatic reset after a series ends loads the queued match. It is dropped when:

- `css_restart` or `css_endmatch` resets the server. The reply includes `cleared_queued_match=<id>`.
- `at_clear_queued_match` is run (server console or RCON only). The reply is
  `cleared_queued_match=<id>`, or `cleared_queued_match=none` if nothing was queued.

### Bootstrap config

A controller such as Auto Tournament points a server at its bootstrap endpoint with two server console or RCON
commands:

```
at_bootstrap_token "<token>"
at_bootstrap_url "http://<controller>/api/servers/<server_id>/bootstrap"
```

The plugin fetches that URL, sending the token as `X-Auto-Tournament-Token`, and runs the commands in the
payload. The fetch happens about 1.5 seconds after the last change to either value. Every change
restarts the timer, and the fetch uses whatever URL and token are set when it fires, so the two
commands can come in either order and still cause one fetch. On startup the saved URL and token are
fetched immediately.

If the payload sets a `at_server_id` that differs from the id in the bootstrap URL, or from
the id the server already had, the plugin logs a `[Bootstrap] WARNING` and applies the payload
anyway. This usually means the bootstrap URL is stale.

### Logs don't contain secrets

Server logs, console output and chat never show secret values. The bootstrap, match and report
tokens, the remote log, demo upload and backup header values, `sv_password`, `rcon_password`, and
any other setting with `token`, `password`, `secret` or `header_value` in its name are logged as
`(hidden, N chars)`. The same applies to those values inside logged payloads, match configs, HTTP
responses, request headers and URL query strings (`?token=`). You can share logs when asking for
help.

Older versions printed the token when saving it, for example
`[SaveConfigValue] Saved config for server '...': at_bootstrap_token = <token>`. If you
shared logs from an older version, rotate the Auto Tournament `SERVER_TOKEN` and push the new token to your
servers.

### Several servers sharing one database

Several servers can use the same MySQL database. Match, map and player stats in the
`at_stats_*` tables are shared between them, which is the reason to do this in the first
place.

Persistent config is stored per server. The `at_server_config` table and the event retry
queue are keyed by the identity of the server that wrote them, so one server can't overwrite
another's values. Before this change the last server to write won, and after a restart every
server on the box loaded that server's `at_server_id`, bootstrap URL and remote log settings.

These settings are stored per server:

- `at_server_id`
- `at_bootstrap_url`, `at_bootstrap_token`
- `at_remote_log_url`, `at_remote_log_header_key`, `at_remote_log_header_value`
- `at_webhook_url`, `at_heartbeat_url`
- `at_report_endpoint`, `at_report_token`, `at_match_token`
- `at_demo_upload_url`
- `at_admins_url`, `at_admins_refresh_seconds`
- `at_chat_prefix`, `at_admin_chat_prefix`
- all `at_warmup_*` settings

The chat prefixes and warmup settings are usually the same on every server, but they're scoped
like the rest. The old single shared row for them came from how storage used to work, not from a
design choice, and "last writer wins" is a poor way to share a value. Set them per server, or keep
the existing shared value (see backwards compatibility below).

**How a server identifies itself.** The identity is the bind address plus the game port, for
example `cs2:27015`, `cs2:27025` and `cs2:27035` for three servers on a box named `cs2`. The bind
address is used when it names a real interface. CS2 servers are nearly always started with
`-ip 0.0.0.0`, which doesn't identify anything, so the machine name is used instead. You don't
need to configure anything for this, and it works before a controller like Auto Tournament has talked to the
server.

Changing the game port or renaming the box changes the identity. No data is lost: the server finds
no row of its own, falls back to the shared pre-upgrade row, and the controller pushes its values
again on the next configure. To keep a fixed name through both, set a scope explicitly:

```
# in the server's start arguments (config.cfg may not have run yet, so this is more reliable)
+at_config_scope tournament-eu-3
```

`at_config_scope` also works in `config.cfg`, but prefer the start argument. It wins when both
are set. The scope is never saved to the database, since it decides which rows are read.

The scope is logged once at startup, for example
`[ConfigScope] Using scope 'cs2-server-2' (from start argument)`. It is resolved in this order: the
`+at_config_scope` start argument, the `at_config_scope` convar, `-port` in the start
arguments, then the `hostport` convar once the server has activated. On Linux, start arguments are
read from `/proc/self/cmdline`. If none of these identify the server, the plugin uses a key derived
from the install path (still different for each server, never one key for the whole box) and logs
a warning. Add `+at_config_scope` if you see it.

**Upgrading from 1.4.26.** 1.4.26 couldn't read the start arguments inside the game process, so it
resolved every server on a box to the same `<host>:27015` scope, and those rows hold whatever the
last server wrote. They stay in the database, but a server that resolves to a different scope won't
read them: reads only fall back to the pre-scoping shared row, never to another scope. The
controller pushes the correct values again on the next configure. Once every server logs its own
scope you can remove the stale rows, for example
`DELETE FROM at_server_config WHERE server_scope = 'cs2:27015';`. Only do this if no server on
that box really resolves to that scope. A server on port 27015 without `+at_config_scope`
does.

**Backwards compatibility.** Rows written before this change are kept and used as shared
fallbacks. A server reads its own row if it has one and the shared row otherwise, and only writes
its own row. One server per database keeps working without any changes, and a multi-server setup
behaves as before until each server has written its own values. The schema migration runs on
startup and does nothing once applied.

If you moved servers to separate SQLite files to work around this, you can move them back to the
shared MySQL database.

## Documentation

- [Configuration](https://docs.sivert.io/docs/me/user/configuration): all convars, with examples
- [Commands](https://docs.sivert.io/docs/me/user/commands): player and admin commands
- [Integration](https://docs.sivert.io/docs/me/advanced/integration): API endpoints and events
- [Changelog](https://docs.sivert.io/docs/me/advanced/changelog)

## Related projects

- [Auto Tournament](https://github.com/Auto-Tournament/auto-tournament): the tournament platform this plugin is built for
- [CS2 Server Manager](https://github.com/Auto-Tournament/cs2-server-manager): sets up and runs multiple CS2 servers

## License

MatchZy Enhanced is MIT licensed, see [LICENSE](LICENSE) — free for any use, including paid work and commercial servers.

[Ready Up](https://github.com/Auto-Tournament/ready-up), the new native CS2 plugin, is a separate project and is not MIT: it's licensed under PolyForm Noncommercial.

## Sponsors

Your logo here — [sponsor Auto Tournament](https://discord.gg/n7gHYau7aW) to be listed.

## Credits

MatchZy Enhanced is forked from [MatchZy](https://github.com/shobhit-pathak/MatchZy) by shobhit-pathak.

It is maintained by [sivert-io](https://github.com/sivert-io) and built on
[CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp/). The upstream copyright
and licence notice is kept in [LICENSE](LICENSE).
