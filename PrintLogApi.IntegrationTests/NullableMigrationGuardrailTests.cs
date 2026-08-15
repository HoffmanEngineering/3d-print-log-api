using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace PrintLogApi.IntegrationTests
{
    /// <summary>
    /// Guards the scaffolding that keeps the nullable reference type migration (#46) from
    /// changing request validation by accident.
    ///
    /// These assertions are expected to be REMOVED as part of #45, when the project flips to
    /// Nullable=enable and the implicit-[Required] behaviour is adopted on purpose. Until then a
    /// failure here means someone dropped a guardrail early, which would let the entity (#42) and
    /// DTO (#43) annotation PRs silently start rejecting requests that used to succeed.
    /// </summary>
    public class NullableMigrationGuardrailTests : IClassFixture<CustomWebApplicationFactory>
    {
        private readonly CustomWebApplicationFactory _factory;

        public NullableMigrationGuardrailTests(CustomWebApplicationFactory factory)
        {
            _factory = factory;
        }

        /// <summary>
        /// With nullable annotations active, MVC infers [Required(AllowEmptyStrings = true)] on
        /// every non-nullable reference property of a bound model. Across ~94 DTO files that turns
        /// omitted fields into 400s with no compiler diagnostic to warn anyone. The suppression has
        /// to stay until that change is made deliberately.
        /// </summary>
        [Fact]
        public void ImplicitRequiredForNonNullableReferenceTypes_IsSuppressed()
        {
            var options = _factory.Services.GetRequiredService<IOptions<MvcOptions>>().Value;

            Assert.True(
                options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes,
                "MVC's implicit [Required] inference must stay suppressed until #45 adopts it " +
                "deliberately. Removing it before the DTOs are annotated changes request " +
                "validation with no compiler warning.");
        }
    }
}
