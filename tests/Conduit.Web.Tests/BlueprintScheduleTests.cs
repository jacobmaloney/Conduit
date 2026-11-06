using Conduit.Sync.Orchestration;
using Conduit.Sync.Templates;
using IdentityCenter.Brand.Models;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// SCHEDULE-SHARED-02. A blueprint's default schedule is stored on the project it creates, so it must be one the runner
/// fires and the shared schedule picker reads back. The default was the 6-field "0 0 2 * * ?": NCrontab refused it (the
/// project was silently never scheduled) and the picker flagged it as unreadable.
/// </summary>
public class BlueprintScheduleTests
{
    [Fact]
    public void Every_blueprint_default_schedule_runs_and_reads_back_into_the_picker()
    {
        Assert.NotEmpty(SyncProjectBlueprintCatalog.All);
        foreach (var bp in SyncProjectBlueprintCatalog.All)
        {
            if (string.IsNullOrWhiteSpace(bp.DefaultCronSchedule)) continue;
            Assert.True(ScheduledSyncRunnerJob.TryValidate(bp.DefaultCronSchedule, out var error), $"{bp.Name}: {error}");
            Assert.True(BrandCronConverter.TryFromCron(bp.DefaultCronSchedule, out _), $"{bp.Name}: the picker cannot show its default");
        }
        Assert.Equal("Daily at 2:00 AM", BrandCronConverter.Describe(SyncProjectBlueprintCatalog.DailyCron));
    }
}
