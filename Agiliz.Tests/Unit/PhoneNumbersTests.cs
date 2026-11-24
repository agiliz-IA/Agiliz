using Agiliz.Core.Messaging;
using FluentAssertions;
using Xunit;

namespace Agiliz.Tests.Unit;

public sealed class PhoneNumbersTests
{
    [Theory]
    [InlineData("+55 21 91234-5678", "5521912345678", true)]
    [InlineData("(21) 91234-5678", "5521912345678", true)]
    [InlineData("552112345678", "5521912345678", true)]   // WhatsApp de conta antiga, sem o nono dígito
    [InlineData("5521912345678", "5521987650000", false)]
    [InlineData("", "5521912345678", false)]
    [InlineData("552133334444", "5521933334444", true)]   // fixo de 8 dígitos x celular com 9: mesma regra
    public void Matches_HandlesFormatsAndNinthDigit(string a, string b, bool expected) =>
        PhoneNumbers.Matches(a, b).Should().Be(expected);
}
