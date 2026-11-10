// Calculations, constraints and the expression language.
package VehicleCalculations {
    private import ScalarValues::*;
    private import ISQ::*;
    private import SI::*;
    private import NumericalFunctions::*;

    calc def Power {
        in whlpwr : PowerValue;
        in Cd : Real;
        in Cf : Real;
        in tm : MassValue;
        in v : SpeedValue;
        return : PowerValue;
        attribute drag = Cd * v;
        attribute friction = Cf * tm * v;
        whlpwr - drag - friction
    }

    calc def Acceleration {
        in tp : PowerValue;
        in tm : MassValue;
        in v : SpeedValue;
        return : AccelerationValue = tp / (tm * v);
    }

    calc def Velocity {
        in dt : DurationValue;
        in v0 : SpeedValue;
        in a : AccelerationValue;
        return : SpeedValue = v0 + a * dt;
    }

    calc def Select {
        in x : Real;
        in limit : Real;
        return result : Real;
        if x > limit then limit else x
    }

    constraint def StraightLineDynamicsEquations {
        in p : PowerValue;
        in m : MassValue;
        in dt : DurationValue;
        in x_i : LengthValue;
        in v_i : SpeedValue;
        in x_f : LengthValue;
        in v_f : SpeedValue;
        in a : AccelerationValue;
        attribute v_avg : SpeedValue = (v_i + v_f) / 2;
        a == Acceleration(p, m, v_avg) and
        v_f == Velocity(dt, v_i, a) and
        x_f == x_i + v_avg * dt
    }

    constraint def MassLimit {
        in total : MassValue;
        in limit : MassValue;
        total <= limit
    }

    part def Engine {
        attribute mass : MassValue = 180 [kg];
    }
    part def Body {
        attribute mass : MassValue = 900 [kg];
    }

    part def Vehicle {
        attribute mass : MassValue = engine.mass + body.mass;
        attribute massLimit : MassValue = 2000 [kg];
        attribute isHeavy : Boolean = mass > 1500 [kg] or massLimit < 1000 [kg];
        attribute isValid : Boolean = not isHeavy and mass >= 0 [kg] xor false;
        attribute category : String = if mass > 1800 [kg] then "heavy" else "light";
        attribute speeds : SpeedValue[*] = (10 [m/s], 20 [m/s], 30 [m/s]);
        attribute maxSpeed : SpeedValue = max(speeds);
        attribute peak : PowerValue = Power(100 [kW], 0.3, 0.01, mass, 30 [m/s]);
        attribute ratio : Real = (engine.mass / mass) * 100 % 7 ** 2;
        part engine : Engine;
        part body : Body;
        constraint massCheck : MassLimit;
        bind massCheck.total = mass;
        bind massCheck.limit = massLimit;
        assert constraint massOk : MassLimit {
            in total = mass;
            in limit = massLimit;
        }
        constraint { mass > 0 [kg] }
    }
}
