using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MediaBrowser.Controller.LiveTv;
using Xunit;

namespace Jellyfin.Plugin.MovieNight.Tests;

/// <summary>
/// Guards the DI cycle that took the whole Jellyfin server down on 2026-09-03 (v0.4.0.0).
/// <para>
/// Jellyfin constructs every <see cref="ITunerHost"/> while it is building ILiveTvManager. So if
/// anything reachable from our tuner host's constructor needs ILiveTvManager or IGuideManager, the
/// graph closes into a loop:
/// ILiveTvManager -> ILiveTvService -> ITunerHostManager -> ITunerHost -> LiveSession ->
/// BroadcastManager -> IGuideManager -> ILiveTvManager
/// and the server aborts at startup with "A circular dependency was detected". Unit tests over
/// individual classes cannot see this - it only appears once a real container resolves the graph -
/// which is why it shipped.
/// </para>
/// Anything from Live TV that we genuinely need must be resolved lazily at call time (see
/// BroadcastManager.TriggerGuideRefresh), never taken as a constructor parameter.
/// </summary>
public class DependencyGraphTests
{
    /// <summary>Live TV services that are downstream of ITunerHost and so must never be injected.</summary>
    private static readonly Type[] Forbidden = [typeof(IGuideManager), typeof(ILiveTvManager)];

    [Fact]
    public void TunerHostConstructorGraph_DoesNotReachLiveTvManagerOrGuideManager()
    {
        var offenders = new List<string>();
        var seen = new HashSet<Type>();

        Walk(typeof(MovieNightTunerHost), seen, offenders, typeof(MovieNightTunerHost).Name);

        Assert.True(
            offenders.Count == 0,
            "A plugin type reachable from MovieNightTunerHost's constructor injects a Live TV "
            + "service that depends back on ITunerHost. This is the 2026-09-03 startup-killing "
            + "cycle. Resolve it lazily instead. Paths:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Walks constructor parameters, following only types this plugin owns - a Jellyfin type's own
    /// dependencies are its business; what matters is whether OUR graph asks for one of the
    /// forbidden services in a constructor.
    /// </summary>
    private static void Walk(Type type, HashSet<Type> seen, List<string> offenders, string path)
    {
        if (!seen.Add(type))
        {
            return;
        }

        var ctor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();

        if (ctor is null)
        {
            return;
        }

        foreach (var parameter in ctor.GetParameters())
        {
            var parameterType = parameter.ParameterType;
            var next = path + " -> " + parameterType.Name;

            if (Array.IndexOf(Forbidden, parameterType) >= 0)
            {
                offenders.Add(next);
                continue;
            }

            if (parameterType.Assembly == typeof(MovieNightTunerHost).Assembly)
            {
                Walk(parameterType, seen, offenders, next);
            }
        }
    }
}
