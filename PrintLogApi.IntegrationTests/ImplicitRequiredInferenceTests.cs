using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace PrintLogApi.IntegrationTests
{
    /// <summary>
    /// Guards the one behaviour change that enabling nullable reference types makes to the HTTP
    /// contract, and that no compiler diagnostic can catch.
    ///
    /// With the annotation context on, MVC's <c>DataAnnotationsMetadataProvider</c> attaches an
    /// implicit <c>[Required(AllowEmptyStrings = true)]</c> to every non-nullable REFERENCE type it
    /// binds. A request that omits that value stops binding null and starts returning 400 — a
    /// working client breaks, and nothing in the build says so.
    ///
    /// #46 suppressed the inference project-wide while the annotation PRs (#42-#44) landed. #45
    /// removes the suppression, which is only safe because every bound reference type is annotated
    /// nullable and therefore has nothing for the inference to attach to. This test is what makes
    /// that a durable property instead of a one-time review: it fails the moment someone adds a
    /// non-nullable bound reference type, naming the exact member and the request it would break.
    ///
    /// Deliberately covers PARAMETERS as well as model properties. #45's issue text framed the risk
    /// as a review of the ~94 DTO files, but the live exposure was in controller action parameters —
    /// <c>[FromQuery] string searchText</c> and friends, which bind null today on every request that
    /// omits them.
    ///
    /// Value types are excluded throughout: MVC treats a non-nullable <c>int</c> or enum as required
    /// too, but it did so long before the nullable migration and the annotation context does not
    /// change it. Only reference types are in scope here.
    /// </summary>
    public class ImplicitRequiredInferenceTests : IClassFixture<CustomWebApplicationFactory>
    {
        private readonly CustomWebApplicationFactory _factory;

        public ImplicitRequiredInferenceTests(CustomWebApplicationFactory factory)
        {
            _factory = factory;
        }

        /// <summary>
        /// Bounds the recursion into nested bound models. Deep enough for the DTO graphs this API
        /// actually binds, shallow enough that a cyclic entity navigation cannot run away even if
        /// the visited-type set were ever bypassed.
        /// </summary>
        private const int MaxDepth = 4;

        /// <summary>
        /// The inference is evaluated with the suppression explicitly OFF rather than against the
        /// app's own configuration, and that is the point: it asserts the codebase is SAFE to run
        /// without the suppression, not merely that the suppression is currently switched on. The
        /// two are different properties, and only the first one survives someone deleting a line in
        /// Startup.
        /// </summary>
        [Fact]
        public void NoBoundReferenceType_WouldGainAnImplicitRequiredAttribute()
        {
            using var factory = _factory.WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                    services.Configure<MvcOptions>(options =>
                        options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = false)));

            var actions = factory.Services
                .GetRequiredService<IActionDescriptorCollectionProvider>()
                .ActionDescriptors.Items
                .OfType<ControllerActionDescriptor>()
                .ToList();

            // The abstract ModelMetadataProvider, not the interface: GetMetadataForParameter is
            // declared on the base class only, and it is the overload that reads a parameter's own
            // nullability rather than falling back to the bare type.
            var metadataProvider = (ModelMetadataProvider)factory.Services
                .GetRequiredService<IModelMetadataProvider>();

            var offenders = new SortedSet<string>(StringComparer.Ordinal);
            var actionsInspected = 0;

            foreach (var action in actions)
            {
                actionsInspected++;
                var route = $"{action.ControllerName}.{action.ActionName}";

                foreach (var parameter in action.Parameters)
                {
                    if (IsNotBoundFromTheRequest(parameter))
                    {
                        continue;
                    }

                    var metadata = parameter is ControllerParameterDescriptor controllerParameter
                        ? metadataProvider.GetMetadataForParameter(controllerParameter.ParameterInfo)
                        : metadataProvider.GetMetadataForType(parameter.ParameterType);

                    Inspect(metadata, $"{route}({parameter.Name})", offenders, new HashSet<Type>(), 0);
                }
            }

            Assert.True(
                offenders.Count == 0,
                "These bound reference types are non-nullable, so MVC would attach an implicit " +
                "[Required] and reject requests that omit them with a 400. Annotate each one " +
                "nullable to preserve the current contract, or -- if it really is required -- give " +
                "it an explicit [Required] so the intent is recorded in source:\n  " +
                string.Join("\n  ", offenders));

            // Guards against the assertion above passing vacuously if action discovery ever breaks
            // and returns nothing to inspect.
            Assert.True(
                actionsInspected >= 100,
                $"Only {actionsInspected} controller actions were discovered; far more are " +
                "expected. Did the action descriptor lookup break?");
        }

        /// <summary>
        /// Walks a bound model, recording every non-nullable reference type the implicit-[Required]
        /// inference would attach to.
        /// </summary>
        private static void Inspect(
            ModelMetadata metadata,
            string path,
            SortedSet<string> offenders,
            HashSet<Type> visited,
            int depth)
        {
            if (metadata.ModelType.IsValueType)
            {
                return;
            }

            // IsRequired is the metadata layer's own answer to "will validation demand a value
            // here", which is exactly the question being asked -- reading it beats re-deriving the
            // inference rule and drifting from whatever MVC actually does.
            if (metadata.IsRequired && !HasExplicitRequiredAttribute(metadata))
            {
                offenders.Add($"{path} : {FriendlyTypeName(metadata.ModelType)}");
            }

            // Stop at the assembly boundary. Framework types like IFormFile and IHeaderDictionary
            // bind the same way they always have, and their members are not ours to annotate --
            // recursing in only produces findings nobody can act on (IFormFile.Headers.Keys,
            // Array.SyncRoot). The parameter itself is still reported above, which is the part that
            // belongs to this API's contract.
            if (!IsOurs(metadata.ModelType))
            {
                return;
            }

            if (depth >= MaxDepth || !visited.Add(metadata.ModelType))
            {
                return;
            }

            foreach (var property in metadata.Properties)
            {
                Inspect(property, $"{path}.{property.PropertyName}", offenders, visited, depth + 1);
            }

            visited.Remove(metadata.ModelType);
        }

        /// <summary>
        /// An explicitly written <c>[Required]</c> is a deliberate contract decision that predates
        /// this migration and must not be reported. The implicit one is indistinguishable from it
        /// in <see cref="ModelMetadata.ValidatorMetadata"/> -- both are a plain RequiredAttribute --
        /// so the only way to tell them apart is to go back to the declaring member and look.
        /// </summary>
        private static bool HasExplicitRequiredAttribute(ModelMetadata metadata)
        {
            if (metadata.MetadataKind == ModelMetadataKind.Property &&
                metadata.ContainerType is not null &&
                metadata.PropertyName is not null)
            {
                var property = metadata.ContainerType.GetProperty(
                    metadata.PropertyName,
                    BindingFlags.Public | BindingFlags.Instance);

                return property?.IsDefined(typeof(RequiredAttribute), inherit: true) == true;
            }

            // Parameter and type-level metadata: a parameter's own [Required] is visible through
            // the descriptor, and a type-level binding has no member to carry one.
            return false;
        }

        /// <summary>
        /// Parameters MVC does not bind from the request body, route, query or form cannot be
        /// affected by the inference -- services are resolved from DI and cancellation tokens are
        /// supplied by the framework.
        /// </summary>
        private static bool IsNotBoundFromTheRequest(ParameterDescriptor parameter)
        {
            var source = parameter.BindingInfo?.BindingSource;

            return source == BindingSource.Services ||
                   source == BindingSource.Special ||
                   parameter.ParameterType == typeof(System.Threading.CancellationToken);
        }

        /// <summary>
        /// Whether a type is declared by this API, and so is something the migration can annotate.
        /// A closed generic counts when its arguments do -- <c>List&lt;PrintStatus&gt;</c> reaches
        /// our own enum through the framework's List.
        /// </summary>
        private static bool IsOurs(Type type)
        {
            if (type.Assembly == typeof(Startup).Assembly)
            {
                return true;
            }

            return type.IsGenericType && type.GetGenericArguments().Any(IsOurs);
        }

        private static string FriendlyTypeName(Type type) =>
            type.IsGenericType
                ? $"{type.Name[..type.Name.IndexOf('`')]}<{string.Join(", ", type.GetGenericArguments().Select(FriendlyTypeName))}>"
                : type.Name;
    }
}
