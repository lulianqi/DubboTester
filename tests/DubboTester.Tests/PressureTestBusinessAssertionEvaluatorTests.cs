using DubboTester.Models;
using DubboTester.Services;
using Xunit;

namespace DubboTester.Tests;

public sealed class PressureTestBusinessAssertionEvaluatorTests
{
    private const string Response = """
        {
          "code": 200,
          "success": true,
          "message": "operation completed",
          "data": {
            "items": [ { "id": 42, "name": "alpha" } ]
          }
        }
        """;

    [Fact]
    public void Evaluate_SupportsContainsAndJsonPathWithAndCombination()
    {
        var configuration = new BusinessAssertionConfiguration
        {
            Join = BusinessAssertionJoin.And,
            Conditions =
            [
                new BusinessAssertionCondition
                {
                    Type = BusinessAssertionType.ResponseContains,
                    ExpectedValue = "operation completed"
                },
                new BusinessAssertionCondition
                {
                    Type = BusinessAssertionType.JsonPath,
                    JsonPath = "$.code",
                    Operator = BusinessAssertionOperator.Equals,
                    ExpectedValue = "200"
                },
                new BusinessAssertionCondition
                {
                    Type = BusinessAssertionType.JsonPath,
                    JsonPath = "$.data.items[0].id",
                    Operator = BusinessAssertionOperator.GreaterThan,
                    ExpectedValue = "40"
                }
            ]
        };

        BusinessAssertionEvaluation result =
            PressureTestBusinessAssertionEvaluator.Evaluate(configuration, Response);

        Assert.True(result.Success);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Evaluate_SupportsBracketPropertiesExistsAndOrCombination()
    {
        var configuration = new BusinessAssertionConfiguration
        {
            Join = BusinessAssertionJoin.Or,
            Conditions =
            [
                new BusinessAssertionCondition
                {
                    Type = BusinessAssertionType.JsonPath,
                    JsonPath = "$['missing']",
                    Operator = BusinessAssertionOperator.Exists
                },
                new BusinessAssertionCondition
                {
                    Type = BusinessAssertionType.JsonPath,
                    JsonPath = "$['data']['items'][0]['name']",
                    Operator = BusinessAssertionOperator.Contains,
                    ExpectedValue = "pha"
                }
            ]
        };

        Assert.True(PressureTestBusinessAssertionEvaluator.Evaluate(configuration, Response).Success);
    }

    [Fact]
    public void Evaluate_ReturnsReadableFailureForUnmetCondition()
    {
        var configuration = new BusinessAssertionConfiguration
        {
            Conditions =
            [
                new BusinessAssertionCondition
                {
                    Type = BusinessAssertionType.JsonPath,
                    JsonPath = "$.code",
                    Operator = BusinessAssertionOperator.Equals,
                    ExpectedValue = "500"
                }
            ]
        };

        BusinessAssertionEvaluation result =
            PressureTestBusinessAssertionEvaluator.Evaluate(configuration, Response);

        Assert.False(result.Success);
        Assert.Contains("业务断言失败", result.Error);
        Assert.Contains("实际为 200", result.Error);
    }

    [Fact]
    public void Validate_RejectsIncompleteConditions()
    {
        var configuration = new BusinessAssertionConfiguration
        {
            Conditions =
            [
                new BusinessAssertionCondition
                {
                    Type = BusinessAssertionType.JsonPath,
                    JsonPath = "code",
                    Operator = BusinessAssertionOperator.Equals,
                    ExpectedValue = "200"
                }
            ]
        };

        Assert.Throws<ArgumentException>(() =>
            PressureTestBusinessAssertionEvaluator.Validate(configuration));
    }
}
