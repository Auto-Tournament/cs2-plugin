# Reference

Queued match loads, the bootstrap config, secrets in logs, and several servers sharing one database.

## Queued match loads

`at_loadmatch_url` (or `at match load`) sent while the current series is in postgame
doesn't load right away. The match is queued and loads after the series resets. The reply ends with
`queued_match=<id>`, where `<id>` is the config file name without its extension (for
`/api/matches/r2m1.json` that is `r2m1`). The `at_tournament_next_match` convar holds the
same id. Sending another URL while one is queued replaces it.

Only the automatic reset after a series ends loads the queued match. It is dropped when:

- `css_restart` or `css_endmatch` resets the server. The reply includes `cleared_queued_match=<id>`.
- `at_clear_queued_match` is run (server console or RCON only). The reply is
  `cleared_queued_match=<id>`, or `cleared_queued_match=none` if nothing was queued.

## Bootstrap config

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

## Logs don't contain secrets

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

## Several servers sharing one database

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

