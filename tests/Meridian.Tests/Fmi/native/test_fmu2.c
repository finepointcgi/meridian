/*
 * Tiny FMI 2.0 Co-Simulation FMUs for Meridian's co-simulation tests, compiled by the test setup with the system C compiler.
 * Select the model with -DMODEL_LAG, -DMODEL_GAIN, -DMODEL_CRASH or -DMODEL_HANG.
 *
 *   LAG    first-order lag  T dy/dt = u - y, y(0) = x0, integrated exactly over each step (u held constant)
 *          vr 0 T (parameter), 1 x0 (parameter), 2 u (input), 3 y (output)
 *   GAIN   y = k u           vr 0 k (parameter), 1 u (input), 2 y (output)
 *   CRASH  a gain that dereferences NULL in fmi2DoStep once t >= 0.5 (crash isolation test)
 *   HANG   a gain that never returns from fmi2DoStep once t >= 0.1 (timeout test)
 *
 * Only the functions Meridian's FMU host binds for real-valued models are implemented.
 */
#include <math.h>
#include <string.h>
#include "fmi2Functions.h"

#if defined(MODEL_LAG)
#define GUID "{8c4e810f-3df3-4a00-8276-176fa3c9f000}"
#define NREAL 4
#else
#define GUID "{8c4e810f-3df3-4a00-8276-176fa3c9f001}"
#define NREAL 3
#endif

typedef struct {
    fmi2Real r[NREAL];
    fmi2Real x;
    const fmi2CallbackFunctions *cb;
} Instance;

static void log_message(Instance *c, fmi2Status s, const char *msg) {
    if (c->cb && c->cb->logger) c->cb->logger(c->cb->componentEnvironment, "test", s, "info", msg);
}

fmi2Component fmi2Instantiate(fmi2String instanceName, fmi2Type fmuType, fmi2String fmuGUID, fmi2String fmuResourceLocation,
                              const fmi2CallbackFunctions *functions, fmi2Boolean visible, fmi2Boolean loggingOn) {
    Instance *c;
    (void)instanceName; (void)fmuResourceLocation; (void)visible; (void)loggingOn;
    if (fmuType != fmi2CoSimulation || fmuGUID == NULL || strcmp(fmuGUID, GUID) != 0) return NULL;
    c = (Instance *)functions->allocateMemory(1, sizeof(Instance));
    if (!c) return NULL;
    c->cb = functions;
    c->r[0] = 1;
    log_message(c, fmi2OK, "instantiated");
    return c;
}

void fmi2FreeInstance(fmi2Component c) {
    Instance *i = (Instance *)c;
    if (i) i->cb->freeMemory(i);
}

fmi2Status fmi2SetupExperiment(fmi2Component c, fmi2Boolean toleranceDefined, fmi2Real tolerance, fmi2Real startTime,
                               fmi2Boolean stopTimeDefined, fmi2Real stopTime) {
    (void)c; (void)toleranceDefined; (void)tolerance; (void)startTime; (void)stopTimeDefined; (void)stopTime;
    return fmi2OK;
}

fmi2Status fmi2EnterInitializationMode(fmi2Component c) { (void)c; return fmi2OK; }

fmi2Status fmi2ExitInitializationMode(fmi2Component c) {
    Instance *i = (Instance *)c;
#if defined(MODEL_LAG)
    if (i->r[0] <= 0) { log_message(i, fmi2Error, "T must be positive"); return fmi2Error; }
    i->x = i->r[1];
#else
    (void)i;
#endif
    return fmi2OK;
}

fmi2Status fmi2Terminate(fmi2Component c) { (void)c; return fmi2OK; }

static fmi2Real output(Instance *i) {
#if defined(MODEL_LAG)
    return i->x;
#else
    return i->r[0] * i->r[1];
#endif
}

fmi2Status fmi2SetReal(fmi2Component c, const fmi2ValueReference vr[], size_t nvr, const fmi2Real value[]) {
    Instance *i = (Instance *)c;
    size_t k;
    for (k = 0; k < nvr; k++) {
        if (vr[k] >= NREAL - 1) return fmi2Error;   /* the output cannot be set */
        i->r[vr[k]] = value[k];
    }
    return fmi2OK;
}

fmi2Status fmi2GetReal(fmi2Component c, const fmi2ValueReference vr[], size_t nvr, fmi2Real value[]) {
    Instance *i = (Instance *)c;
    size_t k;
    for (k = 0; k < nvr; k++) {
        if (vr[k] >= NREAL) return fmi2Error;
        value[k] = vr[k] == NREAL - 1 ? output(i) : i->r[vr[k]];
    }
    return fmi2OK;
}

fmi2Status fmi2DoStep(fmi2Component c, fmi2Real currentCommunicationPoint, fmi2Real communicationStepSize,
                      fmi2Boolean noSetFMUStatePriorToCurrentPoint) {
    Instance *i = (Instance *)c;
    (void)noSetFMUStatePriorToCurrentPoint;
#if defined(MODEL_LAG)
    {
        fmi2Real u = i->r[2];
        i->x = u + (i->x - u) * exp(-communicationStepSize / i->r[0]);
    }
#elif defined(MODEL_CRASH)
    if (currentCommunicationPoint >= 0.5) {
        volatile int *p = (volatile int *)0;
        *p = 42;   /* segmentation fault */
    }
#elif defined(MODEL_HANG)
    if (currentCommunicationPoint >= 0.1) { volatile int spin = 1; while (spin) { } }
#endif
    (void)i; (void)currentCommunicationPoint; (void)communicationStepSize;
    return fmi2OK;
}
