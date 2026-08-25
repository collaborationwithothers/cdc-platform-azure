using Microsoft.Data.SqlClient;

namespace Lexfield.Onboarding;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length is < 2 or > 3)
        {
            Console.Error.WriteLine("Usage: Lexfield.Onboarding <manifest-path> <admin-connection-string> [connector-identity]");
            return 2;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(args[1]);
        var resolver = new SqlConnectionStringBuilder(args[1]);
        var runner = new TenantOnboardingRunner(
            tenant => new SqlConnectionStringBuilder(resolver.ConnectionString) { InitialCatalog = tenant.Database }.ConnectionString,
            args.Length == 3 ? args[2] : null);
        await runner.RunAsync(args[0]);
        return 0;
    }
}
