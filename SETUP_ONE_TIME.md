# BEV — One-Time CI/CD Setup (do this once, deploys are automatic after)

You're setting up a pipeline so that pushing a build to GitHub deploys it to Azure
on its own — no more PowerShell blocks from me, no hand-deploying. After this is
wired, a deploy is: drop the new files in the repo, and it goes live by itself.

There are exactly THREE things only you can do (they create the trust between
GitHub and your Azure — I can't do these from here). Everything else I've already
built. Each is paste-ready. Do them in order.

================================================================================
PART 1 — Create the Azure identity GitHub will use to log in (PowerShell)
================================================================================
This makes a passwordless identity ("app registration") that GitHub uses to
deploy. Paste this whole block into PowerShell where you run your az commands.

```powershell
# values for your environment (already filled in)
$sub = "bb970237-b96d-4a1c-82fe-4572d3a8bc95"
$rg  = "bev-platform-rg"
$appName = "bev-github-deployer"

az account set --subscription $sub

# 1. create the app registration GitHub will authenticate as
$appId = az ad app create --display-name $appName --query appId -o tsv
$appId   # <-- this prints the CLIENT ID. Save it.

# 2. create a service principal for it and give it deploy rights on the resource group
az ad sp create --id $appId | Out-Null
$spId = az ad sp show --id $appId --query id -o tsv
az role assignment create --assignee $appId --role "Contributor" `
  --scope "/subscriptions/$sub/resourceGroups/$rg" | Out-Null

# 3. print the three IDs you'll paste into GitHub in Part 3
$tenant = az account show --query tenantId -o tsv
"CLIENT_ID       = $appId"
"TENANT_ID       = $tenant"
"SUBSCRIPTION_ID = $sub"
```

Copy those three printed values somewhere — you paste them into GitHub in Part 3.

================================================================================
PART 2 — Tell that identity it's allowed to log in FROM your GitHub repo (PowerShell)
================================================================================
This is the OIDC trust: it says "GitHub Actions, running in THIS repo, may log in
as this identity, with no password." Replace YOUR_GH_USER and YOUR_REPO with your
actual GitHub username and repository name, then paste.

```powershell
$appId = "<paste the CLIENT_ID from Part 1>"
$ghUser = "YOUR_GH_USER"
$ghRepo = "YOUR_REPO"

# trust pushes to the main branch
$cred = @{
  name = "bev-main"
  issuer = "https://token.actions.githubusercontent.com"
  subject = "repo:$ghUser/${ghRepo}:ref:refs/heads/main"
  audiences = @("api://AzureADTokenExchange")
} | ConvertTo-Json -Compress
$cred | Out-File fic-main.json -Encoding ascii
az ad app federated-credential create --id $appId --parameters fic-main.json

# also trust manual "Run workflow" runs from the Actions tab
$cred2 = @{
  name = "bev-dispatch"
  issuer = "https://token.actions.githubusercontent.com"
  subject = "repo:$ghUser/${ghRepo}:environment:production"
  audiences = @("api://AzureADTokenExchange")
} | ConvertTo-Json -Compress
$cred2 | Out-File fic-dispatch.json -Encoding ascii
az ad app federated-credential create --id $appId --parameters fic-dispatch.json
```

================================================================================
PART 3 — Put the three IDs into GitHub as repo secrets (clicks, no code)
================================================================================
In your GitHub repo in the browser:

1. Settings -> Secrets and variables -> Actions -> "New repository secret"
2. Add these three (name on the left, value = what Part 1 printed):
     AZURE_CLIENT_ID        = the CLIENT_ID
     AZURE_TENANT_ID        = the TENANT_ID
     AZURE_SUBSCRIPTION_ID  = the SUBSCRIPTION_ID
3. Save each.

That's the entire one-time setup. You never touch these again.

================================================================================
HOW DEPLOYS WORK AFTER THIS (the whole point)
================================================================================
The repo has this layout (I set it up — see the repo zip):
    hive/      <- the Hive source + BUILD_LABEL.txt
    server/    <- the Server source
    .github/workflows/   <- the two pipelines (already written)

To deploy the Hive: put the new Hive source in hive/, set the new label in
hive/BUILD_LABEL.txt, and commit/push to main. GitHub builds it, deploys it to
bev-hive-fn, and stamps the label — automatically, in ~2 minutes, with zero
PowerShell from you. The Server pipeline works the same on server/.

You can also deploy by hand WITHOUT pushing: GitHub repo -> Actions tab ->
"Deploy Hive" -> "Run workflow". One click.

===============================================================================

================================================================================
ALL THREE ARE WIRED — Hive, Server, AND Gateway
================================================================================
- Hive + Server: push to hive/ or server/, deploys itself.
- Gateway: pushing to gateway/ (or Actions -> Deploy Gateway -> Run workflow)
  builds the MSI on a Windows runner, uploads it to the gateway-updates blob,
  mints the SAS, and updates the manifest on bev-server-fn — the exact steps you
  ran by hand, now automatic. Your beta boxes pull on their next manifest check,
  or force on a box with: Restart-Service NexusGateway.
  Manual run asks for the build label (e.g. GW.0611.26-A) and a unique MSI name.
The same three Azure secrets (Part 3) cover all three pipelines — no extra setup.
