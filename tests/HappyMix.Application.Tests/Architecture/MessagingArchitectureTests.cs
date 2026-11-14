using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Bot.Interactions;
using HappyMix.Domain.Abstractions;
using MediatR;

namespace HappyMix.Application.Tests.Architecture;

public sealed class MessagingArchitectureTests
{
    private static readonly Assembly Application = typeof(ICommand).Assembly;

    [Fact]
    public void ApplicationAndDomainOnlyDependOnInnerProjectLayers()
    {
        AssertProjectReferences(typeof(Result).Assembly);
        AssertProjectReferences(Application, "HappyMix.Domain");
    }

    [Fact]
    public void BotDoesNotReferenceGuildPersistencePorts()
    {
        using var stream = File.OpenRead(typeof(GeneralModule).Assembly.Location);
        using var executable = new PEReader(stream);
        var metadata = executable.GetMetadataReader();
        var forbidden = metadata.TypeReferences.Select(metadata.GetTypeReference)
            .Where(type => metadata.GetString(type.Namespace) == "HappyMix.Application.Abstractions.Guilds")
            .Select(type => metadata.GetString(type.Name))
            .Where(name => name.EndsWith("Store", StringComparison.Ordinal));
        Assert.Empty(forbidden);
    }

    [Fact]
    public void ApplicationRequestsUseCommandOrQueryContractsAndHaveOneMatchingHandler()
    {
        var types = Application.GetTypes();
        var requests = types.Where(type => type is { IsClass: true, IsAbstract: false } &&
            typeof(IBaseRequest).IsAssignableFrom(type)).ToArray();
        Assert.NotEmpty(requests);

        foreach (var request in requests)
        {
            var contracts = request.GetInterfaces();
            bool command = contracts.Contains(typeof(ICommand)) || contracts.Any(type => IsGeneric(type, typeof(ICommand<>)));
            bool query = contracts.Any(type => IsGeneric(type, typeof(IQuery<>)));
            Assert.True(command ^ query, $"{request.FullName} must implement exactly one command or query contract.");
            Assert.EndsWith(command ? "Command" : "Query", request.Name, StringComparison.Ordinal);

            var handlers = types.Where(type => type is { IsClass: true, IsAbstract: false } &&
                type.GetInterfaces().Any(contract => IsGeneric(contract, typeof(IRequestHandler<,>)) &&
                    contract.GenericTypeArguments[0] == request));
            var handler = Assert.Single(handlers);
            Assert.False(handler.IsVisible, $"{handler.FullName} must stay internal to Application.");
            Assert.Contains(handler.GetInterfaces(), contract => contract.IsGenericType &&
                contract.GenericTypeArguments[0] == request && (query
                    ? IsGeneric(contract, typeof(IQueryHandler<,>))
                    : IsGeneric(contract, typeof(ICommandHandler<>)) || IsGeneric(contract, typeof(ICommandHandler<,>))));
        }
    }

    private static bool IsGeneric(Type type, Type definition) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == definition;

    private static void AssertProjectReferences(Assembly assembly, params string[] allowed) =>
        Assert.All(assembly.GetReferencedAssemblies().Select(reference => reference.Name)
            .OfType<string>().Where(name => name.StartsWith("HappyMix.", StringComparison.Ordinal)),
            name => Assert.Contains(name, allowed));
}
