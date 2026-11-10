/*
 * A tiny FMI 3.0 Co-Simulation FMU for Meridian's tests: the same first-order lag as test_fmu2.c (T dy/dt = u - y, y(0) = x0),
 * integrated exactly over each step. vr 1 T (parameter), 2 x0 (parameter), 3 u (input), 4 y (output), 5 stopAt (parameter):
 * when stopAt is positive the FMU asks the master to end the simulation (terminateSimulation) once t reaches it.
 */
#include <math.h>
#include <stdlib.h>
#include <string.h>
#include "fmi3Functions.h"

#define TOKEN "{6a5f1d2e-0b7c-4c55-9a51-1b2f0d3e3000}"

typedef struct {
    fmi3Float64 r[6];
    fmi3Float64 x;
    fmi3LogMessageCallback log;
    fmi3InstanceEnvironment env;
} Instance;

fmi3Instance fmi3InstantiateCoSimulation(fmi3String instanceName, fmi3String instantiationToken, fmi3String resourcePath,
    fmi3Boolean visible, fmi3Boolean loggingOn, fmi3Boolean eventModeUsed, fmi3Boolean earlyReturnAllowed,
    const fmi3ValueReference requiredIntermediateVariables[], size_t nRequiredIntermediateVariables,
    fmi3InstanceEnvironment instanceEnvironment, fmi3LogMessageCallback logMessage, fmi3IntermediateUpdateCallback intermediateUpdate) {
    Instance *c;
    (void)instanceName; (void)resourcePath; (void)visible; (void)loggingOn; (void)eventModeUsed; (void)earlyReturnAllowed;
    (void)requiredIntermediateVariables; (void)nRequiredIntermediateVariables; (void)intermediateUpdate;
    if (instantiationToken == NULL || strcmp(instantiationToken, TOKEN) != 0) return NULL;
    c = (Instance *)calloc(1, sizeof(Instance));
    if (!c) return NULL;
    c->r[1] = 1; c->log = logMessage; c->env = instanceEnvironment;
    if (c->log) c->log(c->env, fmi3OK, "info", "instantiated (FMI 3.0)");
    return c;
}

void fmi3FreeInstance(fmi3Instance instance) { free(instance); }

fmi3Status fmi3EnterInitializationMode(fmi3Instance instance, fmi3Boolean toleranceDefined, fmi3Float64 tolerance,
    fmi3Float64 startTime, fmi3Boolean stopTimeDefined, fmi3Float64 stopTime) {
    (void)instance; (void)toleranceDefined; (void)tolerance; (void)startTime; (void)stopTimeDefined; (void)stopTime;
    return fmi3OK;
}

fmi3Status fmi3ExitInitializationMode(fmi3Instance instance) {
    Instance *i = (Instance *)instance;
    if (i->r[1] <= 0) return fmi3Error;
    i->x = i->r[2];
    return fmi3OK;
}

fmi3Status fmi3Terminate(fmi3Instance instance) { (void)instance; return fmi3OK; }

fmi3Status fmi3SetFloat64(fmi3Instance instance, const fmi3ValueReference vr[], size_t nvr, const fmi3Float64 values[], size_t nValues) {
    Instance *i = (Instance *)instance;
    size_t k;
    if (nvr != nValues) return fmi3Error;
    for (k = 0; k < nvr; k++) {
        if (vr[k] < 1 || vr[k] > 5 || vr[k] == 4) return fmi3Error;
        i->r[vr[k]] = values[k];
    }
    return fmi3OK;
}

fmi3Status fmi3GetFloat64(fmi3Instance instance, const fmi3ValueReference vr[], size_t nvr, fmi3Float64 values[], size_t nValues) {
    Instance *i = (Instance *)instance;
    size_t k;
    if (nvr != nValues) return fmi3Error;
    for (k = 0; k < nvr; k++) {
        if (vr[k] < 1 || vr[k] > 5) return fmi3Error;
        values[k] = vr[k] == 4 ? i->x : i->r[vr[k]];
    }
    return fmi3OK;
}

fmi3Status fmi3DoStep(fmi3Instance instance, fmi3Float64 currentCommunicationPoint, fmi3Float64 communicationStepSize,
    fmi3Boolean noSetFMUStatePriorToCurrentPoint, fmi3Boolean *eventHandlingNeeded, fmi3Boolean *terminateSimulation,
    fmi3Boolean *earlyReturn, fmi3Float64 *lastSuccessfulTime) {
    Instance *i = (Instance *)instance;
    fmi3Float64 u = i->r[3];
    (void)noSetFMUStatePriorToCurrentPoint;
    i->x = u + (i->x - u) * exp(-communicationStepSize / i->r[1]);
    *eventHandlingNeeded = fmi3False;
    *earlyReturn = fmi3False;
    *lastSuccessfulTime = currentCommunicationPoint + communicationStepSize;
    *terminateSimulation = (i->r[5] > 0 && currentCommunicationPoint + communicationStepSize >= i->r[5] - 1e-12) ? fmi3True : fmi3False;
    return fmi3OK;
}
