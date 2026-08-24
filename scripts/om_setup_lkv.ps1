# Sets up the WeClapp adapter runtime entity at the lkv tenant (test-2). The pipeline
# YAMLs in ../pipelines carry no credentials or connection data: WeClapp access comes
# from the tenant GlobalConfiguration entry "WeClappApi" (referenced via
# apiConfiguration), SFTP access from the entry "LkvSftp" (referenced via
# serverConfiguration). Three values remain tenant-specific and are marked REPLACE/TBD
# in the YAMLs - the AI submandant (weclapp-orders-to-ai.yaml), the BE warehouseId
# (dilos-be-to-weclapp.yaml) and the AR/BE SFTP remoteDirectory (per-mandate
# subdirectory TBD with LKV) - review them before deploying to a NEW tenant.
#
# Prerequisites:
#   - octo-cli context 'test-2_lkv' active and authenticated (Register-OctoCliContext)
#   - The deployed adapter image is built against SDK >= r3.4.91: the ar/be YAMLs use
#     continueOnError, which older images reject at pipeline registration. The as/ai
#     encoding/onEncodingError properties on SftpUpload@1 ship since r3.4.89 and are
#     therefore below that floor, not the binding constraint
#   - Environment variables WECLAPP_CUSTOMER_API_KEY and WECLAPP_CUSTOMER_BASEURL set
#     (user level; the same variables gate WeClappCustomerSmokeTests):
#       setx WECLAPP_CUSTOMER_API_KEY "<token from WeClapp: Mein Profil -> API-Token>"
#       setx WECLAPP_CUSTOMER_BASEURL "https://<tenant>.weclapp.com/webapp/api/v1"
#     (new shell required after setx)
#
# Key rotation: update the "WeClappApi" entry at the tenant and redeploy the pipelines
# (the GlobalConfiguration is a deploy-time snapshot per pipeline registration), then
# revoke the old key at WeClapp.

$ErrorActionPreference = "Stop"

# Gate: the setup ends with the tenant GlobalConfiguration steps below, which need these
# values at hand - fail fast if they are missing.
if (-not $env:WECLAPP_CUSTOMER_API_KEY) {
    throw "WECLAPP_CUSTOMER_API_KEY is not set. Get the token from WeClapp (Mein Profil -> API-Token) and run: setx WECLAPP_CUSTOMER_API_KEY `"<token>`""
}
if (-not $env:WECLAPP_CUSTOMER_BASEURL) {
    throw "WECLAPP_CUSTOMER_BASEURL is not set (expected https://<tenant>.weclapp.com/webapp/api/v1)."
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Definition

# 1. Enable the communication feature + create the adapter runtime entity.
#    $ErrorActionPreference does not stop native commands - check the exit code
#    explicitly (octo-cli exit codes can be optimistic, so ALSO read the output;
#    a non-zero code is always fatal here).
octo-cli -c EnableCommunication
if ($LASTEXITCODE -ne 0) { throw "EnableCommunication failed (exit $LASTEXITCODE) - read the octo-cli output above." }
octo-cli -c ImportRt -f (Join-Path $scriptRoot "_general/rt-adapter-weclapp.yaml") -w
if ($LASTEXITCODE -ne 0) { throw "ImportRt of the adapter entity failed (exit $LASTEXITCODE) - read the octo-cli output above." }

# 2. Remaining setup happens at the tenant (Studio/AdminPanel) - print the checklist with
#    the concrete values (the API key itself is never printed).
Write-Host ""
Write-Host "Next steps (tenant lkv on test-2):"
Write-Host " 1. Create/verify the GlobalConfiguration entry 'WeClappApi'"
Write-Host "    (System.Communication/WeClappConfiguration). Its WELL-KNOWN NAME must be"
Write-Host "    'WeClappApi' - that name is the lookup key, not the display name."
Write-Host "      BaseUrl = $($env:WECLAPP_CUSTOMER_BASEURL.TrimEnd('/'))"
Write-Host "      ApiKey  = value of WECLAPP_CUSTOMER_API_KEY"
Write-Host " 2. Create/verify the entry 'LkvSftp' (System.Communication/SftpConfiguration)"
Write-Host "    with the LKV SFTP access data (same well-known-name rule)."
Write-Host "    MaxConcurrentConnections MUST carry a value - use 3. The CK attribute is"
Write-Host "    optional, but SftpUpload@1 reads it as a non-nullable int: left unset it"
Write-Host "    arrives as null and every upload run dies while the entry is deserialized,"
Write-Host "    before any node does work (staging, 2026-08-21)."
Write-Host " 3. Deploy the pipeline YAMLs from ../pipelines via the AdminPanel to the"
Write-Host "    adapter 'WeClapp Mesh Adapter (LKV)', associate both entries with every"
Write-Host "    pipeline (Uses association, from the pipeline), then REDEPLOY the"
Write-Host "    pipelines: configurations are snapshotted per pipeline registration, so"
Write-Host "    an association added after a deploy takes effect only on the next one."
Write-Host "    Redeploy via AdminPanel or: octo-cli -c DeployPipeline -id <pipelineRtId>"
Write-Host "    (octo-cli -c DeployDataFlow -id <dataFlowRtId> redeploys a whole flow)."
Write-Host "    For a NEW tenant, first review the REPLACE/TBD values in the YAMLs"
Write-Host "    (AI submandant, BE warehouseId, AR/BE remoteDirectory)."
Write-Host " 4. Ensure the PipelineTrigger runtime entities (cron schedules) exist at the"
Write-Host "    tenant - they are not part of this repo - then run:"
Write-Host "      octo-cli -c DeployTriggers"
Write-Host "    Schedules materialize only through this (or a tenant start); required"
Write-Host "    again after every PipelineTrigger import or Enabled flip."
