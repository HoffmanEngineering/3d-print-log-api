using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace PrintLogApi.OpenApi;

/// <summary>
/// Names the members of every integer enum (#152).
///
/// Enums are serialized as numbers, and the web app, the slicer plugins and the printer
/// connectors all send and parse those numbers, so switching the wire format to strings would be
/// a breaking change. The names are added as vendor extensions instead, which client generators
/// turn into named members over the same integer values: <c>x-enumNames</c> is read by NSwag and
/// openapi-typescript, <c>x-enum-varnames</c> by openapi-generator. The description also lists
/// the mapping, for people and agents reading the document rather than generating from it.
/// </summary>
public class EnumNamesSchemaFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        var type = context.Type;
        if (!type.IsEnum || schema.Enum is not { Count: > 0 })
        {
            return;
        }

        var members = new List<(long Value, string Name)>();
        foreach (var value in schema.Enum)
        {
            // A string-valued enum already carries its names; anything else is not ours to label.
            long? number = value switch
            {
                OpenApiInteger i => i.Value,
                OpenApiLong l => l.Value,
                _ => null,
            };
            if (number is not { } n || Enum.GetName(type, Enum.ToObject(type, n)) is not { } name)
            {
                return;
            }

            members.Add((n, name));
        }

        var names = new OpenApiArray();
        names.AddRange(members.Select(m => new OpenApiString(m.Name)));
        schema.Extensions["x-enumNames"] = names;

        var varNames = new OpenApiArray();
        varNames.AddRange(members.Select(m => new OpenApiString(m.Name)));
        schema.Extensions["x-enum-varnames"] = varNames;

        var mapping = string.Join(", ", members.Select(m => $"`{m.Value}` = {m.Name}"));
        schema.Description = string.IsNullOrWhiteSpace(schema.Description)
            ? $"Sent as an integer: {mapping}."
            : $"{schema.Description.TrimEnd()}\n\nSent as an integer: {mapping}.";
    }
}
