# Area: gitops/

How everything reaches the cluster. Terraform installs Argo CD and one root
Application; Argo converges the rest of the platform from a Git tree in sync
waves, so recreating the disposable layer is a convergence to watch rather than
a script to run. ADR-010 is the design authority for this area; this file
decides the implementation detail the ADR leaves open and marks each such
decision SPEC-LEVEL.

Paths owned: `gitops/`, plus the Argo-install and root-Application slice of
`infra/disposable/` and the Entra-registration slice of `infra/persistent/`.
Those two infra slices share paths with existing infra tickets, so the tickets
that touch them carry blocking edges rather than racing (see Dependencies).

## Deliverables

### The gitops/ tree

`gitops/` at the repo root, an app-of-apps: the root Application points at the
tree and every leaf is an Argo Application.

- `bootstrap/`: the root Application.
- `platform/`: istio, gateway, cert-manager, external-dns, eso.
- `workloads/`: strimzi, connect, services.

Sync waves order the convergence so a consumer never starts before what it
needs exists:

| Wave | Converges | Why here |
| --- | --- | --- |
| 0 | ESO and its SecretStore | Later resources read hydrated secrets; nothing that needs a secret can precede this. |
| 1 | Istio | The mesh and its CRDs underpin every gateway and policy resource. |
| 2 | gateway, cert-manager, external-dns | North-south entry, the origin cert, and the DNS record, all on top of Istio. |
| 3 | Strimzi | Kafka before anything that produces to or consumes from it. |
| 4 | Connect and the services | The workloads, last, on a converged platform. |

SPEC-LEVEL beyond this ordering: the exact Application manifests, Helm value
overrides, and repository layout under each directory are this area's to decide
and review may change them freely.

### Terraform delta

- Disposable layer: an argocd install (a `helm_release` or applied manifests,
  SPEC-LEVEL which) plus the single root Application. Terraform's delivery job
  ends there; Argo owns everything downstream.
- Persistent layer: the Argo Entra app registration, the two groups
  (argocd-admins, argocd-readonly), and the Argo OIDC client secret written to
  Key Vault. These live in the persistent layer because the app registration
  and its domain-based redirect URI are stable across recreates (ADR-010).
- The Cloudflare API token is seeded to Key Vault once, by hand, and the step
  is documented rather than automated, because it is a long-lived credential
  that predates any cluster.

### Identity and exposure

Argo CD runs its own OIDC against Entra and maps the two groups to roles;
gateway-level JWT is deliberately not used for the UI, because a browser
arrives without a token and a JWT demand at the door would break the
interactive login (ADR-010). Exposure is a public Azure Load Balancer fronting
the Istio gateway, behind proxied Cloudflare DNS: edge TLS and WAF at
Cloudflare, the LB locked to Cloudflare IP ranges, the origin certificate
issued by cert-manager over DNS-01, and the record kept current by external-dns
as the LB IP churns each recreate.

### Existing tickets change delivery mechanism

Per ADR-010, delivery moves from per-workload GitHub Actions workflows to Argo
Applications. The already-shipped Strimzi delivery (#34) and the Connect deploy
(#82) become Argo Applications, and the standalone Strimzi workflow is retired
in the migration ticket rather than left as a second, divergent delivery path.

## External interfaces

- The Argo CD API and UI: public, Cloudflare-fronted, Entra OIDC with
  group-mapped RBAC. The Argo API/CLI path also carries the one Istio JWT
  authorisation policy exercised in v1.
- The Istio gateway: the single north-south entry for the platform's two public
  HTTPS surfaces (the Argo UI and the demo queue API).
- The ESO SecretStore: authenticates to Key Vault via workload identity and
  hydrates the Cloudflare API token and the Argo OIDC client secret.
- Kafka traffic is excluded from mesh interception; Strimzi's mTLS owns it.

## Verification

Containers wherever the exposure chain is not in play, live only where it is.

- Argo CD and ESO: exercised against a kind cluster in CI (no Azure).
- Istio configuration: validated by `istioctl analyze` in CI.
- The exposure chain (LB, Cloudflare, cert issuance) and SSO: live only,
  serialized and Hari-run, because none of it exists without real Azure, a real
  public IP, and the Cloudflare account.

## Dependencies

- Blocked by the GitOps verification set (G1): the Istio version pin, Gateway
  API conformance, ambient GA status, and the ESO Key Vault workload-identity
  path must be answered before any manifest ships, because AGENTS.md forbids
  shipping a remembered version or API-shape claim.
- The Argo-install slice depends on the disposable-layer AKS foundation, and
  shares `infra/disposable/` paths with existing infra tickets, so it carries a
  blocking edge to any open disposable-layer ticket touching those paths.
- The Entra-registration slice touches `infra/persistent/`; those tickets are
  closed, so it adds to the layer without conflict.
- The Strimzi and Connect migration is blocked by the tree scaffold, because a
  workload Application needs the app-of-apps and the ESO wave underneath it.

## Candidate tickets

Sizes are files and changed-line forecasts against `.github/pr-size-policy.json`
(10 files, 500 lines). G-IDs are resolved to issue numbers when the tickets are
cut.

| # | Behavior | Blocked by | Verification | Size forecast |
| --- | --- | --- | --- | --- |
| G1 | The GitOps verification set is answered and recorded: Istio version pin, Gateway API conformance, ambient GA status, ESO Key Vault workload-identity path. | none | documentation check | 1 file, 60 lines |
| G2 | Persistent layer gains the Argo Entra app registration, the two groups, and the OIDC secret; the Cloudflare token is seeded to Key Vault, documented. | none | unit | 5 files, 240 lines |
| G3 | Bootstrap: Terraform installs Argo CD plus the one root Application. | G1, infra/disposable AKS foundation | containers | 5 files, 300 lines |
| G4a | GitOps tree scaffold: app-of-apps, sync waves, ESO plus SecretStore. | G3, G2 | containers | 8 files, 420 lines |
| G4b | Istio, Gateway API resources, cert-manager, external-dns, origin lockdown, Argo UI exposure with Entra SSO. Live and serialized. | G4a, G1 | live | 9 files, 480 lines |
| G5 | Migrate the already-shipped Strimzi delivery and Connect to Argo Applications; retire the standalone workflow. | G4a | containers | 6 files, 350 lines |
| G6 | Istio JWT authorisation on the demo queue API and the Argo API path; Kafka mesh exclusion. | G4b | containers | 5 files, 300 lines |
| G7 | ADR-010 transcription, recreate runbook rewrite, gitops-diverged and recover-ingress runbooks landed with their alerts. | G4b, G5 | unit | 6 files, 400 lines |

G4b is the one live ticket in this set: it needs a real public IP, the
Cloudflare account, and real certificate issuance, so it is marked
needs-live-test and run by Hari, serialized against every other live ticket.
G7 carries the alert-with-runbook binding rule from observability.md section 8:
each runbook body lands in the same ticket as its alert.
