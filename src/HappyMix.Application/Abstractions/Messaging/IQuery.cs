using HappyMix.Domain.Abstractions;
using MediatR;

namespace HappyMix.Application.Abstractions.Messaging;

public interface IQuery<TResponse> : IRequest<Result<TResponse>>;
