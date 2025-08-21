#Name of the project folder and csproj
$appName = "VRChatOSCClient"

#autoprops
$projectPath = "src\$appName\$appName.csproj"

$basePath = Split-Path -Parent $PSCommandPath

Write-Host "Starting dotnet publish"
dotnet publish "$basePath\$projectPath" `
    --configuration Release `
    --runtime win-x64 `
    /p:PublishProfile= `
    /p:PublishSingleFile=false `
    /p:PublishTrimmed=false `
    /p:SelfContained=true `
    -v:normal