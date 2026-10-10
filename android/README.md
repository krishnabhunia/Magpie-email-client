# Magpie for Android

There is no Android app yet, and none is planned. This folder exists because every repo has the
`docs/`, `windows/`, `macOS/` and `android/` folders (Krishna's GitHub rule).

## How CI would pick it up

The **android** job in `.github/workflows/build.yml` runs only when `android/build.sh` exists. It is called as
`android/build.sh <version>` and must write `android/out/Magpie_<version>.apk`; the package job then puts it into
`Android/` inside `Magpie_<version>.zip`. Until then the job is skipped ("code not found in the main source can be skipped").
