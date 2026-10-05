namespace PrintLogApi.Achievements;

/// <summary>
/// Which kinds of entity change can affect a definition. The save interceptor records these per
/// user, and a triggered evaluation pass only measures the definitions whose flags overlap.
/// </summary>
[Flags]
public enum AchievementTrigger
{
    None = 0,
    PrintAdded = 1,
    PrintUpdated = 2,
    PrinterChanged = 4,
    FilamentChanged = 8,
    ProjectChanged = 16,
    PrintImageAdded = 32,
    CommentReceived = 64,
    MaintenanceChanged = 128,
    ProfileChanged = 256,
    TimeZoneChanged = 512,
}
