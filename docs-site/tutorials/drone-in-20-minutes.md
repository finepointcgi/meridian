# Model a drone in 20 minutes

In this tutorial you build a small but complete SysML model of a camera drone: requirements, structure, an internal
block diagram, a parametric endurance calculation, a state machine, satisfy and verify links, and a committed
version. It works the same in the desktop app and on a server.

**You will use:** the template gallery, the SysML v2 text editor, block definition, internal block, parametric,
state machine and requirement diagrams, the Parametrics panel, validation and the traceability table.

## 1. Create the project (1 min)

1. Open the project menu (the project name in the title bar) → **New project…**.
2. Pick **Blank project**, name it `Drone` and choose **Create project**.

The model explorer shows three packages, *Structure*, *Requirements* and *Behavior*, and the empty *System Structure*
block definition diagram is open.

> [!NOTE]
> **TODO (screenshot):** the new Drone project with the three packages in the model explorer.

## 2. Write the requirements as text (2 min)

1. Select the *Requirements* package in the model explorer.
2. Click **Generate → Edit as SysML** on the ribbon. The *SysML v2 text* tab opens with the (empty) package.
3. Replace the text with:

   ```sysml
   package Requirements {
       requirement def <'REQ-001'> 'Flight Time' {
           doc /* The drone shall hover for at least 15 minutes on one battery. */
       }
       requirement def <'REQ-002'> 'Takeoff Mass' {
           doc /* The takeoff mass shall not exceed 1.5 kg. */
       }
       requirement def <'REQ-003'> 'Aerial Imaging' {
           doc /* The drone shall record 4K video of the ground. */
       }
   }
   ```

4. Press **Apply** (or **⌘Enter** / **Ctrl+Enter**).

Three requirements appear under *Requirements*, each with its id and text. Select one and look at the inspector: you
can set priority, status and verification method there.

## 3. Type the structure (3 min)

1. Select the *Structure* package and choose the *Structure* scope in the SysML v2 text tab (or click **Edit as SysML**
   again).
2. Replace the text with:

   ```sysml
   package Structure {
       port def PowerPort;
       port def DataPort;
       part def Drone {
           attribute mass : ScalarValues::Real = 1.2;
           attribute batteryCapacity : ScalarValues::Real = 60;
           attribute hoverPower : ScalarValues::Real = 180;
           attribute endurance : ScalarValues::Real;
           attribute enduranceEq : Endurance;
           part frame : Frame;
           part propulsion : Propulsion;
           part battery : Battery;
           part flightController : FlightController;
           part camera : Camera;
           bind batteryCapacity = enduranceEq.capacity;
           bind hoverPower = enduranceEq.power;
           bind endurance = enduranceEq.minutes;
       }
       part def Frame;
       part def Battery {
           out port powerOut : PowerPort;
       }
       part def Propulsion {
           in port powerIn : PowerPort;
           in port command : DataPort;
       }
       part def FlightController {
           in port powerIn : PowerPort;
           out port command : DataPort;
       }
       part def Camera {
           in port powerIn : PowerPort;
       }
       constraint def Endurance {
           attribute capacity : ScalarValues::Real;
           attribute power : ScalarValues::Real;
           attribute minutes : ScalarValues::Real;
           minutes = capacity * 0.8 / power * 60
       }
   }
   ```

3. **Apply**.

You now have six blocks, two interface blocks for the ports, a constraint block with the endurance equation, value
properties (mass in kg, battery capacity in Wh, hover power in W) and binding connectors. The whole edit is one undo
step: **⌘Z** would take it all back.

> [!TIP]
> Everything you typed is ordinary model content. You could have drawn it instead: blocks from the shapes panel,
> properties and ports with **Add feature** in the canvas menu, types in the inspector.

## 4. Draw the block definition diagram (3 min)

1. Open the *System Structure* diagram (double-click it in the model explorer).
2. Drag *Drone*, *Frame*, *Propulsion*, *Battery*, *FlightController* and *Camera* from the model explorer onto the
   canvas.
3. Open the shapes panel (**Show shapes**), pick **Composition**, and drag from *Drone* to *Frame*. Hold **Shift** while
   you release so the tool stays active, and draw compositions from *Drone* to the other four blocks. Press **Esc**.
