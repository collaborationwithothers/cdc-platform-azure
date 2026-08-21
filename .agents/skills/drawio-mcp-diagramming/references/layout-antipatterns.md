# Draw.io Layout Anti-Patterns Reference

Worked examples of layout problems encountered in real diagram reviews, and how to fix them. Covers both Azure and AWS diagrams.

---

## Common Root Causes of Cluttered Diagrams

Eight issues frequently compound to make lines and labels unreadable:

| # | Problem | Symptom |
|---|---------|---------|
| 1 | Repeated identical label on 3+ edges from the same hub node | Labels stacked on top of each other at the source exit |
| 2 | 3+ dashed lines leaving the same node face within 20px | Lines rendered as one thick bar |
| 3 | Azure Monitor placed inside a VNet/subnet container | Architecturally incorrect and visually clutters private network |
| 4 | CloudWatch placed inside a VPC/subnet container | Architecturally incorrect — CloudWatch is a regional managed service |
| 5 | Decorative resource icon free-floating inside a container, overlapping service icons | Icon covers service icon |
| 6 | Two services sharing one subnet with exits on the same face | Shared corridor, all edges stacked |
| 7 | All `mxCell` elements on one line | Any patch edit fails context match |
| 8 | Using `image=img/lib/aws4/...` style for AWS4 shapes | AWS4 icons are stencils — must use `shape=mxgraph.aws4.*` |

---

## Fix Patterns

### Repeated edge labels

Before — all three labels identical:

```xml
<mxCell id="33" value="Route" edge="1" parent="1" source="hub" target="A"><mxGeometry relative="1" as="geometry"/></mxCell>
<mxCell id="34" value="Route" edge="1" parent="1" source="hub" target="B"><mxGeometry relative="1" as="geometry"/></mxCell>
<mxCell id="35" value="Route" edge="1" parent="1" source="hub" target="C"><mxGeometry relative="1" as="geometry"/></mxCell>
```

After — each label names its specific target:

```xml
<mxCell id="33" value="Service A" edge="1" parent="1" source="hub" target="A"><mxGeometry relative="1" as="geometry"/></mxCell>
<mxCell id="34" value="Service B" edge="1" parent="1" source="hub" target="B"><mxGeometry relative="1" as="geometry"/></mxCell>
<mxCell id="35" value="Service C" edge="1" parent="1" source="hub" target="C"><mxGeometry relative="1" as="geometry"/></mxCell>
```

### Stacked edges from one node face

Work through these in order — the first two solve almost every case, and neither adds brittle XML:

1. **Reduce the edges.** Consolidate same-protocol fan-outs into one labelled edge, and cap observability/identity edges (see [xml-authoring-rules.md](xml-authoring-rules.md)).
2. **Re-run with `routing: "libavoid"`.** It spreads parallel connectors and routes them around shapes, using your existing node positions.
3. **Offset the labels, not the edges.** The `x`/`y` attributes on `<mxGeometry relative="1">` shift the *label* along the edge path — enough to unstack labels on edges that share a segment:

```xml
<mxCell id="33" value="Service A" style="edgeStyle=orthogonalEdgeStyle;rounded=1;html=1;" edge="1" parent="1" source="hub" target="A">
  <mxGeometry relative="1" x="-0.55" y="-16" as="geometry"/>
</mxCell>
<mxCell id="34" value="Service B" style="edgeStyle=orthogonalEdgeStyle;rounded=1;html=1;" edge="1" parent="1" source="hub" target="B">
  <mxGeometry relative="1" x="-0.2" y="-4" as="geometry"/>
</mxCell>
<mxCell id="35" value="Service C" style="edgeStyle=orthogonalEdgeStyle;rounded=1;html=1;" edge="1" parent="1" source="hub" target="C">
  <mxGeometry relative="1" x="0.2" y="10" as="geometry"/>
</mxCell>
```

Only if all three fail — and the face an edge leaves genuinely carries meaning — fan `exitY` values at least 0.15 apart (`0.35 / 0.5 / 0.65`). Hand-written waypoints remain a last resort; see the documented exceptions in [xml-authoring-rules.md](xml-authoring-rules.md).

### Observability zone placement

```
WRONG layout:
  ┌─────────────────────────────────────────────┐
  │ VNet                                        │
  │  ┌──────────────────┐  ┌─────────────────┐  │
  │  │ snet-ingress     │  │ snet-mgmt       │  │
  │  │  [GW]  [Hub Svc] │  │  [KV] [DNS]     │  │
  │  └──────────────────┘  └─────────────────┘  │
  │  [Monitor]  ← WRONG: Monitor inside VNet     │
  └─────────────────────────────────────────────┘

CORRECT layout:
  ┌─────────────────────────────────────────────┐   [Monitor] ──→ [Log Analytics] ──→ [Sentinel]
  │ VNet                                        │       ↑
  │  ┌──────────────────┐  ┌─────────────────┐  │   dashed telemetry edge exits VNet boundary
  │  │ snet-ingress     │  │ snet-mgmt       │  │
  │  │  [GW]  [Hub Svc] │  │  [KV] [DNS]     │  │
  │  └──────────────────┘  └─────────────────┘  │
  └─────────────────────────────────────────────┘
```

Azure Monitor, Log Analytics Workspace, and Microsoft Sentinel are **not VNet resources**. They must be positioned in a zone outside/right of the VNet boundary. The telemetry edge (dashed) crosses the VNet boundary — that's correct and communicates the service model.

