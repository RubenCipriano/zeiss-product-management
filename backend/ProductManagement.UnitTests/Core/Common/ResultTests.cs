using FluentValidation.Results;
using ProductManagement.Core.Common.Errors;
using ProductManagement.Core.Common.Results;
using ProductManagement.Core.Common.Validation;

namespace ProductManagement.UnitTests.Core.Common;

public class ResultTests
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
