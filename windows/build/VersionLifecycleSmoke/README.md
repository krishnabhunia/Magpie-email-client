# Windows version lifecycle smoke

Run on Windows with the .NET 8 SDK. This harness needs two self-contained, single-file builds of itself. The
`LifecycleFixture` assembly metadata makes their executable hashes different without changing the code under test.

```powershell
dotnet publish windows/build/VersionLifecycleSmoke/VersionLifecycleSmoke.csproj -c Release -o version-lifecycle-smoke/original -p:LifecycleFixture=original
dotnet publish windows/build/VersionLifecycleSmoke/VersionLifecycleSmoke.csproj -c Release -o version-lifecycle-smoke/selected -p:LifecycleFixture=selected
$selected = (Resolve-Path version-lifecycle-smoke/selected/VersionLifecycleSmoke.exe).Path
& .\version-lifecycle-smoke\original\VersionLifecycleSmoke.exe --selected-exe $selected
```

The workflow runs these commands as a blocking Windows check. A successful run prints
`VERSION_LIFECYCLE_SMOKE_PASSED`; any assertion or child-process timeout returns a nonzero exit code.

The cases exercise the production `WindowsStartup`, `WindowsVersionRecovery`, `SelfUpdate`, `VersionBackup`,
`VersionPin` and history APIs:

- A replacement acknowledges startup while its parent owns the mutex, then times out without creating a profile.
- A conflicting Windows kernel object prevents mutex creation, acknowledgement and profile access.
- A running fixture renames its own executable and launches a different selected payload. That child waits for
  ownership, changes mail, a local draft, settings, encrypted secrets, MIME files and the schema, then fails startup.
  Recovery replaces the running failed executable from its validated snapshot, even while the previous executable
  is locked. The saved child waits for ownership before restoring the paired data. It verifies original hashes,
  mail and draft contents, DPAPI decryption, exact pin, all three legacy automatic-update flags, and final history.

Every case uses a unique temporary directory and unique named Windows objects. HTTP is rejected, mail sync is never
started, and no default Magpie profile is opened. Failed or timed-out children are terminated before fixture cleanup.

The recovery prompt is treated as accepted by the fixture; this does not automate a real user's consent. These are
native process and filesystem checks, not a full WPF application startup test or an all-users installer test. The
separate `ProductivitySmoke` project covers the actual Windows views and exact-version chooser.
