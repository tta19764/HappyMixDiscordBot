using HappyMix.Domain.Abstractions;
using MediatR;

namespace HappyMix.Application.Abstractions.Messaging;

public interface ICommandHandler<in TCommand> : IRequestHandler<TCommand, Result>
    where TCommand : ICommand;
