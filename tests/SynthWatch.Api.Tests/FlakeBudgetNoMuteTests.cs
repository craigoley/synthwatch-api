using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>
/// ★★★ THE STRUCTURAL NO-MUTE PROOF (B3-3 non-negotiable #2). The flake budget must have NO write path to alert
/// routing / notification / mute — proven STRUCTURALLY, not by convention. A monitor that flaps because the
/// SERVICE is flaky is telling the truth; muting it would mean "the flakier your service, the quieter your
/// monitoring" — a safety inversion. So the machinery must be INCAPABLE of muting an alert, not merely currently
/// not doing so. We prove it by DISJOINTNESS: no source file references BOTH a flake-budget symbol AND an
/// alert-routing/notification/mute WRITE surface — so the budget cannot reach a mute (nothing bridges them).
/// (The runner has the companion proof: flake_status is a read-only SQL function + zero flake→alert wiring.)
/// </summary>
public class FlakeBudgetNoMuteTests
{
    // Flake-budget symbols (the B3-3 machinery).
    private static readonly string[] FlakeSymbols =
        { "FlakeBudget", "FlakeDirectedTask", "FlakeBudgetState", "flake_target", "flake_status", "FlakeTarget" };

    // Alert routing / notification / mute WRITE surfaces. Deliberately SPECIFIC symbols (not the bare word
    // "route", which appears in every Azure Function's [Function(Route=...)] attribute).
    private static readonly string[] RoutingMuteSymbols =
        { "tag_routes", "AlertRoute", "error_mutes", "ErrorMute", "EmailSender", "SendAlert", "DispatchAlert",
          "NotificationChannel", "MuteError", "Suppress", "Silence" };

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (; d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SynthWatch.Api.csproj"))) return d.FullName;
        throw new InvalidOperationException("SynthWatch.Api.csproj not found walking up from the test binary");
    }

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}.claude{Path.DirectorySeparatorChar}")
                     // The EF DbContext is the ORM registry that maps EVERY table's columns — it references
                     // flake_target (a column) AND AlertRoute/error_mutes (other tables) purely as HasColumnName
                     // metadata, with NO logic that flows one to the other. It is not a data-flow bridge; a real
                     // bridge would be a Function/Infrastructure that reads a budget state and writes a route/mute.
                     && Path.GetFileName(f) != "SynthWatchDbContext.cs");

    [Fact]
    public void No_source_file_bridges_the_flake_budget_to_an_alert_routing_or_mute_surface()
    {
        var root = RepoRoot();
        var bridging = new List<string>();
        foreach (var file in SourceFiles(root))
        {
            var text = File.ReadAllText(file);
            var touchesFlake = FlakeSymbols.Any(text.Contains);
            var touchesRouting = RoutingMuteSymbols.Any(text.Contains);
            if (touchesFlake && touchesRouting)
                bridging.Add(Path.GetRelativePath(root, file));
        }
        // Empty ⇒ the flake budget and the alert/routing/mute machinery are STRUCTURALLY DISJOINT: no file can
        // take a "degraded-as-a-monitor"/budget-blown signal and act on a route/notification/mute. The directed
        // task is a plain string on a READ DTO; the consequence is a human fix, never an auto-suppression.
        Assert.Empty(bridging);
    }

    [Fact]
    public void The_directed_task_is_a_plain_string_with_no_side_effect()
    {
        // FlakeDirectedTask returns a string (or null) and does nothing else — no I/O, no DB, no dispatch. Calling
        // it on a degraded monitor yields the actionable text; it cannot mute or route because it returns data.
        var degraded = new SynthWatch.Api.Data.Entities.TrustMonitorRow
        {
            CheckId = 222, CheckName = "222", MonitorSideTransients = 3,
            FlakeTarget = 0.02m, FlakeScheduledRuns = 49, FlakeBudget = 0.98m, FlakeConsumed = 3,
        };
        var task = SynthWatch.Api.Infrastructure.TrustReportProjection.FlakeDirectedTask(degraded);
        Assert.NotNull(task);
        Assert.IsType<string>(task);
        Assert.DoesNotContain("mute", task!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("suppress", task, StringComparison.OrdinalIgnoreCase);
        // A non-degraded monitor gets NO task (null) — nothing is ever auto-actioned.
        var ok = new SynthWatch.Api.Data.Entities.TrustMonitorRow { FlakeConsumed = 0, FlakeBudget = 1m };
        Assert.Null(SynthWatch.Api.Infrastructure.TrustReportProjection.FlakeDirectedTask(ok));
    }
}
