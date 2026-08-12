namespace ChargeKeeper.Vendors.Hp;

/// <summary>
/// Standby scheduling is Lenovo-only. It is implemented there by the <c>LenovoSmartStandby</c>
/// Windows service, and HP ships no equivalent — nothing in <c>root\HP\InstrumentedBIOS</c>
/// controls Modern Standby engagement.
///
/// Reporting not-supported / not-running / write-failed is the contract's documented way to say
/// "this vendor does not have this", so no exception is thrown and no capability is faked.
///
/// <see cref="IsSupported"/> is what hides the Smart Standby toggle:
/// <c>SmartStandbyFeature.IsAvailable</c> used to be hardcoded <c>true</c> ("always present on
/// ThinkPads") and now reads through to here, so the toggle is not offered on HP rather than
/// rendering enabled and silently doing nothing.
/// </summary>
internal sealed class HpStandby : IStandbyProvider
{
    public bool IsSupported => false;

    public bool IsRunning() => false;

    public bool SetEnabled(bool enable) => false;
}
