using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using RynthCore.PluginSdk.Manifest;
using RynthCore.RynthAiTests.Harness;

namespace RynthServerPack.Tests;

// Every server plugin's csproj (Plugins\RynthCore.Plugin.Rynth*\ with RynthPluginServerId), read as
// XML: the detection data RynthCore's loader and sections use must be unique and must not overlap,
// or a client would load (or show) the wrong server's plugin.
internal static class ServerProjectsTests
{
    public static void Register(Runner r)
    {
        r.Add("server projects: every listed server has a plugin", AllServersPresent);
        r.Add("server projects: ids, plugin names and hosts are unique", Unique);
        r.Add("server projects: no server's host is claimed by another server's list", NoHostOverlap);
        r.Add("server projects: each new server plugin has ServerData.cs and imports the shared props", Layout);
        r.Add("server projects: the launcher's known servers match the plugins RynthCore loads for them", LauncherCatalog);
    }

    private sealed record Project(string Dir, string PluginName, string Id, string Name, string[] Hosts, string[] Worlds, bool Shared);

    /// <summary>The servers in the request (the server list's busiest, plus Aelrynth) and their plugins.</summary>
    private static readonly string[] Expected =
    {
        "coldeve", "ilt", "frostace", "reefcull", "conquest", "dreamweave", "portalstorm", "sundering",
        "achard", "leafdawn", "levistras", "derptide", "frostcull", "thistlecrown", "eversong", "leafdawning", "aelrynth",
    };

    private static string PluginsDir()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "Plugins", "Shared", "ServerPack")))
            dir = Path.GetDirectoryName(dir);
        if (dir == null) throw new DirectoryNotFoundException("RynthSuite\\Plugins not found above " + AppContext.BaseDirectory);
        return Path.Combine(dir, "Plugins");
    }

    private static List<Project> Load()
    {
        var list = new List<Project>();
        foreach (string dir in Directory.GetDirectories(PluginsDir(), "RynthCore.Plugin.Rynth*"))
        {
            string csproj = Path.Combine(dir, Path.GetFileName(dir) + ".csproj");
            if (!File.Exists(csproj)) continue;
            XDocument doc = XDocument.Load(csproj);
            string Prop(string name) => doc.Descendants(name).FirstOrDefault()?.Value.Trim() ?? "";
            string id = Prop("RynthPluginServerId");
            if (id.Length == 0) continue;
            string[] Split(string v) => v.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            bool shared = doc.Descendants("Import").Any(i => (i.Attribute("Project")?.Value ?? "").EndsWith("ServerPlugin.props"));
            string asm = Prop("AssemblyName");
            list.Add(new Project(dir, asm.Replace("RynthCore.Plugin.", ""), id, Prop("RynthPluginServerName"),
                                 Split(Prop("RynthPluginServerHosts")), Split(Prop("RynthPluginServerWorldNames")), shared));
        }
        return list;
    }

    private static void AllServersPresent()
    {
        List<Project> projects = Load();
        foreach (string id in Expected)
            Check.True(projects.Any(p => p.Id == id), "a server plugin for " + id);
        Check.Eq(projects.Count, Expected.Length, "no unexpected server plugins");
    }

    private static void Unique()
    {
        List<Project> projects = Load();
        Check.Eq(projects.Select(p => p.Id).Distinct().Count(), projects.Count, "ids unique");
        Check.Eq(projects.Select(p => p.PluginName.ToLowerInvariant()).Distinct().Count(), projects.Count, "plugin names unique");
        var hosts = projects.SelectMany(p => p.Hosts).Select(h => h.ToLowerInvariant()).ToList();
        Check.Eq(hosts.Distinct().Count(), hosts.Count, "hosts unique");
        foreach (Project p in projects)
        {
            Check.True(p.Hosts.Length > 0, p.PluginName + " lists a host (the loader picks plugins by host)");
            Check.True(p.Worlds.Length > 0, p.PluginName + " lists a world name");
            Check.True(p.Name.Length > 0, p.PluginName + " has a display name");
        }
    }

    private static void NoHostOverlap()
    {
        List<Project> projects = Load();
        foreach (Project owner in projects)
            foreach (string host in owner.Hosts.Where(h => !h.Contains('*')))
                foreach (Project other in projects.Where(o => o != owner))
                {
                    var server = new RynthPluginServer { Id = other.Id, Hosts = other.Hosts.ToList() };
                    Check.False(server.MatchesHost(host, out string hit),
                                $"{owner.PluginName}'s host {host} is not claimed by {other.PluginName} ({hit})");
                }
    }

    private static void Layout()
    {
        foreach (Project p in Load().Where(p => p.Id != "ilt"))
        {
            Check.True(p.Shared, p.PluginName + " imports Shared\\ServerPack\\ServerPlugin.props");
            Check.True(File.Exists(Path.Combine(p.Dir, "ServerData.cs")), p.PluginName + " has ServerData.cs");
        }
    }

    /// <summary>
    /// A profile made from the launcher's picker connects to the catalog's host; RynthCore's load
    /// gate then picks the plugin whose hosts claim it. So each entry must name a real server plugin
    /// with the same id, that plugin (and no other) must claim the host, and the port and emulator
    /// must agree with the plugin's ServerData.cs (the card shows those).
    /// </summary>
    private static void LauncherCatalog()
    {
        List<Project> projects = Load();
        var catalog = RynthCore.App.KnownServerCatalog.All;
        Check.Eq(catalog.Count, projects.Count, "one known server per server plugin");
        foreach (RynthCore.App.KnownServer known in catalog)
        {
            Project? p = projects.FirstOrDefault(x => string.Equals(x.PluginName, known.PluginName, StringComparison.Ordinal));
            Check.True(p != null, $"{known.Name}: plugin {known.PluginName} exists");
            if (p == null) continue;
            Check.Eq(p.Id, known.Id, known.PluginName + " server id");

            foreach (Project candidate in projects)
            {
                var server = new RynthPluginServer { Id = candidate.Id, Hosts = candidate.Hosts.ToList() };
                bool claims = server.MatchesHost(known.Host, out _);
                if (candidate == p)
                    Check.True(claims, $"{p.PluginName} claims the launcher's host {known.Host}");
                else
                    Check.False(claims, $"{candidate.PluginName} does not claim {known.Name}'s host {known.Host}");
            }

            if (p.Id == "ilt") continue;   // RynthILT has no ServerData.cs
            string data = File.ReadAllText(Path.Combine(p.Dir, "ServerData.cs"));
            var port = System.Text.RegularExpressions.Regex.Match(data, @"\bPort\s*=\s*(\d+)");
            int dataPort = port.Success ? int.Parse(port.Groups[1].Value) : 9000;
            Check.Eq(known.Port, dataPort, known.PluginName + " port matches ServerData.cs");
            var emu = System.Text.RegularExpressions.Regex.Match(data, @"\bEmulator\s*=\s*""([^""]+)""");
            string dataEmu = emu.Success ? emu.Groups[1].Value : "ACE";
            Check.Eq(known.Emulator.ToString().ToUpperInvariant(), dataEmu.ToUpperInvariant(), known.PluginName + " emulator matches ServerData.cs");
        }
    }
}
