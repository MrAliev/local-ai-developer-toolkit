param(
    [Parameter(Mandatory = $false)]
    [string] $Configuration = "Release",

    [Parameter(Mandatory = $false)]
    [string] $Runtime = "win-x64",

    [Parameter(Mandatory = $false)]
    [string] $PublishRoot = "publish"
)

$ErrorActionPreference = "Stop"
$RetryDelaySeconds = 2
$MaxAttempts = 3

$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $Root

dotnet restore LocalAi.slnx -r $Runtime

$projects = @(
    "src/CodeSearch.Cli/CodeSearch.Cli.csproj",
    "src/CodeSearch.Mcp/CodeSearch.Mcp.csproj",
    "src/LocalLm.Mcp/LocalLm.Mcp.csproj",
    "src/LocalAi.Cli/LocalAi.Cli.csproj",
    "src/LocalAi.Launcher/LocalAi.Launcher.csproj",
    "src/LocalAi.Installer/LocalAi.Installer.csproj"
)

if (Test-Path $PublishRoot) {
    Remove-Item -Recurse -Force $PublishRoot
}
New-Item -ItemType Directory -Force -Path $PublishRoot | Out-Null

function Invoke-SafePublish {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ProjectName,

        [Parameter(Mandatory = $true)]
        [string] $ProjectPath,

        [Parameter(Mandatory = $true)]
        [string] $OutputPath,

        [int] $MaxAttempts = 3,

        [int] $RetryDelaySeconds = 2
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        Write-Host "Publishing $ProjectName (attempt $attempt/$MaxAttempts)"
        dotnet publish $ProjectPath -c $Configuration -r $Runtime `
            -p:SelfContained=true -p:PublishSingleFile=true -p:BuildInParallel=false -p:UseSharedCompilation=false `
            --no-restore -o $OutputPath

        if ($LASTEXITCODE -eq 0) {
            return
        }

        if ($attempt -lt $MaxAttempts) {
            Write-Warning "Публикация $ProjectName завершилась ошибкой. Повторная попытка через $RetryDelaySeconds сек..."
            Start-Sleep -Seconds $RetryDelaySeconds
            continue
        }

        throw "Публикация $ProjectName завершилась ошибкой после $MaxAttempts попыток (код ошибки $LASTEXITCODE)."
    }
}

foreach ($project in $projects) {
    $name = [System.IO.Path]::GetFileNameWithoutExtension($project)
    $out = Join-Path $PublishRoot $name
    Invoke-SafePublish -ProjectName $name -ProjectPath $project -OutputPath $out -MaxAttempts $MaxAttempts -RetryDelaySeconds $RetryDelaySeconds
}

Write-Host "DONE"
