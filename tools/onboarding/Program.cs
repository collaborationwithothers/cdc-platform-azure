using Microsoft.Data.SqlClient;

namespace Lexfield.Onboarding;

public static class Program
{
    private const string Usage = """
        Tenant onboarding prepares each tenant database for this change data capture (CDC) platform. It creates the tables and database settings used by Debezium, which reads committed database changes, and the reconciler, which checks downstream state for missed changes. It can also grant Kafka Connect access to the database.
        Usage: Lexfield.Onboarding <manifest-path> <admin-connection-string> [connector-identity]
        <manifest-path> is a JSON file whose top-level value is an array of tenant objects with tenantId, database, and streamIsolated properties.
        <admin-connection-string> is the administrative SQL connection string used to open each database named by the manifest.
        [connector-identity] is the optional Microsoft Entra ID identity used by Kafka Connect, the worker service that runs the Debezium connector. Supply it when the connector must read this tenant database.
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length is < 2 or > 3)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        if (string.IsNullOrWhiteSpace(args[1]))
        {
            throw new ArgumentException(
                "The admin-connection-string input is required. Supply an administrative SQL connection string as the second argument, then rerun onboarding.",
                "admin-connection-string");
        }
        SqlConnectionStringBuilder resolver;
        try
        {
            resolver = new SqlConnectionStringBuilder(args[1]);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(
                "The admin-connection-string input is not a valid SQL connection string. " +
                "Supply a valid administrative SQL connection string as the second argument, then rerun onboarding.",
                "admin-connection-string",
                exception);
        }

        var runner = new TenantOnboardingRunner(
            tenant => new SqlConnectionStringBuilder(resolver.ConnectionString) { InitialCatalog = tenant.Database }.ConnectionString);
        var connectorIdentity = args.Length == 3 ? args[2] : null;
        await runner.RunAsync(args[0], connectorIdentity, log: Console.WriteLine);
        return 0;
    }
}
