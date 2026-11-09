using HappyMix.Application;
using HappyMix.Bot.Configuration;
using HappyMix.Infrastructure;
using Microsoft.Extensions.Hosting;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration)
    .AddBot(builder.Configuration);

using IHost host = builder.Build();
// Fail before Discord connects or Quartz starts if the durable store cannot be migrated.
await host.Services.InitializeDatabaseAsync();
await host.RunAsync();
