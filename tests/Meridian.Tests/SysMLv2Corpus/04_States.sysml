// State definitions: entry / do / exit, transitions with triggers, guards and effects, parallel regions, exhibit.
package VehicleStates {
    private import ScalarValues::*;
    private import ISQ::*;
    private import SI::*;

    attribute def VehicleStartSignal;
    attribute def VehicleOnSignal;
    attribute def VehicleOffSignal;
    attribute def OverTemp;
    port def ControlPort;

    action def SenseTemperature {
        out temp : ThermodynamicTemperatureValue;
    }

    action def PerformSelfTest;
    action def ProvidePower;

    state def VehicleStates {
        doc /* The operating states of the vehicle. */
        entry; then off;

        state off;
        accept VehicleStartSignal then starting;

        state starting {
            entry action selfTest : PerformSelfTest;
        }
        transition starting_to_on
            first starting
            accept VehicleOnSignal
            if true
            then on;

        state on {
            entry action warmUp;
            do action providePower : ProvidePower;
            exit action coolDown;
        }
        transition on_to_off
            first on
            accept VehicleOffSignal
            do send VehicleOffSignal() to controller
            then off;
        transition first on accept after 30 [min] then off;
    }

    state def HealthStates {
        entry; then normal;
        state normal;
        state maintenance;
        state degraded;
        transition normal_to_maintenance first normal accept at maintenanceTime then maintenance;
        transition normal_to_degraded first normal accept when senseTemperature.temp > maxTemperature then degraded;
        transition degraded_to_normal first degraded accept OverTemp via ctrl then normal;
    }

    state def OperatingStates parallel {
        state power {
            entry; then idle;
            state idle;
            state running;
        }
        state comms {
            entry; then offline;
            state offline;
            state online;
        }
    }

    part def Vehicle {
        attribute maintenanceTime : Time::TimeInstantValue;
        attribute maxTemperature : ThermodynamicTemperatureValue = 120 ['°C'];
        port ctrl : ControlPort;
        part controller;
        action senseTemperature : SenseTemperature;
        exhibit state vehicleStates : VehicleStates;
        exhibit state healthStates : HealthStates;
    }
}
