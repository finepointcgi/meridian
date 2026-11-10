// Views, viewpoints, renderings, metadata definitions and annotations, filtered imports.
package VehicleViews {
    private import ScalarValues::*;
    private import Views::*;
    private import ModelingMetadata::*;

    metadata def Safety {
        doc /* Marks safety-relevant elements. */
        attribute isMandatory : Boolean;
    }

    metadata def Security;

    package VehicleModel {
        part def Vehicle {
            @Safety { isMandatory = true; }
            part interior : Interior;
            part bodyAssy : BodyAssembly;
        }
        part def Interior {
            #Safety part seatBelt[2];
            #Security part alarm;
            part frontSeat[2];
        }
        part def BodyAssembly {
            @Safety { isMandatory = false; }
            @Rationale { text = "Crash structure"; }
            part bumper;
        }
        #Safety part def Airbag;
    }

    package SafetyFeatures {
        import VehicleModel::**[@Safety];
    }

    package MandatorySafety {
        import VehicleModel::**[@Safety and Safety::isMandatory];
    }

    viewpoint def SafetyViewpoint {
        frame concern safetyConcern;
    }

    view def SafetyView {
        satisfy SafetyViewpoint;
        filter @Safety;
        render asTreeDiagram;
    }

    view safetyView : SafetyView {
        expose VehicleModel::**;
    }

    rendering def CustomTable :> TabularRendering;

    metadata issue : Issue about VehicleModel::Vehicle {
        text = "Mass budget not closed yet.";
    }

    comment about VehicleModel::Airbag /* Supplier still to be selected. */

    part def Telemetry {
        doc /* A documented element with a textual representation. */
        rep inOCL language "ocl" /* self.rate > 0 */
        language "naturalLanguage" /* Telemetry is sent once per second. */
    }
}
