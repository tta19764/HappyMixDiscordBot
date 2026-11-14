using HappyMix.Domain.Abstractions;
using MediatR;

namespace HappyMix.Application.Abstractions.Messaging;

public interface ICommand : IRequest<Result>;

public interface ICommand<TResponse> : IRequest<Result<TResponse>>;
