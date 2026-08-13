param(
    [Parameter(Mandatory = $false)]
    [string] $Configuration = "Release",

    [Parameter(Mandatory = $false)]
    [string] $Runtime = "win-x64",

    [Parameter(Mandatory = $false)]
    [string] $PublishRoot = "publish",

    # Mandatory, and with no default on purpose. The default used to be a real past version
    # together with a PackageUri pointing at that version's asset, so forgetting the argument
    # produced a package that stamped itself 0.1.27 and pointed installers at 0.1.27's download.
    # A forgotten argument now stops the script instead of shipping a plausible lie.
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string] $ReleaseVersion,

    [Parameter(Mandatory = $false)]
    [string] $VersionDirectory = "",

    # Derived from the version unless overridden. These two could never disagree without the
    # manifest telling installers to fetch a different release than the one being signed.
    [Parameter(Mandatory = $false)]
    [string] $PackageUri = "",

    [Parameter(Mandatory = $false)]
    [switch] $SignManifest
)

$ErrorActionPreference = "Stop"
$RetryDelaySeconds = 2
$MaxAttempts = 3

$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $Root
# Set-Location moves the PowerShell location and nothing else. .NET keeps its own current
# directory — where the process was started — and this script resolves paths both ways:
# Test-Path and the relative artifact paths follow the PowerShell location, while
# [System.IO.Path]::GetFullPath and dotnet publish follow the process one. Started from anywhere
# but the repository root, a worktree for instance, the two disagree: every project publishes
# into the other tree and the run then fails at "Required release artifact is missing" having
# just reported seven successful publishes.
[System.Environment]::CurrentDirectory = $Root

if ([string]::IsNullOrWhiteSpace($PackageUri)) {
    $PackageUri = "https://github.com/MrAliev/local-ai-developer-toolkit/releases/download/" +
        "$ReleaseVersion/localai-package.zip"
}

Write-Host "Release version: $ReleaseVersion"
Write-Host "Package URI:     $PackageUri"

if ([string]::IsNullOrWhiteSpace($VersionDirectory)) {
    $VersionDirectory = (git rev-parse --short=12 HEAD).Trim()
    if (-not [string]::IsNullOrWhiteSpace((git status --porcelain))) {
        $VersionDirectory += "-dirty"
    }
}

dotnet restore LocalAi.slnx -r $Runtime

# The signer is built once, here, and every later invocation of it runs with --no-build.
#
# This script packs and signs by re-entering LocalAi.ReleaseSigner, and since `localai-release-signer
# release --publish` is what now drives this script, that re-entry can happen while an instance of
# that very assembly is running. A rebuild then tries to overwrite the binary of the process that
# started it, and MSBuild fails with a locked-file error whose message never reaches the caller.
# Building up front and running the built output afterwards removes the overlap entirely, and keeps
# the script usable on its own, where nothing has built the signer yet.
$SignerProject = "src/LocalAi.ReleaseSigner/LocalAi.ReleaseSigner.csproj"
dotnet build $SignerProject -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "Building the release signer failed with code $LASTEXITCODE."
}

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
        # The version is stamped from what this release is actually called, not from the
        # fallback in Directory.Build.props. That fallback went six releases without being
        # updated, so 0.1.29 shipped binaries reporting 0.1.22 — a number that has to be kept in
        # step by hand is a number that goes stale unnoticed.
        # Captured rather than streamed, so a failure can say what went wrong. Only the exit
        # code used to be kept: when publishing went to the wrong tree, this reported seven
        # successes and then failed on the missing artifacts with nothing to explain either.
        $output = dotnet publish $ProjectPath -c $Configuration -r $Runtime `
            -p:SelfContained=true -p:PublishSingleFile=true -p:BuildInParallel=false -p:UseSharedCompilation=false `
            -p:Version=$ReleaseVersion -p:VersionPrefix=$ReleaseVersion `
            -p:AssemblyVersion="$ReleaseVersion.0" -p:FileVersion="$ReleaseVersion.0" `
            -p:InformationalVersion=$ReleaseVersion `
            --no-restore -o $resolvedOutput 2>&1
        $publishExitCode = $LASTEXITCODE
        $output | ForEach-Object { Write-Host "  $_" }

        if ($publishExitCode -eq 0) {
            return
        }

        # The last lines are where MSBuild puts the reason; the whole log is already above.
        $tail = ($output | Select-Object -Last 12) -join [System.Environment]::NewLine

        if ($attempt -lt $MaxAttempts) {
            Write-Warning "Публикация $ProjectName завершилась ошибкой (код $publishExitCode). Повторная попытка через $RetryDelaySeconds сек...`n$tail"
            Start-Sleep -Seconds $RetryDelaySeconds
            continue
        }

        throw "Публикация $ProjectName завершилась ошибкой после $MaxAttempts попыток (код ошибки $publishExitCode).`n$tail"
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
dotnet run --project $SignerProject `
    -c $Configuration --no-restore --no-build -- pack `
    --input $artifacts --release-version $ReleaseVersion `
    --version-directory $VersionDirectory --out $package
if ($LASTEXITCODE -ne 0) {
    throw "Release package creation failed with code $LASTEXITCODE."
}

if ($SignManifest) {
    dotnet run --project $SignerProject `
        -c $Configuration --no-restore --no-build -- sign `
        --package $package --package-uri $PackageUri `
        --release-version $ReleaseVersion --version-directory $VersionDirectory --out $release
    if ($LASTEXITCODE -ne 0) {
        throw "Release manifest signing failed with code $LASTEXITCODE."
    }

    dotnet run --project $SignerProject `
        -c $Configuration --no-restore --no-build -- verify-package `
        --package $package `
        --manifest (Join-Path $release "release-manifest.json") `
        --signature (Join-Path $release "release-manifest.sig")
    if ($LASTEXITCODE -ne 0) {
        throw "Release package verification failed with code $LASTEXITCODE."
    }
}

Write-Host "DONE release=$ReleaseVersion directory=$VersionDirectory package=$package"
