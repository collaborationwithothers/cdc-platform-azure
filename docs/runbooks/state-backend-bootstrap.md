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
- You have chosen five names, none of which are committed anywhere. Choose them
  now, before the commands below use them:
  - `<STATE_RG>`: the resource group that holds the state storage account. It
    must not be `rg-cdc-platform-persistent`, which Terraform creates and
    manages separately.
  - `<STATE_SA>`: the storage account name. It must be globally unique, 3 to 24
    lowercase letters and digits.
  - `<STATE_CONTAINER>`: the blob container name, for example `tfstate`.
  - `<STATE_KEY>`: the state blob name, for example `persistent.tfstate`.
  - `<DISPOSABLE_STATE_KEY>`: a different state blob name for the disposable
    layer, for example `disposable.tfstate`.
- Use UK South: set `<LOCATION>` to `uksouth`.

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

## Part 2: give CI the five names as Actions variables

CI passes these to `terraform init -backend-config` at init time. They are
GitHub Actions **variables**, not secrets, and they are the only place the
backend names live. Set them on the repository (or environment) so the
`terraform.yml` workflow's gated `plan` job can read them.

1. Set the five variables to the names you chose in the prerequisites. Do not
   add whitespace before or after either state key:

   ```bash
   gh variable set TF_BACKEND_RESOURCE_GROUP  --body "<STATE_RG>"
   gh variable set TF_BACKEND_STORAGE_ACCOUNT --body "<STATE_SA>"
   gh variable set TF_BACKEND_CONTAINER       --body "<STATE_CONTAINER>"
   gh variable set TF_BACKEND_STATE_KEY       --body "<STATE_KEY>"
   gh variable set TF_DISPOSABLE_BACKEND_STATE_KEY --body "<DISPOSABLE_STATE_KEY>"
   ```

2. Confirm they are set:

   ```bash
   gh variable list
   ```

The OIDC login identifiers the `plan` job also needs
(`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`) are `azure-plan`
environment secrets, set separately through the
[CI identity bootstrap runbook](ci-identity-bootstrap.md), not here. This
runbook covers only the state backend.

## Before the disposable plan

The persistent layer must already have state written by an earlier apply. A
persistent plan reads state but does not write the outputs that the disposable
layer needs. The disposable plan stops when that existing state is missing.

The workflow plans the persistent layer first. It starts the disposable plan
only after the persistent plan succeeds. The disposable plan uses
`<DISPOSABLE_STATE_KEY>` for its own backend and reads persistent outputs from
`<STATE_KEY>`. Neither plan writes state.

## What you have after this

The three backend resources exist, and CI knows their names without any of
those names being committed. The persistent and disposable layers share the
backend resources but use different state blobs. A `terraform init` that
supplies the backend coordinates and the layer's state key finds a real,
versioned backend without either Terraform layer naming or managing it.
