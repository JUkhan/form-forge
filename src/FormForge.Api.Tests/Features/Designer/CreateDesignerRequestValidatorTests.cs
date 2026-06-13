using FluentValidation.TestHelper;
using FormForge.Api.Features.Designer.Dtos;
using FormForge.Api.Features.Designer.Validators;

namespace FormForge.Api.Tests.Features.Designer;

public sealed class CreateDesignerRequestValidatorTests
{
    private readonly CreateDesignerRequestValidator _validator = new();

    // Identifier-shape rules (empty/length/charset/reserved) live in SafeIdentifier,
    // not in FluentValidation — see SafeIdentifierTests for those. The validator only
    // guards DisplayName so every 422 for a bad designerId carries the AC-2
    // IDENTIFIER_INVALID envelope from the service layer.

    [Theory]
    [InlineData("CRUD")]
    [InlineData("VIEW")]
    public void Valid_Request_Passes(string mode)
    {
        var req = new CreateDesignerRequest("incident_report", "Incident Report", mode);
        _validator.TestValidate(req).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Empty_DisplayName_Fails()
    {
        var req = new CreateDesignerRequest("incident_report", string.Empty, "CRUD");
        _validator.TestValidate(req).ShouldHaveValidationErrorFor(r => r.DisplayName);
    }

    [Fact]
    public void DisplayName_Over_200_Chars_Fails()
    {
        var req = new CreateDesignerRequest("incident_report", new string('x', 201), "CRUD");
        _validator.TestValidate(req).ShouldHaveValidationErrorFor(r => r.DisplayName);
    }

    [Theory]
    [InlineData("has\nnewline")]
    [InlineData("has\ttab")]
    [InlineData("has\0null")]
    public void DisplayName_With_ControlCharacters_Fails(string displayName)
    {
        var req = new CreateDesignerRequest("incident_report", displayName, "CRUD");
        _validator.TestValidate(req).ShouldHaveValidationErrorFor(r => r.DisplayName);
    }

    [Fact]
    public void Validator_Does_Not_Constrain_DesignerId_Shape()
    {
        // Negative assurance — the validator must let identifier-shape failures
        // through to SafeIdentifier so the 422 envelope stays uniform across all
        // invalid identifiers (AC-2 contract).
        var req = new CreateDesignerRequest("Has-Bad-Chars", "Valid Display", "CRUD");
        _validator.TestValidate(req).ShouldNotHaveValidationErrorFor(r => r.DesignerId);
    }

    // FR-54 AC-1 — mode is required and must be CRUD or VIEW.
    [Fact]
    public void Empty_Mode_Fails()
    {
        var req = new CreateDesignerRequest("incident_report", "Incident Report", string.Empty);
        _validator.TestValidate(req).ShouldHaveValidationErrorFor(r => r.Mode);
    }

    [Theory]
    [InlineData("READ")]
    [InlineData("crud")]
    [InlineData("Crud")]
    [InlineData("DELETE")]
    public void Invalid_Mode_Fails(string mode)
    {
        var req = new CreateDesignerRequest("incident_report", "Incident Report", mode);
        _validator.TestValidate(req).ShouldHaveValidationErrorFor(r => r.Mode);
    }
}
