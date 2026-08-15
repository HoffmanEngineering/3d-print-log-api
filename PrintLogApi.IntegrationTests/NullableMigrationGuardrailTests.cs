using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PrintLogApi;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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
        /// This assertion alone is NOT sufficient, which is why
        /// <see cref="EveryDtoFile_EnablesTheNullableAnnotationContext"/> exists alongside it.
        /// Reflection cannot see a deleted header on a file whose every property already carries an
        /// explicit `?` — an explicitly written `?` emits nullable metadata even in an oblivious
        /// context, so those properties still read as Nullable. That is most of this directory, so
        /// the header has to be checked in source. What this test adds on top is reach: it covers
        /// DTO types declared outside `Models/DTOs/`, which a directory scan never sees.
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

        /// <summary>
        /// The file-level half of the guardrail, and the one that actually holds.
        ///
        /// Deleting a `#nullable enable` header is invisible to reflection once the file's
        /// properties carry explicit `?` annotations — the metadata survives the header, so the
        /// sibling test above goes quiet while the file is silently back in an oblivious context.
        /// The next property added to it is then unannotated by default, and at #45 it flips to
        /// non-nullable along with everything else. Source is the only place that state is
        /// visible, so read it.
        ///
        /// Checks the whole file, not just the first line: a `#nullable disable` further down
        /// re-opens exactly the hole this is closing.
        /// </summary>
        [Fact]
        public void EveryDtoFile_EnablesTheNullableAnnotationContext()
        {
            var dtoDirectory = Path.Combine(RepositoryRoot(), "PrintLogApi", "Models", "DTOs");
            Assert.True(Directory.Exists(dtoDirectory), $"DTO directory not found at {dtoDirectory}.");

            var files = Directory.GetFiles(dtoDirectory, "*.cs", SearchOption.AllDirectories);
            var offenders = new List<string>();

            foreach (var file in files)
            {
                var source = File.ReadAllText(file);
                var relative = Path.GetRelativePath(dtoDirectory, file);

                if (!source.Contains("#nullable enable", System.StringComparison.Ordinal))
                {
                    offenders.Add($"{relative}: missing '#nullable enable'");
                }
                else if (source.Contains("#nullable disable", System.StringComparison.Ordinal))
                {
                    offenders.Add($"{relative}: contains '#nullable disable'");
                }
            }

            Assert.True(
                offenders.Count == 0,
                "Every file under PrintLogApi/Models/DTOs must opt into the nullable annotation " +
                "context until #45 turns it on project-wide:\n" + string.Join("\n", offenders));

            Assert.True(
                files.Length >= 90,
                $"Only {files.Length} DTO source files were found under {dtoDirectory}; ~94 are " +
                "expected. Did the path resolution break and make this test vacuous?");
        }

        /// <summary>
        /// The same file-level check as <see cref="EveryDtoFile_EnablesTheNullableAnnotationContext"/>,
        /// widened to the rest of the project once #44 annotated it: services, MCP tools,
        /// controllers, profiles, middleware and the loose root files.
        ///
        /// It subsumes the DTO-directory test above. Both are kept rather than collapsed because
        /// #45 removes this whole class at once, and the narrower one carries its own vacuity
        /// guard for a directory that is expected to keep growing.
        ///
        /// Migrations are excluded deliberately: all 169 of them produce zero nullable warnings
        /// (measured in #46), they are generated rather than written, and #45 leaves them alone.
        /// </summary>
        [Fact]
        public void EverySourceFile_EnablesTheNullableAnnotationContext()
        {
            var projectDirectory = Path.Combine(RepositoryRoot(), "PrintLogApi");
            Assert.True(Directory.Exists(projectDirectory), $"Project directory not found at {projectDirectory}.");

            var files = Directory.GetFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
                .Where(f => !IsExcludedFromNullableScan(projectDirectory, f))
                .ToList();

            var offenders = new List<string>();

            foreach (var file in files)
            {
                var source = File.ReadAllText(file);
                var relative = Path.GetRelativePath(projectDirectory, file);

                if (!source.Contains("#nullable enable", System.StringComparison.Ordinal))
                {
                    offenders.Add($"{relative}: missing '#nullable enable'");
                }
                else if (source.Contains("#nullable disable", System.StringComparison.Ordinal))
                {
                    offenders.Add($"{relative}: contains '#nullable disable'");
                }
            }

            Assert.True(
                offenders.Count == 0,
                "Every PrintLogApi source file outside Migrations must opt into the nullable " +
                "annotation context until #45 turns it on project-wide. A new file added without " +
                "the header is silently oblivious, and flips to non-nullable at #45 along with " +
                "everything else:\n" + string.Join("\n", offenders));

            Assert.True(
                files.Count >= 250,
                $"Only {files.Count} source files were scanned under {projectDirectory}; ~290 are " +
                "expected. Did the path resolution or the exclusion filter break and make this " +
                "test vacuous?");
        }

        /// <summary>
        /// Build output and generated migrations, matched on path segments so a directory named
        /// e.g. "Binding" is not caught by a substring test for "bin".
        /// </summary>
        private static bool IsExcludedFromNullableScan(string projectDirectory, string file) =>
            Path.GetRelativePath(projectDirectory, file)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj" or "Migrations");

        /// <summary>
        /// Resolves the repo root from this file's compile-time path rather than the working
        /// directory, which for a test run is the output folder and varies between local runs,
        /// `dotnet test`, and CI.
        /// </summary>
        private static string RepositoryRoot([CallerFilePath] string thisFile = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, ".."));
    }
}
