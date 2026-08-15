using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PrintLogApi;
using System.Collections.Generic;
using System.Reflection;
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

        /// <summary>
        /// Covers the one class of entity-annotation mistake that CI structurally cannot catch.
        ///
        /// `dotnet-ef migrations has-pending-model-changes` guards scalar properties, because a
        /// scalar's annotation is what EF infers the column's nullability from. Reference
        /// NAVIGATIONS are different: optionality comes from the foreign key property, so a
        /// navigation annotated `Filament?` on a required relationship (or the reverse) produces an
        /// identical schema and leaves that check green — while handing every consumer nullability
        /// information that contradicts the database.
        ///
        /// So assert it directly against the built model: a required relationship must have a
        /// non-nullable navigation, an optional one must have a nullable navigation.
        /// </summary>
        [Fact]
        public void ReferenceNavigationAnnotations_MatchRelationshipRequiredness()
        {
            using var scope = _factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var nullabilityContext = new NullabilityInfoContext();

            var mismatches = new List<string>();
            var checkedCount = 0;

            foreach (var entityType in context.Model.GetEntityTypes())
            {
                foreach (var navigation in entityType.GetNavigations())
                {
                    if (navigation.IsCollection || navigation.PropertyInfo is null)
                    {
                        continue;
                    }

                    var state = nullabilityContext.Create(navigation.PropertyInfo).ReadState;

                    // Unknown means the declaring file has no `#nullable enable` yet. Nothing to
                    // check there, and it must not fail — this test has to stay valid while the
                    // rest of the migration (#43, #44) is still outstanding.
                    if (state == NullabilityState.Unknown)
                    {
                        continue;
                    }

                    checkedCount++;
                    var isRequired = navigation.ForeignKey.IsRequired;
                    var isNullable = state == NullabilityState.Nullable;

                    if (isRequired == isNullable)
                    {
                        mismatches.Add(
                            $"{entityType.ClrType.Name}.{navigation.Name}: relationship is " +
                            $"{(isRequired ? "REQUIRED" : "OPTIONAL")} but the navigation is " +
                            $"annotated {(isNullable ? "nullable" : "non-nullable")}");
                    }
                }
            }

            Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));

            // Guards against the assertion above passing vacuously if the annotations were ever
            // reverted or the entity files lost their `#nullable enable`.
            Assert.True(
                checkedCount >= 50,
                $"Only {checkedCount} annotated reference navigations were checked; the entity " +
                "models are expected to contribute far more. Did PrintLogApi/Models lose its " +
                "'#nullable enable' headers?");
        }
    }
}
