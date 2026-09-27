# Builds Magpie.exe on Windows (needs .NET 8 SDK + Python 3). XAML static check is blocking.
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$env:DOTNET_CLI_TELEMETRY_OPTOUT = 1; $env:DOTNET_NOLOGO = 1
dotnet test tests/Magpie.Core.Tests/Magpie.Core.Tests.csproj -c Release --filter "Category!=Integration"; if ($LASTEXITCODE) { throw 'tests failed' }
dotnet build src/Magpie.App/Magpie.App.csproj -c Release; if ($LASTEXITCODE) { throw 'build failed' }
dotnet run --project build/DpDump -c Release -- build/app-types.json src/Magpie.App/bin/Release/net8.0-windows/win-x64/Magpie.dll | Out-Null
python build/xaml_check.py --dps build/app-types.json; if ($LASTEXITCODE) { throw 'XAML check failed' }
if (Test-Path publish) { Remove-Item publish -Recurse -Force }
dotnet publish src/Magpie.App/Magpie.App.csproj -c Release -o publish; if ($LASTEXITCODE) { throw 'publish failed' }
(Get-FileHash publish/Magpie.exe -Algorithm SHA256).Hash | Tee-Object publish/Magpie.exe.sha256
