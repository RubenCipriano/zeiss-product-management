using System.Text.Json;
using FluentValidation.Results;
using ProductManagement.Core.Common.Errors;

namespace ProductManagement.Core.Common.Validation;

/// <summary>
/// Turns a FluentValidation result into our <see cref="Error"/>, so invalid input travels the same way as every other
/// expected failure (a <c>Result</c> that the API turns into a 400).
/// </summary>
public static class ValidationResultExtensions
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
