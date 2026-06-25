param(
    [switch]$Single
)

$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "..\steelseries-sonar-tray\steelseries-sonar-tray.csproj"
$profile = if ($Single) { "SingleFile" } else { "Folder" }

dotnet publish $project -p:PublishProfile=$profile
