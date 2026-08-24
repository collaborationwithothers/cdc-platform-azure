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
   path. Kafka traffic is excluded from mesh interception; Strimzi's mTLS owns
   Kafka, and because ambient redirection is whole-pod with no per-port opt-out,
   that exclusion is namespace-level rather than a port list.

   Dataplane mode is ambient, on Istio 1.30.3, pinned by V16 of the verification
   register rather than from memory. The evidence is recorded as it stands and
   no further. Ambient itself reached GA in Istio 1.24. The combination of
   ambient with Gateway API ingress carries no single statement grading it GA:
   three component statements are each marked Stable, namely "Connecting Istio
   ingress gateways to ambient workloads", "Kubernetes Gateway APIs for ingress
   (`Gateway` `parentRef`)", and "Waypoints: Gateway API Stable Channel
   (`HTTPRoute`, `GRPCRoute`)", and the pair is not jointly stated.

   Ambient is accepted on that partial evidence because the unverified part does
   not overlap the path v1 exercises. Gateway API use here is north-south only,
   and Istio's ingress gateway is a standalone Envoy deployment under either
   dataplane mode, so the gateway does not change with the choice; the one
   capability beyond ingress is gateway-attached policy, not workload-side L7.
   Ambient and Gateway API interact in novel ways east-west, through ztunnel
   enrolment and waypoint proxies, and v1 enrols no workloads there. Sidecar
   remains the fallback with a named trigger rather than a judgement call:
   gateway or policy misbehaviour attributable to the ambient dataplane at
   G4b's live verification flips the mode, which is a values change with no
   contract impact, and the flip is recorded. Enrolling any workload into
   ambient re-opens the verification question for the east-west path rather than
   inheriting this answer.
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
