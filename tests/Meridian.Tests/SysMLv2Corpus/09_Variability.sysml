// Feature modifiers, keyword relationship forms, variation points and references.
package VehicleFamily {
    private import ScalarValues::*;
    private import ISQ::*;
    private import SI::*;
    private import Parts::*;

    part def Engine specializes Part;
    part def FourCylinderEngine specializes Engine {
        attribute cylinders : Integer = 4;
    }
    part def SixCylinderEngine specializes Engine {
        attribute cylinders : Integer = 6;
    }
    part def Transmission;
    part def ManualTransmission :> Transmission;
    part def AutomaticTransmission :> Transmission;
    part def Wheel;

    variation part def EngineChoice :> Engine {
        variant part fourCyl : FourCylinderEngine;
        variant part sixCyl : SixCylinderEngine;
    }

    variation part def TransmissionChoice :> Transmission {
        variant part manual : ManualTransmission;
        variant part automatic : AutomaticTransmission;
    }

    abstract part def VehicleFamily {
        part engine defined by EngineChoice;
        part transmission typed by TransmissionChoice;
        part wheels : Wheel[4..*];
        part spare : Wheel[0..1] subsets wheels;
        ref part currentWheel : Wheel ::> wheels;
        attribute mass : MassValue;
        derived readonly attribute massPerWheel : MassValue;
        constant attribute wheelCount : Natural = 4;
        abstract attribute rating : Real;
        attribute ratingFactor : Real redefines rating;
        end part owner : Part[1];
        private attribute secret : String;
        protected attribute serviceCode : String;
    }

    part def SportsVehicle :> VehicleFamily {
        part redefines engine : SixCylinderEngine;
        attribute :>> mass = 1350 [kg];
        attribute :>> rating = 9.5;
    }

    individual part def Vehicle_1 :> SportsVehicle;

    part vehicleFamily : VehicleFamily[1..*];
}
