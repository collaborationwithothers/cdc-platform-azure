# ADR-010: GitOps delivery via Argo CD; Istio Gateway API ingress; proxied public exposure

Status: Accepted

## Context

Workloads were deployed by per-workload GitHub Actions workflows (Strimzi
first); each new workload needed another workflow encoding its own ordering,
which does not scale and makes the teardown/recreate cycle a script sequence
rather than a convergence. The platform recreates its disposable layer every
session, so delivery must be declarative and self-ordering.

## Decision

In five parts:

1. Argo CD, self-hosted in the cluster, owns all in-cluster delivery.
   Terraform's job ends at: install Argo CD, apply one root Application
   pointing at the gitops/ tree (app-of-apps). Everything else (Istio,
   gateway, cert-manager, external-dns, ESO, Strimzi, Connect, services) is an
   Argo Application converged in sync waves. Per-workload deploy workflows are
   retired.
2. Ingress is Istio's Gateway API implementation, upstream Istio, self-managed
   via Argo. Stated honestly: a service mesh at three services is premature;
   the fleet condition that changes the answer is per-tenant consumer
   deployments and cross-service authorisation at 400-tenant scale. v1
   exercises exactly one mesh capability beyond ingress: JWT authorisation
   policy (Entra-issued tokens) on the demo queue API and the Argo API/CLI
   path. Dataplane mode (ambient preferred for node headroom, sidecar
   fallback) is pinned by the verification ticket, not memory. Kafka traffic is
   excluded from mesh interception; Strimzi's mTLS owns Kafka.
3. Exposure: public Azure Load Balancer fronting the Istio gateway, behind
   proxied (orange-cloud) Cloudflare DNS on consultwithcloud.com. Edge TLS and
   WAF at Cloudflare; origin lockdown restricts the LB to Cloudflare IP ranges;
   cert-manager issues the origin certificate via DNS-01 against the Cloudflare
   API; external-dns updates the record as the LB IP churns per recreate.
   Alternatives recorded: Cloudflare Tunnel (zero inbound surface, one moving
   part; rejected by operator preference for the conventional pattern, remains
   the hardening option); Front Door plus Private Link plus firewalled
   hub-spoke (the production-scale evolution: correct at multiple spokes with
   compliance-driven inspection, rejected at build scale on cost, an Azure
   Firewall alone exceeding the monthly ceiling, and on demonstrating estate
   engineering this platform does not yet need).
4. Identity: Argo CD performs its own OIDC against Entra. App registration and
   two groups (argocd-admins, argocd-readonly) live in the persistent Terraform
   layer because identity and the domain-based redirect URI are stable across
   recreates; Argo RBAC maps the groups to roles. Gateway-level JWT enforcement
   is NOT used for the UI: a browser arrives without a token, so a JWT demand at
   the door breaks the interactive login flow; JWT policy belongs on
   machine-called doors only.
5. Secrets: External Secrets Operator, authenticating to Key Vault via workload
   identity, hydrates the two runtime secrets (Cloudflare API token; Argo OIDC
   client secret). Terraform state never carries secret material; rotation is a
   Key Vault write. The bootstrap ordering this forces (ESO and its SecretStore
   converge before consumers) is expressed declaratively as Argo sync waves.

## Consequences

- Recovery becomes "terraform apply the disposable layer, watch Argo
  converge", exercised every session.
- The recreate runbook carries four proxied-exposure obligations (public IP
  churn, DNS automation, origin lockdown, origin cert).
- The management UI is public and SSO-gated, a stated change to the prior
  no-public-ingress posture.
- Existing tickets #34 and #82 change delivery mechanism from workflow to Argo
  Application.
- The Argo UI is a session-time surface, dark between sessions by design.
