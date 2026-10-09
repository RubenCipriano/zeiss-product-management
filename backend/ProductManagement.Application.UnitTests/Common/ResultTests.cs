using FluentValidation.Results;
using ProductManagement.Application.Common.Errors;
using ProductManagement.Application.Common.Results;
using ProductManagement.Application.Common.Validation;

namespace ProductManagement.Application.UnitTests.Common;

public sealed class ResultTests
{
    [Fact]
    public void AccessingTheValueOfAFailure_IsAProgrammingError()
    {
        Result<int> result = Error.NotFound("x", "missing");

        result.IsFailure.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void ImplicitConversionFromAValue_IsASuccess()
    {
        Result<int> result = 42;

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(42);
        result.Error.ShouldBeNull();
    }

    [Fact]
    public void ValidationFailures_AreKeyedByCamelCasePropertyName()
    {
        var validation = new ValidationResult(
        [
            new ValidationFailure("StockQuantity", "too low"),
            new ValidationFailure("StockQuantity", "too low"),
            new ValidationFailure("Name", "required"),
        ]);

        var error = validation.ToError();

        error.Type.ShouldBe(ErrorType.Validation);
        error.ValidationErrors["stockQuantity"].ShouldBe(["too low"]);
        error.ValidationErrors["name"].ShouldBe(["required"]);
    }
}
