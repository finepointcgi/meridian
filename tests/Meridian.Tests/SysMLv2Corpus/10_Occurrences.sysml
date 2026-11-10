// Occurrences, individuals, snapshots, time slices, events and messages.
package VehicleLife {
    private import ScalarValues::*;
    private import ISQ::*;
    private import SI::*;
    private import Time::*;

    attribute def StartCommand;
    attribute def Ack;

    part def Vehicle {
        attribute mass : MassValue;
        attribute odometer : LengthValue;
        part engine : Engine;
    }
    part def Engine;
    part def Driver;

    individual part def Vehicle_1 :> Vehicle;
    individual part def Engine_1 :> Engine;

    individual part vehicle1 : Vehicle_1 {
        snapshot vehicle1_t0 {
            attribute :>> mass = 1500 [kg];
            attribute :>> odometer = 0 [km];
        }
        timeslice vehicle1_drive {
            snapshot start_t : Vehicle_1;
            snapshot end_t : Vehicle_1;
        }
        timeslice vehicle1_parked;
    }

    occurrence def MaintenanceVisit {
        attribute date : TimeInstantValue;
        event occurrence arrival;
        event occurrence departure;
    }

    occurrence def StartSequence {
        part driver : Driver;
        part vehicle : Vehicle;
        message startCmd from driver to vehicle;
        #reply message ack from vehicle to driver;
        message of StartCommand from driver to vehicle;
    }

    occurrence maintenance : MaintenanceVisit;
}
