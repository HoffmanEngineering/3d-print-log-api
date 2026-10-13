using System.Reflection;
using BrunoZell.ModelBinding;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace PrintLogApi.OpenApi;

/// <summary>
/// Documents the request body of an action that reads <c>Request.Body</c> itself rather than
/// binding a parameter, which leaves ApiExplorer with nothing to describe (#152).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class OpenApiRequestBodyAttribute(Type type) : Attribute
{
    public Type Type { get; } = type;

    public string ContentType { get; init; } = "application/json";
}

/// <summary>
/// Applies <see cref="OpenApiRequestBodyAttribute"/>. Never replaces a body ApiExplorer found.
/// </summary>
public class OpenApiRequestBodyOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (operation.RequestBody is not null
            || context.MethodInfo.GetCustomAttribute<OpenApiRequestBodyAttribute>() is not { } body)
        {
            return;
        }

        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                [body.ContentType] = new()
                {
                    Schema = context.SchemaGenerator.GenerateSchema(body.Type, context.SchemaRepository),
                },
            },
        };
    }
}

/// <summary>
/// Restores the form fields bound with <see cref="JsonModelBinder"/> — a form part whose value is
/// a JSON document, as the OctoPrint Webhooks plugin sends <c>job</c>, <c>extra</c> and
/// <c>meta</c> (#152). ApiExplorer leaves a field with a custom binder out of the form schema
/// entirely, so the document described the webhook without the fields it reads most.
///
/// Each one is added as an object schema with a part <c>contentType</c> of
/// <c>application/json</c>, which is how OpenAPI 3.0 says "this multipart field is JSON".
/// </summary>
public class JsonFormPartsOperationFilter : IOperationFilter
{
    private const string JsonContentType = "application/json";

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (operation.RequestBody?.Content is not { } content)
        {
            return;
        }

        var jsonParts = context.MethodInfo.GetParameters()
            .Where(p => p.GetCustomAttribute<FromFormAttribute>() is not null)
            .SelectMany(p => p.ParameterType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(p => p.GetCustomAttribute<ModelBinderAttribute>()?.BinderType == typeof(JsonModelBinder))
            .ToList();
        if (jsonParts.Count == 0)
        {
            return;
        }

        foreach (var mediaType in content.Values)
        {
            if (mediaType.Schema is not { } schema)
            {
                continue;
            }

            foreach (var part in jsonParts)
            {
                if (schema.Properties.ContainsKey(part.Name))
                {
                    continue;
                }

                schema.Properties[part.Name] = context.SchemaGenerator.GenerateSchema(part.PropertyType, context.SchemaRepository);
                mediaType.Encoding[part.Name] = new OpenApiEncoding { ContentType = JsonContentType };
            }
        }
    }
}

/// <summary>
/// Documents a JSON request body as <c>application/json</c> only (#152).
///
/// MVC's JSON input formatter also accepts <c>text/json</c> and <c>application/*+json</c>, and
/// ApiExplorer lists all three. openapi-python-client rejects the whole operation over the extra
/// two, which dropped 33 write endpoints from a generated client without failing the build. The
/// aliases keep working; the document just stops offering them.
/// </summary>
public class JsonRequestBodyMediaTypesOperationFilter : IOperationFilter
{
    private static readonly string[] Aliases = ["text/json", "application/*+json"];

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (operation.RequestBody?.Content is not { } content || !content.ContainsKey("application/json"))
        {
            return;
        }

        foreach (var alias in Aliases)
        {
            content.Remove(alias);
        }
    }
}
