# State backend bootstrap

Terraform cannot create the storage account that holds its own state. So the
three resources the `azurerm` backend needs, a resource group, a storage
account, and a blob container, are created once by hand before the first
`terraform init`, and their names reach Terraform as init-time backend
configuration rather than as anything committed to this repo. This runbook is
those steps. Hari runs it once; agents never do.

The companion is `infra/persistent/backend.tf`, which declares an empty
`backend "azurerm" {}`. The trade-off behind this split (the backend is no
longer reproducible from source) is recorded in
[docs/specs/10-infra-persistent.md](../specs/10-infra-persistent.md).

## Who runs this, and when

- Actor: Hari, once, before the persistent layer is ever initialised against a
  real backend.
- Not an agent step. Agents run `terraform init -backend=false` only, which
  needs none of this.

## Prerequisites

- The Azure CLI (`az`) is installed and logged in: `az login`.
- The target subscription is selected: `az account set --subscription <SUBSCRIPTION>`.
- You have chosen four names, none of which are committed anywhere. Choose them
  now, before the commands below use them:
  - `<STATE_RG>`: the resource group that holds the state storage account.
  - `<STATE_SA>`: the storage account name. It must be globally unique, 3 to 24
    lowercase letters and digits.
  - `<STATE_CONTAINER>`: the blob container name, for example `tfstate`.
  - `<STATE_KEY>`: the state blob name, for example `persistent.tfstate`.
- A region, `<LOCATION>`, for example `uksouth`.

## Part 1: create the three backend resources by hand

Run these from any shell where `az` is logged in; there is no repo working
directory, because nothing here touches the repo.

1. Create the resource group:

   ```bash
   az group create \
     --name "<STATE_RG>" \
     --location "<LOCATION>"
   ```

2. Create the storage account, with public blob access off and a modern TLS
   floor:

   ```bash
   az storage account create \
     --name "<STATE_SA>" \
     --resource-group "<STATE_RG>" \
     --location "<LOCATION>" \
     --sku Standard_LRS \
     --kind StorageV2 \
     --min-tls-version TLS1_2 \
     --allow-blob-public-access false
   ```

3. Turn on blob versioning, so a corrupted or truncated state write can be
   rolled back:

   ```bash
   az storage account blob-service-properties update \
     --account-name "<STATE_SA>" \
     --resource-group "<STATE_RG>" \
     --enable-versioning true
   ```

4. Create the container that holds the state blob:

   ```bash
   az storage container create \
     --name "<STATE_CONTAINER>" \
     --account-name "<STATE_SA>" \
     --auth-mode login
   ```

## Part 2: give CI the four names as Actions variables

CI passes these to `terraform init -backend-config` at init time. They are
GitHub Actions **variables**, not secrets, and they are the only place the
backend names live. Set them on the repository (or environment) so the
`terraform.yml` workflow's gated `plan` job can read them.

1. Set the four variables to the names you chose in the prerequisites:

   ```bash
   gh variable set TF_BACKEND_RESOURCE_GROUP  --body "<STATE_RG>"
   gh variable set TF_BACKEND_STORAGE_ACCOUNT --body "<STATE_SA>"
   gh variable set TF_BACKEND_CONTAINER       --body "<STATE_CONTAINER>"
   gh variable set TF_BACKEND_STATE_KEY       --body "<STATE_KEY>"
   ```

2. Confirm they are set:

   ```bash
   gh variable list
   ```

The OIDC login identifiers the `plan` job also needs
(`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`) are repository
secrets, set separately as part of the deploy runbook, not here. This runbook
covers only the state backend.

## What you have after this

The three backend resources exist, and CI knows their names without any of
those names being committed. A `terraform init` that supplies the four
`-backend-config` values now finds a real, versioned backend to write state to,
and `infra/persistent/backend.tf` never has to name or manage it.
