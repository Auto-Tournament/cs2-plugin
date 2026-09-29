# Contributing to Auto Tournament CS2

Thank you for your interest in contributing! 🎉

## 🚀 Quick Start

1. **Fork & Clone**

   ```bash
   git clone https://github.com/YOUR_USERNAME/cs2-plugin.git
   cd cs2-plugin
   ```

2. **Build and test** (needs the .NET 8 SDK)

   ```bash
   dotnet build -c Release
   dotnet test tests/AutoTournamentCS2.Tests -c Release
   ```

   The plugin is built to `build/Release/net8.0/AutoTournamentCS2.dll`. To try it on a server,
   copy the build output to `addons/counterstrikesharp/plugins/AutoTournamentCS2/` and the files
   in `cfg/AutoTournamentCS2/` to `csgo/cfg/AutoTournamentCS2/`.

## 📝 Guidelines

- ✅ Write clear commit messages
- ✅ Test your changes
- ✅ Update documentation if needed
- ✅ Follow existing code style
- ✅ Keep PRs focused on one feature/fix

## Releases

Release notes go in the GitHub release; the [docs changelog](https://docs.autotournament.gg/reference/changelog/cs2-plugin) is generated from them, and [CHANGELOG.md](../CHANGELOG.md) only links there.

## 🐛 Reporting Issues

Found a bug? Please [open an issue](https://github.com/Auto-Tournament/cs2-plugin/issues/new) with:

- Clear description
- Steps to reproduce
- Expected vs actual behavior
- Environment details (plugin version, CounterStrikeSharp version, OS)

## 🙏 Community Requests

Need help testing something or getting feedback? Use the **Community Request** issue template! This is perfect for:
- Features that require multiple players to test
- Cross-platform compatibility testing
- Getting user experience feedback
- Performance testing with real-world scenarios

**Contributors who help with Community Requests will be recognized and credited!** 🏆

## 💬 Questions?

- [GitHub Discussions](https://github.com/Auto-Tournament/cs2-plugin/discussions) - Ask questions
- [Documentation](https://docs.sivert.io/docs/me) - Read the docs

## 📖 Code of Conduct

Be respectful and constructive. We're all here to build something awesome for the CS2 community! 🎮

---

**Full Contribution Guide:** https://docs.sivert.io/docs/me
