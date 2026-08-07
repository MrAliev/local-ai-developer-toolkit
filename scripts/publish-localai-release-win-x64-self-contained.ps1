param(
    [Parameter(Mandatory = $false)]
    [string] $Configuration = "Release",

    [Parameter(Mandatory = $false)]
    [string] $Runtime = "win-x64",

    [Parameter(Mandatory = $false)]
    [string] $PublishRoot = "publish",

    [Parameter(Mandatory = $false)]
    [string] $ReleaseVersion = "0.1.27",

    [Parameter(Mandatory = $false)]
    [string] $VersionDirectory = "",

    [Parameter(Mandatory = $false)]
    [string] $PackageUri = "https://github.com/MrAliev/local-ai-developer-toolkit/releases/download/0.1.27/localai-package.zip",

    [Parameter(Mandatory = $false)]
    [switch] $SignManifest
)

$ErrorActionPreference = "Stop"
$RetryDelaySeconds = 2
$MaxAttempts = 3

$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $Root

if ([string]::IsNullOrWhiteSpace($VersionDirectory)) {
    $VersionDirectory = (git rev-parse --short=12 HEAD).Trim()
    if (-not [string]::IsNullOrWhiteSpace((git status --porcelain))) {
        $VersionDirectory += "-dirty"
    }
}

dotnet restore LocalAi.slnx -r $Runtime

$projects = @(
    "src/CodeSearch.Cli/CodeSearch.Cli.csproj",
    "src/CodeSearch.Mcp/CodeSearch.Mcp.csproj",
    "src/LocalLm.Mcp/LocalLm.Mcp.csproj",
    "src/LocalAi.Cli/LocalAi.Cli.csproj",
    "src/LocalAi.Launcher/LocalAi.Launcher.csproj",
    # The broker belongs in every version directory: a version without it cannot serve
    # model requests. It ships self-contained so an installed machine never needs a
    # system-wide .NET runtime to start it.
    "src/LocalAi.Broker/LocalAi.Broker.csproj",
    "src/LocalAi.Installer/LocalAi.Installer.csproj"
)

if (Test-Path $PublishRoot) {
    Remove-Item -Recurse -Force $PublishRoot
}
New-Item -ItemType Directory -Force -Path $PublishRoot | Out-Null
$ResolvedPublishRoot = [System.IO.Path]::GetFullPath($PublishRoot).TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar)

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

    $resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
    $publishPrefix = $ResolvedPublishRoot + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedOutput.StartsWith($publishPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Project publish output is outside the release root: $resolvedOutput"
    }

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        if (Test-Path -LiteralPath $resolvedOutput) {
            Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
        }
        New-Item -ItemType Directory -Force -Path $resolvedOutput | Out-Null

        Write-Host "Publishing $ProjectName (attempt $attempt/$MaxAttempts)"
        dotnet publish $ProjectPath -c $Configuration -r $Runtime `
            -p:SelfContained=true -p:PublishSingleFile=true -p:BuildInParallel=false -p:UseSharedCompilation=false `
            --no-restore -o $resolvedOutput

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

$artifacts = Join-Path $PublishRoot "artifacts"
$release = Join-Path $PublishRoot "release"
New-Item -ItemType Directory -Force -Path $artifacts, $release | Out-Null
$artifactMap = [ordered]@{
    "localai.exe"          = "LocalAi.Cli/localai.exe"
    "codesearch.exe"       = "CodeSearch.Cli/codesearch.exe"
    "codesearch-mcp.exe"   = "CodeSearch.Mcp/codesearch-mcp.exe"
    "locallm-mcp.exe"      = "LocalLm.Mcp/locallm-mcp.exe"
    "LocalAi.Broker.exe"   = "LocalAi.Broker/LocalAi.Broker.exe"
    "localai-launcher.exe" = "LocalAi.Launcher/localai-launcher.exe"
}
foreach ($entry in $artifactMap.GetEnumerator()) {
    $source = Join-Path $PublishRoot $entry.Value
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Required release artifact is missing: $source"
    }

    Copy-Item -LiteralPath $source -Destination (Join-Path $artifacts $entry.Key)
}

$package = Join-Path $release "localai-package.zip"
dotnet run --project src/LocalAi.ReleaseSigner/LocalAi.ReleaseSigner.csproj `
    -c $Configuration --no-restore -- pack `
    --input $artifacts --release-version $ReleaseVersion `
    --version-directory $VersionDirectory --out $package
if ($LASTEXITCODE -ne 0) {
    throw "Release package creation failed with code $LASTEXITCODE."
}

if ($SignManifest) {
    dotnet run --project src/LocalAi.ReleaseSigner/LocalAi.ReleaseSigner.csproj `
        -c $Configuration --no-restore -- sign `
        --package $package --package-uri $PackageUri `
        --release-version $ReleaseVersion --version-directory $VersionDirectory --out $release
    if ($LASTEXITCODE -ne 0) {
        throw "Release manifest signing failed with code $LASTEXITCODE."
    }

    dotnet run --project src/LocalAi.ReleaseSigner/LocalAi.ReleaseSigner.csproj `
        -c $Configuration --no-restore -- verify-package `
        --package $package `
        --manifest (Join-Path $release "release-manifest.json") `
        --signature (Join-Path $release "release-manifest.sig")
    if ($LASTEXITCODE -ne 0) {
        throw "Release package verification failed with code $LASTEXITCODE."
    }
}

Write-Host "DONE release=$ReleaseVersion directory=$VersionDirectory package=$package"
