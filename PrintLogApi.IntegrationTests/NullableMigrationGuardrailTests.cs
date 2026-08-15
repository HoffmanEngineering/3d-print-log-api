using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PrintLogApi;
using System.Collections.Generic;
using System.Linq;
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

        /// <summary>
        /// A new DTO file added without a `#nullable enable` header is invisible: it compiles, it
        /// passes review, and its properties are silently nullable-oblivious until #45 flips the
        /// project and they all become non-nullable at once — reinstating the implicit-[Required]
        /// problem across whatever was missed. Reflection can see that state (Unknown), so assert
        /// there is none.
        ///
        /// Deliberately narrow: it checks that the annotation context is ON, not what any given
        /// property was annotated as. Nullability per property is a judgement call recorded in
        /// AGENTS.md, not something to freeze in a test.
        ///
        /// One case it cannot see: deleting `#nullable enable` from a file whose every property
        /// already carries an explicit `?`. An explicitly written `?` emits nullable metadata even
        /// in an oblivious context, so those properties still read as Nullable. The case that
        /// matters — a property declared with no `?` and no context — is caught.
        /// </summary>
        [Fact]
        public void EveryDtoProperty_HasAnAnnotationContext()
        {
            var nullabilityContext = new NullabilityInfoContext();
            var oblivious = new List<string>();
            var checkedCount = 0;

            var dtoTypes = typeof(Startup).Assembly
                .GetTypes()
                .Where(t => t.Namespace is not null
                            && t.Namespace.StartsWith("PrintLogApi.Models.DTOs", System.StringComparison.Ordinal));

            foreach (var type in dtoTypes)
            {
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (property.PropertyType.IsValueType)
                    {
                        continue;
                    }

                    checkedCount++;

                    if (nullabilityContext.Create(property).ReadState == NullabilityState.Unknown)
                    {
                        oblivious.Add($"{type.FullName}.{property.Name}");
                    }
                }
            }

            Assert.True(
                oblivious.Count == 0,
                "These DTO properties are in a nullable-oblivious context. The declaring file is " +
                "missing its '#nullable enable' header:\n" + string.Join("\n", oblivious));

            Assert.True(
                checkedCount >= 200,
                $"Only {checkedCount} reference-typed DTO properties were found; PrintLogApi/Models/DTOs " +
                "is expected to contribute far more. Did the assertion above go vacuous?");
        }
    }
}
