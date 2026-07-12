/*
 * lenpower.c — thin native bridge to the Lenovo Power Manager local-RPC interface.
 *
 * Exposes flat, P/Invoke-friendly exports that the managed app calls:
 *   LenGetChargeThreshold  — read battery charge start/stop thresholds
 *   LenSetChargeThreshold  — write them (start==stop==0 disables, i.e. charge to 100%)
 *   LenGetAcAdapterWattage — read the connected AC adapter's rated wattage
 *
 * The heavy lifting (NDR marshaling, context handles) is done by the MIDL-generated
 * client stub in pwrmgr_c.c. We just compose the ncalrpc binding, create a server
 * context, call the proc, and tear down — wrapping every RPC call in structured
 * exception handling so a missing/!installed driver returns an error code instead
 * of crashing the host process.
 *
 * Returns 0 on success; otherwise an RPC_STATUS / RPC exception code (non-zero).
 */

#include <windows.h>
#include <stdlib.h>
#include "pwrmgr_h.h"

/* The RPC runtime calls these to (de)allocate memory for [out] data. */
void* __RPC_USER midl_user_allocate(size_t bytes) { return malloc(bytes); }
void  __RPC_USER midl_user_free(void* p)          { free(p); }

/* Compose a local-RPC binding and create a server-side context handle. */
static int Connect(RPC_BINDING_HANDLE* outBinding, void** outCtx)
{
    RPC_WSTR          stringBinding = NULL;
    RPC_BINDING_HANDLE binding      = NULL;
    void*             ctx           = NULL;
    int               rc            = 0;

    RPC_STATUS status = RpcStringBindingComposeW(
        NULL,                                   /* interface GUID — supplied by the stub */
        (RPC_WSTR)L"ncalrpc",                   /* local RPC transport */
        NULL,                                   /* no network address (local) */
        (RPC_WSTR)L"BaseModuleRpcEndpoint_0",   /* Lenovo Power Manager endpoint */
        NULL,
        &stringBinding);
    if (status != RPC_S_OK) return (int)status;

    status = RpcBindingFromStringBindingW(stringBinding, &binding);
    RpcStringFreeW(&stringBinding);
    if (status != RPC_S_OK) return (int)status;

    RpcTryExcept
    {
        LpcCreateContext(binding, &ctx);
    }
    RpcExcept(EXCEPTION_EXECUTE_HANDLER)
    {
        rc = (int)RpcExceptionCode();
    }
    RpcEndExcept

    if (rc != 0 || ctx == NULL)
    {
        RpcBindingFree(&binding);
        return rc ? rc : -1;
    }

    *outBinding = binding;
    *outCtx     = ctx;
    return 0;
}

static void Disconnect(RPC_BINDING_HANDLE binding, void* ctx)
{
    RpcTryExcept { LpcFreeContext(&ctx); }
    RpcExcept(EXCEPTION_EXECUTE_HANDLER) { /* best effort */ }
    RpcEndExcept
    RpcBindingFree(&binding);
}

__declspec(dllexport) int LenGetChargeThreshold(
    int battery, int* capable, int* enabled, int* start, int* stop)
{
    RPC_BINDING_HANDLE binding = NULL;
    void*              ctx     = NULL;
    int rc = Connect(&binding, &ctx);
    if (rc) return rc;

    short cap = 0, en = 0;
    long  st  = 0, sp = 0;

    RpcTryExcept
    {
        LpcGetChargeThreshold(ctx, (long)battery, &cap, &en, &st, &sp);
    }
    RpcExcept(EXCEPTION_EXECUTE_HANDLER)
    {
        rc = (int)RpcExceptionCode();
    }
    RpcEndExcept

    if (!rc)
    {
        if (capable) *capable = cap;
        if (enabled) *enabled = en;
        if (start)   *start   = st;
        if (stop)    *stop    = sp;
    }

    Disconnect(binding, ctx);
    return rc;
}

__declspec(dllexport) int LenSetChargeThreshold(int battery, int start, int stop)
{
    RPC_BINDING_HANDLE binding = NULL;
    void*              ctx     = NULL;
    int rc = Connect(&binding, &ctx);
    if (rc) return rc;

    RpcTryExcept
    {
        LpcSetChargeThreshold(ctx, (long)battery, (long)start, (long)stop);
    }
    RpcExcept(EXCEPTION_EXECUTE_HANDLER)
    {
        rc = (int)RpcExceptionCode();
    }
    RpcEndExcept

    Disconnect(binding, ctx);
    return rc;
}

__declspec(dllexport) int LenGetAcAdapterWattage(int battery, int* capable, int* wattage)
{
    RPC_BINDING_HANDLE binding = NULL;
    void*              ctx     = NULL;
    int rc = Connect(&binding, &ctx);
    if (rc) return rc;

    short cap = 0;
    long  val = 0;

    RpcTryExcept
    {
        LpcGetAcAdapterWattage(ctx, (long)battery, &val, &cap);
    }
    RpcExcept(EXCEPTION_EXECUTE_HANDLER)
    {
        rc = (int)RpcExceptionCode();
    }
    RpcEndExcept

    if (!rc)
    {
        if (capable) *capable = cap;
        if (wattage) *wattage = val;
    }

    Disconnect(binding, ctx);
    return rc;
}
