using FluentAssertions;
using WorkerRClone.Services.Utils;
using Xunit;

namespace WorkerRClone.Tests.Services.Utils;

public class RClonePasswordObscurerTests
{
    [Fact]
    public void Obscure_ThenReveal_ReturnsOriginalPassword()
    {
        // Arrange
        var password = "MySecretPassword123!";
        
        // Act
        var obscured = RClonePasswordObscurer.Obscure(password);
        var revealed = RClonePasswordObscurer.Reveal(obscured);
        
        // Assert
        revealed.Should().Be(password);
    }

    [Fact]
    public void Obscure_WithSpecialCharacters_RoundTripsCorrectly()
    {
        // Arrange
        var password = "p@$$w0rd!#%^&*()_+-=[]{}|;':\",./<>?`~";
        
        // Act
        var obscured = RClonePasswordObscurer.Obscure(password);
        var revealed = RClonePasswordObscurer.Reveal(obscured);
        
        // Assert
        revealed.Should().Be(password);
    }

    [Fact]
    public void Obscure_WithUnicodeCharacters_RoundTripsCorrectly()
    {
        // Arrange
        var password = "Passwörd™_日本語_🔐";
        
        // Act
        var obscured = RClonePasswordObscurer.Obscure(password);
        var revealed = RClonePasswordObscurer.Reveal(obscured);
        
        // Assert
        revealed.Should().Be(password);
    }

    [Fact]
    public void Obscure_ProducesDifferentOutputEachTime()
    {
        // Arrange
        var password = "test";
        
        // Act
        var obscured1 = RClonePasswordObscurer.Obscure(password);
        var obscured2 = RClonePasswordObscurer.Obscure(password);
        
        // Assert - Due to random nonce, outputs should differ
        obscured1.Should().NotBe(obscured2);
        
        // But both should reveal to the same password
        RClonePasswordObscurer.Reveal(obscured1).Should().Be(password);
        RClonePasswordObscurer.Reveal(obscured2).Should().Be(password);
    }

    [Fact]
    public void Obscure_ProducesValidBase64Output()
    {
        // Arrange
        var password = "testpassword";
        
        // Act
        var obscured = RClonePasswordObscurer.Obscure(password);
        
        // Assert
        obscured.Should().NotBeNullOrEmpty();
        
        // Should be valid base64
        var action = () => Convert.FromBase64String(obscured);
        action.Should().NotThrow();
    }

    [Theory]
    [InlineData("")]
    public void Obscure_EmptyString_ReturnsEmpty(string password)
    {
        // Act
        var result = RClonePasswordObscurer.Obscure(password);
        
        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void Obscure_NullString_ReturnsEmpty()
    {
        // Act
        var result = RClonePasswordObscurer.Obscure(null!);
        
        // Assert
        result.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    public void Reveal_EmptyString_ReturnsEmpty(string obscured)
    {
        // Act
        var result = RClonePasswordObscurer.Reveal(obscured);
        
        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void Reveal_NullString_ReturnsEmpty()
    {
        // Act
        var result = RClonePasswordObscurer.Reveal(null!);
        
        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void Reveal_InvalidBase64_ThrowsException()
    {
        // Arrange
        var invalidBase64 = "not-valid-base64!!!";
        
        // Act
        var action = () => RClonePasswordObscurer.Reveal(invalidBase64);
        
        // Assert
        action.Should().Throw<FormatException>();
    }

    [Fact]
    public void Reveal_TooShortData_ThrowsException()
    {
        // Arrange - Valid base64 but too short (less than 16 bytes nonce)
        var tooShort = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5 });
        
        // Act
        var action = () => RClonePasswordObscurer.Reveal(tooShort);
        
        // Assert
        action.Should().Throw<ArgumentException>()
            .WithMessage("*too short*");
    }

    [Fact]
    public void Obscure_LongPassword_RoundTripsCorrectly()
    {
        // Arrange - Password longer than AES block size (16 bytes)
        var password = new string('x', 1000);
        
        // Act
        var obscured = RClonePasswordObscurer.Obscure(password);
        var revealed = RClonePasswordObscurer.Reveal(obscured);
        
        // Assert
        revealed.Should().Be(password);
    }

    [Fact]
    public void Obscure_SingleCharacter_RoundTripsCorrectly()
    {
        // Arrange
        var password = "x";
        
        // Act
        var obscured = RClonePasswordObscurer.Obscure(password);
        var revealed = RClonePasswordObscurer.Reveal(obscured);
        
        // Assert
        revealed.Should().Be(password);
    }
}
