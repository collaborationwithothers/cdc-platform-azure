# Platform Applications

Platform Applications converge after ESO. They provide the cluster services
that workloads depend on, in the order defined by ADR-010.

- Wave 0: ESO and its namespaced SecretStore. The root chart owns this child
  Application and selects the fake or Azure Key Vault adapter.
- Wave 1: Istio and its CRDs.
- Wave 2: the Gateway API gateway, cert-manager, and external-dns.

The wave 1 and wave 2 Applications are future work. Their content is not
created by this scaffold.
