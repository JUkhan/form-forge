using FluentValidation.TestHelper;
using FormForge.Api.Features.Auth.Dtos;
using FormForge.Api.Features.Auth.Validators;

namespace FormForge.Api.Tests.Features.Auth;

public sealed class LoginRequestValidatorTests
{
    private readonly LoginRequestValidator _validator = new();

    [Fact]
    public void Valid_Request_PassesValidation()
    {
        var request = new LoginRequest("test@example.com", "Password1!");

        var result = _validator.TestValidate(request);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void EmptyEmail_FailsValidation()
    {
        var request = new LoginRequest(string.Empty, "Password1!");

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(r => r.Email);
    }

    [Fact]
    public void InvalidEmailFormat_FailsValidation()
    {
        var request = new LoginRequest("not-an-email", "Password1!");

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(r => r.Email);
    }

    [Fact]
    public void EmptyPassword_FailsValidation()
    {
        var request = new LoginRequest("test@example.com", string.Empty);

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(r => r.Password);
    }

    [Fact]
    public void TooLongPassword_FailsValidation()
    {
        // BCrypt truncates input at 72 bytes; validator caps at 72.
        var request = new LoginRequest("test@example.com", new string('a', 73));

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(r => r.Password);
    }

    [Fact]
    public void PasswordAt72CharBoundary_PassesValidation()
    {
        var request = new LoginRequest("test@example.com", new string('a', 72));

        var result = _validator.TestValidate(request);

        result.ShouldNotHaveValidationErrorFor(r => r.Password);
    }
}
