# Sets up the WeClapp adapter runtime entity at the lkv tenant (test-2) and prepares
# the pipeline YAMLs with the local API key.
#
# Prerequisites:
#   - octo-cli context 'test-2_lkv' active and authenticated (Register-OctoCliContext)
#   - Environment variable WECLAPP_TRIAL_API_KEY set (user level):
#       setx WECLAPP_TRIAL_API_KEY "<token from WeClapp: Mein Profil → API-Token>"
#     (new shell required after setx)
#
# The key is substituted into a TEMP copy of the pipeline YAML only — nothing
# containing the key is written into the repo.

$ErrorActionPreference = "Stop"

if (-not $env:WECLAPP_TRIAL_API_KEY) {
    throw "WECLAPP_TRIAL_API_KEY is not set. Get the token from WeClapp (Mein Profil → API-Token) and run: setx WECLAPP_TRIAL_API_KEY `"<token>`""
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Definition

# 1. Enable the communication feature + create the adapter runtime entity
octo-cli -c EnableCommunication
octo-cli -c ImportRt -f (Join-Path $scriptRoot "_general/rt-adapter-weclapp.yaml") -w

# 2. Prepare deployable pipeline YAMLs (key substituted) in the local TEMP directory
$pipelineDir = Join-Path $scriptRoot "../pipelines"
$outDir = Join-Path $env:TEMP "weclapp-pipelines"
New-Item -ItemType Directory -Force $outDir | Out-Null
Get-ChildItem $pipelineDir -Filter "*.yaml" | ForEach-Object {
    (Get-Content $_.FullName -Raw).Replace('${WECLAPP_API_KEY}', $env:WECLAPP_TRIAL_API_KEY) |
        Set-Content (Join-Path $outDir $_.Name) -NoNewline
    Write-Host "Prepared $($_.Name) -> $outDir (key substituted, do NOT commit)"
}

Write-Host ""
Write-Host "Next: deploy the prepared pipeline YAMLs from $outDir to the adapter"
Write-Host "'WeClapp Mesh Adapter (LKV)' via the AdminPanel of test-2 (tenant lkv)."
