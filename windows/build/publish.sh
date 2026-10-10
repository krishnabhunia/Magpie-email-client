#!/usr/bin/env bash
# Builds the single-file Windows executable from Linux/macOS/Windows (needs .NET 8 SDK + Python 3).
# The XAML static check is BLOCKING: it catches WPF run-time crashes we can't see on Linux.
set -euo pipefail
cd "$(dirname "$0")/../.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet test common/tests/Magpie.Core.Tests/Magpie.Core.Tests.csproj -c Release --filter "Category!=Integration"
dotnet build windows/Magpie.App/Magpie.App.csproj -c Release
dotnet run --project windows/build/DpDump -c Release -- windows/build/app-types.json windows/Magpie.App/bin/Release/net8.0-windows10.0.19041.0/win-x64/Magpie.dll >/dev/null
python3 windows/build/xaml_check.py --dps windows/build/app-types.json
dotnet run --project windows/build/DpDump -c Release -- --check-refs windows/Magpie.App/bin/Release/net8.0-windows10.0.19041.0/win-x64
rm -rf publish
dotnet publish windows/Magpie.App/Magpie.App.csproj -c Release -o publish
ls -la publish/Magpie.exe
sha256sum publish/Magpie.exe | tee publish/Magpie.exe.sha256
