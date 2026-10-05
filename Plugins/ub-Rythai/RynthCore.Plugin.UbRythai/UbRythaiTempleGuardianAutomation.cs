using System;

namespace RynthCore.Plugin.UbRythai;

/// <summary>
/// UB-ILT TempleGuardianHelper parity (best-effort): vendor → /fillcomps → buyall style chat pump.
/// RynthCore does not expose UB's AutoVendor / UB_GiveItem surfaces, so this is intentionally conservative.
/// </summary>
internal sealed class UbRythaiTempleGuardianAutomation
{
    private int _step;
    private int _waitTicks;
    private string _itemName = "";

    public bool IsActive => _step != 0;

    public void Start(string itemName, string npcName, Func<string, bool> dispatchChatCommand, Action<string> logInfo)
    {
        Stop();
        if (string.IsNullOrWhiteSpace(itemName) || string.IsNullOrWhiteSpace(npcName))
            return;

        _itemName = itemName.Trim();
        bool skipVendor = npcName.IndexOf("Guardian of Attribute Enlightenment", StringComparison.OrdinalIgnoreCase) >= 0;
        _step = skipVendor ? 10 : 1;
        _waitTicks = 0;

        logInfo(skipVendor
            ? "[Guardian] Attribute guardian detected: skipping vendor flow (hand-in automation not available on this host)."
            : "[Guardian] Auto hand-in: starting vendor/fillcomps pump (best-effort).");

        if (!skipVendor)
        {
            // UB-ILT uses AutoVendor chat verbs; many shards expose similar vendor tooling via UB macros.
            dispatchChatCommand("/ub vendor clearbuy");
            dispatchChatCommand("/ub vendor open");
        }
    }

    public void Tick(Func<string, bool> dispatchChatCommand, Action<string> logInfo)
    {
        if (_step == 0)
            return;

        switch (_step)
        {
            case 1:
                _step = 2;
                _waitTicks = 0;
                break;
            case 2:
                _waitTicks++;
                if (_waitTicks > 120)
                {
                    logInfo("[Guardian] Auto hand-in: vendor open timeout — stopping.");
                    Stop();
                    return;
                }

                // Heuristic: once vendor tooling reports ready, UB users typically proceed with fillcomps.
                // Without vendor id introspection, we advance on a fixed delay (similar to UB frame waits).
                if (_waitTicks == 30)
                {
                    dispatchChatCommand("/fillcomps");
                    logInfo("[Guardian] /fillcomps");
                }

                if (_waitTicks == 60)
                {
                    dispatchChatCommand("/ub vendor buyall");
                    logInfo("[Guardian] vendor buyall (best-effort)");
                }

                if (_waitTicks >= 90)
                {
                    _step = 10;
                    _waitTicks = 0;
                }

                break;
            case 10:
                logInfo($"[Guardian] Resolved item: {_itemName}. Manual hand-in: stand next to the guardian and use the item on them (host automation not wired).");
                Stop();
                break;
        }
    }

    public void Stop()
    {
        _step = 0;
        _waitTicks = 0;
        _itemName = "";
    }
}
