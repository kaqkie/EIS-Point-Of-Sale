using Microsoft.Extensions.Logging.Abstractions;
using PointOfSale.App.Services;
using PointOfSale.Mra.Contracts.Common;
using PointOfSale.Mra.Http;
using PointOfSale.Mra.Services;
using Xunit;

namespace PointOfSale.Tests;

public sealed class MraEisResponseEvaluatorTests
{
    private readonly IMraEisResponseEvaluator _evaluator =
        new MraEisResponseEvaluator(NullLogger<MraEisResponseEvaluator>.Instance);

    [Fact]
    public void Evaluate_Success_WhenStatusCodeIsOne()
    {
        var response = new EisApiResponse<object> { StatusCode = 1, Remark = "OK", Data = new object() };
        var result = _evaluator.Evaluate(response);

        Assert.True(result.IsSuccess);
        Assert.Equal(MraEisFailureCategory.None, result.Category);
        Assert.Equal(MraEisRecommendedAction.None, result.RecommendedAction);
    }

    [Theory]
    [InlineData(MraEisStatusCodes.ServerError, MraEisFailureCategory.ServerError, MraEisRecommendedAction.RetryLater, false)]
    [InlineData(MraEisStatusCodes.AuthenticationFailure, MraEisFailureCategory.AuthenticationFailure, MraEisRecommendedAction.RefreshCredentials, false)]
    [InlineData(MraEisStatusCodes.BusinessRuleViolation, MraEisFailureCategory.BusinessRuleViolation, MraEisRecommendedAction.BlockUntilReady, true)]
    [InlineData(MraEisStatusCodes.OutdatedConfiguration, MraEisFailureCategory.OutdatedConfiguration, MraEisRecommendedAction.SyncLatestConfigs, false)]
    [InlineData(MraEisStatusCodes.TerminalDeactivated, MraEisFailureCategory.TerminalDeactivated, MraEisRecommendedAction.ReactivateTerminal, true)]
    public void Evaluate_StatusCodes_MapToExpectedCategoryAndAction(
        int statusCode,
        MraEisFailureCategory category,
        MraEisRecommendedAction action,
        bool shouldQuarantine)
    {
        var result = _evaluator.Evaluate(statusCode, "test remark", errors: null);

        Assert.False(result.IsSuccess);
        Assert.Equal(category, result.Category);
        Assert.Equal(action, result.RecommendedAction);
        Assert.Equal(shouldQuarantine, result.ShouldQuarantine);
        Assert.False(string.IsNullOrWhiteSpace(result.OperatorTitle));
        Assert.False(string.IsNullOrWhiteSpace(result.OperatorMessage));
        Assert.Contains(statusCode.ToString(), result.TechnicalDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_MissingMandatoryField_Quarantines()
    {
        var errors = new[]
        {
            new EisApiError
            {
                ErrorCode = MraEisStatusCodes.MissingMandatoryField,
                FieldName = "invoiceHeader.sellerTIN",
                ErrorMessage = "Field is required"
            }
        };

        var result = _evaluator.Evaluate(statusCode: 0, remark: "Validation failed", errors);

        Assert.Equal(MraEisFailureCategory.MissingMandatoryField, result.Category);
        Assert.Equal(MraEisRecommendedAction.QuarantinePayload, result.RecommendedAction);
        Assert.True(result.ShouldQuarantine);
        Assert.Contains("sellerTIN", result.OperatorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_InvalidFieldValue_Quarantines()
    {
        var errors = new[]
        {
            new EisApiError
            {
                ErrorCode = MraEisStatusCodes.InvalidFieldValue,
                FieldName = "invoiceHeader.paymentMethod",
                ErrorMessage = "Value does not match pattern"
            }
        };

        var result = _evaluator.Evaluate(statusCode: 0, remark: "Validation failed", errors);

        Assert.Equal(MraEisFailureCategory.InvalidFieldValue, result.Category);
        Assert.Equal(MraEisRecommendedAction.QuarantinePayload, result.RecommendedAction);
        Assert.True(result.ShouldQuarantine);
        Assert.Contains("paymentMethod", result.TechnicalDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_FieldErrorTakesPrecedenceOverStatusCode()
    {
        var errors = new[]
        {
            new EisApiError
            {
                ErrorCode = MraEisStatusCodes.MissingMandatoryField,
                FieldName = "invoiceSummary.amountTendered",
                ErrorMessage = "required"
            }
        };

        var result = _evaluator.Evaluate(MraEisStatusCodes.ServerError, "ignored", errors);

        Assert.Equal(MraEisFailureCategory.MissingMandatoryField, result.Category);
        Assert.Equal(MraEisRecommendedAction.QuarantinePayload, result.RecommendedAction);
    }

    [Fact]
    public void EvaluateException_ParsesLogicalStatusFromHttpBody()
    {
        var body = """
            {"statusCode":-199999,"remark":"Terminal deactivated","errors":[]}
            """;
        var ex = new MraApiException("MRA EIS HTTP 403", 403, body);

        var result = _evaluator.EvaluateException(ex);

        Assert.Equal(MraEisFailureCategory.TerminalDeactivated, result.Category);
        Assert.Equal(MraEisRecommendedAction.ReactivateTerminal, result.RecommendedAction);
        Assert.True(result.ShouldQuarantine);
    }

    [Fact]
    public void EvaluateException_ParsesFieldValidationFromHttpBody()
    {
        var body = """
            {"statusCode":0,"remark":"Validation failed","errors":[{"errorCode":-200011,"fieldName":"invoiceHeader.siteId","errorMessage":"length invalid"}]}
            """;
        var ex = new MraApiException("MRA EIS HTTP 500", 500, body);

        var result = _evaluator.EvaluateException(ex);

        Assert.Equal(MraEisFailureCategory.InvalidFieldValue, result.Category);
        Assert.True(result.ShouldQuarantine);
    }

    [Fact]
    public void EvaluateException_OpaqueSandboxInternalError_RetriesNotQuarantines()
    {
        var ex = new MraApiException(
            "MRA EIS HTTP 500: Internal Server Error — An internal error occurred",
            500,
            """{"message":"An internal error occurred"}""");

        var result = _evaluator.EvaluateException(ex);

        Assert.Equal(MraEisFailureCategory.ServerError, result.Category);
        Assert.Equal(MraEisRecommendedAction.RetryLater, result.RecommendedAction);
        Assert.False(result.ShouldQuarantine);
    }

    [Fact]
    public void CashierOperatorMessages_FromEvaluation_MapsTitles()
    {
        var evaluation = _evaluator.Evaluate(MraEisStatusCodes.OutdatedConfiguration, "stale", null);
        var message = CashierOperatorMessages.FromEvaluation(evaluation);

        Assert.Equal("MRA configuration outdated", message.Title);
        Assert.Contains("get-latest-configs", message.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OperatorMessageSeverity.Warning, message.Severity);
    }

    [Fact]
    public void Evaluate_PurchaseAuthorizationRemark_Quarantines()
    {
        var result = _evaluator.Evaluate(
            statusCode: -2,
            remark: "B2B transaction for this buyer requires a Purchase Authorization Code.",
            errors: null);

        Assert.Equal(MraEisFailureCategory.MissingMandatoryField, result.Category);
        Assert.Equal(MraEisRecommendedAction.QuarantinePayload, result.RecommendedAction);
        Assert.True(result.ShouldQuarantine);
        Assert.Contains("Authorization Code", result.OperatorTitle, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_CatalogDescriptionMismatchRemark_Quarantines()
    {
        var result = _evaluator.Evaluate(
            statusCode: -2,
            remark: "The description for product '534197687152' doesn't match the one configured for site 'SITE'. Please use 'Air Cleaner  AB399601AB'.",
            errors: null);

        Assert.Equal(MraEisFailureCategory.InvalidFieldValue, result.Category);
        Assert.Equal(MraEisRecommendedAction.QuarantinePayload, result.RecommendedAction);
        Assert.True(result.ShouldQuarantine);
    }
}
