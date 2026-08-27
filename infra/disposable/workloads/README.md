# Disposable workload chart

This chart renders the four build-scale .NET workload Deployments: `task-api`,
`queue-builder`, `queue-reconciler`, and `notifier`. A single telemetry value
group supplies the same trace sampler name and argument to every Deployment.

Terraform supplies the sensitive Application Insights connection string at
release time. The chart creates the Kubernetes Secret and each Deployment reads
the value through `secretKeyRef`; no connection-string value belongs in chart
source or ordinary Helm values.

Container stdout and stderr are collected by Container Insights through the
Azure Monitor Agent and a Data Collection Rule. AKS control-plane resource logs
are a separate stream routed by the AKS Diagnostic Setting. The Diagnostic
Setting does not collect workload container logs.
