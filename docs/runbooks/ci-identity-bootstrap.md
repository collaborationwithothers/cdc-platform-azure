# CI identity bootstrap

GitHub Actions needs an Entra application before it can run the persistent
Terraform plan. Hari creates this bootstrap identity once outside Terraform.
Terraform does not manage or import the application, service principal, or
federated credential.

This runbook uses OpenID Connect (OIDC), a token exchange that gives the
workflow short-lived Azure access without a client secret or certificate.

## 1. Open the repository and authenticate the command-line tools

Start in any directory inside the repository clone. Move to the repository root:

```bash
cd "$(git rev-parse --show-toplevel)"
```

Confirm the GitHub CLI is authenticated to an account that administers the
repository:

```bash
gh auth status
```

If that check fails, authenticate and check again:

```bash
gh auth login --web --hostname github.com
gh auth status
```

Sign in to Azure, list the available subscriptions, then select the target
subscription by the name or ID shown in that list:

```bash
az login
az account list --output table
az account set --subscription "<SUBSCRIPTION_NAME_OR_ID>"
az account show --query '{subscriptionId:id,tenantId:tenantId}' --output yaml
```

Keep the displayed subscription and tenant IDs available for step 4. Do not
write them to a file in this repository.

## 2. Select the external Entra application

Hari performs these steps in the Azure portal while signed in to the tenant
selected in step 1:

1. Open **Microsoft Entra ID**, then **App registrations**.
2. Open the existing `Github Actions deployment` registration. If it is
   missing, stop. Creating or importing another CI identity is outside this
   ticket.
3. On **Overview**, confirm **Managed application in local directory** links to
   the corresponding service principal.
4. Copy **Object ID** for step 3 and **Application (client) ID** for step 4.
   Confirm **Directory (tenant) ID** matches the tenant selected in step 1.

Do not add anything under **Client credentials**. This workflow uses federation
instead of a client secret or certificate.

## 3. Verify the immutable GitHub federated credential

From the repository root established in step 1, list the credentials on the
external application. Replace the placeholder with the application Object ID
copied in step 2:

```bash
az ad app federated-credential list \
  --id "<APPLICATION_OBJECT_ID>" \
  --query "[].{name:name,issuer:issuer,subject:subject,audiences:audiences}" \
  --output yaml
```

The existing credential must show this issuer, audience, and subject:

| Field | Value |
| --- | --- |
| Issuer | `https://token.actions.githubusercontent.com` |
| Audience | `api://AzureADTokenExchange` |
| Subject | `repo:collaborationwithothers@243412459/cdc-platform-azure@1341524323:environment:azure-plan` |

If no credential matches all three values, stop. Do not create a second
credential as part of this ticket.

The full subject is:

```text
repo:collaborationwithothers@243412459/cdc-platform-azure@1341524323:environment:azure-plan
```

The subject follows GitHub's
[immutable OIDC format](https://docs.github.com/en/actions/reference/security/oidc#immutable-subject-claims).
The numeric owner and repository IDs prevent another repository from inheriting
trust by reusing the same names.

## 4. Store the Azure identifiers in the protected environment

In GitHub, open **Settings**, then **Environments**, then `azure-plan`. Require
Hari as a reviewer and restrict deployment branches to `main`.

From the repository root established in step 1, run each command separately.
Each command prompts for one value and encrypts it before sending it to GitHub:

```bash
gh secret set AZURE_CLIENT_ID --env azure-plan
gh secret set AZURE_TENANT_ID --env azure-plan
gh secret set AZURE_SUBSCRIPTION_ID --env azure-plan
```

Paste the application client ID from step 2, then the tenant ID and subscription
ID selected in step 1. Do not pass the values through `--body`, a shell variable,
or a file that could preserve them locally.

## 5. Verify the static trust configuration

Confirm the three secret names exist without displaying their values:

```bash
gh secret list --env azure-plan
```

Confirm the repository emits immutable subjects:

```bash
gh api \
  /repos/collaborationwithothers/cdc-platform-azure/actions/oidc/customization/sub
```

The response must show `"use_immutable_subject": true` and this prefix:

```text
repo:collaborationwithothers@243412459/cdc-platform-azure@1341524323
```

Stop after the static checks. The identity spike owns dispatching the workflow
and proving that either identity authenticates against Azure.