### AWS: CloudWatch placed inside a VPC/subnet

CloudWatch, AWS Config, CloudTrail, and similar managed observability services are **regional services**, not VPC-resident resources. Place them outside the VPC boundary in an "AWS Managed Services" or "Observability" zone.

```
WRONG layout:
  ┌─────────────────────────────────────────────┐
  │ VPC                                         │
  │  ┌──────────────────┐                       │
  │  │ Private Subnet   │                       │
  │  │  [EC2]  [RDS]    │                       │
  │  └──────────────────┘                       │
  │  [CloudWatch]  ← WRONG: CloudWatch inside VPC│
  └─────────────────────────────────────────────┘

CORRECT layout:
  ┌─────────────────────────────────────────────┐   [CloudWatch] ──→ [S3 Logs] ──→ [Athena]
  │ VPC                                         │       ↑
  │  ┌──────────────────┐                       │   dashed telemetry edge exits VPC boundary
  │  │ Private Subnet   │                       │
  │  │  [EC2]  [RDS]    │                       │
  │  └──────────────────┘                       │
  └─────────────────────────────────────────────┘
```

### AWS: Wrong icon style for AWS4 shapes

AWS4 icons are stencil-based and must **not** be referenced as SVG image paths.

Bad — AWS4 has no SVG files at `img/lib/aws4/`:

```xml
<mxCell style="image;aspect=fixed;html=1;image=img/lib/aws4/compute/Lambda.svg;" vertex="1" parent="1">
  <mxGeometry x="300" y="400" width="64" height="64" as="geometry"/>
</mxCell>
```

Good — stencil shape notation:

```xml
<mxCell style="shape=mxgraph.aws4.lambda;fillColor=#ED7100;fontColor=#ffffff;strokeColor=none;html=1;" vertex="1">
  <mxGeometry x="300" y="400" width="64" height="64" as="geometry"/>
</mxCell>
```

Always confirm the exact AWS4 style string via `drawio/search_shapes` before adding an AWS icon.

### Decorative icon positioning

Icons such as the Virtual Network icon (`networking/Virtual_Networks.svg`) used as a visual label companion should be anchored to a fixed corner of their parent container — typically top-right. Without anchoring, draw.io renders them at the computed top-left of the container where they land on top of subnet boxes or service icons.

Anchor to the top-right of the region/VNet container — set `x` to `container_x + container_width - icon_width - 20` for a 20px margin:

```xml
<mxCell id="8" value="Virtual Network"
  style="image;aspect=fixed;...;image=img/lib/azure2/networking/Virtual_Networks.svg;"
  vertex="1" parent="1">
  <mxGeometry x="1360" y="180" width="140" height="90" as="geometry"/>
</mxCell>
```

### Single-line XML — how to avoid

When generating `mxGraphModel` XML, always emit one `mxCell` per line with child elements indented:

Bad — impossible to patch, and `xmllint` errors all point at char 0:

```xml
<mxCell id="0"/><mxCell id="1" parent="0"/><mxCell id="2" value="Title" style="rounded=1;html=1;" vertex="1" parent="1"><mxGeometry x="30" y="20" width="1940" height="70" as="geometry"/></mxCell>
```

Good — each element independently patchable:

```xml
<mxCell id="0"/>
<mxCell id="1" parent="0"/>
<mxCell id="2" value="Title" style="..." vertex="1" parent="1">
  <mxGeometry x="30" y="20" width="1940" height="70" as="geometry"/>
</mxCell>
```

---

## Quick Checklist Before Finalising Any Diagram

**General:**
- [ ] All sibling edges (same source → different targets) have **unique** labels
- [ ] 3+ edges from same node face have spread `exitX` values (≥0.15 gap) + waypoints
- [ ] Edge labels are offset using `mxGeometry x`/`y` when edges share a path segment
- [ ] At most 2 dashed cross-zone lines (one security/secrets, one telemetry)
- [ ] XML is indented (one `mxCell` per line) — not minified
- [ ] `xmllint --noout <file>` returns no errors

**Azure-specific:**
- [ ] Monitor / Log Analytics / Sentinel are **outside** any VNet or subnet container
- [ ] Decorative network/resource icons are corner-anchored, not free-floating
- [ ] Azure icons use `image=img/lib/azure2/...` style (not `shape=mxgraph.*`)

**AWS-specific:**
- [ ] CloudWatch / CloudTrail / Config are **outside** any VPC or subnet container
- [ ] AWS icons use `shape=mxgraph.aws4.<name>` style (not `image=img/lib/aws4/...`)
- [ ] Shape style strings confirmed via `drawio/search_shapes` before use
- [ ] fillColor matches AWS service category colour conventions

## Pre-Flight Layout Checklist

Before finalising any diagram, run through these checks:

- [ ] No overlapping nodes or labels
- [ ] No edges passing through unrelated shapes (use `routing: "libavoid"` if needed)
- [ ] Labels are readable and not clipped
- [ ] One icon per major service; no icon-per-step clutter
- [ ] Edge colours consistently encode meaning (request, response, error, async, token)
- [ ] Containers/swimlanes clearly group related elements, with children parented to their container (not stacked on top of it)
- [ ] Cross-container edges declared at `parent="1"`
- [ ] Canvas size fits content without excessive whitespace
- [ ] Animation (`flowAnimation=1;`) only applied where the user requested it
- [ ] For topology diagrams: legend and isolation explanation boxes present
- [ ] For sequence/flow diagrams: steps numbered, error paths shown, actors labelled
