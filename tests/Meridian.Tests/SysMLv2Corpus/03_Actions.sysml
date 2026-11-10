// Action definitions and action bodies: parameters, successions, control nodes and action-body statements.
package VehicleActions {
    private import ScalarValues::*;
    private import ISQ::*;
    private import SI::*;

    attribute def Scene;
    attribute def Image;
    attribute def Picture;
    attribute def StartSignal;
    attribute def StopSignal;
    attribute def BrakeCommand {
        attribute force : ForceValue;
    }
    port def CommandPort;

    action def Focus {
        in scene : Scene;
        out image : Image;
    }

    action def Shoot {
        in image : Image;
        out picture : Picture;
    }

    action def TakePicture {
        doc /* Focus, then shoot. */
        in scene : Scene;
        out picture : Picture;

        action focus : Focus;
        flow focus.image to shoot.image;
        first focus then shoot;
        action shoot : Shoot;
    }

    action def ProvidePower {
        in cmd : BrakeCommand;
        out torque : TorqueValue;
    }

    action def Drive {
        attribute speed : SpeedValue;
        attribute counter : Integer := 0;

        first start;
        then action checkSystems;
        then decide checkResult;
            if speed > 0 [km/h] then accelerate;
            else idle;
        action accelerate;
        action idle;
        then merge joined;
        then fork split;
            then steer;
            then brake;
        action steer;
        action brake;
        first steer then sync;
        first brake then sync;
        join sync;
        then done;
    }

    action def Operate {
        in port cmdPort : CommandPort;

        first start;
        then accept startSig : StartSignal via cmdPort;
        then action warmUp;
        then send StopSignal() via cmdPort;
        then assign counter := counter + 1;
        then if counter > 3 {
            action report;
        } else {
            action retry;
        }
        then while counter < 10 {
            assign counter := counter + 1;
        } until counter >= 10;
        then for i in (1, 2, 3) {
            action step;
        }
        then action trigger accept stopSig : StopSignal;
        then terminate;
        attribute counter : Integer = 0;
    }

    action def Monitor {
        loop action poll {
            action read;
        }
    }

    part def Vehicle {
        perform action providePower : ProvidePower;
        perform action drive : Drive;
        perform Operate;
    }
}
