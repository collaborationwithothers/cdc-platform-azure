# Argo CD bootstrap

Terraform installs Argo CD and applies exactly one thing from this directory:
the root Application. Everything else the platform runs is an Argo Application
that this root discovers from the `gitops/` tree, converged in sync waves
(ADR-010). That is the Terraform-to-Argo boundary: Terraform stops here, Argo
takes over.

This chart renders the root Application. It is a Helm chart, like
`infra/disposable/kafka`, so Terraform can apply it with the same `helm_release`
mechanism the layer already uses; the alternative, a plain manifest applied with
`kubernetes_manifest`, needs the Argo CRDs to exist at plan time and so cannot
plan before the cluster is built.

## What the root Application watches

- `repoURL` and `targetRevision`: the public repository and the ref Argo
  follows. Production follows `main`; the kind CI job overrides both to the
  branch under test so it proves sync against the pull request's own commit.
- `path`: `gitops`, the whole tree.
- `directory.recurse`: true, so nested leaves are found as they are added.
- `directory.exclude`: `bootstrap/**`. Argo reads a directory source as plain
  manifests, so it must not descend into this chart; its `Chart.yaml` and its
  Helm-templated files are not valid manifests. Excluding `bootstrap/**` keeps
  Argo out of its own bootstrap directory.

The tree is empty until the scaffold ticket adds `platform/` and `workloads/`.
Until then the root Application manages zero resources and reports Synced, which
is what the kind CI job asserts.

## Verification

The `gitops-kind` workflow brings up a kind cluster, installs Argo CD from the
pinned chart, applies this root Application pointed at the branch under test, and
waits for it to report Synced. No Azure is involved.
