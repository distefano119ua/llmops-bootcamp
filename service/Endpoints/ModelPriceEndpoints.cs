using System.Text.Json.Serialization;
using LlmOps.Infrastructure;

namespace LlmOps.Endpoints;

public sealed record ModelPriceInput(
    [property: JsonPropertyName("completion_tokens_price")] decimal? CompletionTokensPrice,
    [property: JsonPropertyName("prompt_tokens_price")] decimal? PromptTokensPrice,
    string? User);

public static class ModelPriceEndpoints
{
    // PostgreSQL NUMERIC(18, 8): ten integer digits, eight fractional digits.
    private const decimal MaximumPrice = 9_999_999_999.99999999m;

    public static void MapModelPriceEndpoints(this WebApplication app)
    {
        app.MapPut("/model-prices/{model}", async (string model, ModelPriceInput body,
            ModelPriceRepository prices) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(model))
                errors["model"] = ["Model is required."];
            if (string.IsNullOrWhiteSpace(body.User))
                errors["user"] = ["User is required."];
            ValidatePrice(body.CompletionTokensPrice, "completion_tokens_price", errors);
            ValidatePrice(body.PromptTokensPrice, "prompt_tokens_price", errors);

            if (errors.Count > 0)
                return (IResult)Results.ValidationProblem(errors);

            var result = await prices.Upsert(model.Trim(), body.CompletionTokensPrice!.Value,
                body.PromptTokensPrice!.Value, body.User!.Trim());
            return Results.Ok(result);
        });
    }

    private static void ValidatePrice(decimal? price, string field, Dictionary<string, string[]> errors)
    {
        if (price is null)
            errors[field] = ["Price is required."];
        else if (price < 0 || price > MaximumPrice)
            errors[field] = ["Price must be between 0 and 9999999999.99999999 USD per 1000 tokens."];
        else if (decimal.Round(price.Value, 8) != price.Value)
            errors[field] = ["Price must have at most eight decimal places."];
    }
}
