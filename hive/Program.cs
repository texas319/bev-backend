// ============================================================
// FILE        : Program.cs
// STATUS      : Phase 1b — Hive /v1/seven/query stub
// LAST UPD    : 2026-05-24 13:00 CST
// PURPOSE     : Functions host bootstrap. Registers Key Vault
//               client + JWT validator. No Cosmos in 1b — query
//               is a stub returning canned text.
// OWNS        : DI container, host lifecycle.
// CALLED BY   : Functions runtime at cold start.
// CHANGE LOG  :
//   2026-05-24 13:00 CST  v0-26.0524-B  Initial scaffold (Phase 1b).
// ============================================================

using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using BEV.Hive.Services;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

try
{
    Console.WriteLine("[BEV.Hive] Bootstrap starting...");
    Console.WriteLine($"[BEV.Hive] KEYVAULT_URI={Environment.GetEnvironmentVariable("KEYVAULT_URI")}");
    Console.WriteLine($"[BEV.Hive] COSMOS_ENDPOINT={Environment.GetEnvironmentVariable("COSMOS_ENDPOINT")}");
    Console.WriteLine($"[BEV.Hive] HIVE_BUILD_LABEL={Environment.GetEnvironmentVariable("HIVE_BUILD_LABEL")}");

    var host = new HostBuilder()
        .ConfigureFunctionsWebApplication()
        .ConfigureLogging(logging =>
        {
            logging.AddConsole();
            logging.SetMinimumLevel(LogLevel.Information);
        })
        .ConfigureServices(services =>
        {
            services
                .AddApplicationInsightsTelemetryWorkerService()
                .ConfigureFunctionsApplicationInsights();

            services.AddSingleton<SecretClient>(sp =>
            {
                var uri = Environment.GetEnvironmentVariable("KEYVAULT_URI")
                    ?? throw new InvalidOperationException("KEYVAULT_URI not configured.");
                return new SecretClient(new Uri(uri), new DefaultAzureCredential());
            });

            services.AddSingleton<CosmosClient>(sp =>
            {
                var endpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT")
                    ?? throw new InvalidOperationException("COSMOS_ENDPOINT not configured.");
                var options = new CosmosClientOptions
                {
                    SerializerOptions = new CosmosSerializationOptions
                    {
                        PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
                    },
                    ApplicationName = "BEV.Hive"
                };
                return new CosmosClient(endpoint, new DefaultAzureCredential(), options);
            });

            services.AddSingleton<IJwtValidator, JwtValidator>();
            services.AddHttpClient();
            services.AddSingleton<ISignalRService, SignalRService>();
            services.AddSingleton<IFunctionCatalog, FunctionCatalog>();
            services.AddSingleton<IHiveStorage, HiveStorage>();

            // Audit pipeline (item 11): Postgres-backed ingest store.
            // Connection string from AUDIT_PG_CONN (a Key Vault reference
            // in prod app settings). Registered as a singleton so the
            // information_schema column cache + partition-ensured set
            // persist across invocations on a warm instance.
            services.AddSingleton<IAuditStore>(sp =>
            {
                var conn = Environment.GetEnvironmentVariable("AUDIT_PG_CONN")
                    ?? throw new InvalidOperationException("AUDIT_PG_CONN not configured.");
                return new PostgresAuditStore(conn);
            });
        })
        .Build();

    Console.WriteLine("[BEV.Hive] Host built. Starting...");
    host.Run();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[BEV.Hive] STARTUP FAILURE: {ex.GetType().FullName}");
    Console.Error.WriteLine($"[BEV.Hive] Message: {ex.Message}");
    Console.Error.WriteLine($"[BEV.Hive] Stack: {ex.StackTrace}");
    if (ex.InnerException is not null)
    {
        Console.Error.WriteLine($"[BEV.Hive] Inner: {ex.InnerException.GetType().FullName}");
        Console.Error.WriteLine($"[BEV.Hive] Inner message: {ex.InnerException.Message}");
    }
    throw;
}
