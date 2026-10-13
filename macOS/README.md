# Magpie for Mac

Coming in the next PR (queue #74, plan PX1 part B): an Avalonia app in `macOS/Magpie.Mac/` that reuses
`common/src/Magpie.Core`, for Apple Silicon (arm64), packaged as `Magpie_<version>.dmg`.

## How CI picks it up

The **macos** job in `.github/workflows/build.yml` runs only when both exist:

| File | Job |
|---|---|
| `macOS/Magpie.Mac/*.csproj` | the app |
| `macOS/build/build.sh <version>` | builds it and writes `macOS/out/Magpie_<version>.dmg` |

The package job puts that file into `macOS/` inside `Magpie_<version>.zip` and on the release.
