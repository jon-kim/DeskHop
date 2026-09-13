param(
	[Parameter(Mandatory = $true)]
	[ValidatePattern('^\d+\.\d+\.\d+$')]
	[string]$Version,

	[string]$Configuration = "Release",
	[string]$Runtime = "win-x64",
	[string]$Owner = "jon-kim",
	[string]$Repository = "ScreenManager",
	[switch]$Draft
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
	throw "dotnet SDK is required."
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
	throw "GitHub CLI (gh) is required. Install from https://cli.github.com/."
}

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $root "DeskHop/DeskHop.csproj"
$tag = "v$Version"
$releaseName = "DeskHop $tag"

$publishDir = Join-Path $root ".artifacts/publish/$tag/$Runtime"
$assetDir = Join-Path $root ".artifacts/assets/$tag"
$zipPath = Join-Path $assetDir "DeskHop-$tag-$Runtime.zip"

if (Test-Path $publishDir) {
	Remove-Item $publishDir -Recurse -Force
}

if (Test-Path $assetDir) {
	Remove-Item $assetDir -Recurse -Force
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $assetDir -Force | Out-Null

Write-Host "Publishing $projectPath..."
dotnet publish $projectPath -c $Configuration -r $Runtime --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:Version=$Version -o $publishDir

Write-Host "Creating release artifact $zipPath..."
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

$repoRef = "$Owner/$Repository"
$releaseExists = $true
try {
	gh release view $tag --repo $repoRef | Out-Null
}
catch {
	$releaseExists = $false
}

if ($releaseExists) {
	Write-Host "Release $tag already exists, uploading asset..."
	gh release upload $tag $zipPath --repo $repoRef --clobber
}
else {
	Write-Host "Creating release $tag..."
	$createArgs = @("release", "create", $tag, $zipPath, "--repo", $repoRef, "--title", $releaseName, "--generate-notes")
	if ($Draft) {
		$createArgs += "--draft"
	}

	gh @createArgs
}

Write-Host "Done. Release artifact: $zipPath"
