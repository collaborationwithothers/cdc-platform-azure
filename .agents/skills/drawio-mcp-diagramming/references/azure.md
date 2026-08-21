# Azure Diagram Guidance

Everything Azure-specific for draw.io MCP diagrams: icon libraries and their caveats, VNet/subnet container structure, colour conventions, and the topology checklist.

Read this file when the diagram contains Azure services — whether it is a full VNet topology or a handful of Azure icons in a flow diagram.

Contents:
- [Azure icon libraries and caveats](#azure-icon-libraries-and-caveats)
- [Known-good azure2 icon paths](#known-good-azure2-icon-paths)
- [VNet and subnet container structure](#vnet-and-subnet-container-structure)
- [Styles and colours](#styles-and-colours)
- [Traffic flow colour palette](#traffic-flow-colour-palette)
- [Annotation boxes](#annotation-boxes)
- [Complete topology example](#complete-topology-example)
- [Azure topology checklist](#azure-topology-checklist)

---

## Azure icon libraries and caveats

Azure icons come from **two separate libraries**. `drawio/search_shapes` returns icons from either — use the style string exactly as returned.

### azure2 (most Azure services)

Path pattern: `image=img/lib/azure2/<category>/<Icon_Name>.svg`

- Prefer the `image;aspect=fixed;...` style form. `shape=mxgraph.azure2.*` does not render in some hosts.
- Some embedded viewers do not resolve `img/lib/azure2/...` consistently — if icons are missing, test the same XML in `app.diagrams.net` before assuming the style string is wrong. (The hosted App Server rewrites relative `img/lib/...` paths to absolute `https://app.diagrams.net/img/lib/...` URLs for you, so relative paths are safe to submit.)
- Absolute URLs also work when a renderer is unreliable:
  `image=https://raw.githubusercontent.com/jgraph/drawio/dev/src/main/webapp/img/lib/azure2/networking/Application_Gateways.svg`

### mscae (Sentinel, DNS Private Zones, some older icons)

Path pattern: `image=img/lib/mscae/<Icon_Name>.svg`

- These **must include `sketch=0`** in the style, or the shape renders with a hand-drawn sketch effect.
- Correct prefix: `image;sketch=0;aspect=fixed;html=1;points=[];align=center;...`
- If `search_shapes` returns a style containing `sketch=0`, the icon is from mscae — preserve that attribute.

### If an Azure icon still fails

Do not generate the diagram with an unresolved icon. Re-query `search_shapes` (raise `limit`, up to 50), propose verified replacements, and generate only once every style string is confirmed.

---

## Known-good azure2 icon paths

Confirm via `search_shapes` before use — these are a starting point, not a substitute for lookup.

```text
image=img/lib/azure2/networking/Front_Doors.svg
image=img/lib/azure2/networking/Private_Link_Hub.svg
image=img/lib/azure2/networking/Network_Watcher.svg
image=img/lib/azure2/app_services/API_Management_Services.svg
image=img/lib/azure2/app_services/App_Services.svg
image=img/lib/azure2/databases/Azure_Cosmos_DB.svg
image=img/lib/azure2/identity/Managed_Identities.svg
image=img/lib/azure2/management_governance/Policy.svg
image=img/lib/azure2/analytics/Log_Analytics_Workspaces.svg
image=img/lib/azure2/management_governance/Monitor.svg
image=img/lib/azure2/devops/Application_Insights.svg
image=img/lib/azure2/devops/API_Connections.svg
```

---

## VNet and subnet container structure

Model the hierarchy with real containment, not stacked rectangles — see [xml-authoring-rules.md](xml-authoring-rules.md) for the general rule and why it matters.

- Each VNet is a `swimlane;startSize=24;` at `parent="1"`.
- Each subnet is a `swimlane;startSize=24;` with `parent="<vnet_id>"` and geometry **relative to the VNet** (start at `y=40` to clear the title bar).
- Each resource icon is parented to its subnet, with coordinates relative to the subnet.
- Edges between resources in different subnets or VNets sit at `parent="1"`.
- Peering and external-services zones are separate top-level containers, not overlapping rectangles.

---

## Styles and colours

| Element | Style |
|---|---|
| VNet (Perimeter / Ingress) | `swimlane;startSize=24;fillColor=#fff2cc;strokeColor=#d6b656;strokeWidth=4;` |
| VNet (Internal) | `swimlane;startSize=24;fillColor=#d5e8d4;strokeColor=#82b366;strokeWidth=4;` |
| VNet (Management) | `swimlane;startSize=24;fillColor=#dae8fc;strokeColor=#6c8ebf;strokeWidth=4;` |
| Subnet | `swimlane;startSize=24;dashed=1;dashPattern=8 8;strokeWidth=2;` in a lighter shade of the parent VNet colour |
| VNet Peering zone | `swimlane;startSize=24;fillColor=#f5f5f5;strokeColor=#666666;strokeWidth=2;` |
| External Services zone | `swimlane;startSize=24;fillColor=#ffe6cc;strokeColor=#d79b00;strokeWidth=2;` |

Label subnets with name + CIDR. For delegated subnets add the delegation to the label, e.g. `PostgreSQL Subnet - 10.0.4.0/24 (Delegated to Microsoft.DBforPostgreSQL/flexibleServers)`. Only use a DMZ/perimeter box when the diagram explicitly models a real perimeter subnet, firewall, or NVA; otherwise keep the edge layer clean and omit it.

Canvas: `pageWidth="1900" pageHeight="1500"` for complex multi-VNet diagrams.

---

## Traffic flow colour palette

| Traffic type | Colour | Style |
|---|---|---|
| HTTPS:443 internet ingress | `#0078D4` Azure blue | solid, `strokeWidth=3` |
| HTTP:80/8080 backend | `#00897B` Teal | solid, `strokeWidth=2` |
| Database (PostgreSQL:5432) | `#5C6BC0` Indigo | dashed, `strokeWidth=2` |
| Storage (NFS/Gluster) | `#43A047` Green | solid, `strokeWidth=2` |
| Management/Identity | `#F57C00` Amber | dashed, `strokeWidth=2` |
| Denied/blocked | `#C62828` Red | solid — reserve exclusively for blocked traffic |

Label every flow with protocol and port.

---

## Annotation boxes

1. **Network Isolation Explanation** — top-left, `fillColor=#fff9cc`: VNet/subnet conventions, NSG and Private DNS notes. Add it when the topology needs the explanation, not by default.
2. **Zone Separation** — VNet Peering zone and External Services zone as distinct containers when they add useful separation.

---

## Complete topology example

Nested containers, relative child coordinates, cross-container edges at `parent="1"`, and `as="geometry"` on every geometry element.

```xml
<mxGraphModel pageWidth="1900" pageHeight="1500">
  <root>
    <mxCell id="0"/>
    <mxCell id="1" parent="0"/>
    <mxCell id="iso" value="&lt;b&gt;Network Isolation&lt;/b&gt;&lt;br&gt;VNets: thick borders&lt;br&gt;Subnets: dashed borders&lt;br&gt;NSGs control traffic&lt;br&gt;Private DNS for internal resolution" style="text;html=1;strokeColor=#d6b656;fillColor=#fff9cc;align=left;verticalAlign=top;spacingLeft=10;rounded=1;" vertex="1" parent="1">
      <mxGeometry x="40" y="40" width="240" height="110" as="geometry"/>
    </mxCell>
    <mxCell id="vnet" value="Internal VNet - 10.0.0.0/16" style="swimlane;startSize=24;html=1;fillColor=#d5e8d4;strokeColor=#82b366;strokeWidth=4;fontSize=16;fontStyle=1;" vertex="1" parent="1">
      <mxGeometry x="340" y="200" width="900" height="480" as="geometry"/>
    </mxCell>
    <mxCell id="snet-app" value="Application Subnet - 10.0.2.0/24" style="swimlane;startSize=24;html=1;fillColor=#e6f4ea;strokeColor=#82b366;strokeWidth=2;dashed=1;dashPattern=8 8;fontSize=13;fontStyle=1;" vertex="1" parent="vnet">
      <mxGeometry x="40" y="50" width="380" height="380" as="geometry"/>
    </mxCell>
    <mxCell id="vm" value="App VM" style="image;aspect=fixed;html=1;points=[];align=center;verticalLabelPosition=bottom;verticalAlign=top;image=img/lib/azure2/compute/Virtual_Machine.svg;" vertex="1" parent="snet-app">
      <mxGeometry x="60" y="60" width="64" height="59" as="geometry"/>
    </mxCell>
    <mxCell id="snet-db" value="PostgreSQL Subnet - 10.0.4.0/24 (Delegated to Microsoft.DBforPostgreSQL/flexibleServers)" style="swimlane;startSize=24;html=1;fillColor=#e6f4ea;strokeColor=#82b366;strokeWidth=2;dashed=1;dashPattern=8 8;fontSize=13;fontStyle=1;" vertex="1" parent="vnet">
      <mxGeometry x="460" y="50" width="380" height="380" as="geometry"/>
    </mxCell>
    <mxCell id="pg" value="PostgreSQL Flexible Server" style="image;aspect=fixed;html=1;points=[];align=center;verticalLabelPosition=bottom;verticalAlign=top;image=img/lib/azure2/databases/Azure_Database_PostgreSQL_Server.svg;" vertex="1" parent="snet-db">
      <mxGeometry x="60" y="60" width="64" height="59" as="geometry"/>
    </mxCell>
    <mxCell id="e-vm-pg" value="PostgreSQL:5432" style="edgeStyle=orthogonalEdgeStyle;rounded=1;html=1;strokeColor=#5C6BC0;strokeWidth=2;dashed=1;" edge="1" parent="1" source="vm" target="pg">
      <mxGeometry relative="1" as="geometry"/>
    </mxCell>
  </root>
</mxGraphModel>
```

Generate with `routing: "libavoid"` so connectors route around the containers without moving them.

---

## Azure topology checklist

- [ ] VNets are containers with thick borders (`strokeWidth=4`), colour-coded by purpose
- [ ] Subnets are containers parented to their VNet, dashed (`strokeWidth=2;dashPattern=8 8`), labelled with CIDR
- [ ] Every resource is parented to its subnet with relative coordinates
- [ ] Cross-subnet and cross-VNet edges declared at `parent="1"`
- [ ] Traffic arrows labelled with protocols and ports, using the palette above
- [ ] Network isolation explanation box included only when it adds clarity
- [ ] VNet peering and external services in separate zone containers when they help explain the topology
- [ ] Canvas 1900×1500 for complex infrastructure
- [ ] `routing: "libavoid"` applied; no hand-written waypoints or exit/entry points
- [ ] Animation preference confirmed before generating
