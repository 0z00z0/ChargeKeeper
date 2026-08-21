namespace ChargeKeeper.Vendors.Hp;

/// <summary>
/// HP ships no equivalent to Lenovo's <c>LenovoSmartStandby</c> service, and nothing in
/// <c>root\HP\InstrumentedBIOS</c> controls Modern Standby engagement. Reporting not-supported is
/// the contract's way to say so, and is what hides the Smart Standby toggle on HP.
/// </summary>
internal sealed class HpStandby : IStandbyProvider
{
    public bool IsSupported => false;

    public bool IsRunning() => false;

    public bool SetEnabled(bool enable) => false;
}
