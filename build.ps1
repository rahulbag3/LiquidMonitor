$ErrorActionPreference = 'Stop'
dotnet restore .\LiquidMonitor.sln
dotnet build .\LiquidMonitor.sln -c Debug -p:Platform=x64
