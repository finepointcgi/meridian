// Ports with directions, conjugation, interfaces, connections, flows, bindings and allocation.
package PowerTrain {
    private import ScalarValues::*;
    private import ISQ::*;
    private import SI::*;

    item def Fuel;
    item def ElectricalPower;
    attribute def FuelCmd {
        attribute throttle : Real;
    }

    port def FuelOutPort {
        out item fuelSupply : Fuel;
        in item fuelReturn : Fuel;
    }

    port def PowerPort {
        out item power : ElectricalPower;
        attribute voltage : ElectricPotentialValue = 12 [V];
    }

    interface def FuelInterface {
        doc /* Connects a fuel tank to an engine. */
        end port supplierPort : FuelOutPort;
        end port consumerPort : ~FuelOutPort;
    }

    connection def PowerConnection {
        end part source : Battery;
        end part sink : Starter;
    }

    flow def FuelFlow;

    part def FuelTank {
        attribute capacity : VolumeValue = 60 [L];
        port fuelOut : FuelOutPort;
    }

    part def Engine {
        port fuelIn : ~FuelOutPort;
        attribute fuelCmd : FuelCmd;
    }

    part def Battery {
        port powerOut : PowerPort;
        attribute charge : ElectricChargeValue = 60 [A*h];
    }

    part def Starter {
        port powerIn : ~PowerPort;
    }

    part def PowerSystem {
        part tank : FuelTank;
        part engine : Engine;
        part battery : Battery;
        part starter : Starter;
        attribute throttleSetting : FuelCmd;

        interface fuelLine : FuelInterface connect tank.fuelOut to engine.fuelIn;
        connection cable connect battery.powerOut to starter.powerIn;
        connect battery.powerOut to starter.powerIn;
        flow of Fuel from tank.fuelOut to engine.fuelIn;
        flow electricity of ElectricalPower from battery.powerOut to starter.powerIn;
        bind throttleSetting = engine.fuelCmd;
    }

    part def LogicalArchitecture;
    part def PhysicalArchitecture;
    allocate LogicalArchitecture to PhysicalArchitecture;
    allocation def SoftwareToHardware;
}
