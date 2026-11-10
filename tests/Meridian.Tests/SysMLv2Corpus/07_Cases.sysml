// Use cases, analysis cases and a trade study.
package VehicleCases {
    private import ScalarValues::*;
    private import ISQ::*;
    private import SI::*;
    private import TradeStudies::*;

    part def Vehicle {
        attribute mass : MassValue;
        attribute fuelEconomy : Real;
    }
    part def Driver;
    part def Passenger;
    part def Engine {
        attribute power : PowerValue;
        attribute mass : MassValue;
    }

    use case def StartVehicle {
        subject vehicle : Vehicle;
        actor driver : Driver;
    }

    use case def ProvideTransportation {
        doc /* Get the driver and passengers to their destination. */
        subject vehicle : Vehicle;
        actor driver : Driver;
        actor passengers : Passenger[0..4];
        objective {
            doc /* Arrive safely. */
        }
        include use case StartVehicle;
        include use case enter : EnterVehicle;
        first start;
        then include use case drive : DriveVehicle;
        then done;
    }

    use case def EnterVehicle;
    use case def DriveVehicle;

    analysis def FuelEconomyAnalysis {
        subject vehicle : Vehicle;
        objective fuelEconomyAnalysisObjective {
            doc /* Estimate the fuel economy on a standard cycle. */
            require constraint { fuelEconomyResult > 30 }
        }
        in attribute scenario : Real[*];
        return fuelEconomyResult : Real = vehicle.fuelEconomy;
    }

    analysis case def MassAnalysis {
        subject vehicle : Vehicle;
        return totalMass : MassValue = vehicle.mass;
    }

    analysis def EngineTradeStudy :> TradeStudy {
        subject engines : Engine[2..*];
        objective : MaximizeObjective;
        calc evaluationFunction {
            in part engine : Engine;
            return : Real = engine.power / engine.mass;
        }
        return part selectedEngine : Engine;
    }
}
