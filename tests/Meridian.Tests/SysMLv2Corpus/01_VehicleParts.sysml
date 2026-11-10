// Parts, attributes with units, ports, redefinition, subsetting and multiplicities.
package VehicleParts {
    private import ScalarValues::*;
    private import ISQ::*;
    private import SI::*;
    public import Definitions::**;
    alias Torque for ISQ::TorqueValue;

    doc /* A small vehicle structure in the style of the SysML v2 training material. */

    package Definitions {
        enum def DriveKind {
            doc /* How the vehicle is driven. */
            enum front;
            enum rear;
            enum allWheel;
        }

        enum def Level :> Integer {
            low = 1;
            medium = 2;
            high = 3;
        }

        attribute def FuelLevel :> Real;

        part def Vehicle {
            attribute mass : MassValue = 1450 [kg];
            attribute dryMass : MassValue default 1300 [kg];
            attribute cargoMass : MassValue := 0 [kg];
            attribute maxSpeed : SpeedValue = 180 [km/h];
            attribute drive : DriveKind = DriveKind::rear;
            readonly attribute vin : String;
            derived attribute totalMass : MassValue;
            ref part driver : Driver[0..1];
            part eng : Engine;
            part trans : Transmission;
            part wheels : Wheel[4] ordered;
            part frontWheels : Wheel[2] :> wheels;
            port fuelIn : ~FuelPort;
            port ctrl : ControlPort[1..*] nonunique;
        }

        abstract part def Engine {
            attribute power : PowerValue;
            attribute displacement : VolumeValue[0..1];
            attribute cylinders : Natural[1] = 4;
            port fuelIn : FuelPort;
            port torqueOut : TorquePort;
        }

        part def SixCylinderEngine :> Engine {
            attribute :>> cylinders = 6;
            attribute :>> power = 220 [kW];
        }

        part def Transmission {
            attribute ratios : Real[*] = (3.5, 2.1, 1.4, 1.0, 0.8);
            port torqueIn : ~TorquePort;
        }

        part def Wheel {
            attribute diameter : LengthValue = 18 * 25.4 [mm];
            attribute pressure : PressureValue = 2.4 [bar];
        }

        part def Driver;

        port def FuelPort {
            out item fuel : Fuel;
            in attribute fuelLevel : FuelLevel;
        }

        port def TorquePort {
            out attribute torque : Torque;
        }

        port def ControlPort {
            inout attribute command : String;
        }

        item def Fuel {
            attribute density : DensityValue = 0.745 [kg/L];
        }
    }

    part vehicle : Vehicle {
        doc /* The configured vehicle. */
        part :>> eng : SixCylinderEngine;
        attribute :>> mass = 1520 [kg];
    }

    comment Note about vehicle /* The vehicle is the system of interest. */
}
