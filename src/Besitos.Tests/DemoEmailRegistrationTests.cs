using Besitos.Demo.Worker;
using Besitos.Email;
using Besitos.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Besitos.Tests;

public sealed class DemoEmailRegistrationTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    public void RelationalDemoRegistersBothHandlersWithoutOpeningTheJournal(string provider)
    {
        var services = new ServiceCollection();
        services.AddEmailBatches(new());
        var journal = services.AddProtectedDemoEmails(new ConfigurationBuilder().Build(), provider, "unused", new());
        Assert.NotNull(journal);
        using var container = services.BuildServiceProvider();
        Assert.Same(journal, container.GetRequiredService<IEmailDeliveryJournal>());
        var catalogue = container.GetRequiredService<IHandlerCatalogue>();
        Assert.Equal(2, catalogue.Handlers.Count);
        Assert.Equal(DuplicateHandling.DurableReservation, catalogue.Find(ProtectedEmailBatch.Command)!.DuplicateHandling);
    }

    [Fact]
    public void AzureRequiresAnExplicitSharedJournal()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        Assert.Null(services.AddProtectedDemoEmails(configuration, "azurestorage", "unused", new()));
        Assert.Empty(services);
        configuration["ConnectionStrings:emailJournal"] = "unused";
        Assert.Throws<ArgumentException>(() => services.AddProtectedDemoEmails(configuration, "azurestorage", "unused", new()));
        configuration["EmailJournal:Provider"] = "postgres";
        Assert.NotNull(services.AddProtectedDemoEmails(configuration, "azurestorage", "unused", new()));
    }

    [Fact]
    public void SubmissionAcceptsProtectedBatchesOnlyWhenRegistered()
    {
        var envelope = new ProtectedEmailBatch(Guid.NewGuid(), "Subject", "Text", [new("one", "one@example.test")]).ToEnvelope("many-email");
        DemoEmailRegistration.ValidateBatch(envelope, true);
        Assert.Throws<ArgumentException>(() => DemoEmailRegistration.ValidateBatch(envelope, false));
        Assert.Throws<ArgumentException>(() => DemoEmailRegistration.ValidateBatch(envelope with { Payload = "{broken" }, true));
        Assert.Throws<ArgumentException>(() => DemoEmailRegistration.ValidateBatch(envelope with { Command = "unknown.v1" }, true));
        DemoEmailRegistration.ValidateBatch(new EmailBatch("Subject", "Text", ["one@example.test"]).ToEnvelope(), false);
    }
}
