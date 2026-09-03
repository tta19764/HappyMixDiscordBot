using HappyMix.Application;
using HappyMix.Bot.Configuration;
using HappyMix.Infrastructure;
using Microsoft.Extensions.Hosting;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddApplication()
    .AddInfrastructure()
    .AddBot(builder.Configuration);

await builder.Build().RunAsync();
