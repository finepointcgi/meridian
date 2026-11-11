# Deployment diagrams

A UML deployment diagram shows where the system runs: the **nodes** (servers, computers), **devices** (boards,
controllers, phones) and **execution environments** (a JVM, a container runtime, an RTOS) it runs on, the **artifacts**
(executables, libraries, configuration files, firmware images) deployed on them, and the **communication paths** between
nodes. Artifacts **manifest** the design: an artifact `orders-service.jar` manifests the `OrderService` component, so
you can trace from a requirement to the block that satisfies it and on to the machine that runs it.

## Create one

⌘K ▸ **New Deployment Diagram**, or right-click a package ▸ **New diagram ▸ Deployment Diagram**. The diagram's header
is `deploy`. The shapes panel offers:

| Shape | Notation | Use it for |
| --- | --- | --- |
| **Node** | 3D box | a computational resource: a server, a VM, a vehicle computer |
| **Device** | 3D box with «device» | physical hardware: a board, a controller, a sensor hub |
| **Execution Environment** | 3D box with «executionEnvironment» | software that hosts other software: a JVM, Kubernetes, an RTOS |
| **Artifact** | box with «artifact» and the document icon | a file that is deployed: `.jar`, `.dll`, `.hex`, a container image, a config file |
| **Deployment Specification** | box with «deployment spec» | the parameters of a deployment: install location, execution environment |
| Component, Package | as on other diagrams | what artifacts manifest; databases and schemas (packages) can be deployed too |

Nodes, devices and execution environments **nest**: click inside a device with the *Execution Environment* tool and
the new environment is created inside it (and owned by it), so a device holding a JVM holding a container is three
boxes inside each other. Give a node a stereotype such as «server» or «container» in the Inspector, and its capacities
as value properties (for example `cores = 8` and `ramGb = 32`): they are listed in the node's box.

## Deploy, manifest and connect

Draw a line between two shapes (hover a shape and drag one of its arrows onto another) and Meridian picks the
connector from what you connected:

| From → to | Connector |
| --- | --- |
| artifact → node, device or execution environment | **«deploy»** |
| artifact → block, component, class, package or code element | **«manifest»** |
| node ↔ node (device, execution environment) | **communication path** (set its **Protocol** in the Inspector: HTTPS, AMQP, CAN …) |
| database or schema package → node | **«deploy»** (on a deployment diagram) |

The chip on the new line switches it to another valid kind in one click.

A deployment can be shown two ways, and both are the same `«deploy»` in the model:

- **as a line**: the artifact beside the node with a dashed «deploy» arrow;
- **by nesting**: create the artifact *inside* the node's box (click inside it with the *Artifact* tool). The artifact is
  deployed to that node and drawn in it; the «deploy» line is not drawn because the nesting already says it.

### Deploy to…

Right-click an artifact (on the canvas or in the model explorer), or a selection of several, ▸ **Deploy to…**, and pick
the node. Each artifact gets one «deploy», in one undo step; the lines are drawn when both ends are on the diagram. The
same command works on a **database** or **schema** from [Database modelling](database.md). The Inspector of an artifact
lists where it is deployed (with **Deploy to…** and a button to remove a deployment) and what it manifests; the
Inspector of a node lists what is deployed on it and its communication paths.

## Tables, matrices and checks

- **Deployment table**: ⌘K ▸ *Deployment table*, or **Model views ▸ Generic table** ▸ table menu ▸ *New deployment
  table*: every artifact with the **node** and **execution environment** it runs on, its file name and what it
  manifests. Save it like any [generic table](tables-queries.md). The column keys are `deploymentNode` and
  `deploymentEnvironment`; `deployedTo`, `deployedArtifacts`, `manifests` and `manifestedBy` work in any table.
- **Matrix**: **Traceability ▸ Matrix ▸ Deployment** shows artifacts × nodes; click an empty cell to deploy.
- **Validation**: *DEP-001* (warning) flags an artifact that is deployed nowhere (an artifact nested in a deployed
  artifact counts as deployed); *DEP-002* (info) flags a communication path between nodes that have nothing deployed.
- **Auto-diagram** draws a **Deployment overview** when the model has nodes: every node with its nested devices and
  environments, the artifacts nested in the nodes they are deployed to, and the communication paths.
- The **System Design Description** report template has a **Deployment view** section: the deployment diagrams, the
  deployment table (artifact, node, environment, manifests) and the communication paths with their protocols.

## Deploying code

[Code sync](code-sync.md) can keep an artifact for the code it syncs, so you can deploy it: in the repository's
connection settings, **Deployment artifacts** is *None* (the default), *One for the repository* (an artifact that
manifests the repository's package) or *One per top-level package*. The artifacts are kept up to date with the code
like the rest of the synced model. Deploy them with **Deploy to…** or a line on a deployment diagram; if the setting is
turned off later, an artifact you deployed is kept and marked removed rather than deleted.

## Import and export

| Format | Deployment content |
| --- | --- |
| `.meridian` | everything, exactly |
| XMI 2.5.1 | `uml:Node`, `uml:Device`, `uml:ExecutionEnvironment` (nested as `nestedNode`), `uml:Artifact` (with `fileName`), `uml:DeploymentSpecification`, `uml:Deployment`, `uml:Manifestation`, `uml:CommunicationPath`; the protocol in the Meridian extension |
| SysML v2 text | part definitions tagged `#node`, `#device`, `#executionEnvironment`, `#artifact`, `#deploymentSpec`; `#deploy`, `#manifest` and `#communicationPath` dependencies |
| Cameo / MagicDraw | the UML deployment metaclasses above, deployments nested in their node, and deployment diagrams |
| Enterprise Architect | `Node` (a «device» / «executionEnvironment» node becomes a device / environment), `Device`, `ExecutionEnvironment`, `Artifact`, `DeploymentSpecification`; `Deployment` and `Manifest` connectors (and «deploy» / «manifest» dependencies), communication paths, deployment diagrams |

File names, install locations and protocols are not part of SysML v2 text; they round-trip through `.meridian` and XMI.