4. Click **Arrange** in the ribbon's *Diagram* group. *Drone* moves to the top, its parts underneath.

> [!NOTE]
> **TODO (screenshot):** the arranged drone BDD with the part and value compartments of *Drone*.

## 5. Wire the internal block diagram (3 min)

1. Select *Drone* and click **IBD** in the ribbon. The new internal block diagram already shows Drone's properties.
2. Select the value properties and the `enduranceEq` property on the IBD and press **Backspace** to remove them from
   this diagram (they stay in the model). Keep the five parts.
3. Right-click each part → **Show ports on border**.
4. Pick **Connector** in the shapes panel and drag from `battery`'s `powerOut` port to `propulsion`'s `powerIn` port.
   Draw connectors from `powerOut` to the `powerIn` ports of `flightController` and `camera` too, and from
   `flightController`'s `command` port to `propulsion`'s `command` port.
5. Select a power connector and set **Item flow (conveyed item)** in the inspector to `Power`.

## 6. Calculate the endurance (2 min)

1. With *Drone* selected, choose **Diagram → More → Parametric Diagram**.
2. Drag `batteryCapacity`, `hoverPower`, `endurance` and `enduranceEq` from under *Drone* in the model explorer onto the
   diagram.
3. Right-click `enduranceEq` → **Show ports on border**. Its three parameters appear on its border and the binding
   connectors you typed are drawn (if one is missing, right-click the value property → **Display related elements**).
4. Click **Simulate → Parametrics**, choose the *Drone* block and press **Solve**.

`endurance` is computed as **16** minutes (60 Wh × 0.8 usable ÷ 180 W × 60). Try a 50 Wh battery: change
`batteryCapacity` in the inspector and solve again; endurance drops to 13.3 minutes and no longer meets REQ-001.
Set it back to 60.

> [!NOTE]
> **TODO (screenshot):** the parametric diagram next to the Parametrics panel result.

## 7. Describe the flight modes (3 min)

1. Select *Drone* and click **STM** in the ribbon. Meridian creates a *Drone States* state machine and its diagram.
2. From the shapes panel add an **Initial** state and four **States**: `Landed`, `Armed`, `Flying` and `Returning`.
   Type the name right after placing each one.
3. With the **Transition** tool, draw: initial → `Landed`; `Landed` → `Armed`; `Armed` → `Flying`; `Flying` →
   `Returning`; `Returning` → `Landed`; `Armed` → `Landed`.
4. Select each transition and set its name in the inspector; the name is its trigger: `arm`, `takeOff`,
   `lowBattery`, `landed` and `disarm` (the transition from the initial state needs no name).
5. Optional: **Simulate → Simulate → Timed**, pick *Drone States*, **Start**, then send the signals `arm`, `takeOff`,
   `lowBattery` and `landed` and watch the states change on the timeline.

## 8. Link requirements to the design (2 min)

1. Select the *Requirements* package and click **REQ** in the ribbon.
2. Drag the three requirements, *Drone* and *Camera* onto the diagram.
3. Pick **Satisfy** in the shapes panel and drag from *Drone* to *Flight Time* and to *Takeoff Mass*, and from *Camera*
   to *Aerial Imaging*.
4. Select the *Requirements* package and click **Requirements → Test case** three times. Rename the test cases
   `Hover Endurance Test`, `Weigh-in` and `Video Check`, drag them onto the diagram and draw **Verify** from each to
   its requirement.
5. Click **Arrange**.

Each requirement now shows *satisfiedBy* and *verifiedBy* compartments.

## 9. Check and commit (1 min)

1. Press **F7**. The *Validation* tab should show no errors or warnings.
2. Open **Traceability → Table**: every requirement is satisfied and verified.
3. Click **Commit**, type `First drone model`, and confirm. The version is now in **Version history**.

## Where next

- [Requirements to verification](requirements-to-verification.md) goes deeper into requirement management.
- [Your first trade study](first-trade-study.md) compares design alternatives.
- The *CubeSat mission* sample shows a richer version of everything you just built.
