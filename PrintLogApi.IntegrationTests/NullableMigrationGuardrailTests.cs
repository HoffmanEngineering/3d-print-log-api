using System.Collections.Generic;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace PrintLogApi.IntegrationTests
{
    /// <summary>
    /// What is left of the nullable reference type migration's scaffolding (#46), now that #45 has
    /// flipped PrintLogApi to &lt;Nullable&gt;enable&lt;/Nullable&gt;.
    ///
    /// The file-level checks this class used to carry are gone. They existed to prove that every
    /// source file had opted into the annotation context with its own "#nullable enable" header,
    /// because a file that had not was invisible: it compiled, it reviewed cleanly, and its
    /// properties would flip to non-nullable all at once at the project flip. The project setting
    /// now guarantees what those tests asserted, and &lt;WarningsAsErrors&gt;nullable&lt;/WarningsAsErrors&gt;
    /// keeps it that way, so re-checking it in a test would only assert that MSBuild works. The
    /// headers themselves have since been removed; the only #nullable directives left in the
    /// solution are EF's generated "#nullable disable" under Migrations/.
    ///
    /// The navigation check below is NOT scaffolding and stays permanently. See its own comment.
    ///
    /// The other half of the migration's lasting guard lives in
    /// <see cref="ImplicitRequiredInferenceTests"/>, which covers the HTTP contract.
    /// </summary>
    public class NullableMigrationGuardrailTests : IClassFixture<CustomWebApplicationFactory>
    {
        private readonly CustomWebApplicationFactory _factory;

        public NullableMigrationGuardrailTests(CustomWebApplicationFactory factory)
        {
            _factory = factory;
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

                    // Unknown means the declaring type is in a nullable-oblivious context. That was
                    // routine while the migration was in flight; since #45 turned the annotation
                    // context on project-wide it can only be a type from an oblivious dependency,
                    // which is not ours to annotate.
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
            // reverted or the model failed to build out.
            Assert.True(
                checkedCount >= 50,
                $"Only {checkedCount} annotated reference navigations were checked; the entity " +
                "models are expected to contribute far more. Did the model fail to build?");
        }
    }
}
