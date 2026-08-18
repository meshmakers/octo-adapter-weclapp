# Sets up the WeClapp adapter runtime entity at the lkv tenant (test-2). The pipeline
# YAMLs in ../pipelines deploy as-is — they carry no tenant-specific values: WeClapp
# access comes from the tenant GlobalConfiguration entry "WeClappApi" (referenced via
# apiConfiguration), SFTP access from the entry "LkvSftp" (referenced via
# serverConfiguration). Both entries are maintained at the tenant (Studio/AdminPanel),
# so no key or host name ever enters the repo or a deployed pipeline definition.
#
# Prerequisites:
#   - octo-cli context 'test-2_lkv' active and authenticated (Register-OctoCliContext)
#   - Environment variables WECLAPP_CUSTOMER_API_KEY and WECLAPP_CUSTOMER_BASEURL set
#     (user level; the same variables gate WeClappCustomerSmokeTests):
#       setx WECLAPP_CUSTOMER_API_KEY "<token from WeClapp: Mein Profil → API-Token>"
#       setx WECLAPP_CUSTOMER_BASEURL "https://<tenant>.weclapp.com/webapp/api/v1"
#     (new shell required after setx)
#
# Key rotation: update the "WeClappApi" entry at the tenant and redeploy the pipelines
# (the GlobalConfiguration is a deploy-time snapshot per pipeline registration), then
# revoke the old key at WeClapp.

$ErrorActionPreference = "Stop"

# Gate: the setup ends with the tenant GlobalConfiguration steps below, which need these
# values at hand — fail fast if they are missing.
if (-not $env:WECLAPP_CUSTOMER_API_KEY) {
    throw "WECLAPP_CUSTOMER_API_KEY is not set. Get the token from WeClapp (Mein Profil → API-Token) and run: setx WECLAPP_CUSTOMER_API_KEY `"<token>`""
}
if (-not $env:WECLAPP_CUSTOMER_BASEURL) {
    throw "WECLAPP_CUSTOMER_BASEURL is not set (expected https://<tenant>.weclapp.com/webapp/api/v1)."
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Definition

# 1. Enable the communication feature + create the adapter runtime entity
octo-cli -c EnableCommunication
octo-cli -c ImportRt -f (Join-Path $scriptRoot "_general/rt-adapter-weclapp.yaml") -w

# 2. Remaining setup happens at the tenant (Studio/AdminPanel) — print the checklist with
#    the concrete values (the API key itself is never printed).
Write-Host ""
Write-Host "Next steps (tenant lkv on test-2):"
Write-Host " 1. Create/verify the GlobalConfiguration entry 'WeClappApi'"
Write-Host "    (System.Communication/WeClappConfiguration):"
Write-Host "      BaseUrl = $($env:WECLAPP_CUSTOMER_BASEURL.TrimEnd('/'))"
Write-Host "      ApiKey  = value of WECLAPP_CUSTOMER_API_KEY"
Write-Host " 2. Create/verify the GlobalConfiguration entry 'LkvSftp'"
Write-Host "    (System.Communication/SftpConfiguration) with the LKV SFTP access data."
Write-Host " 3. Deploy the pipeline YAMLs from ../pipelines as-is via the AdminPanel to the"
Write-Host "    adapter 'WeClapp Mesh Adapter (LKV)' and associate both entries with every"
Write-Host "    pipeline (Uses association, from the pipeline) — an entry reaches a"
Write-Host "    pipeline's GlobalConfiguration only through that association."
Write-Host " 4. octo-cli -c DeployTriggers   # cron schedules materialize only through this"
Write-Host "                                 # (or a tenant start) — required after every"
Write-Host "                                 # PipelineTrigger import or Enabled flip"
