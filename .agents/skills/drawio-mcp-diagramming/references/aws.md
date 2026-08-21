# AWS Diagram Guidance

Everything AWS-specific for draw.io MCP diagrams: the AWS4 stencil library and its caveats, VPC/AZ/subnet container structure, colour conventions, and the topology checklist.

Read this file when the diagram contains AWS services — whether it is a full VPC topology or a handful of AWS icons in a flow diagram.

Contents:
- [AWS4 icon library and caveats](#aws4-icon-library-and-caveats)
- [Known-good AWS4 style strings](#known-good-aws4-style-strings)
- [VPC, AZ, and subnet container structure](#vpc-az-and-subnet-container-structure)
- [Styles and colours](#styles-and-colours)
- [Resource placement rules](#resource-placement-rules)
- [Traffic flow colour palette](#traffic-flow-colour-palette)
- [Annotation boxes](#annotation-boxes)
- [Complete topology example](#complete-topology-example)
- [AWS topology checklist](#aws-topology-checklist)

---

## AWS4 icon library and caveats

AWS4 icons are **stencils, not SVG files**. Two things go wrong most often:

1. **Wrong style approach** — do not use `image=img/lib/aws4/...`. The correct form is
   `shape=mxgraph.aws4.<name>;fillColor=<colour>;fontColor=#ffffff;strokeColor=none;`
   Shape names use underscores (`elastic_kubernetes_service`, not `elastic-kubernetes-service`).
2. **Library/environment mismatch** — some embedded viewers do not load the `mxgraph.aws4` stencil library. If shapes do not render in VS Code, test the same XML in `app.diagrams.net` before changing the style string.

Fill colour conventions by service category:

| Category | `fillColor` |
|---|---|
| Compute | `#ED7100` |
| Storage | `#3F8624` |
| Database | `#C7131F` |
| Networking / content delivery | `#8C4FFF` |
| Security, identity, compliance | `#DD344C` |
| Management / integration | `#E7157B` |
| Generic / AWS dark | `#232F3E` |

If a shape still fails, re-query `search_shapes` (raise `limit`, up to 50) and substitute a confirmed style string rather than guessing a variant name.

---

## Known-good AWS4 style strings

Confirm via `search_shapes` before use — these are a starting point, not a substitute for lookup.

```text
shape=mxgraph.aws4.ec2;fillColor=#ED7100;fontColor=#ffffff;strokeColor=none;
shape=mxgraph.aws4.lambda;fillColor=#ED7100;fontColor=#ffffff;strokeColor=none;
shape=mxgraph.aws4.elastic_container_service;fillColor=#ED7100;fontColor=#ffffff;strokeColor=none;
shape=mxgraph.aws4.elastic_kubernetes_service;fillColor=#ED7100;fontColor=#ffffff;strokeColor=none;
shape=mxgraph.aws4.application_load_balancer;fillColor=#8C4FFF;fontColor=#ffffff;strokeColor=none;
shape=mxgraph.aws4.cloudfront;fillColor=#8C4FFF;fontColor=#ffffff;strokeColor=none;
shape=mxgraph.aws4.route_53;fillColor=#8C4FFF;fontColor=#ffffff;strokeColor=none;
shape=mxgraph.aws4.vpc;fillColor=#8C4FFF;fontColor=#ffffff;strokeColor=none;
shape=mxgraph.aws4.transit_gateway;fillColor=#8C4FFF;fontColor=#ffffff;strokeColor=none;
shape=mxgraph.aws4.api_gateway;fillColor=#8C4FFF;fontColor=#ffffff;strokeColor=none;
shape=mxgraph.aws4.s3;fillColor=#3F8624;fontColor=#ffffff;strokeColor=none;
shape=mxgraph.aws4.efs;fillColor=#3F8624;fontColor=#ffffff;strokeColor=none;
shape=mxgraph.aws4.rds;fillColor=#C7131F;fontColor=#ffffff;strokeColor=none;
shape=mxgraph.aws4.dynamodb;fillColor=#C7131F;fontColor=#ffffff;strokeColor=none;
shape=mxgraph.aws4.elasticache;fillColor=#C7131F;fontColor=#ffffff;strokeColor=none;
shape=mxgraph.aws4.iam;fillColor=#DD344C;fontColor=#ffffff;strokeColor=none;
shape=mxgraph.aws4.key_management_service;fillColor=#DD344C;fontColor=#ffffff;strokeColor=none;
shape=mxgraph.aws4.waf;fillColor=#DD344C;fontColor=#ffffff;strokeColor=none;
shape=mxgraph.aws4.cognito;fillColor=#DD344C;fontColor=#ffffff;strokeColor=none;
shape=mxgraph.aws4.cloudwatch;fillColor=#E7157B;fontColor=#ffffff;strokeColor=none;
shape=mxgraph.aws4.cloudformation;fillColor=#E7157B;fontColor=#ffffff;strokeColor=none;
shape=mxgraph.aws4.cloudtrail;fillColor=#E7157B;fontColor=#ffffff;strokeColor=none;
shape=mxgraph.aws4.sqs;fillColor=#E7157B;fontColor=#ffffff;strokeColor=none;
shape=mxgraph.aws4.sns;fillColor=#E7157B;fontColor=#ffffff;strokeColor=none;
shape=mxgraph.aws4.eventbridge;fillColor=#E7157B;fontColor=#ffffff;strokeColor=none;
```

---

## VPC, AZ, and subnet container structure

Model the hierarchy with real containment, not stacked rectangles — see [xml-authoring-rules.md](xml-authoring-rules.md) for the general rule and why it matters.

- Each VPC is a `swimlane;startSize=24;` at `parent="1"`.
- Each Availability Zone is a `swimlane;startSize=24;` with `parent="<vpc_id>"`.
- Each subnet is a `swimlane;startSize=24;` with `parent="<az_id>"` — this is what makes AZ redundancy visible.
- Resource icons are parented to their subnet, with coordinates relative to the subnet.
- Edges between resources in different subnets, AZs, or VPCs sit at `parent="1"`.
- Edge/internet services and Transit Gateway/peering zones are separate top-level containers.

---

## Styles and colours

| Element | Style |
|---|---|
| VPC (Production) | `swimlane;startSize=24;fillColor=#d5e8d4;strokeColor=#82b366;strokeWidth=4;` |
| VPC (Development) | `swimlane;startSize=24;fillColor=#dae8fc;strokeColor=#6c8ebf;strokeWidth=4;` |
| VPC (Shared Services) | `swimlane;startSize=24;fillColor=#fff2cc;strokeColor=#d6b656;strokeWidth=4;` |
| AZ container | `swimlane;startSize=24;fillColor=#f5f5f5;strokeColor=#999999;strokeWidth=1;` |
| Public subnet | `swimlane;startSize=24;fillColor=#e6f4ea;strokeColor=#82b366;dashed=1;dashPattern=8 8;strokeWidth=2;` |
| Private subnet | `swimlane;startSize=24;fillColor=#EFF7FF;strokeColor=#6c8ebf;dashed=1;dashPattern=8 8;strokeWidth=2;` |
| Isolated subnet (databases) | `swimlane;startSize=24;fillColor=#fff3e0;strokeColor=#e6821e;dashed=1;dashPattern=8 8;strokeWidth=2;` |

Label subnets with name, AZ, and CIDR. Canvas: `pageWidth="1900" pageHeight="1500"` for multi-VPC or multi-account topologies.

---

## Resource placement rules

- Internet-facing (ALB, NAT Gateway, Bastion) → **public subnets**
- Application servers / ECS tasks / EKS nodes → **private subnets**
- Databases (RDS, ElastiCache) → **isolated subnets**, no outbound internet
- Always show the NAT Gateway path for private subnet → internet egress
- Annotate Security Group and NACL boundaries where they carry meaning

---

## Traffic flow colour palette

| Traffic type | Colour | Style |
|---|---|---|
| HTTPS:443 internet ingress | `#0078D4` Blue | solid, `strokeWidth=3` |
| HTTP:80/8080 backend | `#00897B` Teal | solid, `strokeWidth=2` |
| Database (PostgreSQL:5432, MySQL:3306) | `#5C6BC0` Indigo | dashed, `strokeWidth=2` |
| VPC Endpoint / AWS managed service | `#43A047` Green | solid, `strokeWidth=2` |
| SSH:22 / SSM management | `#F57C00` Amber | dashed, `strokeWidth=2` |
| Denied/blocked | `#C62828` Red | solid — reserve exclusively for blocked traffic |

---

## Annotation boxes

1. **Network Isolation Explanation** — top-left: VPC thick borders, subnet tiers, SG/NACL notes, VPC Endpoints for private AWS service access.
2. **Zone Separation** — Internet/Edge zone (CloudFront, Route 53, WAF, Shield), VPC Peering/Transit Gateway zone, AWS Managed Services zone.

---

## Complete topology example

Nested containers, relative child coordinates, cross-container edges at `parent="1"`, and `as="geometry"` on every geometry element.

```xml
<mxGraphModel pageWidth="1900" pageHeight="1500">
  <root>
    <mxCell id="0"/>
    <mxCell id="1" parent="0"/>
    <mxCell id="iso" value="&lt;b&gt;Network Isolation&lt;/b&gt;&lt;br&gt;VPCs: thick borders&lt;br&gt;Public / private / isolated subnets&lt;br&gt;Security Groups + NACLs&lt;br&gt;VPC Endpoints for private service access" style="text;html=1;strokeColor=#d6b656;fillColor=#fff9cc;align=left;verticalAlign=top;spacingLeft=10;rounded=1;" vertex="1" parent="1">
      <mxGeometry x="40" y="40" width="260" height="120" as="geometry"/>
    </mxCell>
    <mxCell id="vpc" value="Production VPC - 10.0.0.0/16" style="swimlane;startSize=24;html=1;fillColor=#d5e8d4;strokeColor=#82b366;strokeWidth=4;fontSize=16;fontStyle=1;" vertex="1" parent="1">
      <mxGeometry x="360" y="200" width="920" height="520" as="geometry"/>
    </mxCell>
    <mxCell id="az-a" value="AZ us-east-1a" style="swimlane;startSize=24;html=1;fillColor=#f5f5f5;strokeColor=#999999;strokeWidth=1;fontSize=13;" vertex="1" parent="vpc">
      <mxGeometry x="40" y="50" width="400" height="440" as="geometry"/>
    </mxCell>
    <mxCell id="pub-a" value="Public Subnet - 10.0.1.0/24" style="swimlane;startSize=24;html=1;fillColor=#e6f4ea;strokeColor=#82b366;strokeWidth=2;dashed=1;dashPattern=8 8;fontSize=12;" vertex="1" parent="az-a">
      <mxGeometry x="20" y="40" width="360" height="170" as="geometry"/>
    </mxCell>
    <mxCell id="alb" value="ALB" style="shape=mxgraph.aws4.application_load_balancer;fillColor=#8C4FFF;fontColor=#ffffff;strokeColor=none;verticalLabelPosition=bottom;verticalAlign=top;html=1;" vertex="1" parent="pub-a">
      <mxGeometry x="40" y="50" width="64" height="64" as="geometry"/>
    </mxCell>
    <mxCell id="prv-a" value="Private Subnet - 10.0.2.0/24" style="swimlane;startSize=24;html=1;fillColor=#EFF7FF;strokeColor=#6c8ebf;strokeWidth=2;dashed=1;dashPattern=8 8;fontSize=12;" vertex="1" parent="az-a">
      <mxGeometry x="20" y="230" width="360" height="190" as="geometry"/>
    </mxCell>
    <mxCell id="ecs" value="ECS Task" style="shape=mxgraph.aws4.elastic_container_service;fillColor=#ED7100;fontColor=#ffffff;strokeColor=none;verticalLabelPosition=bottom;verticalAlign=top;html=1;" vertex="1" parent="prv-a">
      <mxGeometry x="40" y="60" width="64" height="64" as="geometry"/>
    </mxCell>
    <mxCell id="e-alb-ecs" value="HTTP:8080" style="edgeStyle=orthogonalEdgeStyle;rounded=1;html=1;strokeColor=#00897B;strokeWidth=2;" edge="1" parent="1" source="alb" target="ecs">
      <mxGeometry relative="1" as="geometry"/>
    </mxCell>
  </root>
</mxGraphModel>
```

Generate with `routing: "libavoid"` so connectors route around the containers without moving them.

---

## AWS topology checklist

- [ ] VPCs are containers with thick borders (`strokeWidth=4`), colour-coded by environment
- [ ] AZ containers group subnets per Availability Zone
- [ ] Subnets are containers parented to their AZ, dashed and colour-coded by tier (public/private/isolated)
- [ ] Every resource is parented to its subnet with relative coordinates
- [ ] Cross-subnet, cross-AZ, and cross-VPC edges declared at `parent="1"`
- [ ] Internet Gateway and NAT Gateway shown, with the private-subnet egress path
- [ ] Traffic arrows labelled with protocols and ports, using the palette above
- [ ] Security Group / NACL boundaries annotated where important
- [ ] Network isolation explanation box included
- [ ] VPC Peering / Transit Gateway and edge services in separate zone containers
- [ ] Canvas 1900×1500 for complex infrastructure
- [ ] `routing: "libavoid"` applied; no hand-written waypoints or exit/entry points
- [ ] Animation preference confirmed before generating
