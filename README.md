<div align="center">
  <h1>MatchZy Enhanced</h1>
  <p><strong>CounterStrikeSharp plugin that lets Auto Tournament run matches on CS2 servers</strong></p>
  <p>
    <a href="https://github.com/Auto-Tournament/matchzy-enhanced/releases/latest"><img src="https://img.shields.io/github/v/release/Auto-Tournament/matchzy-enhanced?cacheSeconds=3600" alt="GitHub Release" /></a>
    <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-blue.svg" alt="License: MIT" /></a>
    <a href="https://docs.autotournament.gg/cs2/plugin"><img src="https://img.shields.io/badge/docs-docs.autotournament.gg-blue" alt="Docs" /></a>
    <a href="https://discord.gg/n7gHYau7aW"><img src="https://img.shields.io/badge/Discord-join-5865F2?logo=discord&logoColor=white" alt="Discord" /></a>
  </p>
</div>

<br />

> [!NOTE]
> **New setups: use [Ready Up](https://github.com/Auto-Tournament/ready-up).** Ready Up is the native match plugin Auto Tournament 3.0 talks to. MatchZy Enhanced stays supported for servers that already run it and for Auto Tournament 2.x.

MatchZy Enhanced is the [MatchZy](https://github.com/shobhit-pathak/MatchZy) fork that [Auto Tournament](https://github.com/Auto-Tournament/auto-tournament) drives over RCON. It adds what a tournament platform needs to set up, control and follow matches from outside the game.

## Features

- More events, so an external tool can follow a match in real time
- Match report API that returns the match state as structured JSON
- Pull API for reading match stats directly
- Thread-safe operations, so automation calls don't trip over each other
- Event retry queue: events that fail to send are queued and sent again
- Server tracking, with health monitoring and status events
- Simulation mode for testing and demos
- Auto-ready, so a match can start without everyone typing `.ready` (optional)
- Pause limits per team, timeouts, and unpausing that needs both teams
- Timer on the side choice after the knife round; when it runs out, the side is picked automatically
- `.gg`: a team can vote to forfeit
- Forfeit (FFW) handling when a whole team disconnects
- Shorter 10 second restart delay when demos are disabled
- Important events shown in the center of the screen, with countdowns

## Install

The easiest way is [CS2 Server Manager](https://github.com/Auto-Tournament/cs2-server-manager), which sets up servers with the plugin installed and configured (its legacy stack). To install by hand, on a server with [CounterStrikeSharp](https://docs.cssharp.dev/):

1. Download the [latest release](https://github.com/Auto-Tournament/matchzy-enhanced/releases/latest).
2. Extract it into the server's `game/csgo/` directory.
3. Restart the server.

The plugin lives in `addons/counterstrikesharp/plugins/AutoTournamentCS2/`, its config in `cfg/AutoTournamentCS2/`. Convars and commands start with `at_`. Coming from 1.x (`matchzy_*` names): read [docs/UPGRADING.md](docs/UPGRADING.md) first.

## Documentation

Full docs at **[docs.autotournament.gg](https://docs.autotournament.gg/cs2/plugin)**. In this repo:

- [Upgrading from 1.x](docs/UPGRADING.md)
- [Reference: queued match loads, bootstrap config, secrets in logs, several servers on one database](docs/REFERENCE.md)

## Contributing

See the [contributing guide](.github/CONTRIBUTING.md). New features go into [Ready Up](https://github.com/Auto-Tournament/ready-up); this plugin gets fixes.

## Sponsors

MatchZy Enhanced is part of Auto Tournament, built by one person. A sponsorship pays for development and test servers: [GitHub Sponsors](https://github.com/sponsors/sivert-io) or [Ko-fi](https://ko-fi.com/sivert).

<!-- sponsors:start -->
<!-- sponsors:end -->

## Acknowledgments

- [MatchZy](https://github.com/shobhit-pathak/MatchZy) by shobhit-pathak: the plugin this is forked from
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp/): the plugin framework it runs on

## License

MatchZy Enhanced is licensed under the [MIT License](LICENSE). Copyright (c) 2023 Shobhit Pathak, with changes by Sivert Gullberg Hansen; the upstream notice is kept in [LICENSE](LICENSE). Free for any use, including paid work and commercial servers.
