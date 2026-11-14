using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace TenderScout.Api;

/// <summary>
/// Describes in the OpenAPI document how the API is called: with a JWT as a bearer token. An operation that
/// requires a scope names the scheme, the scope, and the 401 and 403 it can answer with.
/// </summary>
internal sealed class BearerSecurityScheme : IOpenApiDocumentTransformer, IOpenApiOperationTransformer
{
    private const string Name = "bearer";

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[Name] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
        };
        return Task.CompletedTask;
    }

    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        // A policy is named as the scope it requires.
        var scopes = context.Description.ActionDescriptor.EndpointMetadata.OfType<IAuthorizeData>()
            .Select(authorize => authorize.Policy)
            .OfType<string>()
            .ToList();
        if (scopes.Count == 0)
        {
            return Task.CompletedTask;
        }

        operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(Name, context.Document)] = scopes }];
        operation.Responses ??= [];
        operation.Responses["401"] = new OpenApiResponse { Description = "No token, or a token that is not valid." };
        operation.Responses["403"] = new OpenApiResponse { Description = $"The token does not have the scope {string.Join(", ", scopes)}." };
        return Task.CompletedTask;
    }
}
