# Magpie for Android

There is no Android app yet, and none is planned. This folder exists because every repo has the
`docs/`, `windows/`, `macOS/` and `android/` folders (Krishna's GitHub rule).

## How CI would pick it up

The independent **android** workflow in `.github/workflows/build-android.yml` runs only Android steps;
while `android/build.sh` is absent, its build and upload steps skip. Once an app exists, the workflow calls
`android/build.sh <version>` and expects `android/out/Magpie_<version>.apk`. The separate release assembler
can include it in `Android/` inside `Magpie_<version>.zip` when it also has matching Windows build artifacts.
An Android-only change never starts a Windows or Mac job.
