using Microsoft.Data.SqlClient;

namespace Lexfield.Onboarding;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: Lexfield.Onboarding <manifest-path> <admin-connection-string>");
            return 2;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(args[1]);
        var resolver = new SqlConnectionStringBuilder(args[1]);
        var runner = new TenantOnboardingRunner(
            tenant => new SqlConnectionStringBuilder(resolver.ConnectionString) { InitialCatalog = tenant.Database }.ConnectionString);
        await runner.RunAsync(args[0]);
        return 0;
    }
}
