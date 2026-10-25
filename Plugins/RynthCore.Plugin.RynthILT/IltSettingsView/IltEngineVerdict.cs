// IltEngineVerdict.cs - RynthCore's Infinite Leaftide verdict as RynthILT last heard it.
//
// RynthCore decides whether this world is ILT once, for itself and every plugin, with this
// character's Auto / On / Off override ("/rc server ilt"). RynthILT reads that verdict from the
// engine's server list (plugin API v86, GetServerStateJson; the plugin tick polls it into
// IltSettingsModel.Engine). The Hub's world check (IltServerOptions, ServerFeatureGate) follows it
// once one has arrived; before that, or on an engine older than v86, RynthILT's own server list
// and "Treat this world as ILT" decide.
//
// RynthILT's status export must keep reporting its own verdict, not this one: the engine adds
// RynthILT's verdict to its detection, and an echo would keep ILT on after "/rc ilt auto".
// Any thread: one volatile reference read.
using System.Threading;

namespace RynthCore.Plugin.RynthILT.IltSettingsView;

internal static class IltEngineVerdict
{
    private static IltSettingsModel? _model;

    /// <summary>The model the plugin feeds from RynthCore's server list (RynthILTPlugin's constructor).</summary>
    public static void Attach(IltSettingsModel? model) => Volatile.Write(ref _model, model);

    /// <summary>RynthCore has sent its server list at least once since login (it may list no ILT).</summary>
    public static bool Pushed => Volatile.Read(ref _model)?.Engine != null;

    /// <summary>RynthCore's ILT entry; null until a RynthCore with the server list (v86) has sent one.</summary>
    public static IltServerView? Server => Volatile.Read(ref _model)?.Engine?.Server;

    /// <summary>RynthCore's verdict for this world; null when it hasn't given one.</summary>
    public static bool? Active => Server?.Active;
}
