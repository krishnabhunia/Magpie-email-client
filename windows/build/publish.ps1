# Builds Magpie.exe on Windows (needs .NET 8 SDK + Python 3). XAML static check is blocking.
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..\..')
$env:DOTNET_CLI_TELEMETRY_OPTOUT = 1; $env:DOTNET_NOLOGO = 1
dotnet test common/tests/Magpie.Core.Tests/Magpie.Core.Tests.csproj -c Release --filter "Category!=Integration"; if ($LASTEXITCODE) { throw 'tests failed' }
dotnet build windows/Magpie.App/Magpie.App.csproj -c Release; if ($LASTEXITCODE) { throw 'build failed' }
dotnet run --project windows/build/DpDump -c Release -- windows/build/app-types.json windows/Magpie.App/bin/Release/net8.0-windows10.0.19041.0/win-x64/Magpie.dll | Out-Null
python windows/build/xaml_check.py --dps windows/build/app-types.json; if ($LASTEXITCODE) { throw 'XAML check failed' }
dotnet run --project windows/build/DpDump -c Release -- --check-refs windows/Magpie.App/bin/Release/net8.0-windows10.0.19041.0/win-x64; if ($LASTEXITCODE) { throw 'missing assembly in the build' }
if (Test-Path publish) { Remove-Item publish -Recurse -Force }
dotnet publish windows/Magpie.App/Magpie.App.csproj -c Release -o publish; if ($LASTEXITCODE) { throw 'publish failed' }
(Get-FileHash publish/Magpie.exe -Algorithm SHA256).Hash | Tee-Object publish/Magpie.exe.sha256
