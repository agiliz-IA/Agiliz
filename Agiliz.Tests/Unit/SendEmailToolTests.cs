using Agiliz.Core.Tools;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Agiliz.Tests.Unit;

public sealed class SendEmailToolTests
{
    private readonly Mock<IEmailSender> _emailSenderMock = new();
    private readonly SendEmailTool _tool;

    public SendEmailToolTests()
    {
        _tool = new SendEmailTool(NullLogger<SendEmailTool>.Instance, _emailSenderMock.Object);
    }

    [Fact]
    public async Task ExecuteAsync_WithValidJson_CallsSenderAndReturnsSuccess()
    {
        var json = """
                   {
                     "to": "teste@exemplo.com",
                     "subject": "Olá Mundo",
                     "body": "Corpo da mensagem"
                   }
                   """;

        var result = await _tool.ExecuteAsync(json);

        _emailSenderMock.Verify(x => x.SendEmailAsync("teste@exemplo.com", "Olá Mundo", "Corpo da mensagem", It.IsAny<CancellationToken>()), Times.Once);
        result.Output.Should().Be("E-mail enviado com sucesso para teste@exemplo.com");
        result.Cost.Should().Be(0.02m);
    }

    [Fact]
    public async Task ExecuteAsync_WithInvalidJson_ReturnsFailureString()
    {
        var json = "{ invalid json }";

        var result = await _tool.ExecuteAsync(json);

        _emailSenderMock.Verify(x => x.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        result.Output.Should().StartWith("Falha ao enviar e-mail");
    }
}
