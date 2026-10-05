using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace PrintLogApi.Email.Templates;

public interface IEmailTemplateRenderer
{
    /// <summary>Renders a Razor component to an HTML string.</summary>
    Task<string> RenderAsync<TComponent>(IDictionary<string, object?> parameters) where TComponent : IComponent;
}

/// <summary>Static Razor rendering with the in-box <see cref="HtmlRenderer"/>; no browser, no circuit.</summary>
public sealed class EmailTemplateRenderer(IServiceProvider services, ILoggerFactory loggerFactory) : IEmailTemplateRenderer
{
    public async Task<string> RenderAsync<TComponent>(IDictionary<string, object?> parameters) where TComponent : IComponent
    {
        await using var renderer = new HtmlRenderer(services, loggerFactory);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<TComponent>(ParameterView.FromDictionary(parameters));
            return output.ToHtmlString();
        });
    }
}
