// Requirements, concerns, satisfaction, derivation and verification.
package VehicleRequirements {
    private import ScalarValues::*;
    private import ISQ::*;
    private import SI::*;
    private import VerificationCases::*;

    part def Vehicle {
        attribute mass : MassValue;
        attribute fuelEconomy : Real;
    }
    part def Engine;
    part def Driver;

    requirement def MassRequirement {
        doc /* The actual mass shall be less than the required mass. */
        attribute massRequired : MassValue;
        attribute massActual : MassValue;
        require constraint { massActual <= massRequired }
    }

    requirement def <'R1'> VehicleMass {
        doc /* The vehicle mass shall not exceed 2000 kg. */
        subject vehicle : Vehicle;
        attribute massLimit : MassValue = 2000 [kg];
        assume constraint fuelFull { vehicle.fuelEconomy > 0 }
        require constraint { vehicle.mass <= massLimit }
    }

    requirement def <'R2'> Braking {
        doc /* The vehicle shall stop from 100 km/h within 40 m. */
        subject vehicle : Vehicle;
    }

    requirement def <'R2.1'> BrakingHardware {
        doc /* The brake system shall use ABS. */
    }

    concern def Safety {
        doc /* Occupants shall be protected. */
        stakeholder driver : Driver;
    }

    requirement def SafetyGoal {
        frame concern Safety;
        subject vehicle : Vehicle;
    }

    requirement vehicleSpecification : VehicleMass {
        doc /* Instance of the mass requirement. */
    }

    #derivation dependency from BrakingHardware to Braking;
    #refinement dependency from MassRequirement to VehicleMass;
    dependency from Engine to Vehicle;

    satisfy VehicleMass by Vehicle;
    satisfy Braking by Engine;

    verification def VehicleMassTest {
        subject testVehicle : Vehicle;
        objective {
            verify VehicleMass;
        }
        action weighVehicle {
            out massMeasured : MassValue;
        }
        then action evaluate {
            in massMeasured : MassValue = weighVehicle.massMeasured;
            out verdict : VerdictKind = PassIf(massMeasured <= 2000 [kg]);
        }
        return verdict : VerdictKind = evaluate.verdict;
    }

    verification def BrakingTest {
        objective brakingObjective {
            doc /* Check stopping distance. */
            verify Braking;
        }
        @VerificationMethod { kind = VerificationMethodKind::test; }
    }
}
