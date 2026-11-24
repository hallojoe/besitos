using System.Data.Common;
using System.Text.Json;
using Besitos.Email;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Besitos.Demo.Worker;

public static class DemoEmailRegistration
{
    public static RelationalEmailDeliveryJournal? AddProtectedDemoEmails(this IServiceCollection services,
        IConfiguration configuration, string queueProvider, string queueConnection, SmtpOptions smtp)
    {
        var provider = configuration["EmailJournal:Provider"] ??
            (queueProvider is "sqlite" or "postgres" or "sqlserver" ? queueProvider : null);
        var connection = configuration.GetConnectionString("emailJournal") ??
            (provider == queueProvider ? queueConnection : null);
        if (provider is null && connection is null) return null;
        if (string.IsNullOrWhiteSpace(connection))
            throw new ArgumentException("Set ConnectionStrings__emailJournal for the selected email journal provider.");
        var kind = provider switch
        {
            "sqlite" => EmailJournalProvider.Sqlite,
            "postgres" => EmailJournalProvider.Postgres,
            "sqlserver" => EmailJournalProvider.SqlServer,
            _ => throw new ArgumentException("Set EmailJournal__Provider to sqlite, postgres, or sqlserver.")
        };
        DbConnection CreateConnection() => kind switch
        {
            EmailJournalProvider.Sqlite => new SqliteConnection(connection),
            EmailJournalProvider.Postgres => new NpgsqlConnection(connection),
            _ => new SqlConnection(connection)
        };
        var journal = new RelationalEmailDeliveryJournal(CreateConnection, kind);
        services.AddSingleton<IEmailDeliveryJournal>(journal);
        services.AddProtectedEmailBatches(smtp, new ProtectedEmailOptions(
            configuration["EmailJournal:Scope"] ?? $"besitos-demo/{queueProvider}/many-email"));
        return journal;
    }

    public static void ValidateBatch(Envelope envelope, bool protectedAvailable)
    {
        if (envelope.Command == EmailBatch.Command) { EmailBatch.Parse(envelope.Payload); return; }
        if (protectedAvailable && envelope.Command == ProtectedEmailBatch.Command)
        {
            try
            {
                var batch = JsonSerializer.Deserialize<ProtectedEmailBatch>(envelope.Payload, JsonSerializerOptions.Web)
                    ?? throw new ArgumentException("Missing protected email batch.");
                batch.Validate();
                return;
            }
            catch (JsonException) { throw new ArgumentException("Invalid protected email batch JSON."); }
        }
        throw new ArgumentException("This group accepts its registered email batch commands only.");
    }
}
