using Evently.Modules.Events.Application.Abstractions.Messaging;

namespace Evently.Modules.Events.Application.Events.ResheduleEvent;

public sealed record RescheduleEventCommand(Guid EventId, DateTime StartsAtUtc, DateTime? EndsAtUtc) : ICommand;
