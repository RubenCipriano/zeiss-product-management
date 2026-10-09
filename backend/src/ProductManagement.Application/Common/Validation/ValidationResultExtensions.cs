using System.Text.Json;
using FluentValidation.Results;
using ProductManagement.Application.Common.Errors;

namespace ProductManagement.Application.Common.Validation;

internal static class ValidationResultExtensions
{
    /// <summary>
    /// Converts FluentValidation failures to a validation <see cref="Error"/>. Keys are camelCase so they match
    /// the JSON property names the client sent.
    /// </summary>
    public static Error ToError(this ValidationResult result) =>
        Error.Validation(result.Errors
            .GroupBy(failure => JsonNamingPolicy.CamelCase.ConvertName(failure.PropertyName))
            .ToDictionary(
                group => group.Key,
                group => group.Select(failure => failure.ErrorMessage).Distinct().ToArray()));
}
