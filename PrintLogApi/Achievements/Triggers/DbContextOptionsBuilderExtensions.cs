using Microsoft.EntityFrameworkCore;

namespace PrintLogApi.Achievements.Triggers;

public static class DbContextOptionsBuilderExtensions
{
    /// <summary>
    /// Attaches the save and transaction hooks that trigger achievement evaluation. Every
    /// registration of <c>PrintLogContext</c> must call this, the test host's included, or saves
    /// there never evaluate.
    /// </summary>
    public static DbContextOptionsBuilder AddAchievementInterceptors(this DbContextOptionsBuilder builder, IServiceProvider sp) =>
        builder.AddInterceptors(
            sp.GetRequiredService<AchievementSaveChangesInterceptor>(),
            sp.GetRequiredService<AchievementTransactionInterceptor>());
}
