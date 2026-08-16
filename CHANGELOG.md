# Changelog

DotnetCronner ships as several packages that are **versioned and released independently** — a fix to one
package does not require releasing the others. Each package keeps its own changelog:

| Package | Changelog |
| --- | --- |
| `DotnetCronner` | [src/DotnetCronner/CHANGELOG.md](src/DotnetCronner/CHANGELOG.md) |
| `DotnetCronner.Abstractions` | [src/DotnetCronner.Abstractions/CHANGELOG.md](src/DotnetCronner.Abstractions/CHANGELOG.md) |
| `DotnetCronner.Stores.Redis` | [src/DotnetCronner.Stores.Redis/CHANGELOG.md](src/DotnetCronner.Stores.Redis/CHANGELOG.md) |
| `DotnetCronner.Stores.EntityFrameworkCore` | [src/DotnetCronner.Stores.EntityFrameworkCore/CHANGELOG.md](src/DotnetCronner.Stores.EntityFrameworkCore/CHANGELOG.md) |

Each package's changelog follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). See
[CONTRIBUTING.md](CONTRIBUTING.md) for how a release is cut (tag `<key>/vX.Y.Z`).
