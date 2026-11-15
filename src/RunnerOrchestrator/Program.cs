using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RunnerOrchestrator;
using RunnerOrchestrator.Configuration;
using RunnerOrchestrator.Docker;
using RunnerOrchestrator.GitHub;
using RunnerOrchestrator.Pool;

var builder = Host.CreateApplicationBuilder(args);

// The repository list is its own file (docs/architecture.md); its path can be overridden.
var repositoriesFile = builder.Configuration["RepositoriesFile"] ?? "repositories.json";
builder.Configuration.AddJsonFile(repositoriesFile, optional: true, reloadOnChange: true);

builder.Logging.AddSimpleConsole(console =>
{
    console.SingleLine = true;
    console.UseUtcTimestamp = true;
    console.TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ ";
});

builder.Services.AddOptions<OrchestratorOptions>()
    .Bind(builder.Configuration.GetSection(OrchestratorOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<OrchestratorOptions>, OrchestratorOptionsValidator>();
builder.Services.AddOptions<RepositoryListOptions>().Bind(builder.Configuration);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => GitHubHttp.CreateClient(sp.GetRequiredService<IOptions<OrchestratorOptions>>().Value.GitHubApp.ApiBaseUrl));
builder.Services.AddSingleton<IGitHubCredentialSource, GitHubAppCredentials>();
builder.Services.AddSingleton<IGitHubRunnerApi, GitHubRunnerApi>();
builder.Services.AddSingleton<IContainerRuntime>(sp => DockerEngineClient.ForSocket(sp.GetRequiredService<IOptions<OrchestratorOptions>>().Value.DockerSocket));
builder.Services.AddSingleton<DemandPoller>();
builder.Services.AddSingleton<RepositoryGuard>();
builder.Services.AddSingleton<RunnerLauncher>();
builder.Services.AddSingleton<PoolCycle>();
builder.Services.AddHostedService<OrchestratorWorker>();

await builder.Build().RunAsync().ConfigureAwait(false);
